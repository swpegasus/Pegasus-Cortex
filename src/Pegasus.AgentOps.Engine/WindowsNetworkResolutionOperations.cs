using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

internal sealed record WindowsNetworkDnsAnswer(string Family, string Address, uint TtlSeconds);
internal sealed record WindowsNetworkResolutionCoverage(string Scope, bool NetworkRequested, bool CustomServer,
    bool NativePreallocationCapped, int MaximumRecords, int MaximumConcurrentQueries, IReadOnlyList<string> Limitations);
internal sealed record WindowsNetworkResolutionOutcome(bool Ok, string Code, string? Name, string? RecordType,
    IReadOnlyList<WindowsNetworkDnsAnswer> Answers, bool QueryIssued, bool CancellationRequested, bool DeadlineElapsed,
    DateTimeOffset ObservedUtc, long ElapsedMilliseconds, WindowsNetworkResolutionCoverage Coverage,
    int? NativeError = null, int? CancelNativeError = null);
internal sealed record WindowsNetworkDnsNativeResult(bool Ok, string Code, IReadOnlyList<WindowsNetworkDnsAnswer> Answers,
    bool QueryIssued, bool CancellationRequested, int? NativeError = null, int? CancelNativeError = null);
internal interface IWindowsNetworkDnsNative
{
    // Completion owns the entire native operation, including entered cancellation and cleanup.
    Task<WindowsNetworkDnsNativeResult> ResolveAsync(WindowsNetworkResolveInvocation invocation, CancellationToken token);
}

/// <summary>Ordinary-owner active resolution with no waiting admission queue or abandoned native work.</summary>
internal sealed class WindowsNetworkResolutionOperations : IAsyncDisposable
{
    internal const int MaximumConcurrentQueries = 2, MaximumRecords = 64, MaximumOutputBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly OwnerHostIdentity _owner;
    private readonly IWindowsNetworkDnsNative _native;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _active;
    private bool _stopping;
    private TaskCompletionSource? _idle, _disposed;

    [SupportedOSPlatform("windows")]
    internal WindowsNetworkResolutionOperations(string ownerSid)
        : this(ownerSid, LocalIdentity.Current, new WindowsNetworkDnsNative()) { }
    internal WindowsNetworkResolutionOperations(string ownerSid, Func<LocalPeer> current,
        IWindowsNetworkDnsNative native, TimeProvider? clock = null)
    { _owner = new(ownerSid, current); _native = native ?? throw new ArgumentNullException(nameof(native)); _clock = clock ?? TimeProvider.System; }
    internal bool IsCurrentOwner() => _owner.IsCurrentOwner();

