using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PegasusCortex.CerebralLocations.Tests")]

namespace Pegasus.Cortex.Product;

public enum CortexCerebralDataMode { Live, Test, Starter }

/// <summary>A user-owned selection, never an override of the machine live binding.</summary>
public sealed record CortexCerebralLocationMapV1(
    int Version,
    string ClientId,
    string Generation,
    CortexCerebralDataMode Mode,
    string? TestDataLocation,
    string? StarterDataLocation);

/// <summary>
/// Immutable reload snapshot. Nonlive paths contain semantic state only; they
/// never relocate PAO, service state, credentials, or the shared Codex host.
/// A selection is configuration, not authority to open, migrate, or seed data.
/// </summary>
public sealed class CortexCerebralDataSelection
{
    internal CortexCerebralDataSelection(string clientId, CortexCerebralDataMode mode, string? generation,
        string dataRoot, string databasePath)
    {
        ClientId = clientId;
        Mode = mode;
        Generation = generation;
        DataRoot = dataRoot;
        DatabasePath = databasePath;
    }

    public string ClientId { get; }
    public CortexCerebralDataMode Mode { get; }
    public string? Generation { get; }
    public string DataRoot { get; }
    public string DatabasePath { get; }
    public bool RequiresIsolatedComposition => Mode != CortexCerebralDataMode.Live;
    public bool AllowsLiveProfileAutoAttach => Mode == CortexCerebralDataMode.Live;
    public bool AllowsSharedResidentEndpoint => Mode == CortexCerebralDataMode.Live;
    public bool RequiresFreshStarterDestination => Mode == CortexCerebralDataMode.Starter;

    public void RequireIsolatedComposition()
    {
        if (!RequiresIsolatedComposition)
            throw new InvalidOperationException("cerebral_isolated_selection_required");
    }
}

/// <summary>
/// Registry ownership: HKLM/64 InstallLocation + DataLocation remain the sole
/// live machine binding. This distinct HKCU/64 REG_SZ is a Cerebral selector.
/// Client IDs come from a validated runtime hello/lease, never a display name;
/// this configuration neither registers clients nor grants connection access.
/// The consumer takes a new snapshot on restart/reload, drains its old isolated
/// composition, and never changes the default resident application's store.
/// </summary>
public static class CortexCerebralDataLocations
{
    public const string RegistryKeyPath = @"SOFTWARE\Pegasus\Cortex\Cerebral\DataLocations";
    public const string RegistryValueName = "MapV1";
    public const int MaximumMapUtf8Bytes = 4096;
    private static readonly string[] Fields =
        ["version", "clientId", "generation", "mode", "testDataLocation", "starterDataLocation"];

    /// <summary>
    /// Reads one location value only. The consumer's explicit mode must match;
    /// losing a test/starter registration cannot turn that session into live.
    /// Does not read profile/settings bodies or create state.
    /// </summary>
    public static CortexCerebralDataSelection LoadForClient(CortexProductLayout liveLayout,
        string authenticatedClientId, CortexCerebralDataMode requiredMode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RequireClientId(authenticatedClientId);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(RegistryKeyPath + "\\" + authenticatedClientId, writable: false);
        var value = key is null ? null : ReadBoundedLocationValue(key);
        if (value is null) return ResolveExpected(liveLayout, null, authenticatedClientId, requiredMode);
        return ResolveExpected(liveLayout, Parse(value), authenticatedClientId, requiredMode);
    }

