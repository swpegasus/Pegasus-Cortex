using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

/// <summary>Create-only protected defaults before a genuinely fresh optional service exists.</summary>
internal static class CortexOptionalMaintenanceDefaults
{
    internal static void Ensure(CortexProductLayout layout, bool allowFresh)
    {
        var directory = Path.Combine(layout.DataRoot, "execution-maintenance");
        if (Exists(directory)) { ValidateExisting(layout, directory); return; }
        if (!allowFresh || CortexWindowsProductRegistration.PreviouslyCreatedService(layout) ||
            Exists(Path.Combine(layout.InstallStateDirectory, "optional-service.v1.json")) ||
            Directory.EnumerateFileSystemEntries(layout.ServiceStateDirectory)
                .Any(path => Path.GetFileName(path) != "service.v1.json"))
            throw new InvalidDataException("Required or historical maintenance state is missing; it was not reset.");
        if (Path.Combine(directory, "ack-interval-" + new string('0', 32) + ".json").Length > 240)
            throw new InvalidDataException("The maintenance defaults exceed the Windows path budget.");
        if (layout.IsIsolatedFixture) Directory.CreateDirectory(directory);
        else
        {
            CortexPrivilegedCodePath.RequireProtectedAncestors(layout.DataRoot);
            using var privilege = CortexProductStateProvisioner.BeginSystemOwnedCreation();
            var security = new DirectorySecurity();
            security.SetOwner(new SecurityIdentifier("S-1-5-18"));
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(layout.OwnerSid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).Create(security);
        }
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Maintenance defaults collided with existing state; nothing was overwritten.");
        // Both names are CreateNew. A crash/partial write remains unavailable;
        // an existing required/mode record is never changed to manufacture OFF.
        WriteNew(Path.Combine(directory, "mode.v1.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, epoch = Guid.NewGuid().ToString("N"), phase = "off",
            jid = Guid.NewGuid().ToString("N"), rid = Guid.NewGuid().ToString("N"),
            scopeId = "", taskId = "", ownerSid = layout.OwnerSid, ownerSession = 0,
            serviceInstance = "", host = (object?)null, revision = 1
        }));
        WriteNew(Path.Combine(directory, "required.v1"), "maintenance/1\n"u8.ToArray());
        ValidateExisting(layout, directory);
    }

    private static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Maintenance state is redirected.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes); stream.Flush(flushToDisk: true);
    }

    private static void ValidateExisting(CortexProductLayout layout, string directory)
    {
        var sentinel = Path.Combine(directory, "required.v1");
        var mode = Path.Combine(directory, "mode.v1.json");
        if (!Exists(sentinel) || !Exists(mode))
            throw new InvalidDataException("Required maintenance state is missing; it was preserved without reinitialization.");
        if (!layout.IsIsolatedFixture)
        {
            CortexUserSessionStartup.RequireOwnedFile(layout.DataRoot, sentinel, 64);
            CortexUserSessionStartup.RequireOwnedFile(layout.DataRoot, mode, 16384);
        }
        if (new FileInfo(mode).Length > 16384 || File.ReadAllText(sentinel, Encoding.UTF8) != "maintenance/1\n")
            throw new InvalidDataException("The maintenance default marker is invalid; it was preserved.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(mode), new JsonDocumentOptions { MaxDepth = 8 });
        var value = document.RootElement;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != 12 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            !names.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "version", "epoch", "phase", "jid", "rid", "scopeId",
                "taskId", "ownerSid", "ownerSession", "serviceInstance", "host", "revision" }) ||
            value.GetProperty("version").GetInt32() != 1 || value.GetProperty("revision").GetInt64() < 1 ||
            value.GetProperty("ownerSid").GetString() != layout.OwnerSid ||
            value.GetProperty("phase").GetString() is not ("off" or "preparing" or "active" or "ending" or "uncertain") ||
            !Id(value.GetProperty("epoch").GetString()) || !Id(value.GetProperty("jid").GetString()) ||
            !Id(value.GetProperty("rid").GetString()))
            throw new InvalidDataException("Existing maintenance metadata is invalid; it was preserved.");
        if (value.GetProperty("phase").GetString() != "off" &&
            (!Id(value.GetProperty("scopeId").GetString()) ||
             !Guid.TryParseExact(value.GetProperty("taskId").GetString(), "D", out _) ||
             value.GetProperty("ownerSession").GetInt32() <= 0 || !Id(value.GetProperty("serviceInstance").GetString())))
            throw new InvalidDataException("Existing maintenance ownership is invalid; it was preserved.");
    }

    private static bool Id(string? value) => value is { Length: 32 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
