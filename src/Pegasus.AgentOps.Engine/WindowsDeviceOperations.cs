using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

internal sealed record WindowsDeviceIssue(string Field, string Code, int? NativeCode = null, string? NativeDomain = null);
internal sealed record WindowsDeviceItem(string? InstanceId, Guid? ClassGuid, string? Name, string? Class,
    string? Manufacturer, string? Service, uint? StatusFlags, uint? ProblemCode, string? DriverInf,
    string? DriverVersion, DateTimeOffset? DriverDateUtc, string? DriverProvider, IReadOnlyList<WindowsDeviceIssue> Issues);
internal sealed record WindowsDeviceOutcome(bool Ok, string Code, string Action, string Scope,
    IReadOnlyList<WindowsDeviceItem> Devices, int Scanned, bool HasMore, string? NextAfterInstanceId, bool Partial,
    int? NativeError = null, string? NativeDomain = null);

internal enum WindowsDeviceField { FriendlyName, Description, Class, Manufacturer, Service, DriverInf, DriverVersion, DriverDate, DriverProvider }
internal sealed record WindowsDeviceProperty(uint Type, byte[]? Bytes, int Error = 0, bool TooLarge = false);
internal sealed record WindowsDeviceIdentity(string? InstanceId, int Error = 0);
internal sealed record WindowsDeviceStatus(uint Flags, uint Problem, uint ConfigRet = 0);
internal sealed record WindowsDeviceEntry(IWindowsDeviceNode? Node = null, bool End = false, int Error = 0);
internal interface IWindowsDeviceNode
{
    Guid ClassGuid { get; }
    WindowsDeviceIdentity Identity();
    // Returned bytes are owned by the caller and cleared after typed decoding.
    WindowsDeviceProperty Property(WindowsDeviceField field, int maximumBytes);
    WindowsDeviceStatus Status();
}
internal interface IWindowsDeviceSet : IDisposable { WindowsDeviceEntry At(uint index); }
internal interface IWindowsDeviceNative
{
    IWindowsDeviceSet OpenList(bool presentOnly);
    IWindowsDeviceSet OpenExact(string instanceId);
}
internal sealed class WindowsDeviceNativeException(int code, bool cleanup = false) : Exception("device_inventory_unavailable")
{ internal int NativeCode { get; } = code; internal bool Cleanup { get; } = cleanup; }

/// <summary>
/// Local ordinary-owner inventory only. Each request retains one exact native
/// info set; constructors never enumerate. Forward anchors seek in a fresh live
/// set, not a stable snapshot. Topology changes can cause omissions/duplicates
/// outside the observed anchor; detected changes fail explicitly. Native setup
/// set allocation is OS-owned; managed enumeration, buffers and output are bounded.
/// </summary>
internal sealed class WindowsDeviceOperations
{
    private readonly OwnerHostIdentity _owner;
    private readonly IWindowsDeviceNative _native;
    private static readonly UnicodeEncoding Unicode = new(false, false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const uint StringType = 0x12;
    private const uint HasProblem = 0x400;

    [SupportedOSPlatform("windows")]
    internal WindowsDeviceOperations(string ownerSid) : this(ownerSid, LocalIdentity.Current, new NativeWindowsDevices()) { }
    internal WindowsDeviceOperations(string ownerSid, Func<LocalPeer> current, IWindowsDeviceNative native)
    {
        _owner = new(ownerSid, current);
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }
    internal bool IsCurrentOwner() => _owner.IsCurrentOwner();

    internal WindowsDeviceOutcome Execute(WindowsDeviceInvocation invocation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner()) return Failure(invocation, "owner_denied");
        try
        {
            WindowsDeviceOutcome result;
            using (var set = invocation.Action == "list" ? _native.OpenList(invocation.Scope == "present") : _native.OpenExact(invocation.InstanceId!))
            {
                CheckOwner(token);
                result = invocation.Action == "list" ? List(set, invocation, token) : Read(set, invocation, token);
            }
            // Also cover identity loss/disposal failures at the native-set boundary.
            CheckOwner(token);
            if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length >
                WindowsDeviceOperationContract.MaximumPageBytes - WindowsDeviceOperationContract.PageEnvelopeReserveBytes)
                return Failure(invocation, "result_limit");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OwnerLostException) { return Failure(invocation, "owner_denied"); }
        catch (WindowsDeviceNativeException failure)
        {
            token.ThrowIfCancellationRequested();
            return !IsCurrentOwner() ? Failure(invocation, "owner_denied") :
                Failure(invocation, failure.Cleanup ? "cleanup_failed" : NativeCode(failure.NativeCode),
                    nativeError: failure.NativeCode);
        }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { return Failure(invocation, "unsupported"); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { return Failure(invocation, "inventory_unavailable"); }
    }

