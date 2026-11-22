using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

internal sealed record WindowsAccountIssue(string Area, string Code, int? NativeError = null, string? NativeDomain = null);
internal sealed record WindowsAccountItem(string Key, string Kind, IReadOnlyDictionary<string, object?> Fields);
internal sealed record WindowsAccountCoverage(string Scope, bool Snapshot, bool NativePreallocationCapped,
    int? PreferredNativePageBytes, int MaximumNativeRows, IReadOnlyList<string> Limitations);
internal sealed record WindowsAccountCapture(bool Ok, string Code, IReadOnlyList<WindowsAccountItem> Items,
    bool Partial, WindowsAccountCoverage Coverage, IReadOnlyList<WindowsAccountIssue> Issues);
internal sealed record WindowsAccountOutcome(bool Ok, string Code, string View, string? Group, IReadOnlyList<WindowsAccountItem> Items,
    string? NextKey, bool HasMore, bool Partial, DateTimeOffset ObservedUtc, WindowsAccountCoverage Coverage,
    IReadOnlyList<WindowsAccountIssue> Issues);
internal interface IWindowsAccountNative
{ WindowsAccountCapture Capture(string view, string? group, CancellationToken token); }

// One bounded fresh capture per request, never a retained native enumeration or
// snapshot session. Keys are local observation coordinates, not identity grants
// or account/session generations. OS permissions remain authoritative.
internal sealed class WindowsAccountOperations
{
    internal const int MaximumRows = 4096, MaximumPageBytes = 256 * 1024, PreferredPageBytes = 8192, MaximumNativePages = 256;
    internal const int MaximumIssues = 512, MaximumCaptureBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _ownerSid;
    private readonly Func<LocalPeer> _current;
    private readonly OwnerHostIdentity _owner;
    private readonly IWindowsAccountNative _native;
    private static readonly HashSet<string> SourceCodes = new(StringComparer.Ordinal)
    {
        "observed", "account_unavailable", "account_invalid_native_data", "account_limit", "account_unproven_local_role",
        "account_domain_controller_refused", "account_changed_during_read", "session_identity_query_limit"
    };
    private static readonly HashSet<string> Areas = new(StringComparer.Ordinal)
        { "local_role", "users", "groups", "members", "sessions", "session_user", "session_domain", "current" };
    private static readonly HashSet<string> Limitations = new(StringComparer.Ordinal)
    {
        "pages_reobserve_changing_state", "keys_are_coordinates_not_generations_or_authority", "synchronous_native_calls_are_joined_not_aborted",
        "netapi_preferred_bytes_not_allocation_cap", "native_resume_internal_to_single_capture", "native_totalentries_is_hint",
        "local_sam_normal_accounts_only", "domain_controller_sam_views_refused", "direct_membership_sids_without_name_resolution",
        "windows_allocates_entire_membership_before_count_admission", "windows_allocates_wts_array_before_count_admission",
        "caller_query_permissions_limit_visibility", "session_ids_are_reusable", "interactive_slots_not_authentication_proof",
        "optional_wts_identity_queries_capped_at_256", "current_identity_observation_not_authority"
    };

    [SupportedOSPlatform("windows")]
    internal WindowsAccountOperations(string ownerSid) : this(ownerSid, LocalIdentity.Current, new WindowsAccountNative()) { }
    internal WindowsAccountOperations(string ownerSid, Func<LocalPeer> current, IWindowsAccountNative native)
    {
        _ownerSid = ownerSid; _current = current ?? throw new ArgumentNullException(nameof(current));
        _owner = new(ownerSid, current); _native = native ?? throw new ArgumentNullException(nameof(native));
    }
    internal bool IsCurrentOwner() => _owner.IsCurrentOwner();

