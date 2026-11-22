using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class WindowsNetworkResolutionTests
{
    private const string Sid = "S-1-5-21-10-20-30-1001";
    private static int _checks;
    internal static async Task RunAsync()
    {
        _checks = 0;
        Contract();
        await ProviderAsync();
        await NativeAsync();
        Console.WriteLine("PASS DNS resolution: " + _checks +
            " pure/injected parser, owner, two-slot/deadline/disposal and native ABI/callback/cancel/record cleanup checks; no DNS/network or native DNS API execution");
    }
    internal static WindowsNetworkResolutionOperations CreateEngineFixtureOperations(string ownerSid, Func<LocalPeer> current) =>
        new(ownerSid, current, new FakeResolver());
    internal static EngineFixture CreateEngineFixture(string ownerSid, Func<LocalPeer> current) => new(ownerSid, current);

    private static WindowsNetworkResolveInvocation Invocation(string type = "A", int timeout = 5000) =>
        WindowsNetworkResolutionContract.Parse(JsonSerializer.Serialize(new { version = 1, name = "example.test", recordType = type, timeoutMs = timeout }));
    private static LocalPeer Owner() => new(Sid, 7, @"C:\fixture\PegasusCortex.Host.exe", Elevated: false);
    private static void Contract()
    {
        var expected = Invocation();
        Check(expected.Name == "example.test." && expected.RecordType == "A" && expected.TimeoutMilliseconds == 5000 &&
            !expected.IsMutation && expected.IsActiveProbe, "absolute canonical A request");
        Check(WindowsNetworkResolutionContract.Parse("{\"version\":1,\"name\":\"EXAMPLE.TEST.\",\"recordType\":\"A\"}").RequestIdentity ==
            expected.RequestIdentity, "canonical identity/default timeout independent of JSON order");
        Check(Invocation("AAAA", 30000).RecordType == "AAAA" && Invocation("A", 100).TimeoutMilliseconds == 100, "closed type/timeout boundaries");
        Check(!expected.ToString().Contains("example", StringComparison.Ordinal), "diagnostic representation omits queried name");
        var valid = new OperationRequest("windows_network", new Dictionary<string, string> { ["op"] = "resolve" },
            "dns-fixture", Body: "{\"version\":1,\"name\":\"example.test\",\"recordType\":\"A\"}");
        Check(WindowsNetworkResolutionContract.IsRequest(valid) && WindowsNetworkResolutionContract.Parse(valid).Name == expected.Name,
            "exact active operation envelope");
        foreach (var invalid in new[]
        {
            valid with { Root = "ABCD" }, valid with { Ws = "ambient" }, valid with { Cwd = @"C:\fixture" },
            valid with { Body = null }, valid with { Op = "run" },
            valid with { Arg = new Dictionary<string, string> { ["op"] = "resolve", ["wait"] = "false" } },
            valid with { Arg = new Dictionary<string, string> { ["op"] = "resolve", ["elevate"] = "uac" } },
            valid with { Arg = new Dictionary<string, string> { ["op"] = "resolve", ["rid"] = "different" } }
        }) Refused(() => WindowsNetworkResolutionContract.Parse(invalid));
        foreach (var name in new[] { "", ".", "1.2.3.4", "2001:db8::1", "https://example.test", @"a\b", "*.example.test",
            "a..test", "-a.test", "a-.test", "a_b.test", "é.test", "a\u0000.test", new string('a', 64) + ".test" })
            Refused(() => WindowsNetworkResolutionContract.Parse(JsonSerializer.Serialize(new { version = 1, name, recordType = "A" })));
        foreach (var body in new[]
        {
            "null", "[]", "{\"version\":\"1\",\"name\":\"a.test\",\"recordType\":\"A\"}",
            "{\"version\":1,\"name\":\"a.test\",\"recordType\":\"ANY\"}",
            "{\"version\":1,\"name\":\"a.test\",\"recordType\":\"A\",\"timeoutMs\":\"100\"}",
            "{\"version\":1,\"name\":\"a.test\",\"recordType\":\"A\",\"timeoutMs\":99}",
            "{\"version\":1,\"name\":\"a.test\",\"recordType\":\"A\",\"timeoutMs\":30001}",
            "{\"version\":1,\"name\":\"a.test\",\"recordType\":\"A\",\"server\":\"192.0.2.1\"}",
            "{\"version\":1,\"name\":\"a.test\",\"name\":\"b.test\",\"recordType\":\"A\"}"
        }) Refused(() => WindowsNetworkResolutionContract.Parse(body));
        Refused(() => WindowsNetworkResolutionContract.Parse(new string(' ', 2049)));
        // Existing inventory remains separate: neither branch pretends resolve is a passive view.
        Check(WindowsNetworkOperationContract.Actions.SequenceEqual(new[] { "list", "read" }) &&
            !WindowsNetworkOperationContract.IsKnownAction("resolve"), "inventory actions remain unchanged");
    }

    private static async Task ProviderAsync()
    {
        var peer = Owner();
        var native = new FakeResolver();
        await using (var operations = new WindowsNetworkResolutionOperations(Sid, () => peer, native))
        {
            var result = await operations.ExecuteAsync(Invocation());
            Check(result.Ok && result.Answers.Count == 1 && native.Calls == 1 && result.QueryIssued &&
                !result.CancellationRequested && !result.DeadlineElapsed, "actual typed invocation reaches injected resolver once");
            Check(!result.Coverage.NativePreallocationCapped && result.Coverage.MaximumConcurrentQueries == 2 &&
                result.Coverage.Limitations.Contains("packet_count_and_server_identity_not_observed"), "honest resolver coverage");
            native.Result = Success() with { Answers = new[] { new WindowsNetworkDnsAnswer("ipv4", null!, 1) } };
            Check((await operations.ExecuteAsync(Invocation())).Code == "dns_invalid_native_data", "null address refuses bounded");
            native.Result = Success() with { Answers = new[] { new WindowsNetworkDnsAnswer("ipv6", "2001:db8::1", 1) } };
            Check(!(await operations.ExecuteAsync(Invocation())).Ok, "wrong requested family refuses");
            native.Result = Success() with { Code = "untrusted_provider_value" };
            Check((await operations.ExecuteAsync(Invocation())).Code == "dns_unavailable", "unrecognized provider code not echoed");
            native.Result = Success() with { Answers = Enumerable.Repeat(new WindowsNetworkDnsAnswer("ipv4", "192.0.2.1", uint.MaxValue), 65).ToArray() };
            Check(!(await operations.ExecuteAsync(Invocation())).Ok, "too many answers refuse without clipping");
            native.Result = Success() with { Answers = Enumerable.Repeat(new WindowsNetworkDnsAnswer("ipv4", "192.0.2.1", uint.MaxValue), 64).ToArray() };
            result = await operations.ExecuteAsync(Invocation());
            Check(result.Ok && JsonSerializer.SerializeToUtf8Bytes(result).Length <= WindowsNetworkResolutionOperations.MaximumOutputBytes,
                "worst bounded answer set stays within whole output budget");
            native.Result = Success();
            native.After = () => peer = peer with { Session = 8 };
            result = await operations.ExecuteAsync(Invocation());
            Check(!result.Ok && result.Code == "owner_denied" && result.Name is null && result.RecordType is null &&
                result.Answers.Count == 0 && result.QueryIssued, "post-resolution owner loss discards payload, preserves entry evidence");
            var before = native.Calls;
            Check((await operations.ExecuteAsync(Invocation())).Code == "owner_denied" && native.Calls == before,
                "pre-entry foreign owner never resolves");
            peer = Owner(); native.After = null;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await CanceledAsync(() => operations.ExecuteAsync(Invocation(), canceled.Token));
            Check(native.Calls == before, "pre-entry canceled call issues no query");
        }

        using (var fixture = new EngineFixture(Sid, Owner))
        {
            using var cancel = new CancellationTokenSource();
            var first = fixture.Operations.ExecuteAsync(Invocation(), cancel.Token);
            var second = fixture.Operations.ExecuteAsync(Invocation());
            try
            {
                await fixture.Native.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var busy = await fixture.Operations.ExecuteAsync(Invocation());
                Check(!busy.Ok && busy.Code == "dns_busy" && !busy.QueryIssued && fixture.Native.Calls == 2,
                    "two occupied slots refuse new query without a waiting queue");
                cancel.Cancel();
                await fixture.Native.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!first.IsCompleted && (await fixture.Operations.ExecuteAsync(Invocation())).Code == "dns_busy",
                    "observer/native cancellation cannot release actual held query slot");
                fixture.Native.Release();
                var results = await Task.WhenAll(first, second);
                Check(results[0].Code == "canceled" && results[0].CancellationRequested && results[0].Answers.Count == 0 &&
                    results[1].Ok, "original calls settle after release with separate cancellation outcomes");
            }
            finally { fixture.Native.Release(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        using (var fixture = new EngineFixture(Sid, Owner))
        {
            var pending = fixture.Operations.ExecuteAsync(Invocation());
            var disposal = fixture.Operations.DisposeAsync().AsTask();
            try
            {
                await fixture.Native.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!disposal.IsCompleted && !pending.IsCompleted &&
                    (await fixture.Operations.ExecuteAsync(Invocation())).Code == "dns_disposed", "disposal closes admission but joins original resolver");
                fixture.Native.Release();
                Check((await pending).Code == "dns_disposed", "owned stop preserves cancellation state");
                await disposal;
                await fixture.Operations.DisposeAsync();
                Check(true, "repeated disposal joins same completion");
            }
            finally { fixture.Native.Release(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); await disposal.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        var clock = new ManualClock();
        var deadlineNative = new FakeResolver { Hold = true };
        await using (var operations = new WindowsNetworkResolutionOperations(Sid, Owner, deadlineNative, clock))
        {
            var pending = operations.ExecuteAsync(Invocation(timeout: 100));
            try
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await deadlineNative.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!pending.IsCompleted, "deadline is cancellation request, not native return bound");
                deadlineNative.Release();
                var result = await pending;
                Check(!result.Ok && result.Code == "dns_timeout" && result.QueryIssued && result.CancellationRequested &&
                    result.DeadlineElapsed && result.Answers.Count == 0, "deadline late-result discard after original settled");
            }
            finally { deadlineNative.Release(); await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }

    private static async Task NativeAsync()
    {
        Check(WindowsNetworkDnsNative.Pending == 9506 && WindowsNetworkDnsNative.QueryOptions == 0x19e8 &&
            Marshal.SizeOf<WindowsNetworkDnsNative.Request>() == 64 && Marshal.SizeOf<WindowsNetworkDnsNative.Result>() == 32,
            "SDK status/options/x64 ABI");
        foreach (var (name, offset) in new[] { ("Version", 0), ("Name", 8), ("Type", 16), ("Options", 24),
            ("Servers", 32), ("InterfaceIndex", 40), ("Callback", 48), ("Context", 56) })
            Check(Marshal.OffsetOf<WindowsNetworkDnsNative.Request>(name).ToInt32() == offset, "request field offset " + name);
        foreach (var (name, offset) in new[] { ("Version", 0), ("Status", 4), ("Options", 8), ("Records", 16), ("Reserved", 24) })
            Check(Marshal.OffsetOf<WindowsNetworkDnsNative.Result>(name).ToInt32() == offset, "result field offset " + name);
        await using (var fixture = new NativeFixture())
        {
            var result = await fixture.Start(Invocation());
            Check(result.Ok && result.Answers[0].Address == "192.0.2.1" && fixture.Calls.Frees == 1 &&
                fixture.Calls.Queries == 1 && fixture.Calls.Cancels == 0, "synchronous resolver/free once");
            Check(fixture.Calls.Captured.Options == 0x19e8 && fixture.Calls.Captured.InterfaceIndex == 0 &&
                fixture.Calls.Captured.Servers == 0 && fixture.Calls.Name == "example.test." &&
                fixture.Calls.CancelAligned, "fixed query/provider/interface and aligned opaque cancellation memory");
        }
        await using (var fixture = new NativeFixture())
        {
            fixture.Calls.Status = 997;
            var result = await fixture.Start(Invocation());
            Check(!result.Ok && result.NativeError == 997 && fixture.Calls.Cancels == 0 && fixture.Calls.Frees == 1,
                "997 is immediate error, never DNS pending sentinel");
        }
        foreach (var mode in new[] { "pending", "aaaa", "bad-length", "cycle", "too-many", "other-type", "native-error" })
        {
            await using var fixture = new NativeFixture();
            fixture.Calls.Status = WindowsNetworkDnsNative.Pending;
            fixture.Calls.Mode = mode;
            var task = fixture.Start(Invocation(mode == "aaaa" ? "AAAA" : "A"));
            Check(!task.IsCompleted && fixture.Calls.Frees == 0, "pending ownership retained " + mode);
            fixture.Calls.Complete(mode == "native-error" ? 9003 : 0);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Calls.Frees == 1 && !fixture.Calls.FreedInsideQuery && !fixture.Calls.FreedInsideCancel,
                "entire original list freed after operation " + mode);
            Check(mode switch
            {
                "pending" or "aaaa" => result.Ok && result.Answers.Count == 1,
                "too-many" => !result.Ok && result.Code == "dns_record_limit" && result.Answers.Count == 0,
                "other-type" => !result.Ok && result.Code == "dns_no_records",
                "native-error" => !result.Ok && result.NativeError == 9003,
                _ => !result.Ok && result.Code == "dns_invalid_native_data" && result.Answers.Count == 0
            }, "bounded typed record/error projection " + mode);
        }
        await using (var fixture = new NativeFixture())
        {
            using var cancel = new CancellationTokenSource();
            fixture.Calls.Status = WindowsNetworkDnsNative.Pending;
            fixture.Calls.Inline = true;
            fixture.Calls.AfterInline = cancel.Cancel;
            var result = await fixture.Start(Invocation(), cancel.Token);
            Check(result.Ok && !result.CancellationRequested && fixture.Calls.Cancels == 0 && fixture.Calls.Frees == 1,
                "inline callback before API return wins later cancellation");
        }
        await using (var fixture = new NativeFixture())
        {
            using var cancel = new CancellationTokenSource();
            fixture.Calls.Status = WindowsNetworkDnsNative.Pending;
            fixture.Calls.BeforeReturn = cancel.Cancel;
            fixture.Calls.CompleteInCancel = true;
            var result = await fixture.Start(Invocation(), cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!result.Ok && result.CancellationRequested && fixture.Calls.Cancels == 1 &&
                fixture.Calls.Frees == 1 && !fixture.Calls.FreedInsideCancel, "pre-pending cancellation plus reentrant callback joins native cancel");
        }
        await using (var fixture = new NativeFixture())
        {
            using var cancel = new CancellationTokenSource();
            fixture.Calls.Status = WindowsNetworkDnsNative.Pending;
            fixture.Calls.BlockCancel = true;
            var task = fixture.Start(Invocation(), cancel.Token);
            cancel.Cancel();
            await fixture.Calls.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!task.IsCompleted && fixture.Calls.Frees == 0, "entered native cancellation remains owned");
            var callback = Task.Run(() => fixture.Calls.Complete(995));
            try
            {
                Check(!task.IsCompleted && fixture.Calls.Frees == 0, "callback cannot release live cancel storage");
                fixture.Calls.CancelRelease.Set();
                await callback.WaitAsync(TimeSpan.FromSeconds(5));
                var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(result.CancellationRequested && fixture.Calls.Cancels == 1 && fixture.Calls.Frees == 1 &&
                    !fixture.Calls.FreedInsideCancel, "both callback and original cancel return precede cleanup");
            }
            finally { fixture.Calls.CancelRelease.Set(); await callback.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        await using (var fixture = new NativeFixture())
        {
            using var cancel = new CancellationTokenSource();
            fixture.Calls.Status = WindowsNetworkDnsNative.Pending; fixture.Calls.CancelStatus = 12345;
            var task = fixture.Start(Invocation(), cancel.Token); cancel.Cancel();
            await fixture.Calls.CancelReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Calls.Complete(995);
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.CancellationRequested && result.CancelNativeError == 12345 && result.NativeError == 995 &&
                fixture.Calls.Cancels == 1 && fixture.Calls.Frees == 1, "actual query/cancel status domains retained without inferred codes");
        }
        await using (var fixture = new NativeFixture())
        {
            fixture.Calls.ThrowFree = true;
            Check((await fixture.Start(Invocation())).Code == "dns_cleanup_failed", "cleanup failure is not a success");
            Check(!(await fixture.Native.ResolveAsync(Invocation(), default)).QueryIssued && fixture.Calls.Queries == 1,
                "cleanup failure poisons resolver to prevent repeated leaking entries");
        }
    }

    private static WindowsNetworkDnsNativeResult Success() =>
        new(true, "observed", new[] { new WindowsNetworkDnsAnswer("ipv4", "192.0.2.1", 60) }, true, false);
    private static void Refused(Action action)
    {
        try { action(); throw new Exception("Expected DNS contract refusal."); }
        catch (WindowsNetworkResolutionContractException) { _checks++; }
    }
    private static async Task CanceledAsync(Func<Task<WindowsNetworkResolutionOutcome>> action)
    {
        try { await action(); throw new Exception("Expected cancellation before DNS entry."); }
        catch (OperationCanceledException) { _checks++; }
    }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException("DNS fixture: " + message); }

    internal sealed class EngineFixture : IDisposable
    {
        internal FakeResolver Native { get; } = new() { Hold = true };
        internal WindowsNetworkResolutionOperations Operations { get; }
        internal int Calls => Native.Calls;
        internal Task Entered => Native.Entered.Task;
        internal void Release() => Native.Release();
        internal EngineFixture(string ownerSid, Func<LocalPeer> current) => Operations = new(ownerSid, current, Native);
        public void Dispose() { Native.Release(); Operations.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    internal sealed class FakeResolver : IWindowsNetworkDnsNative
    {
        internal int Calls;
        internal bool Hold;
        internal Action? After;
        internal WindowsNetworkDnsNativeResult Result = Success();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public async Task<WindowsNetworkDnsNativeResult> ResolveAsync(WindowsNetworkResolveInvocation invocation, CancellationToken token)
        {
            var count = Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (count == 2) TwoEntered.TrySetResult();
            using var registration = token.Register(() => CancelObserved.TrySetResult());
            if (Hold) await _release.Task;
            After?.Invoke();
            return token.IsCancellationRequested ? Result with { CancellationRequested = true } : Result;
        }
    }
    private sealed class NativeFixture : IAsyncDisposable
    {
        internal NativeCalls Calls { get; } = new();
        internal WindowsNetworkDnsNative Native { get; }
        private Task<WindowsNetworkDnsNativeResult>? _operation;
        internal NativeFixture() => Native = new(Calls);
        internal Task<WindowsNetworkDnsNativeResult> Start(WindowsNetworkResolveInvocation invocation, CancellationToken token = default) =>
            _operation = Native.ResolveAsync(invocation, token);
        public async ValueTask DisposeAsync()
        {
            Calls.CancelRelease.Set();
            if (_operation is { IsCompleted: false }) Calls.Complete(995);
            try { if (_operation is not null) await _operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Calls.Dispose(); }
        }
    }
    private sealed class NativeCalls : IWindowsDnsNativeCalls, IDisposable
    {
        internal int Status, CancelStatus, Queries, Cancels, Frees;
        internal string Mode = "pending";
        internal bool Inline, CompleteInCancel, BlockCancel, ThrowFree, CancelAligned, FreedInsideQuery, FreedInsideCancel;
        internal Action? BeforeReturn, AfterInline;
        internal string? Name;
        internal WindowsNetworkDnsNative.Request Captured;
        internal TaskCompletionSource CancelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim CancelRelease { get; } = new();
        private nint _result, _records;
        private bool _queryActive, _cancelActive, _completed;
        private readonly List<nint> _owned = [];
        public int Query(nint request, nint result, nint cancel)
        {
            Queries++; _queryActive = true; _result = result;
            try
            {
                Captured = Marshal.PtrToStructure<WindowsNetworkDnsNative.Request>(request);
                Name = Marshal.PtrToStringUni(Captured.Name);
                CancelAligned = (long)cancel % 8 == 0;
                Build();
                Marshal.StructureToPtr(new WindowsNetworkDnsNative.Result { Version = 1, Status = Status, Records = Status == WindowsNetworkDnsNative.Pending ? 0 : _records }, result, false);
                if (Inline) { Complete(0); AfterInline?.Invoke(); }
                BeforeReturn?.Invoke(); return Status;
            }
            finally { _queryActive = false; }
        }
        internal void Complete(int status)
        {
            if (_completed || _result == 0) return;
            _completed = true;
            Marshal.StructureToPtr(new WindowsNetworkDnsNative.Result { Version = 1, Status = status, Records = _records }, _result, false);
            var callback = Marshal.GetDelegateForFunctionPointer<WindowsNetworkDnsNative.Completion>(Captured.Callback);
            callback(Captured.Context, _result);
        }
        public int Cancel(nint cancel)
        {
            Cancels++; _cancelActive = true; CancelEntered.TrySetResult();
            try
            {
                if (BlockCancel && !CancelRelease.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("fixture_cancel_release_missing");
                if (CompleteInCancel) Complete(995);
                return CancelStatus;
            }
            finally { _cancelActive = false; CancelReturned.TrySetResult(); }
        }
        public void Free(nint records)
        {
            Frees++; FreedInsideQuery |= _queryActive; FreedInsideCancel |= _cancelActive;
            if (records != _records) throw new InvalidOperationException("fixture_wrong_record_owner");
            if (ThrowFree) throw new InvalidOperationException("fixture_free_failed");
            FreeOwned();
        }
        private void Build()
        {
            var count = Mode == "too-many" ? 65 : 1;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.AllocHGlobal(48); _owned.Add(item);
                for (var offset = 0; offset < 48; offset++) Marshal.WriteByte(item, offset, 0);
                if (i == 0) _records = item;
                else Marshal.WriteIntPtr(_owned[i - 1], item);
                var type = Mode == "aaaa" ? 28 : Mode == "other-type" ? 5 : 1;
                var data = type == 28 ? IPAddress.Parse("2001:db8::1").GetAddressBytes() : IPAddress.Parse("192.0.2.1").GetAddressBytes();
                Marshal.WriteInt16(item, 16, (short)type); Marshal.WriteInt16(item, 18, (short)(Mode == "bad-length" ? 3 : data.Length));
                Marshal.WriteInt32(item, 20, 1); Marshal.WriteInt32(item, 24, 60); Marshal.Copy(data, 0, item + 32, data.Length);
            }
            if (Mode == "cycle") Marshal.WriteIntPtr(_owned[^1], _records);
        }
        private void FreeOwned() { foreach (var item in _owned) Marshal.FreeHGlobal(item); _owned.Clear(); }
        public void Dispose() { CancelRelease.Set(); FreeOwned(); CancelRelease.Dispose(); }
    }
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private readonly List<ManualTimer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
        internal void Advance(TimeSpan elapsed) { _ticks += elapsed.Ticks; foreach (var timer in _timers.ToArray()) timer.Fire(_ticks); }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue; private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (_disposed) return false; _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks; return true; }
            internal void Fire(long ticks) { if (!_disposed && ticks >= _due) { _due = long.MaxValue; callback(state); } }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