    private WindowsDeviceOutcome List(IWindowsDeviceSet set, WindowsDeviceInvocation invocation, CancellationToken token)
    {
        var items = new List<WindowsDeviceItem>();
        var scanned = 0;
        uint index = 0;
        var seeking = invocation.AfterInstanceId is not null;
        var uncertainSeek = false;
        int? seekNativeError = null;
        var lastBoundaryIdentified = false;
        var encodedItems = 2;
        while (scanned < WindowsDeviceOperationContract.MaximumScannedEntries)
        {
            CheckOwner(token);
            var entry = set.At(index++);
            if (entry.End)
                return seeking ? Failure(invocation, uncertainSeek ? "anchor_unavailable" : "precondition", scanned, seekNativeError) :
                    Page(invocation, items, scanned, false, lastBoundaryIdentified);
            scanned++;
            if (entry.Error != 0 || entry.Node is null) return Failure(invocation, NativeCode(entry.Error), scanned, entry.Error == 0 ? null : entry.Error);
            var identity = Identity(entry.Node, token);
            if (seeking)
            {
                if (!identity.Valid) { uncertainSeek = true; seekNativeError ??= identity.Error; }
                else if (StringComparer.OrdinalIgnoreCase.Equals(identity.Id, invocation.AfterInstanceId)) seeking = false;
                continue;
            }
            if (items.Count == invocation.Limit)
                return Page(invocation, items, scanned, true, lastBoundaryIdentified);
            var observation = Metadata(entry.Node, identity, token);
            var itemBytes = JsonSerializer.SerializeToUtf8Bytes(observation.Item, Json).Length + 1;
            // Leave room for outcome/cursor and the owning Engine receipt. Do not
            // advance beyond an item omitted for bytes; the next anchor remains
            // the last returned device, so the next call re-observes this item.
            if (encodedItems + itemBytes > WindowsDeviceOperationContract.MaximumPageBytes - 2 * WindowsDeviceOperationContract.PageEnvelopeReserveBytes)
                return items.Count == 0 ? Failure(invocation, "result_limit", scanned) :
                    Page(invocation, items, scanned, true, lastBoundaryIdentified);
            items.Add(observation.Item); encodedItems += itemBytes;
            lastBoundaryIdentified = observation.Consistent && observation.Item.InstanceId is not null;
        }
        // There may be more native entries, but no end observation was made.
        // A page prefix alone must never claim complete enumeration.
        return new(false, "enumeration_limit", invocation.Action, invocation.Scope, items.AsReadOnly(), scanned, true, null, true);
    }

    private WindowsDeviceOutcome Read(IWindowsDeviceSet set, WindowsDeviceInvocation invocation, CancellationToken token)
    {
        CheckOwner(token);
        var entry = set.At(0);
        if (entry.End) return Failure(invocation, "not_found");
        if (entry.Error != 0 || entry.Node is null) return Failure(invocation, NativeCode(entry.Error), nativeError: entry.Error == 0 ? null : entry.Error);
        var identity = Identity(entry.Node, token);
        if (!identity.Valid || !StringComparer.OrdinalIgnoreCase.Equals(identity.Id, invocation.InstanceId))
            return Failure(invocation, identity.Valid ? "precondition" : NativeCode(identity.Error ?? 0), 1, identity.Error);
        var observed = Metadata(entry.Node, identity, token);
        return new(observed.Consistent, observed.Consistent ? "observed" : "precondition", invocation.Action, invocation.Scope,
            Array.AsReadOnly(new[] { observed.Item }), 1, false, null, observed.Item.Issues.Count != 0,
            observed.Consistent ? null : IdentityFailure(observed.Item)?.NativeCode,
            observed.Consistent ? null : IdentityFailure(observed.Item)?.NativeDomain);
    }

