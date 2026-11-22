using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.AgentOps.Engine;

internal interface IWindowsDnsNativeCalls
{
    int Query(nint request, nint result, nint cancel);
    int Cancel(nint cancel);
    void Free(nint records);
}

/// <summary>One DnsQueryEx invocation; no application retries or alternate resolver.</summary>
internal sealed class WindowsNetworkDnsNative : IWindowsNetworkDnsNative
{
    internal const int Pending = 9506; // DNS_REQUEST_PENDING, NOT ERROR_IO_PENDING (997).
    internal const ulong QueryOptions = 0x19e8; // Wire/cache bypass, absolute name; no hosts/local/NetBT/multicast.
    private readonly IWindowsDnsNativeCalls _calls;
    private int _cleanupFailed;
    internal WindowsNetworkDnsNative() : this(new Calls()) { }
    internal WindowsNetworkDnsNative(IWindowsDnsNativeCalls calls) => _calls = calls ?? throw new ArgumentNullException(nameof(calls));

    public async Task<WindowsNetworkDnsNativeResult> ResolveAsync(WindowsNetworkResolveInvocation invocation, CancellationToken token)
    {
        if (Volatile.Read(ref _cleanupFailed) != 0)
            return new(false, "dns_cleanup_failed", Array.Empty<WindowsNetworkDnsAnswer>(), false, false);
        if (token.IsCancellationRequested)
            return new(false, "canceled", Array.Empty<WindowsNetworkDnsAnswer>(), false, true);
        Query query;
        try { query = new Query(_calls, invocation); }
        catch (PlatformNotSupportedException)
        { return new(false, "dns_unavailable", Array.Empty<WindowsNetworkDnsAnswer>(), false, false); }
        using var owned = query;
        var result = await query.RunAsync(token).ConfigureAwait(false);
        // An unobserved cleanup failure must not permit repeated leaking queries.
        if (result.Code == "dns_cleanup_failed") Interlocked.Exchange(ref _cleanupFailed, 1);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Request
    {
        internal uint Version;
        internal nint Name;
        internal ushort Type;
        internal ulong Options;
        internal nint Servers;
        internal uint InterfaceIndex;
        internal nint Callback;
        internal nint Context;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        internal uint Version;
        internal int Status;
        internal ulong Options;
        internal nint Records;
        internal nint Reserved;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void Completion(nint context, nint result);

    private sealed class Query : IDisposable
    {
        private static readonly Completion Callback = Complete;
        private readonly IWindowsDnsNativeCalls _calls;
        private readonly WindowsNetworkResolveInvocation _invocation;
        private readonly object _gate = new();
        private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Memory? _request, _result, _cancel, _name;
        private GCHandle _context;
        private bool _issued, _entryReturned, _pending, _completed, _cancellationRequested,
            _cancelReserved, _cancelStarted, _invalid, _disposed;
        private int _callbackActive, _status;
        private int? _cancelError;
        private nint _records;

        internal Query(IWindowsDnsNativeCalls calls, WindowsNetworkResolveInvocation invocation)
        {
            _calls = calls; _invocation = invocation;
            try
            {
                // V1 structures are pointer-size sensitive. This product is x64;
                // refuse an unqualified ABI instead of guessing another layout.
                if (IntPtr.Size != 8 || Marshal.SizeOf<Request>() != 64 || Marshal.SizeOf<Result>() != 32 ||
                    Marshal.OffsetOf<Request>(nameof(Request.Options)).ToInt32() != 24 ||
                    Marshal.OffsetOf<Request>(nameof(Request.Callback)).ToInt32() != 48)
                    throw new PlatformNotSupportedException("dns_abi_unsupported");
                _request = new(64); _result = new(32); _cancel = new(32);
                _name = new(checked((invocation.Name.Length + 1) * 2));
                var characters = invocation.Name.ToCharArray();
                try { Marshal.Copy(characters, 0, _name.DangerousGetHandle(), characters.Length); }
                finally { Array.Clear(characters); }
                _context = GCHandle.Alloc(this);
                Marshal.StructureToPtr(new Request
                {
                    Version = 1, Name = _name.DangerousGetHandle(), Type = invocation.RecordType == "A" ? (ushort)1 : (ushort)28,
                    Options = QueryOptions, Servers = 0, InterfaceIndex = 0,
                    Callback = Marshal.GetFunctionPointerForDelegate(Callback), Context = GCHandle.ToIntPtr(_context)
                }, _request.DangerousGetHandle(), false);
                Marshal.StructureToPtr(new Result { Version = 1 }, _result.DangerousGetHandle(), false);
            }
            catch { Dispose(); throw; }
        }

        internal async Task<WindowsNetworkDnsNativeResult> RunAsync(CancellationToken token)
        {
            using var registration = token.Register(static state => ((Query)state!).RequestCancellation(), this);
            // This is the final cancellable admission check before the native entry.
            if (token.IsCancellationRequested)
                return new(false, "canceled", Array.Empty<WindowsNetworkDnsAnswer>(), false, true);
            int status;
            try
            {
                _issued = true;
                status = _calls.Query(_request!.DangerousGetHandle(), _result!.DangerousGetHandle(), _cancel!.DangerousGetHandle());
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
            {
                _issued = false;
                return new(false, "dns_unavailable", Array.Empty<WindowsNetworkDnsAnswer>(), false, _cancellationRequested);
            }
            lock (_gate)
            {
                _entryReturned = true; _pending = status == Pending;
                if (!_pending)
                {
                    // Documented synchronous/error branch has no callback.
                    if (_completed) _invalid = true;
                    else Capture(_result!.DangerousGetHandle(), status);
                }
                FinishIfReady();
            }
            ScheduleCancellation();
            // No timeout shortcut is safe here. The request/result/cancel/context
            // remain owned until the callback and every entered cancel call drain.
            await _settled.Task.ConfigureAwait(false);
            WindowsNetworkDnsNativeResult outcome;
            try
            {
                if (_invalid) outcome = Failure("dns_invalid_native_data");
                else if (_cancellationRequested) outcome = Failure("canceled");
                else if (_status != 0) outcome = Failure(_status == 9501 ? "dns_no_records" : "dns_query_failed");
                else outcome = ParseRecords();
            }
            catch (Exception error) when (error is ArgumentException or OverflowException or InvalidOperationException)
            { outcome = Failure("dns_invalid_native_data"); }
            // This is the original full list, even if parsing stopped at a bound,
            // malformed record or cycle. Never free only the projected prefix.
            var records = _records; _records = 0;
            if (records != 0)
            {
                try { _calls.Free(records); }
                catch { outcome = Failure("dns_cleanup_failed"); }
            }
            return outcome;
        }

        private WindowsNetworkDnsNativeResult ParseRecords()
        {
            var answers = new List<WindowsNetworkDnsAnswer>();
            var seen = new HashSet<nint>();
            var pointer = _records;
            var expectedType = _invocation.RecordType == "A" ? 1 : 28;
            while (pointer != 0)
            {
                if (seen.Count == WindowsNetworkResolutionOperations.MaximumRecords)
                    return Failure("dns_record_limit");
                if (!seen.Add(pointer) || (long)pointer % 8 != 0)
                    return Failure("dns_invalid_native_data");
                // Fixed common DNS_RECORDW prefix from the SDK. Unknown/CNAME
                // data are never interpreted; the OS resolver owns that behavior.
                var next = Marshal.ReadIntPtr(pointer);
                var type = unchecked((ushort)Marshal.ReadInt16(pointer, 16));
                var length = unchecked((ushort)Marshal.ReadInt16(pointer, 18));
                var flags = unchecked((uint)Marshal.ReadInt32(pointer, 20));
                var ttl = unchecked((uint)Marshal.ReadInt32(pointer, 24));
                if ((flags & 3) == 1 && type == expectedType)
                {
                    var bytesRequired = type == 1 ? 4 : 16;
                    if (length != bytesRequired) return Failure("dns_invalid_native_data");
                    var bytes = new byte[bytesRequired];
                    Marshal.Copy(pointer + 32, bytes, 0, bytes.Length);
                    answers.Add(new(type == 1 ? "ipv4" : "ipv6", new IPAddress(bytes).ToString(), ttl));
                }
                pointer = next;
            }
            if (answers.Count == 0) return Failure("dns_no_records");
            return new(true, "observed", Array.AsReadOnly(answers.ToArray()), _issued, _cancellationRequested,
                _status == 0 ? null : _status, _cancelError);
        }

        private void RequestCancellation()
        {
            lock (_gate)
            {
                // A result that already won is not overwritten by later cancellation.
                if (_completed) return;
                _cancellationRequested = true;
            }
            ScheduleCancellation();
        }
        private void ScheduleCancellation()
        {
            lock (_gate)
            {
                if (!_pending || !_entryReturned || _completed || !_cancellationRequested || _cancelStarted) return;
                _cancelStarted = true; _cancelReserved = true; // Publish ownership BEFORE queuing the worker.
            }
            _ = Task.Run(Cancel); // Bounded to one per owned query; _settled joins it.
        }
        private void Cancel()
        {
            try
            {
                lock (_gate)
                {
                    // Completion may win while this worker is queued. Once the
                    // callback ran, a reserved worker alone cannot use the handle.
                    if (_completed) return;
                    // Keep completion admission serialized through native entry.
                    // DnsCancelQuery does not wait for query completion; a same-thread
                    // callback may reenter this Monitor, and another callback is
                    // retained until this exact cancellation invocation returns.
                    var status = _calls.Cancel(_cancel!.DangerousGetHandle());
                    if (status != 0) _cancelError = status;
                }
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
            {
                // No invented numeric status. Still join the eventual original
                // callback; failure to request cancellation never frees live state.
            }
            finally
            {
                lock (_gate) { _cancelReserved = false; FinishIfReady(); }
            }
        }
        private void OnCompletion(nint result)
        {
            lock (_gate)
            {
                _callbackActive++;
                try
                {
                    if (_completed) { _invalid = true; return; }
                    Capture(result, null);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
                { _invalid = true; _completed = true; }
                finally { _callbackActive--; FinishIfReady(); }
            }
        }
        private void Capture(nint result, int? synchronousStatus)
        {
            if (result != _result!.DangerousGetHandle()) _invalid = true;
            // Read only our own result allocation even for an invalid callback pointer.
            var value = Marshal.PtrToStructure<Result>(_result.DangerousGetHandle());
            if (value.Version != 1) _invalid = true;
            _records = value.Records;
            _status = synchronousStatus ?? value.Status;
            if (_status == Pending) _invalid = true;
            _completed = true;
        }
        private void FinishIfReady()
        {
            if (_entryReturned && _completed && _callbackActive == 0 && !_cancelReserved) _settled.TrySetResult();
        }
        private WindowsNetworkDnsNativeResult Failure(string code) =>
            new(false, code, Array.Empty<WindowsNetworkDnsAnswer>(), _issued, _cancellationRequested,
                _status == 0 ? null : _status, _cancelError);
        private static void Complete(nint context, nint result)
        {
            try { (GCHandle.FromIntPtr(context).Target as Query)?.OnCompletion(result); }
            catch { /* Never let a managed exception cross the fixed native callback ABI. */ }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_context.IsAllocated) _context.Free();
            _name?.Dispose(); _cancel?.Dispose(); _result?.Dispose(); _request?.Dispose();
        }
    }

    private sealed class Memory : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly int _length;
        internal Memory(int length) : base(true)
        {
            _length = length; SetHandle(Marshal.AllocHGlobal(length));
            for (var index = 0; index < length; index++) Marshal.WriteByte(handle, index, 0);
        }
        protected override bool ReleaseHandle()
        {
            try
            {
                for (var index = 0; index < _length; index++) Marshal.WriteByte(handle, index, 0);
                Marshal.FreeHGlobal(handle); return true;
            }
            catch { return false; }
        }
    }
    private sealed class Calls : IWindowsDnsNativeCalls
    {
        public int Query(nint request, nint result, nint cancel) => DnsQueryEx(request, result, cancel);
        public int Cancel(nint cancel) => DnsCancelQuery(cancel);
        public void Free(nint records) => DnsFree(records, 1);
        [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int DnsQueryEx(nint request, nint result, nint cancel);
        [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int DnsCancelQuery(nint cancel);
        // WinDNS.h defines DnsRecordListFree as DnsFree(records,DnsFreeRecordList).
        [DllImport("dnsapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void DnsFree(nint records, int freeType);
    }
}