    /// <summary>
    /// Explicit configuration API, not called during resolution or startup.
    /// Writes one atomic REG_SZ value after admission; no database/profile copy,
    /// initialization, host switch, restart, or current-session reload occurs.
    /// Concurrent saves use last-writer-wins; Generation identifies the snapshot.
    /// </summary>
    public static void ConfigureCurrentUser(CortexProductLayout liveLayout, string authenticatedClientId,
        CortexCerebralLocationMapV1 map)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var text = PrepareRegistration(liveLayout, map, authenticatedClientId);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.CreateSubKey(RegistryKeyPath + "\\" + authenticatedClientId, writable: true) ??
            throw new IOException("cerebral_location_registration_unavailable");
        key.SetValue(RegistryValueName, text, RegistryValueKind.String);
    }

    /// <summary>Pure serialization/admission seam; tests never call the real registry writer.</summary>
    public static string PrepareRegistration(CortexProductLayout liveLayout, CortexCerebralLocationMapV1 map,
        string authenticatedClientId)
    {
        ArgumentNullException.ThrowIfNull(map);
        _ = Resolve(liveLayout, map, authenticatedClientId);
        return Serialize(map);
    }

    public static CortexCerebralDataSelection Resolve(
        CortexProductLayout liveLayout, CortexCerebralLocationMapV1? map, string authenticatedClientId) =>
        Resolve(liveLayout, map, authenticatedClientId, CortexLocationPathIdentity.PhysicalPath,
            CortexLocationPathIdentity.RequireSingleLinkFile);

    public static CortexCerebralDataSelection ResolveExpected(CortexProductLayout liveLayout,
        CortexCerebralLocationMapV1? map, string authenticatedClientId, CortexCerebralDataMode requiredMode)
    {
        if (!Enum.IsDefined(requiredMode) || (map?.Mode ?? CortexCerebralDataMode.Live) != requiredMode)
            throw new InvalidDataException("cerebral_requested_location_mode_unavailable");
        return Resolve(liveLayout, map, authenticatedClientId);
    }

    // Tests can model DOS/SUBST physical aliases without changing drives or
    // machine state. Production entry points always use the fixed metadata API.
    internal static CortexCerebralDataSelection Resolve(CortexProductLayout liveLayout,
        CortexCerebralLocationMapV1? map, string authenticatedClientId,
        Func<string, string> physicalPath, Action<string> requireSingleLink)
    {
        ArgumentNullException.ThrowIfNull(liveLayout);
        ArgumentNullException.ThrowIfNull(physicalPath);
        ArgumentNullException.ThrowIfNull(requireSingleLink);
        RequireClientId(authenticatedClientId);
        if (map is not null)
        {
            RequireClientId(map.ClientId);
            if (!string.Equals(map.ClientId, authenticatedClientId, StringComparison.Ordinal))
                throw new InvalidDataException("cerebral_location_client_mismatch");
            ValidateMap(map);
        }
        liveLayout.RequireRuntimePathsWithinDeclaredRoots();
        if (map is null)
            return Live(liveLayout, authenticatedClientId, null);

        // Validate even unselected configured roots: a stored test/starter alias
        // to live data is never an admissible configuration.
        var protectedRoots = new[] { liveLayout.InstallRoot, liveLayout.DataRoot };
        var roots = new[] { map.TestDataLocation, map.StarterDataLocation }
            .Where(root => root is not null).Select(root => CortexLocationPathIdentity.CanonicalRoot(root!)).ToArray();
        foreach (var root in roots)
        {
            foreach (var protectedRoot in protectedRoots)
                RequireDisjoint(root, protectedRoot, physicalPath);
            foreach (var name in new[] { "cortex-v2.db", "cortex-v2.db-wal", "cortex-v2.db-shm", "cortex-v2.db-journal" })
            {
                var file = Path.Combine(root, name);
                CortexProductLayout.RequirePathBudget(file);
                CortexProductLayout.RejectExistingReparseComponents(file);
                requireSingleLink(file);
            }
        }
        if (roots.Length == 2) RequireDisjoint(roots[0], roots[1], physicalPath);
        if (map.Mode == CortexCerebralDataMode.Live) return Live(liveLayout, authenticatedClientId, map.Generation);
        var selected = map.Mode == CortexCerebralDataMode.Test ? map.TestDataLocation : map.StarterDataLocation;
        if (selected is null) throw new InvalidDataException("cerebral_selected_location_missing");
        return new(authenticatedClientId, map.Mode, map.Generation, selected, Path.Combine(selected, "cortex-v2.db"));
    }

    /// <summary>
    /// Gate immediately before fresh starter initialization. Only metadata is
    /// inspected; a missing/empty destination may be initialized from schema and
    /// explicitly approved generic defaults, never a copied personal database.
    /// </summary>
    public static void RequireFreshStarterDestination(CortexCerebralDataSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Mode != CortexCerebralDataMode.Starter)
            throw new InvalidOperationException("cerebral_starter_selection_required");
        CortexProductLayout.RejectExistingReparseComponents(selection.DataRoot);
        if (File.Exists(selection.DataRoot) ||
            (Directory.Exists(selection.DataRoot) && Directory.EnumerateFileSystemEntries(selection.DataRoot).Any()))
            throw new InvalidDataException("cerebral_starter_destination_not_fresh");
    }

    public static CortexCerebralLocationMapV1 Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumMapUtf8Bytes || Encoding.UTF8.GetByteCount(text) > MaximumMapUtf8Bytes)
            throw new InvalidDataException("cerebral_location_map_too_large");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 3 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("cerebral_location_map_invalid");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                    throw new InvalidDataException("cerebral_location_map_fields_invalid");
            if (names.Count != Fields.Length) throw new InvalidDataException("cerebral_location_map_fields_invalid");
            var mode = root.GetProperty("mode").GetString() switch
            {
                "live" => CortexCerebralDataMode.Live,
                "test" => CortexCerebralDataMode.Test,
                "starter" => CortexCerebralDataMode.Starter,
                _ => throw new InvalidDataException("cerebral_location_mode_invalid")
            };
            var map = new CortexCerebralLocationMapV1(root.GetProperty("version").GetInt32(),
                root.GetProperty("clientId").GetString()!,
                root.GetProperty("generation").GetString()!, mode,
                OptionalString(root.GetProperty("testDataLocation")), OptionalString(root.GetProperty("starterDataLocation")));
            ValidateMap(map);
            return map;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("cerebral_location_map_invalid", error); }
    }

    private static string? OptionalString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static void ValidateMap(CortexCerebralLocationMapV1 map)
    {
        RequireClientId(map.ClientId);
        if (map.Version != 1 || map.Generation is null || !Guid.TryParseExact(map.Generation, "N", out var generation) ||
            generation.ToString("N") != map.Generation || !Enum.IsDefined(map.Mode))
            throw new InvalidDataException("cerebral_location_map_version_or_generation_invalid");
        foreach (var path in new[] { map.TestDataLocation, map.StarterDataLocation })
            if (path is not null) _ = CortexProductLayout.RequireCanonicalAbsoluteDirectory(path, nameof(path));
        if ((map.Mode == CortexCerebralDataMode.Test && map.TestDataLocation is null) ||
            (map.Mode == CortexCerebralDataMode.Starter && map.StarterDataLocation is null))
            throw new InvalidDataException("cerebral_selected_location_missing");
    }

    private static string Serialize(CortexCerebralLocationMapV1 map) => JsonSerializer.Serialize(new
    {
        version = map.Version, clientId = map.ClientId, generation = map.Generation, mode = map.Mode.ToString().ToLowerInvariant(),
        testDataLocation = map.TestDataLocation, starterDataLocation = map.StarterDataLocation
    });

    private static CortexCerebralDataSelection Live(CortexProductLayout layout, string clientId, string? generation) =>
        new(clientId, CortexCerebralDataMode.Live, generation, layout.DataRoot, layout.CortexDatabasePath);

    private static void RequireClientId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new InvalidDataException("cerebral_location_client_invalid");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadBoundedLocationValue(RegistryKey key)
    {
        // A fixed native buffer rejects an oversized value before allocation.
        // A size-query followed by GetValue could race with a larger replacement.
        var buffer = new byte[(MaximumMapUtf8Bytes + 1) * sizeof(char)];
        var size = (uint)buffer.Length;
        var error = RegQueryValueExW(key.Handle, RegistryValueName, 0, out var kind, buffer, ref size);
        if (error == 2) return null; // ERROR_FILE_NOT_FOUND
        if (error == 234 || size > buffer.Length) // ERROR_MORE_DATA
            throw new InvalidDataException("cerebral_location_map_too_large");
        if (error != 0) throw new IOException("cerebral_location_registration_unavailable",
            new System.ComponentModel.Win32Exception(error));
        if (kind != 1 || size < 2 || (size & 1) != 0 || buffer[size - 1] != 0 || buffer[size - 2] != 0)
            throw new InvalidDataException("cerebral_location_map_not_reg_sz");
        try { return new UnicodeEncoding(false, false, true).GetString(buffer, 0, checked((int)size - 2)); }
        catch (DecoderFallbackException errorDetail)
        { throw new InvalidDataException("cerebral_location_map_encoding_invalid", errorDetail); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string valueName, nint reserved,
        out uint kind, [Out] byte[] data, ref uint dataSize);

    private static void RequireDisjoint(string left, string right, Func<string, string> physicalPath)
    {
        if (Overlaps(left, right) || Overlaps(physicalPath(left), physicalPath(right)))
            throw new InvalidDataException("cerebral_location_overlaps_protected_or_other_root");
    }

    private static bool Overlaps(string left, string right)
    {
        static bool Within(string parent, string path)
        {
            parent = parent.TrimEnd('\\', '/');
            path = path.TrimEnd('\\', '/');
            return string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
        }
        return Within(left, right) || Within(right, left);
    }
}