    private Observation Metadata(IWindowsDeviceNode node, IdentityObservation before, CancellationToken token)
    {
        if (!before.Valid) return new(Unavailable(null, new("instanceId", NativeCode(before.Error ?? 0), before.Error, before.Error.HasValue ? "setupapi" : null)), false);
        var issues = new List<WindowsDeviceIssue>();
        var friendly = Text(node, WindowsDeviceField.FriendlyName, "friendlyName", 512, issues, token);
        var description = friendly is null ? Text(node, WindowsDeviceField.Description, "deviceDescription", 512, issues, token) : null;
        var className = Text(node, WindowsDeviceField.Class, "class", 128, issues, token);
        var manufacturer = Text(node, WindowsDeviceField.Manufacturer, "manufacturer", 256, issues, token);
        var service = Text(node, WindowsDeviceField.Service, "service", 256, issues, token);
        var inf = Text(node, WindowsDeviceField.DriverInf, "driverInf", 260, issues, token);
        var version = Text(node, WindowsDeviceField.DriverVersion, "driverVersion", 128, issues, token);
        var date = DriverDate(node, issues, token);
        var provider = Text(node, WindowsDeviceField.DriverProvider, "driverProvider", 256, issues, token);
        CheckOwner(token);
        var status = node.Status();
        CheckOwner(token);
        uint? flags = null, problem = null;
        if (status.ConfigRet == 0)
        {
            flags = status.Flags;
            // CM defines the problem output only when DN_HAS_PROBLEM is set.
            if ((status.Flags & HasProblem) != 0) problem = status.Problem;
        }
        else issues.Add(new("status", status.ConfigRet == 13 ? "not_found" : "config_manager_error",
            unchecked((int)status.ConfigRet), "configmgr32"));
        var after = Identity(node, token);
        if (!after.Valid || !StringComparer.OrdinalIgnoreCase.Equals(before.Id, after.Id))
            return new(Unavailable(before.Id, new("instanceId", "device_changed",
                after.Error, after.Error.HasValue ? "setupapi" : null)), false);
        return new(new(before.Id, node.ClassGuid, friendly ?? description, className, manufacturer, service, flags, problem,
            inf, version, date, provider, issues.AsReadOnly()), true);
    }

    private string? Text(IWindowsDeviceNode node, WindowsDeviceField field, string name, int maximum,
        List<WindowsDeviceIssue> issues, CancellationToken token)
    {
        var raw = Property(node, field, checked((maximum + 1) * 2), token);
        try
        {
            if (Problem(raw, name, checked((maximum + 1) * 2), issues)) return null;
            var bytes = raw.Bytes!;
            if (raw.Type != StringType) { issues.Add(new(name, "unsupported_type")); return null; }
            if (bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[^1] != 0 || bytes[^2] != 0)
            { issues.Add(new(name, "invalid_text")); return null; }
            var text = Unicode.GetString(bytes, 0, bytes.Length - 2);
            if (text.Any(char.IsControl)) { issues.Add(new(name, "invalid_text")); return null; }
            return text; // Literal metadata; no indirect-resource/environment expansion.
        }
        catch (DecoderFallbackException) { issues.Add(new(name, "invalid_text")); return null; }
        finally { if (raw.Bytes is not null) Array.Clear(raw.Bytes); }
    }

