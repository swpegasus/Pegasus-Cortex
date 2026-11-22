using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private long _nextHistoryCompactionTicks;
    private long _historyRetentionSequence;

    internal static bool IsHistoryPruned(OperationReceipt receipt) =>
        receipt.Data?.GetValueOrDefault("history_pruned") is true or JsonElement { ValueKind: JsonValueKind.True };

    private IReadOnlyDictionary<string, object?> HistoryList(OperationRequest request)
    {
        var maximum = Int(request, "max", 100);
        if (maximum is < 1 or > 100) throw new AgentOpsException("invalid");
        if (!request.Arg.ContainsKey("state") && !request.Arg.ContainsKey("before") && !request.Arg.ContainsKey("after"))
        {
            // The established unfiltered route remains newest-first and capped
            // at 100; explicit filtered pages use oldest-first keyset navigation.
            return new Dictionary<string, object?> { ["jobs"] = _store.ListRecent(maximum) };
        }
        var before = ParseHistoryBefore(request, required: false);
        var after = request.Arg.TryGetValue("after", out var cursor) &&
            long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed : request.Arg.ContainsKey("after") ? throw new AgentOpsException("invalid") : 0;
        var page = _store.ListHistory(request.Arg.GetValueOrDefault("state"), before, after, maximum);
        return new Dictionary<string, object?>
        {
            ["jobs"] = page.Jobs, ["count"] = page.Jobs.Count, ["next"] = page.Next, ["more"] = page.More
        };
    }

    private IReadOnlyDictionary<string, object?> PruneHistory(OperationRequest request)
    {
        if (_directOnly || _userHostAdmission is null || request.Root is not null || request.Ws is not null || request.Cwd is not null ||
            request.Body is not null || request.PathRoots is { Count: > 0 } || request.Arg.ContainsKey(AuthenticatedFrozenKey))
            throw new AgentOpsException("auth");
        if (request.Arg.Keys.Any(key => key is not ("op" or "mode" or "before" or "max" or "rid")))
            throw new AgentOpsException("invalid");
        var discard = Required(request, "mode") switch
        {
            "details" => false,
            "discard_unresolved" => true,
            _ => throw new AgentOpsException("invalid")
        };
        var before = ParseHistoryBefore(request, required: true)!.Value;
        var maximum = Int(request, "max", 128);
        if (maximum is < 1 or > 256 || before > DateTimeOffset.UtcNow) throw new AgentOpsException("invalid");
        SqliteJobStore.HistoryPruneResult result;
        lock (_admissionGate)
        {
            if (_stopping) throw new AgentOpsException("host_stopping");
            if (MaintenanceClosed()) throw new AgentOpsException("maintenance_busy");
            var preserveApplications = HasLiveHistoryApplications();
            result = _store.PruneHistory(before, maximum, discard,
                (jid, rid, op) => IsHistoryCandidateActive(jid, rid) ||
                    preserveApplications && op.StartsWith("app_", StringComparison.Ordinal), DateTimeOffset.UtcNow);
        }
        return HistoryPruneData(result, discard ? "discard_unresolved" : "details", before);
    }

    private bool IsHistoryCandidateActive(string jid, string rid) =>
        _queued.ContainsKey(jid) || _jobs.ContainsKey(jid) || _completions.ContainsKey(jid) ||
        _activeEffects.Values.Any(effect => effect.Jid == jid || effect.Rid == rid) ||
        _applicationTransitions.ContainsKey(rid);

    private bool HasLiveHistoryApplications()
    {
        if (_applications is null) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try { return _applications.HasLiveApplicationsAsync(deadline.Token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is AgentOpsException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException) { return true; }
    }

    private static IReadOnlyDictionary<string, object?> HistoryPruneData(SqliteJobStore.HistoryPruneResult result,
        string mode, DateTimeOffset before) => new Dictionary<string, object?>
    {
        ["mode"] = mode, ["before"] = before.ToString("O", CultureInfo.InvariantCulture),
        ["selected"] = result.Selected, ["pruned"] = result.Pruned,
        ["skipped_active"] = result.SkippedActive, ["skipped_invalid"] = result.SkippedInvalid,
        ["next"] = result.Next, ["more"] = result.More,
        ["outcomes_unchanged"] = true, ["rid_barriers_preserved"] = true
    };

    private static DateTimeOffset? ParseHistoryBefore(OperationRequest request, bool required)
    {
        if (!request.Arg.TryGetValue("before", out var raw))
            return required ? throw new AgentOpsException("invalid") : null;
        if (raw.Length > 40 || !raw.Contains('T') || !raw.EndsWith('Z') ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var before) ||
            before.Offset != TimeSpan.Zero) throw new AgentOpsException("invalid");
        return before;
    }

    private void MaybeCompactHistory()
    {
        if (_directOnly || _userHostAdmission is null) return;
        var now = DateTimeOffset.UtcNow;
        lock (_admissionGate)
        {
            if (now.UtcTicks < _nextHistoryCompactionTicks || _stopping || _recoveryPending ||
                _userHostAdmission is { Accepting: false } || MaintenanceClosed()) return;
            _nextHistoryCompactionTicks = now.AddSeconds(30).UtcTicks;
            try
            {
                // Age/size cleanup uses the existing completion hook. Targets
                // never block submissions; active/uncertain floors may exceed
                // them. Expired completed RIDs are not reconstructed or retried.
                var preserveApplications = HasLiveHistoryApplications();
                var result = _store.RetainCompletedHistory(now, _historyRetentionSequence,
                    (jid, rid, op) => IsHistoryCandidateActive(jid, rid) ||
                        preserveApplications && op.StartsWith("app_", StringComparison.Ordinal));
                _historyRetentionSequence = result.NextSequence;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                // Retention failure must not change a just-finished operation's
                // outcome or substitute success for the unpruned history.
                try { Console.Error.WriteLine("history_retention=unavailable cause=" + error.GetType().Name); }
                catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException and not StackOverflowException) { }
            }
        }
    }
}
