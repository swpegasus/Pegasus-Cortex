using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.Persistence.Sqlite.V2;
using M = Pegasus.Cortex.Geometry.Shadow.CortexShadowMetric;

namespace Pegasus.Cortex.Geometry.Shadow;

/// <summary>Explicit research lifetime. Never owns or calls the ordinary provider.
/// Text projections are bounded, transient and cleared at shutdown. Durable records contain references only.</summary>
public sealed class CortexGeometryShadow : IAsyncDisposable
{
    private readonly CortexShadowProfile _profile;
    private readonly CortexShadowOptions _options;
    private readonly HashSet<CortexShadowIdentity> _eligible;
    private readonly ConcurrentDictionary<CortexShadowIdentity, byte> _admittedThisRun = new();
    private int _distinctAdmittedThisRun;
    private readonly LatencyBuckets _foregroundObserverLatency = new();
    private readonly LatencyBuckets _workerLatency = new();
    private readonly LatencyBuckets _queueDelayLatency = new();
    private readonly HashSet<(string, string)> _known;
    private readonly ConcurrentDictionary<(string, string), (long Revision, bool Inactive, string Provenance)> _frontier = new();
    private long _revisionEpoch;
    private readonly Guid _runId = Guid.NewGuid();
    private long _savedEpoch = -1, _baseJournalWrites, _baseJournalEvictions, _metadataBytes;
    private DateTimeOffset _lastSummary;
    private readonly long[] _metrics = new long[Enum.GetValues<M>().Length];
    private readonly Channel<ShadowWork>? _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly ShadowJournal? _journal;
    private readonly Task? _worker;
    private readonly Dictionary<(string, string), Entry> _points = [];
    private readonly Dictionary<(string, string), (long Revision, bool Inactive)> _latest = [];
    private readonly Dictionary<Guid, CortexShadowObservation> _observations = [];
    private readonly HashSet<Guid> _labeled = [];
    private int _disposed, _pointCount;
    private long _sampleOrdinal, _journalBytes, _journalWrites, _journalEvictions;
    private string? _lastFailure;
    private sealed record Entry(ShadowProjection Projection, DateTimeOffset At);

