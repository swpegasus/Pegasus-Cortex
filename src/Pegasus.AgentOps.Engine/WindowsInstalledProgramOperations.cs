using System.Buffers.Binary;
using System.Text;
using Pegasus.AgentOps.Windows.Registry;

namespace Pegasus.AgentOps.Engine;

internal sealed record WindowsInstalledProgramIssue(string Field, string Code);
internal sealed record WindowsInstalledProgramItem(string Source, string Key, string? Generation,
    string? Name, string? Version, string? Publisher, string? InstallDate, long? EstimatedSizeKb,
    IReadOnlyList<WindowsInstalledProgramIssue> Issues);

// Partial describes incomplete observation, never an issued mutation. Entries
// are Uninstall registrations, not proof that a product exists or is healthy.
internal sealed record WindowsInstalledProgramOutcome(bool Ok, string Code, string Action, string Source,
    IReadOnlyList<WindowsInstalledProgramItem> Programs, int Scanned, bool HasMore,
    RegistryBrowseCursor? Continuation, bool Partial);

/// <summary>Read-only projection over the existing admitted, no-link registry module.</summary>
internal sealed class WindowsInstalledProgramOperations(RegistryOwnerBinding? owner)
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    private static readonly (string Name, int MaximumCharacters)[] Fields =
    [ ("DisplayName", 512), ("DisplayVersion", 128), ("Publisher", 512), ("InstallDate", 32), ("EstimatedSize", 0) ];

    internal WindowsInstalledProgramOutcome Execute(WindowsInstalledProgramInvocation invocation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        token.ThrowIfCancellationRequested();
        if (owner?.IsCurrentOwner() != true) return Failure(invocation, "owner_denied");
        var result = invocation.Action == "list" ? List(invocation, token) : Read(invocation, token);
        token.ThrowIfCancellationRequested();
        // No owner metadata survives an observed identity/session/elevation loss.
        return owner.IsCurrentOwner() ? result : Failure(invocation, "owner_denied");
    }

    private WindowsInstalledProgramOutcome List(WindowsInstalledProgramInvocation invocation, CancellationToken token)
    {
        var result = owner!.Operations.Browse(invocation.Browse!, token);
        ObserveCancellation(result.Status, token);
        if (result.Status == RegistryStatus.NotFound && invocation.Cursor is null)
            return Success(invocation, [], 0, false, null, "source_absent");
        if (result.Status != RegistryStatus.Ok || result.Value is not { } page)
            return Failure(invocation, Code(result.Status));
        var items = new List<WindowsInstalledProgramItem>(page.Entries.Count);
        foreach (var entry in page.Entries)
        {
            token.ThrowIfCancellationRequested();
            var key = invocation.Root with { KeyPath = invocation.Root.KeyPath + "\\" + entry.Name };
            var inspected = owner.Operations.InspectKey(key, token);
            ObserveCancellation(inspected.Status, token);
            items.Add(inspected.Status == RegistryStatus.Ok && inspected.Value is { } snapshot
                ? Metadata(invocation.Source, entry.Name, key, snapshot, token)
                : Unavailable(invocation.Source, entry.Name, IssueCode(inspected.Status, inspected.Code)));
        }
        // Registry enumeration is not a snapshot. Never return a continuation
        // from a root whose observed generation changed while reading children.
        var after = owner.Operations.InspectKey(invocation.Root, token);
        ObserveCancellation(after.Status, token);
        if (after.Status != RegistryStatus.Ok) return Failure(invocation,
            after.Status == RegistryStatus.NotFound ? "precondition" : Code(after.Status));
        if (after.Value?.Generation != page.Key.Generation) return Failure(invocation, "precondition");
        return Success(invocation, items.AsReadOnly(), page.Entries.Count, page.HasMore, page.Continuation);
    }

    private WindowsInstalledProgramOutcome Read(WindowsInstalledProgramInvocation invocation, CancellationToken token)
    {
        var key = invocation.Root with { KeyPath = invocation.Root.KeyPath + "\\" + invocation.Key };
        var inspected = owner!.Operations.InspectKey(key, token);
        ObserveCancellation(inspected.Status, token);
        if (inspected.Status != RegistryStatus.Ok || inspected.Value is not { } snapshot)
            return Failure(invocation, Code(inspected.Status));
        if (invocation.Generation is not null && invocation.Generation != snapshot.Generation)
            return Failure(invocation, "precondition");
        var item = Metadata(invocation.Source, invocation.Key!, key, snapshot, token);
        if (item.Generation is null) return Failure(invocation, "precondition");
        return Success(invocation, Array.AsReadOnly(new[] { item }), 1, false, null);
    }

    private WindowsInstalledProgramItem Metadata(string source, string name, RegistryKeyIdentity key,
        RegistryKeySnapshot snapshot, CancellationToken token)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        long? size = null;
        var issues = new List<WindowsInstalledProgramIssue>();
        foreach (var field in Fields)
        {
            token.ThrowIfCancellationRequested();
            var read = owner!.Operations.ReadValue(new(key, field.Name), token);
            ObserveCancellation(read.Status, token);
            if (read.Status == RegistryStatus.Stale || read.Value is { } observation && observation.Key.Generation != snapshot.Generation)
                return Unavailable(source, name, "key_changed");
            if (read.Status == RegistryStatus.NotFound) return Unavailable(source, name, "key_not_found");
            if (read.Status != RegistryStatus.Ok || read.Value is null)
            { issues.Add(new(field.Name, IssueCode(read.Status, read.Code))); continue; }
            if (read.Value.Value is not { } data) continue; // Optional registration field is absent.
            byte[]? bytes = null;
            try
            {
                bytes = Convert.FromBase64String(data.Base64);
                if (field.Name == "EstimatedSize")
                {
                    if (data.Type != RegistryValueType.DWord || bytes.Length != 4) issues.Add(new(field.Name, "unsupported_type"));
                    else size = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                }
                else
                {
                    if (data.Type is not (RegistryValueType.String or RegistryValueType.ExpandString))
                    { issues.Add(new(field.Name, "unsupported_type")); continue; }
                    if (bytes.Length > checked((field.MaximumCharacters + 1) * 2))
                    { issues.Add(new(field.Name, "field_limit")); continue; }
                    if (bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[^1] != 0 || bytes[^2] != 0)
                    { issues.Add(new(field.Name, "invalid_text")); continue; }
                    // REG_EXPAND_SZ remains literal: no environment, filesystem,
                    // executable, icon, uninstall string or provider is consulted.
                    var text = StrictUnicode.GetString(bytes, 0, bytes.Length - 2);
                    if (!ValidText(text)) issues.Add(new(field.Name, "invalid_text"));
                    else values[field.Name] = text;
                }
            }
            catch (Exception failure) when (failure is FormatException or DecoderFallbackException)
            { issues.Add(new(field.Name, "invalid_text")); }
            finally { if (bytes is not null) Array.Clear(bytes); }
        }
        var after = owner!.Operations.InspectKey(key, token);
        ObserveCancellation(after.Status, token);
        if (after.Status != RegistryStatus.Ok || after.Value?.Generation != snapshot.Generation)
            return Unavailable(source, name, after.Status == RegistryStatus.Ok ? "key_changed" : IssueCode(after.Status, after.Code));
        return new(source, name, snapshot.Generation, values.GetValueOrDefault("DisplayName"), values.GetValueOrDefault("DisplayVersion"),
            values.GetValueOrDefault("Publisher"), values.GetValueOrDefault("InstallDate"), size, issues.AsReadOnly());
    }

    private static bool ValidText(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character)) return false;
            if (!char.IsSurrogate(character)) continue;
            if (!char.IsHighSurrogate(character) || index + 1 == value.Length || !char.IsLowSurrogate(value[++index])) return false;
        }
        return true;
    }

    private static WindowsInstalledProgramItem Unavailable(string source, string key, string code) =>
        new(source, key, null, null, null, null, null, null, Array.AsReadOnly(new[] { new WindowsInstalledProgramIssue("key", code) }));
    private static WindowsInstalledProgramOutcome Failure(WindowsInstalledProgramInvocation invocation, string code) =>
        new(false, code, invocation.Action, invocation.Source, Array.Empty<WindowsInstalledProgramItem>(), 0, false, null, false);
    private static WindowsInstalledProgramOutcome Success(WindowsInstalledProgramInvocation invocation,
        IReadOnlyList<WindowsInstalledProgramItem> programs, int scanned, bool more, RegistryBrowseCursor? cursor, string code = "observed") =>
        new(true, code, invocation.Action, invocation.Source, programs, scanned, more, cursor, programs.Any(item => item.Issues.Count != 0));
    private static string Code(RegistryStatus status) => status switch
    {
        RegistryStatus.Denied => "access", RegistryStatus.NotFound => "not_found",
        RegistryStatus.Stale or RegistryStatus.PreconditionFailed => "precondition",
        RegistryStatus.LimitExceeded => "registry_limit", RegistryStatus.Unsupported => "registry_unsupported",
        RegistryStatus.InvalidRequest => "invalid", _ => "inventory_unavailable"
    };
    private static string IssueCode(RegistryStatus status, string code) => code == "registry_link_not_supported" ? code : Code(status);
    private static void ObserveCancellation(RegistryStatus status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (status == RegistryStatus.Cancelled) throw new OperationCanceledException(token);
    }
}