    private DateTimeOffset? DriverDate(IWindowsDeviceNode node, List<WindowsDeviceIssue> issues, CancellationToken token)
    {
        var raw = Property(node, WindowsDeviceField.DriverDate, 8, token);
        try
        {
            if (Problem(raw, "driverDateUtc", 8, issues)) return null;
            if (raw.Type != 0x10 || raw.Bytes!.Length != 8) { issues.Add(new("driverDateUtc", "unsupported_type")); return null; }
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(raw.Bytes);
            try { return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)); }
            catch (ArgumentOutOfRangeException) { issues.Add(new("driverDateUtc", "invalid_value")); return null; }
        }
        finally { if (raw.Bytes is not null) Array.Clear(raw.Bytes); }
    }

    private WindowsDeviceProperty Property(IWindowsDeviceNode node, WindowsDeviceField field, int maximum, CancellationToken token)
    {
        CheckOwner(token);
        var raw = node.Property(field, maximum);
        try { CheckOwner(token); return raw; }
        catch { if (raw.Bytes is not null) Array.Clear(raw.Bytes); throw; }
    }
    private IdentityObservation Identity(IWindowsDeviceNode node, CancellationToken token)
    {
        CheckOwner(token); var identity = node.Identity(); CheckOwner(token);
        try
        {
            if (identity.Error != 0) return new(null, identity.Error, false);
            if (identity.InstanceId is null) return new(null, null, false);
            WindowsDeviceOperationContract.RequireInstanceId(identity.InstanceId);
            return new(identity.InstanceId, null, true);
        }
        catch (WindowsDeviceContractException) { return new(null, null, false); }
    }
    private static bool Problem(WindowsDeviceProperty raw, string field, int maximum, List<WindowsDeviceIssue> issues)
    {
        if (raw.TooLarge || raw.Bytes is { Length: > 0 } bytes && bytes.Length > maximum)
        { issues.Add(new(field, "field_limit", raw.Error == 0 ? null : raw.Error, raw.Error == 0 ? null : "setupapi")); return true; }
        if (raw.Error != 0) { issues.Add(new(field, NativeCode(raw.Error), raw.Error, "setupapi")); return true; }
        if (raw.Bytes is null) { issues.Add(new(field, "invalid_value")); return true; }
        return false;
    }
    private void CheckOwner(CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (!IsCurrentOwner()) throw new OwnerLostException(); }
    private static WindowsDeviceItem Unavailable(string? id, WindowsDeviceIssue issue) =>
        new(id, null, null, null, null, null, null, null, null, null, null, null, Array.AsReadOnly(new[] { issue }));
    private static WindowsDeviceOutcome Page(WindowsDeviceInvocation invocation, List<WindowsDeviceItem> items,
        int scanned, bool more, bool boundaryIdentified)
    {
        if (more && !boundaryIdentified)
        {
            var cause = items.Count == 0 ? null : IdentityFailure(items[^1]);
            return new(false, "continuation_unavailable", invocation.Action, invocation.Scope, items.AsReadOnly(), scanned, true, null, true,
                cause?.NativeCode, cause?.NativeDomain);
        }
        return new(true, "observed", invocation.Action, invocation.Scope, items.AsReadOnly(), scanned, more,
            more ? items[^1].InstanceId : null, items.Any(item => item.Issues.Count != 0));
    }
    private static WindowsDeviceOutcome Failure(WindowsDeviceInvocation invocation, string code, int scanned = 0, int? nativeError = null) =>
        new(false, code, invocation.Action, invocation.Scope, Array.Empty<WindowsDeviceItem>(), scanned, false, null, false,
            nativeError, nativeError.HasValue ? "setupapi" : null);
    private static WindowsDeviceIssue? IdentityFailure(WindowsDeviceItem item) =>
        item.Issues.FirstOrDefault(issue => issue.Field == "instanceId" && issue.NativeCode.HasValue && issue.NativeDomain == "setupapi");
    private static string NativeCode(int error) => error switch
    {
        2 or 1168 or unchecked((int)0xe000020bu) => "not_found",
        5 => "access", 50 => "unsupported", 122 => "field_limit",
        _ => "inventory_unavailable"
    };
    private readonly record struct IdentityObservation(string? Id, int? Error, bool Valid);
    private sealed record Observation(WindowsDeviceItem Item, bool Consistent);
    private sealed class OwnerLostException : Exception { }
}