    public CortexGeometryShadow(CortexShadowProfile profile, string researchRoot,
        CortexShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.EligibleRecords);
        _options = options ?? new(); _options.Validate();
        if (profile.TaskId == Guid.Empty || profile.ProfileId == Guid.Empty ||
            profile.Generation == Guid.Empty || profile.EligibleRecords.Count > 2048)
            throw new ArgumentException("An exact task/profile/generation and bounded eligibility manifest are required.");
        _eligible = profile.EligibleRecords.ToHashSet();
        foreach (var id in _eligible)
            if (id is null || id.Namespace is null || id.RecordId is null || id.ProvenanceSha256 is null ||
                !(id.Namespace == "knowledge.shared" || id.Namespace.StartsWith("project.", StringComparison.Ordinal)) ||
                id.Namespace.Length > 128 || id.Namespace.Any(char.IsControl) ||
                !System.Text.RegularExpressions.Regex.IsMatch(id.RecordId, @"^[A-Za-z0-9._-]{1,128}$") ||
                id.Revision < 1 || !System.Text.RegularExpressions.Regex.IsMatch(id.ProvenanceSha256, "^[0-9a-f]{64}$"))
                throw new ArgumentException("Research admission requires a safe exact shared/project knowledge revision.");
        var eligibilityBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            _eligible.Select(x => new ShadowFrontierEntry(x, false)).ToArray()).Length;
        if (eligibilityBytes + _eligible.Count * 18L + 4096 > 1024 * 1024)
            throw new ArgumentException("The reviewed cohort exceeds the bounded reference-checkpoint budget.");
        _known = _eligible.Select(id => (id.Namespace, id.RecordId)).ToHashSet();
        _profile = profile with { EligibleRecords = _eligible.ToArray() };
        if (!_options.CollectionEnabled) return; // No path access, worker, queue or SQL instrumentation.
        _journal = new(researchRoot, _profile, _options);
        _journalBytes = _journal.Bytes;
        _metadataBytes = _journal.MetadataBytes;
        try
        {
            if (_journal.ReadFrontier() is { } frontier)
            {
                foreach (var entry in frontier.Entries)
                {
                    var id = entry.Identity;
                    if (!_known.Contains((id.Namespace, id.RecordId)) || id.Revision < 1 ||
                        !System.Text.RegularExpressions.Regex.IsMatch(id.ProvenanceSha256, "^[0-9a-f]{64}$"))
                        throw new IOException("Research reference checkpoint escaped its reviewed cohort.");
                    if (!_frontier.TryAdd((id.Namespace, id.RecordId), (id.Revision, entry.Inactive, id.ProvenanceSha256)))
                        throw new IOException("Duplicate research reference checkpoint identity.");
                }
                _revisionEpoch = frontier.Epoch; _savedEpoch = frontier.Epoch;
            }
            if (_journal.ReadSummary() is { } previous)
            {
                foreach (var metric in Enum.GetValues<M>())
                    _metrics[(int)metric] = previous.Status.Counters.GetValueOrDefault(metric.ToString());
                _metrics[(int)M.QueuedBytes] = 0;
                _metrics[(int)M.RevisionStateUpdates] = Math.Max(_metrics[(int)M.RevisionStateUpdates], _revisionEpoch);
                Add(M.RecoveryUncertainWork, previous.Final ? 0 : previous.PendingWorkAtSnapshot);
                _lastFailure = previous.Status.LastFailure;
                _baseJournalWrites = previous.Status.JournalWrites;
                _baseJournalEvictions = previous.Status.JournalEvictions;
                _journalWrites = _baseJournalWrites; _journalEvictions = _baseJournalEvictions;
            }
        }
        catch { _journal.DisposeAsync().GetAwaiter().GetResult(); throw; }
        _queue = Channel.CreateBounded<ShadowWork>(new BoundedChannelOptions(_options.QueueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(WorkAsync);
        else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(WorkAsync); }
    }

    public bool Enabled => _options.CollectionEnabled && Volatile.Read(ref _disposed) == 0 && _worker?.IsCompleted != true;
    public CortexShadowStatus GetStatus()
    {
        var enabled = Enabled;
        return new(enabled, false, _profile.Generation,
        Enum.GetValues<M>().ToDictionary(m => m.ToString(), m => Interlocked.Read(ref _metrics[(int)m])),
        Volatile.Read(ref _pointCount), Interlocked.Read(ref _journalBytes),
        Interlocked.Read(ref _journalWrites), Interlocked.Read(ref _journalEvictions),
        Volatile.Read(ref _lastFailure),
        "Observational agreement/reach/cost only. Accuracy unestablished; labeled sample count is explicit.")
        { Lifecycle = !_options.CollectionEnabled ? "disabled" : Volatile.Read(ref _disposed) != 0
            ? _worker?.IsCompleted == true ? "stopped" : "stopping" : _worker?.IsFaulted == true ? "faulted" : "running",
          ObservedRevisionEpoch = Interlocked.Read(ref _revisionEpoch), MetadataBytes = Interlocked.Read(ref _metadataBytes),
          ReviewedRecordRevisions = _eligible.Count, LastObservedRecordIds = _frontier.Count,
          EvaluationEnabled = enabled && _options.EvaluationEnabled,
          EffectiveMode = !enabled ? "off" : _options.EvaluationEnabled ? "collect-and-score" : "collect-only",
          DistinctAdmittedIdentitiesThisRun = Volatile.Read(ref _distinctAdmittedThisRun),
          LatenciesThisRun = new Dictionary<string, CortexShadowLatencyBuckets>(StringComparer.Ordinal)
          {
              ["ForegroundObserverSection"] = _foregroundObserverLatency.Snapshot(),
              ["Worker"] = _workerLatency.Snapshot(),
              ["QueueDelay"] = _queueDelayLatency.Snapshot()
          } };
    }
    internal void Add(M metric, long amount = 1) => Interlocked.Add(ref _metrics[(int)metric], amount);
    internal void Failure(string code) { Add(M.ObserverFailures); Volatile.Write(ref _lastFailure, code); }
    internal void ObserverCompleted(long ticks)
    {
        Add(M.ObserverTicks, ticks);
        _foregroundObserverLatency.Observe(ticks);
    }

    private sealed class LatencyBuckets
    {
        private static readonly long[] Bounds = [50, 100, 500, 1000, 5000, 10000, 50000, 250000];
        private readonly object _gate = new();
        private readonly long[] _counts = new long[Bounds.Length + 1];
        private long _samples, _total, _maximum;

        internal void Observe(long ticks)
        {
            ticks = Math.Max(0, ticks);
            var microseconds = ticks * (1_000_000d / Stopwatch.Frequency);
            var bucket = 0;
            while (bucket < Bounds.Length && microseconds > Bounds[bucket]) bucket++;
            lock (_gate)
            {
                _counts[bucket]++;
                _samples++;
                _total += ticks;
                _maximum = Math.Max(_maximum, ticks);
            }
        }

        internal CortexShadowLatencyBuckets Snapshot()
        {
            lock (_gate) return new(Bounds.ToArray(), _counts.ToArray(), _samples, _total, _maximum);
        }
    }

    internal void ProviderCompleted(long elapsed, CortexSqliteTraceMeasurement measurement, bool failed)
    {
        Add(M.ProviderOperations); Add(M.ProviderTicks, elapsed);
        Add(M.SqliteStatementTraceEvents, measurement.StatementTraceEvents);
        Add(M.MeasuredConnections, measurement.Connections);
        Add(M.TraceAttachmentFailures, measurement.AttachmentFailures);
        if (failed) Add(M.ForegroundFailures);
    }

    internal void Observe(object? request, object? result, long providerTicks,
        CortexSqliteTraceMeasurement measurement)
    {
        if (!Enabled) return;
        var timer = Stopwatch.StartNew();
        var projections = new List<ShadowProjection>(8);
        var ordinary = new List<CortexShadowRank>(4);
        string? intent = null; string[] namespaces = [];
        var operation = "provider"; var returned = 0;
        void Point(CortexShadowIdentity id, string title, string summary, IReadOnlyList<string> tags,
            string projection, string state)
        {
            if (!_known.Contains((id.Namespace, id.RecordId))) { Add(M.RejectedPoints); return; }
            Note(id, state != "active" || !_eligible.Contains(id));
            if (projections.Count == 8) { Add(M.RejectedPoints); return; }
            var admitted = _eligible.Contains(id) && state == "active";
            if (admitted)
            {
                Add(M.EligiblePoints);
                // Only exact cohort members can enter this set, so its retained
                // identities cannot exceed the existing 2,048-revision admission.
                if (_admittedThisRun.TryAdd(id, 0)) Interlocked.Increment(ref _distinctAdmittedThisRun);
            }
            else Add(M.RejectedPoints);
            CortexKnowledgeGeometryPoint? point = admitted
                ? new($"{id.Namespace}\nknowledge\n{id.RecordId}\n{id.Revision}", id.Namespace, id.RecordId, id.Revision, title[..Math.Min(512, title.Length)],
                    tags.Take(16).Select(t => t[..Math.Min(128, t.Length)]).ToArray(),
                    summary[..Math.Min(4096, summary.Length)], 0, false) : null;
            projections.Add(new(id, point, projection, admitted ? state : state == "active" ? "ineligible-revision" : state));
        }
        if (request is CortexSearchRequestV2 search && result is CortexSearchResultV2 page)
        {
            operation = "search"; returned = page.Items.Count; Add(M.SamplesConsidered);
            var sampled = Interlocked.Increment(ref _sampleOrdinal) % _options.SampleEvery == 0;
            if (sampled && search.Text.Length <= 512 && search.Namespaces.Count <= 4)
            { intent = search.Text; namespaces = search.Namespaces.ToArray(); Add(M.Sampled); }
            else Add(M.SamplingSkipped);
            for (var i = 0; i < Math.Min(32, page.Items.Count); i++)
            {
                var hit = page.Items[i];
                if (hit.RecordType != CortexRecordTypeV2.Knowledge) continue;
                var id = CortexShadowIdentity.From(hit);
                Point(id, hit.Title, hit.Excerpt, [], "search-preview", "active");
                if (i < 4 && _eligible.Contains(id))
                    ordinary.Add(new(id.Coordinate, i + 1));
            }
        }
        else if (result is CortexReadResultV2 { Record: CortexKnowledgeV2 record })
        {
            operation = "read";
            Point(CortexShadowIdentity.From(record), record.Title, record.Content, record.Tags,
                "read-index-projection", record.State == CortexKnowledgeStateV2.Active ? "active" : "inactive");
        }
        else if (result is CortexKnowledgeResultV2 { Record: { } receipt })
        { operation = "knowledge-receipt"; Receipt(receipt); }
        else if (result is CortexFinishResultV2 finish)
        { operation = "finish-receipts"; foreach (var item in finish.Knowledge.Take(32)) Receipt(item); }
        void Receipt(CortexKnowledgeReceiptV2 receipt)
        {
            var identity = new CortexShadowIdentity(receipt.Namespace, receipt.RecordId, receipt.Revision,
                CortexShadowIdentity.Provenance(receipt.Provenance.Source, receipt.Provenance.Actor,
                    receipt.Provenance.SourceId, receipt.Provenance.CapturedAtUtc));
            if (!_known.Contains((identity.Namespace, identity.RecordId))) return;
            Note(identity, receipt.State != CortexKnowledgeStateV2.Active || !_eligible.Contains(identity));
            if (projections.Count < 8)
                projections.Add(new(identity, null, "receipt-only",
                    receipt.State == CortexKnowledgeStateV2.Active ? "receipt" : "inactive"));
            else Add(M.RejectedPoints);
        }
        if (projections.Count == 0 && intent is null) return;
        if (ordinary.Count == 0) intent = null;
        var bytes = 2048 + (intent?.Length ?? 0) * 2 +
            projections.Sum(x => 4096 + (x.Point is null ? 0 :
                (x.Point.Title.Length + x.Point.Summary.Length + x.Point.Tags.Sum(t => t.Length)) * 2));
        timer.Stop();
        Enqueue(new(Guid.NewGuid(), DateTimeOffset.UtcNow, operation, intent, namespaces,
            projections.ToArray(), ordinary.ToArray(), returned, providerTicks, timer.ElapsedTicks,
            measurement.StatementTraceEvents, measurement.Connections, measurement.AttachmentFailures, bytes));
    }

    // Metadata advances even when the work queue drops an invalidation. No vector work or IO here.
    private void Note(CortexShadowIdentity id, bool inactive)
    {
        var key = (id.Namespace, id.RecordId);
        while (true)
        {
            if (!_frontier.TryGetValue(key, out var old))
            {
                if (!_frontier.TryAdd(key, (id.Revision, inactive, id.ProvenanceSha256))) continue;
            }
            else
            {
                if (id.Revision < old.Revision) return;
                var next = id.Revision == old.Revision
                    ? (old.Revision, old.Inactive || inactive || old.Provenance != id.ProvenanceSha256, old.Provenance)
                    : (id.Revision, inactive, id.ProvenanceSha256);
                if (next == old) return;
                if (!_frontier.TryUpdate(key, next, old)) continue;
            }
            Interlocked.Increment(ref _revisionEpoch); Add(M.RevisionStateUpdates); return;
        }
    }

    private bool Current(ShadowProjection point) =>
        _frontier.TryGetValue((point.Identity.Namespace, point.Identity.RecordId), out var state) &&
        state.Revision == point.Identity.Revision && !state.Inactive &&
        state.Provenance == point.Identity.ProvenanceSha256;

    private bool Enqueue(ShadowWork work)
    {
        if (!Enabled || _queue is null) return false;
        var bytes = Interlocked.Add(ref _metrics[(int)M.QueuedBytes], work.Bytes);
        if (bytes > _options.MaximumQueuedBytes || !_queue.Writer.TryWrite(work with { EnqueuedTimestamp = Stopwatch.GetTimestamp() }))
        { Add(M.QueuedBytes, -work.Bytes); Add(M.QueueDropped); return false; }
        long old;
        do { old = Interlocked.Read(ref _metrics[(int)M.PeakQueuedBytes]); if (old >= bytes) break; }
        while (Interlocked.CompareExchange(ref _metrics[(int)M.PeakQueuedBytes], bytes, old) != old);
        Add(M.Queued); return true;
    }

    /// <summary>Optional future human-reviewed labels. No inferred labels or self-grading.
    /// EvidenceId must be an opaque UUID; targets must be exact admitted coordinates.</summary>
    public bool TrySubmitFeedback(CortexShadowFeedback feedback)
    {
        if (!Enabled || feedback.Generation != _profile.Generation ||
            !Guid.TryParse(feedback.EvidenceId, out _) || feedback.CanonicalTargets.Count is < 1 or > 16 ||
            feedback.CanonicalTargets.Distinct().Count() != feedback.CanonicalTargets.Count ||
            feedback.CanonicalTargets.Any(c => !_eligible.Any(id => id.Coordinate == c)))
        { Add(M.FeedbackRejected); return false; }
        return Enqueue(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "feedback", null, [], [], [], 0,
            0, 0, 0, 0, 0, 8192, feedback with { CanonicalTargets = feedback.CanonicalTargets.ToArray() }));
    }

    private async Task WorkAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                await Task.Delay(_options.TickMilliseconds, token).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                Maintain(now);
                if (now - _lastSummary >= TimeSpan.FromSeconds(10))
                {
                    try { await PersistAsync(false, token).ConfigureAwait(false); _lastSummary = now; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception) { Add(M.WorkerFailures); Volatile.Write(ref _lastFailure, "shadow-summary-failed"); }
                }
                if (!_queue!.Reader.TryRead(out var work)) continue;
                Add(M.QueuedBytes, -work.Bytes);
                if (work.EnqueuedTimestamp != 0)
                    _queueDelayLatency.Observe(Stopwatch.GetTimestamp() - work.EnqueuedTimestamp);
                var watch = Stopwatch.StartNew();
                try { await ProcessAsync(work, token).ConfigureAwait(false); Add(M.Processed); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Add(M.WorkerFailures); Volatile.Write(ref _lastFailure, $"shadow-work:{ex.GetType().Name}:0x{ex.HResult:X8}"); }
                finally
                {
                    watch.Stop();
                    Add(M.WorkerTicks, watch.ElapsedTicks);
                    _workerLatency.Observe(watch.ElapsedTicks);
                    if (watch.ElapsedMilliseconds > _options.EvaluationMilliseconds) Add(M.WorkerOverBudget);
                    if (_journal is not null)
                    {
                        Interlocked.Exchange(ref _journalBytes, _journal.Bytes);
                        Interlocked.Exchange(ref _journalWrites, _baseJournalWrites + _journal.Writes);
                        Interlocked.Exchange(ref _journalEvictions, _baseJournalEvictions + _journal.Evictions);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            while (_queue!.Reader.TryRead(out var abandoned))
            { Add(M.QueuedBytes, -abandoned.Bytes); Add(M.QueueDropped); }
            _points.Clear(); _latest.Clear(); _observations.Clear(); _labeled.Clear();
            try { await PersistAsync(true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { Add(M.WorkerFailures); Volatile.Write(ref _lastFailure, "shadow-final-summary-failed"); }
            _frontier.Clear();
            _admittedThisRun.Clear();
            Volatile.Write(ref _pointCount, 0);
            await _journal!.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask PersistAsync(bool final, CancellationToken token)
    {
        var summaryWatch = Stopwatch.StartNew();
        try
        {
        var epoch = Interlocked.Read(ref _revisionEpoch);
        ShadowFrontierSnapshot? frontier = epoch == _savedEpoch ? null : new("cortex.shadow.frontier/1",
            _profile.Generation, epoch, _frontier.Select(x => new ShadowFrontierEntry(
                new(x.Key.Item1, x.Key.Item2, x.Value.Revision, x.Value.Provenance), x.Value.Inactive)).ToArray());
        var status = GetStatus();
        if (final) status = status with { CollectionEnabled = false, CurrentPoints = 0, Lifecycle = "stopped" };
        await _journal!.SaveSummaryAsync(new("cortex.shadow.summary/1", _profile.Generation, _runId,
            DateTimeOffset.UtcNow, final, _queue!.Reader.Count, status), frontier, token).ConfigureAwait(false);
        _savedEpoch = epoch;
        Interlocked.Exchange(ref _metadataBytes, _journal.MetadataBytes);
        }
        finally { Add(M.SummaryTicks, summaryWatch.ElapsedTicks); }
    }

    private DateTimeOffset _lastPrune;
    private void Maintain(DateTimeOffset now)
    {
        var maintenanceWatch = Stopwatch.StartNew();
        try
        {
        foreach (var key in _points.Where(x => !Current(x.Value.Projection) || now - x.Value.At > TimeSpan.FromSeconds(_options.RetentionSeconds))
                     .Select(x => x.Key).ToArray()) { _points.Remove(key); Add(M.PointEvictions); }
        Volatile.Write(ref _pointCount, _points.Count);
        foreach (var id in _observations.Where(x => now - x.Value.ObservedAtUtc > TimeSpan.FromSeconds(_options.RetentionSeconds))
                     .Select(x => x.Key).ToArray()) { _observations.Remove(id); _labeled.Remove(id); }
        if (now - _lastPrune < TimeSpan.FromSeconds(Math.Min(60, _options.RetentionSeconds))) return;
        try { _journal!.Prune(now); Interlocked.Exchange(ref _journalBytes, _journal.Bytes); }
        catch (Exception) { Add(M.WorkerFailures); Volatile.Write(ref _lastFailure, "shadow-retention-failed"); }
        _lastPrune = now;
        }
        finally { Add(M.MaintenanceTicks, maintenanceWatch.ElapsedTicks); }
    }

    private async ValueTask ProcessAsync(ShadowWork work, CancellationToken token)
    {
        if (work.Feedback is { } feedback)
        {
            if (!_observations.TryGetValue(feedback.ObservationId, out var observed) || _labeled.Contains(feedback.ObservationId))
            { Add(M.FeedbackRejected); return; }
            var targets = feedback.CanonicalTargets;
            var reached = targets.Count(observed.Reached.Contains);
            var selectedTargets = targets.Count(t => observed.Shadow.Any(r => r.Coordinate == t));
            await _journal!.WriteAsync(new CortexShadowFeedbackResult("cortex.shadow.feedback/1",
                feedback.ObservationId, _profile.Generation, feedback.EvidenceId, targets.Count,
                reached, selectedTargets, reached - selectedTargets, targets.Count - reached), work.At, token).ConfigureAwait(false);
            _labeled.Add(feedback.ObservationId); Add(M.LabeledSamples);
            Add(M.LabeledTargets, targets.Count); Add(M.LabeledReached, reached);
            Add(M.LabeledSelected, selectedTargets); Add(M.LabeledUnseen, targets.Count - reached); return;
        }
        var watch = Stopwatch.StartNew();
        var epoch = Interlocked.Read(ref _revisionEpoch);
        var evaluatedAt = DateTimeOffset.UtcNow;
        var refs = new List<CortexShadowPointReference>();
        foreach (var item in work.Points)
        {
            token.ThrowIfCancellationRequested();
            var key = (item.Identity.Namespace, item.Identity.RecordId);
            if (item.Point is not null && !Current(item))
            { Add(M.StaleRevisions); continue; }
            var prior = _latest.GetValueOrDefault(key);
            if (item.Identity.Revision < prior.Revision ||
                item.Identity.Revision == prior.Revision && prior.Inactive && item.State != "inactive")
            { Add(M.StaleRevisions); continue; }
            var inactive = item.State is "inactive" or "ineligible-revision";
            _latest[key] = (item.Identity.Revision, inactive);
            if (inactive || _points.TryGetValue(key, out var old) &&
                old.Projection.Identity != item.Identity)
                _points.Remove(key);
            if (inactive) Add(M.Tombstones);
            CortexKnowledgePointDescriptor? descriptor = null;
            if (item.Point is not null)
            {
                descriptor = CortexKnowledgeGeometry.DescribePoint(item.Point); Add(M.PointDescriptorGenerations);
                if (!_points.TryGetValue(key, out var existing) ||
                    existing.Projection.Projection != "read-index-projection" || item.Projection == "read-index-projection")
                    _points[key] = new(item, work.At);
            }
            refs.Add(new(item.Identity, item.Projection, descriptor, item.State));
        }
        foreach (var key in _points.Where(x => work.At - x.Value.At > TimeSpan.FromSeconds(_options.RetentionSeconds))
                     .Select(x => x.Key).ToArray()) { _points.Remove(key); Add(M.PointEvictions); }
        while (_points.Count > _options.MaximumPoints)
        { _points.Remove(_points.MinBy(x => x.Value.At).Key); Add(M.PointEvictions); }
        Volatile.Write(ref _pointCount, _points.Count);
        CortexShadowRank[] selected = []; CortexKnowledgeTraversalCoordinate[] reachedCoords = [];
        var lookups = 0; var status = "collected";
        if (work.Intent is not null && _options.EvaluationEnabled)
        {
            var available = _points.Values.Where(x => Current(x.Projection) && work.Namespaces.Contains(x.Projection.Identity.Namespace))
                .Select(x => x.Projection.Point!).ToArray();
            var seedCoordinates = work.Ordinary.Select(r => r.Coordinate).ToHashSet();
            var seeds = available.Where(x => seedCoordinates.Contains(new(x.Namespace, "knowledge", x.RecordId, x.Revision)))
                .Select(x => x with { Direct = true, LexicalScore = 1 }).Take(4).ToArray();
            var traversal = await CortexKnowledgeGeometry.TraverseAsync(work.Intent, work.Namespaces, seeds,
                (request, ct) =>
                {
                    ct.ThrowIfCancellationRequested(); lookups++;
                    IReadOnlyList<CortexKnowledgeGeometryPoint> neighbors = CortexKnowledgeGeometry.Rank(
                        string.Join(' ', request.Terms), available.Where(x => x.Namespace == request.Parent.Namespace).ToArray(), Math.Min(request.Limit, 32))
                        .Where(x => x.IntentSimilarity > 0).Select(x => x.Point with { Direct = false }).ToArray();
                    return ValueTask.FromResult(neighbors);
                }, ShadowPolicies.Traversal(_options),
                token).ConfigureAwait(false);
            var selection = CortexKnowledgeGeometry.SelectEndpoints(work.Intent, traversal,
                options: ShadowPolicies.Selection(_options),
                cancellationToken: token);
            selected = selection.Selected.Select((x, i) => new CortexShadowRank(x.Node.Coordinate, i + 1)).ToArray();
            reachedCoords = traversal.Admitted.Select(x => x.Coordinate).ToArray();
            status = traversal.Truncated || selection.SelectionWasTruncated ? "evaluation-partial" : "evaluated";
        }
        if (epoch != Interlocked.Read(ref _revisionEpoch))
        { selected = []; reachedCoords = []; status = "state-changed-during-evaluation"; }
        var observation = new CortexShadowObservation("cortex.shadow.observation/1", work.Id,
            _profile.Generation, work.At, work.Operation, work.ProviderTicks, work.ObserverTicks,
            work.SqlEvents, work.Connections, work.TraceFailures, refs, work.Ordinary, selected,
            reachedCoords, work.Returned, work.Ordinary.Length,
            selected.Count(x => work.Ordinary.Any(o => o.Coordinate == x.Coordinate)), lookups,
            watch.ElapsedTicks, status) { ObservedRevisionEpoch = epoch, EvaluatedAtUtc = evaluatedAt };
        await _journal!.WriteAsync(observation, work.At, token).ConfigureAwait(false);
        _observations[work.Id] = observation;
        while (_observations.Count > _options.JournalSlots)
        { var id = _observations.MinBy(x => x.Value.ObservedAtUtc).Key; _observations.Remove(id); _labeled.Remove(id); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue?.Writer.TryComplete(); _stop.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