    internal WindowsAccountOutcome Execute(WindowsAccountInvocation invocation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner()) return Failed(invocation, "owner_denied");
        LocalPeer before;
        try { before = _current(); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return Failed(invocation, "owner_denied"); }
        if (before.Sid != _ownerSid || before.Session <= 0 || before.Elevated) return Failed(invocation, "owner_denied");
        var captured = _native.Capture(invocation.View, invocation.Group, token);
        token.ThrowIfCancellationRequested();
        if (!SameOwner(before)) return Failed(invocation, "owner_denied");
        if (!ValidCaptureEnvelope(captured, invocation.View)) return Failed(invocation, "account_invalid_native_data");
        if (!captured.Ok) return Failed(invocation, captured.Code, captured.Coverage, captured.Issues, captured.Partial);
        if (captured.Items.Count > MaximumRows) return Failed(invocation, "account_limit", captured.Coverage);
        var rows = new List<WindowsAccountItem>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var retainedBytes = 0;
        foreach (var item in captured.Items)
        {
            token.ThrowIfCancellationRequested();
            if (!TryAdmit(item, invocation, before, out var admitted))
                return Failed(invocation, "account_invalid_native_data", captured.Coverage);
            if (!keys.Add(admitted!.Key)) return Failed(invocation, "account_duplicate_coordinates", captured.Coverage);
            var encoded = JsonSerializer.SerializeToUtf8Bytes(admitted, Json);
            try { retainedBytes = checked(retainedBytes + encoded.Length); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            if (retainedBytes > MaximumCaptureBytes) return Failed(invocation, "account_limit", captured.Coverage);
            rows.Add(admitted);
        }
        if (invocation.View == "current" && (rows.Count != 1 || captured.Partial))
            return Failed(invocation, "account_invalid_native_data", captured.Coverage);
        rows.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        IReadOnlyList<WindowsAccountItem> page;
        string? next = null;
        var more = false;
        if (invocation.Action == "read")
        {
            var found = rows.FirstOrDefault(item => item.Key == invocation.Key);
            if (found is null) return Failed(invocation, (captured.Partial || captured.Issues.Count > 0) ? "account_visibility_incomplete" : "not_found",
                captured.Coverage, captured.Issues, captured.Partial || captured.Issues.Count > 0);
            page = Array.AsReadOnly(new[] { found });
        }
        else
        {
            var candidates = rows.Where(item => invocation.AfterKey is null || StringComparer.Ordinal.Compare(item.Key, invocation.AfterKey) > 0)
                .Take(invocation.Limit + 1).ToArray();
            page = Array.AsReadOnly(candidates.Take(invocation.Limit).ToArray());
            more = candidates.Length > page.Count;
            if (more) next = page[^1].Key;
        }
        var outcome = new WindowsAccountOutcome(true, "observed", invocation.View, invocation.Group, page, next, more,
            captured.Partial || captured.Issues.Count > 0, DateTimeOffset.UtcNow, captured.Coverage, captured.Issues);
        var output = JsonSerializer.SerializeToUtf8Bytes(outcome, Json);
        try
        {
            if (output.Length > MaximumPageBytes) return Failed(invocation, "account_output_limit", captured.Coverage);
        }
        finally { CryptographicOperations.ZeroMemory(output); }
        token.ThrowIfCancellationRequested();
        if (!SameOwner(before)) return Failed(invocation, "owner_denied");
        return outcome;
    }