    internal async Task<WindowsNetworkResolutionOutcome> ExecuteAsync(WindowsNetworkResolveInvocation invocation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner()) return Failure(invocation, "owner_denied", reveal: false);
        lock (_gate)
        {
            if (_stopping) return Failure(invocation, "dns_disposed");
            if (_active == MaximumConcurrentQueries) return Failure(invocation, "dns_busy");
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(invocation.TimeoutMilliseconds), _clock);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, _shutdown.Token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentOwner()) return Failure(invocation, "owner_denied", reveal: false);
            var start = _clock.GetTimestamp();
            // Never use WaitAsync or detach this task: cancellation requests native
            // cancellation but retains the slot until the original returns and frees.
            var result = await _native.ResolveAsync(invocation, cancel.Token).ConfigureAwait(false);
            var elapsed = Math.Max(0, (long)_clock.GetElapsedTime(start).TotalMilliseconds);
            var expired = result.CancellationRequested && deadline.IsCancellationRequested;
            if (!IsCurrentOwner()) return From("owner_denied", false, Array.Empty<WindowsNetworkDnsAnswer>(), reveal: false);
            if (result.CancellationRequested)
                return From(_shutdown.IsCancellationRequested ? "dns_disposed" : token.IsCancellationRequested ? "canceled" :
                    expired ? "dns_timeout" : "canceled", false, Array.Empty<WindowsNetworkDnsAnswer>());
            if (!SafeNativeCode(result.Code)) return From("dns_unavailable", false, Array.Empty<WindowsNetworkDnsAnswer>());
            if (!result.Ok) return From(result.Code == "observed" ? "dns_invalid_native_data" : result.Code,
                false, Array.Empty<WindowsNetworkDnsAnswer>());
            if (result.Code != "observed" || !result.QueryIssued || result.Answers is null ||
                result.Answers.Count is < 1 or > MaximumRecords ||
                result.Answers.Any(answer => !ValidAnswer(invocation.RecordType, answer)))
                return From("dns_invalid_native_data", false, Array.Empty<WindowsNetworkDnsAnswer>());
            var outcome = From("observed", true, Array.AsReadOnly(result.Answers.ToArray()));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(outcome, Json);
            try { if (bytes.Length > MaximumOutputBytes) return From("dns_output_limit", false, Array.Empty<WindowsNetworkDnsAnswer>()); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            if (!IsCurrentOwner()) return From("owner_denied", false, Array.Empty<WindowsNetworkDnsAnswer>(), reveal: false);
            return outcome;

            WindowsNetworkResolutionOutcome From(string code, bool ok, IReadOnlyList<WindowsNetworkDnsAnswer> answers, bool reveal = true) =>
                new(ok, code, reveal ? invocation.Name : null, reveal ? invocation.RecordType : null, answers,
                    result.QueryIssued, result.CancellationRequested, expired, _clock.GetUtcNow(), elapsed, Coverage(),
                    result.NativeError, result.CancelNativeError);
        }
        finally
        {
            lock (_gate) if (--_active == 0) _idle!.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Task idle;
        lock (_gate)
        {
            if (_disposed is not null) return new(_disposed.Task);
            _stopping = true; completion = _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _idle?.Task ?? Task.CompletedTask;
        }
        _ = FinishDisposeAsync(idle, completion);
        return new(completion.Task);
    }
    private async Task FinishDisposeAsync(Task idle, TaskCompletionSource completion)
    {
        var failed = false;
        try { await _shutdown.CancelAsync().ConfigureAwait(false); } catch { failed = true; }
        try { await idle.ConfigureAwait(false); } catch { failed = true; }
        _shutdown.Dispose();
        if (failed) completion.TrySetException(new InvalidOperationException("dns_cleanup_failed"));
        else completion.TrySetResult();
    }

    private WindowsNetworkResolutionOutcome Failure(WindowsNetworkResolveInvocation invocation, string code, bool reveal = true) =>
        new(false, code, reveal ? invocation.Name : null, reveal ? invocation.RecordType : null,
            Array.Empty<WindowsNetworkDnsAnswer>(), false, false, false, _clock.GetUtcNow(), 0, Coverage());
    private static bool ValidAnswer(string type, WindowsNetworkDnsAnswer? answer) =>
        answer is not null && answer.Address is { Length: > 0 and <= 45 } && IPAddress.TryParse(answer.Address, out var address) &&
        (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId == 0) &&
        (type == "A" ? answer.Family == "ipv4" && address.AddressFamily == AddressFamily.InterNetwork :
            answer.Family == "ipv6" && address.AddressFamily == AddressFamily.InterNetworkV6);
    private static bool SafeNativeCode(string code) => code is "observed" or "dns_no_records" or "dns_query_failed" or
        "dns_unavailable" or "dns_cleanup_failed" or "dns_invalid_native_data" or "dns_record_limit" or "canceled";
    internal static WindowsNetworkResolutionCoverage Coverage() => new("configured_local_dns", true, false, false,
        MaximumRecords, MaximumConcurrentQueries, Array.AsReadOnly(new[]
        {
            "one_native_api_invocation_windows_may_retry_internally",
            "no_application_retry_or_alternate_provider",
            "packet_count_and_server_identity_not_observed",
            "dns_answers_do_not_prove_reachability_or_authentication",
            "windows_allocates_records_before_count_admission",
            "deadline_requests_cancellation_native_completion_is_not_time_bounded",
            "original_callback_and_entered_cancel_call_are_joined"
        }));
}
