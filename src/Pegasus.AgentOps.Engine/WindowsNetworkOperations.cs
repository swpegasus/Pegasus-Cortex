using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

internal sealed record WindowsNetworkIssue(string Area, string Code, int? NativeError = null);
internal sealed record WindowsNetworkItem(string Key, string Kind, IReadOnlyDictionary<string, object?> Fields, int Occurrences = 1);
internal sealed record WindowsNetworkCoverage(string Scope, IReadOnlyList<string> Families, bool Snapshot,
    bool DnsResolutionPerformed, bool NativePreallocationCapped, int? MaximumCallerBufferBytes, int MaximumNativeRows,
    IReadOnlyList<string> Limitations);
internal sealed record WindowsNetworkCapture(bool Ok, string Code, IReadOnlyList<WindowsNetworkItem> Items,
    bool Partial, WindowsNetworkCoverage Coverage, IReadOnlyList<WindowsNetworkIssue> Issues);
internal sealed record WindowsNetworkOutcome(bool Ok, string Code, string View, IReadOnlyList<WindowsNetworkItem> Items,
    string? NextKey, bool HasMore, bool Partial, DateTimeOffset ObservedUtc, WindowsNetworkCoverage Coverage,
    IReadOnlyList<WindowsNetworkIssue> Issues);
internal interface IWindowsNetworkNative
{ WindowsNetworkCapture Capture(string view, CancellationToken token); }

/// <summary>One stateless local observation per request, admitted by the original ordinary owner.</summary>
internal sealed class WindowsNetworkOperations
{
    internal const int MaximumRows = 4096, MaximumPageBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions FieldJson = new(Json) { MaxDepth = 16 };
    private readonly OwnerHostIdentity _owner;
    private readonly IWindowsNetworkNative _native;

    [SupportedOSPlatform("windows")]
    internal WindowsNetworkOperations(string ownerSid) : this(ownerSid, LocalIdentity.Current, new WindowsNetworkNative()) { }
    internal WindowsNetworkOperations(string ownerSid, Func<LocalPeer> current, IWindowsNetworkNative native)
    { _owner = new(ownerSid, current); _native = native ?? throw new ArgumentNullException(nameof(native)); }
    internal bool IsCurrentOwner() => _owner.IsCurrentOwner();