    private bool SameOwner(LocalPeer before)
    {
        if (!IsCurrentOwner()) return false;
        try
        {
            var after = _current();
            return after.Sid == before.Sid && after.Session == before.Session && !after.Elevated;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
    private static bool ValidCaptureEnvelope(WindowsAccountCapture? captured, string view)
    {
        if (captured is null || !SourceCodes.Contains(captured.Code) || captured.Ok && captured.Code != "observed" ||
            !captured.Ok && captured.Code == "observed" || captured.Items is null || captured.Issues is null ||
            captured.Issues.Count > MaximumIssues || captured.Coverage is not { } coverage ||
            coverage.Scope != "local_computer" || coverage.Snapshot || coverage.NativePreallocationCapped ||
            coverage.PreferredNativePageBytes != (view is "users" or "groups" ? PreferredPageBytes : (int?)null) ||
            coverage.MaximumNativeRows != MaximumRows || coverage.Limitations is null || coverage.Limitations.Count > Limitations.Count ||
            coverage.Limitations.Any(text => !Limitations.Contains(text))) return false;
        return captured.Issues.All(issue => issue is not null && Areas.Contains(issue.Area) && SourceCodes.Contains(issue.Code) &&
            issue.Code != "observed" && issue.NativeDomain is null or "netapi32" or "dsrole" or "win32" &&
            (issue.NativeError is null) == (issue.NativeDomain is null));
    }
    private static bool TryAdmit(WindowsAccountItem? item, WindowsAccountInvocation invocation, LocalPeer before, out WindowsAccountItem? admitted)
    {
        admitted = null;
        if (item is null || !WindowsAccountOperationContract.IsKey(item.Key) || item.Kind != Kind(invocation.View) ||
            item.Fields is null || item.Fields.Count > 8) return false;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in item.Fields)
        {
            object? value = pair.Value;
            var valid = (item.Kind, pair.Key) switch
            {
                ("user" or "local_group", "name") => Text(value, 256, false),
                ("user" or "local_group" or "group_member" or "current_identity", "sid") => value is string sid && IsSid(sid),
                ("user", "rid") or ("windows_session" or "current_identity", "sessionId") => Number(ref value),
                ("user", "disabled") or ("current_identity", "elevated") => value is bool,
                ("group_member", "group") => value is string group && StringComparer.OrdinalIgnoreCase.Equals(group, invocation.Group),
                ("windows_session", "state") => value is string state && state is
                    "active" or "connected" or "connect_query" or "shadow" or "disconnected" or "idle" or "listen" or "reset" or "down" or "init" or "unknown",
                ("windows_session", "stationName") => Text(value, 128, true),
                ("windows_session", "userName" or "domain") => Text(value, 256, true),
                _ => false
            };
            if (!valid || !fields.TryAdd(pair.Key, value)) return false;
        }
        if (item.Kind is "user" or "local_group" && !fields.ContainsKey("name") ||
            item.Kind == "group_member" && (!fields.ContainsKey("group") || !fields.ContainsKey("sid")) ||
            item.Kind == "windows_session" && (!fields.ContainsKey("sessionId") || !fields.ContainsKey("state") ||
                fields["sessionId"] is not uint session || session == 0 || fields["state"] as string == "listen") ||
            item.Kind == "current_identity" && (!fields.ContainsKey("sid") || !fields.ContainsKey("sessionId") || !fields.ContainsKey("elevated") ||
                fields["sid"] as string != before.Sid || fields["sessionId"] is not uint currentSession || currentSession != before.Session ||
                fields["elevated"] is not false)) return false;
        admitted = new(item.Key, item.Kind, new ReadOnlyDictionary<string, object?>(fields));
        return true;
    }
    private static bool Text(object? value, int max, bool allowEmpty) => value is string text &&
        text.Length <= max && (allowEmpty || !string.IsNullOrWhiteSpace(text)) && !text.Any(char.IsControl) &&
        WindowsAccountOperationContract.WellFormed(text);
    private static bool Number(ref object? value)
    {
        if (value is uint) return true;
        if (value is int number && number >= 0) { value = (uint)number; return true; }
        return false;
    }
    internal static bool IsSid(string? value)
    {
        if (value is null || value.Length is < 5 or > 184 || !value.StartsWith("S-1-", StringComparison.Ordinal)) return false;
        var pieces = value[4..].Split('-');
        if (pieces.Length is < 1 or > 16 || pieces.Any(piece => piece.Length == 0 ||
            piece.Length > 1 && piece[0] == '0' || piece.Any(c => c is < '0' or > '9'))) return false;
        if (!ulong.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var authority) || authority > 0xFFFFFFFFFFFF) return false;
        return pieces.Skip(1).All(piece => uint.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
    internal static string Kind(string view) => view switch
    { "users" => "user", "groups" => "local_group", "members" => "group_member", "sessions" => "windows_session", "current" => "current_identity", _ => "invalid" };
    internal static WindowsAccountItem Item(string kind, object identity, Dictionary<string, object?> fields)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { kind, identity }, Json);
        try { return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), kind,
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(fields, StringComparer.Ordinal))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static WindowsAccountCoverage Coverage(string view) => new("local_computer", false, false,
        view is "users" or "groups" ? PreferredPageBytes : null, MaximumRows,
        Array.AsReadOnly(new[] { "pages_reobserve_changing_state", "keys_are_coordinates_not_generations_or_authority",
            "synchronous_native_calls_are_joined_not_aborted" }.Concat(view switch
            {
                "users" => new[] { "netapi_preferred_bytes_not_allocation_cap", "native_resume_internal_to_single_capture", "native_totalentries_is_hint", "local_sam_normal_accounts_only", "domain_controller_sam_views_refused" },
                "groups" => ["netapi_preferred_bytes_not_allocation_cap", "native_resume_internal_to_single_capture", "native_totalentries_is_hint", "domain_controller_sam_views_refused"],
                "members" => ["windows_allocates_entire_membership_before_count_admission", "direct_membership_sids_without_name_resolution", "domain_controller_sam_views_refused"],
                "sessions" => ["windows_allocates_wts_array_before_count_admission", "caller_query_permissions_limit_visibility", "session_ids_are_reusable", "interactive_slots_not_authentication_proof", "optional_wts_identity_queries_capped_at_256"],
                _ => ["current_identity_observation_not_authority"]
            }).ToArray()));
    private static WindowsAccountOutcome Failed(WindowsAccountInvocation invocation, string code, WindowsAccountCoverage? coverage = null,
        IReadOnlyList<WindowsAccountIssue>? issues = null, bool partial = false) =>
        new(false, code, invocation.View, invocation.Group, Array.Empty<WindowsAccountItem>(), null, false, partial, DateTimeOffset.UtcNow,
            coverage ?? Coverage(invocation.View), issues ?? Array.Empty<WindowsAccountIssue>());
}