    internal WindowsNetworkOutcome Execute(WindowsNetworkInvocation invocation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner()) return Failed(invocation, "owner_denied");
        var captured = _native.Capture(invocation.View, token);
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner()) return Failed(invocation, "owner_denied");
        if (!captured.Ok) return Failed(invocation, captured.Code, captured.Coverage, captured.Issues);
        if (captured.Items.Count > MaximumRows)
            return Failed(invocation, "network_limit", captured.Coverage);
        if (captured.Items.Any(item => !WindowsNetworkOperationContract.IsKey(item.Key) ||
            !(invocation.View switch { "adapters" => item.Kind == "adapter", "routes" => item.Kind == "route", _ => item.Kind is "tcp_connection" or "udp_endpoint" })))
            return Failed(invocation, "network_invalid_native_data", captured.Coverage);
        var issues = captured.Issues.ToList();
        var merged = new List<WindowsNetworkItem>();
        foreach (var group in captured.Items.GroupBy(item => item.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var members = group.ToArray();
            if (members.Any(item => item.Kind != members[0].Kind))
                return Failed(invocation, "network_invalid_native_data", captured.Coverage);
            var failure = MergeCoordinates(members, token, out var mergedItem, out var conflict);
            if (failure is not null) return Failed(invocation, failure, captured.Coverage);
            merged.Add(mergedItem!);
            if (conflict) issues.Add(new(invocation.View, "conflicting_coordinates"));
        }
        var rows = merged.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        IReadOnlyList<WindowsNetworkItem> page; string? next = null; var more = false;
        if (invocation.Action == "read")
        {
            var found = rows.FirstOrDefault(item => item.Key == invocation.Key);
            if (found is null) return Failed(invocation, captured.Partial ? "network_visibility_incomplete" : "not_found", captured.Coverage, issues.AsReadOnly());
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
        // Never return a silently clipped item or a cursor past an omitted row.
        // Native/field limits already bound materialization before this check.
        if (JsonSerializer.SerializeToUtf8Bytes(page, Json).Length > MaximumPageBytes)
            return Failed(invocation, "network_output_limit", captured.Coverage, issues.AsReadOnly());
        token.ThrowIfCancellationRequested();
        if (!IsCurrentOwner()) return Failed(invocation, "owner_denied");
        return new(true, "observed", invocation.View, page, next, more, captured.Partial || issues.Count > 0,
            DateTimeOffset.UtcNow, captured.Coverage, issues.AsReadOnly());
    }

    private static string? MergeCoordinates(WindowsNetworkItem[] members, CancellationToken token,
        out WindowsNetworkItem? row, out bool conflict)
    {
        row = null; conflict = false;
        var first = members[0];
        if (members.Length == 1) { row = first with { Occurrences = 1 }; return null; }
        try
        {
            var firstBytes = JsonSerializer.SerializeToUtf8Bytes(first.Fields, FieldJson);
            if (firstBytes.Length > MaximumPageBytes) return "network_output_limit";
            using var firstDocument = JsonDocument.Parse(firstBytes, new JsonDocumentOptions { MaxDepth = 16 });
            var common = new Dictionary<string, object?>(first.Fields, StringComparer.Ordinal);
            foreach (var member in members.AsSpan(1))
            {
                token.ThrowIfCancellationRequested();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(member.Fields, FieldJson);
                if (bytes.Length > MaximumPageBytes) return "network_output_limit";
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                // Object property order is irrelevant; array order remains meaningful (for example DNS priority).
                if (JsonElement.DeepEquals(firstDocument.RootElement, document.RootElement)) continue;
                conflict = true;
                foreach (var name in common.Keys.ToArray())
                {
                    if (!document.RootElement.TryGetProperty(name, out var value) ||
                        !JsonElement.DeepEquals(firstDocument.RootElement.GetProperty(name), value)) common.Remove(name);
                }
            }
            // A coordinate can be shared by multiple OS rows. Never choose a disputed or absent field from one row.
            row = first with { Fields = new ReadOnlyDictionary<string, object?>(common), Occurrences = members.Length };
            return null;
        }
        catch (JsonException) { return "network_invalid_native_data"; }
    }

    internal static WindowsNetworkItem Item(string kind, object identity, Dictionary<string, object?> fields) =>
        new(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { kind, identity }, Json))),
            kind, new ReadOnlyDictionary<string, object?>(fields));
    internal static WindowsNetworkCoverage Coverage(string view) => new("local_computer", Array.AsReadOnly(new[] { "ipv4", "ipv6" }),
        false, false, view != "routes", view == "routes" ? null : WindowsNetworkNative.MaximumBufferBytes, MaximumRows,
        Array.AsReadOnly(view switch
        {
            "routes" => new[] { "windows_allocates_route_table_before_count_admission", "route_metric_is_offset_not_total_interface_metric", "interface_indices_are_not_persistent", "pages_reobserve_changing_state", "synchronous_native_calls_are_joined_not_aborted" },
            "connections" => new[] { "udp_rows_are_local_endpoints_without_remote_peer_or_connection_state", "owner_pid_has_no_process_generation_or_authority", "pages_reobserve_changing_state", "synchronous_native_calls_are_joined_not_aborted" },
            _ => new[] { "dns_servers_are_configuration_not_resolution", "interface_indices_are_not_persistent", "pages_reobserve_changing_state", "synchronous_native_calls_are_joined_not_aborted" }
        }));
    private static WindowsNetworkOutcome Failed(WindowsNetworkInvocation invocation, string code,
        WindowsNetworkCoverage? coverage = null, IReadOnlyList<WindowsNetworkIssue>? issues = null) =>
        new(false, code, invocation.View, Array.Empty<WindowsNetworkItem>(), null, false, false, DateTimeOffset.UtcNow,
            coverage ?? Coverage(invocation.View), issues ?? Array.Empty<WindowsNetworkIssue>());
}
