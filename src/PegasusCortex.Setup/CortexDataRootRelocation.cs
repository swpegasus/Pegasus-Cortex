using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Product;

namespace Pegasus.Cortex.Setup;

/// <summary>One explicit, stopped, copy-first data-location transition. Runtime selection remains the registry.</summary>
internal sealed class CortexDataRootRelocation
{
    internal sealed record Entry(string Relative, bool Directory, long Bytes = 0);
    private bool _prepared;
    private bool _published;
    internal CortexProductLayout Source { get; }
    internal CortexProductLayout Destination { get; }
    internal CortexProductLayout Staging { get; }
    internal CortexProductLayout InstallLayout => _published ? Destination : Staging;
    internal bool Published => _published;
    internal int CopiedFiles { get; private set; }
    internal long CopiedBytes { get; private set; }

    private CortexDataRootRelocation(CortexProductLayout source, string destination, CancellationToken token)
    {
        Source = source;
        CortexProductInstallPlanner.RequireDestinationPolicy(source);
        Destination = Select(source, destination);
        if (Same(source.DataRoot, Destination.DataRoot) || Within(source.DataRoot, Destination.DataRoot) ||
            Within(Destination.DataRoot, source.DataRoot))
            throw new InvalidDataException("A data-location copy requires separate nonoverlapping roots.");
        var parent = Path.GetDirectoryName(Destination.DataRoot) ?? throw new IOException("The data destination has no parent.");
        Staging = Select(source, Path.Combine(parent, ".cx-" + Guid.NewGuid().ToString("N")[..8]));
        token.ThrowIfCancellationRequested();
        if (Exists(Destination.DataRoot))
            throw new IOException("The data destination is occupied; it was preserved, not merged or adopted from a receipt.");
    }

    internal static CortexDataRootRelocation Plan(CortexProductLayout source, string destination,
        CancellationToken token = default) => new(source, destination, token);

    internal void RequireInstallLayout(CortexProductLayout layout)
    {
        if (!_prepared || !Same(layout.InstallRoot, Source.InstallRoot) || layout.OwnerSid != Source.OwnerSid ||
            !Same(layout.DataRoot, InstallLayout.DataRoot))
            throw new InvalidOperationException("The data-copy admission does not bind this install layout.");
    }

    internal void Prepare(CancellationToken token = default, Action<CortexProductLayout>? provision = null,
        Action<string>? checkpoint = null)
    {
        if (!Source.IsIsolatedFixture && (provision is not null || checkpoint is not null))
            throw new InvalidOperationException("Injected copy callbacks are restricted to isolated fixtures.");
        token.ThrowIfCancellationRequested();
        if (_prepared) return;
        if (Exists(Staging.DataRoot)) throw new IOException("The create-only data staging location is occupied.");
        if (Exists(Destination.DataRoot)) throw new IOException("The data destination is occupied; existing data was preserved.");
        if (provision is not null) provision(Staging);
        else
        {
            var parent = Path.GetDirectoryName(Staging.DataRoot)!;
            if (!Directory.Exists(parent))
                new DirectoryInfo(parent).Create(CortexProductStateProvisioner.DescribeDirectorySecurity(
                    new(parent, "S-1-5-32-544", CortexDirectoryAccessProfile.StateTraverse)));
            CortexDataParentBoundary.RequireExistingAncestors(parent);
            CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(Staging.DataRoot);
            CortexQuiescentDatabaseSnapshot.CreatePrivateDirectory(Staging.InstallStateDirectory);
        }
        RequirePrivateDirectory(Staging.DataRoot);
        var copiedInstallDirectory = false;
        var ordinaryFiles = 0;
        long ordinaryBytes = 0;
        // One streamed admission pass; only DB families use managed copying.
        // Ordinary bytes are transferred by the fixed OS multi-threaded copier.
        foreach (var entry in Inventory(Source.DataRoot, token, admitCollisions: true))
        {
            token.ThrowIfCancellationRequested();
            _ = Contained(Destination.DataRoot, entry.Relative);
            var target = Contained(Staging.DataRoot, entry.Relative);
            if (entry.Directory)
            {
                RequirePrivateParent(target);
                if (Exists(target) && (!Same(target, Staging.InstallStateDirectory) || copiedInstallDirectory))
                    throw new IOException("A copied directory collides with an existing staged entry.");
                Directory.CreateDirectory(target);
                if (Same(target, Staging.InstallStateDirectory)) copiedInstallDirectory = true;
                continue;
            }
            if (!CortexRobocopyTransfer.IsDatabaseFile(entry.Relative))
            {
                ordinaryFiles++; ordinaryBytes = checked(ordinaryBytes + entry.Bytes);
                continue;
            }
            var primary = CortexRobocopyTransfer.DatabasePrimary(entry.Relative);
            if (primary != entry.Relative && File.Exists(Contained(Source.DataRoot, primary)))
                continue; // Copied with its primary, regardless of enumeration order.
            IEnumerable<string> relatives = primary == entry.Relative
                ? new[] { "", "-wal", "-shm", "-journal" }.Select(suffix => primary + suffix)
                : [entry.Relative];
            var held = new List<(string Relative, FileStream Stream)>(4);
            try
            {
                foreach (var relative in relatives)
                {
                    _ = Contained(Destination.DataRoot, relative);
                    var source = Contained(Source.DataRoot, relative);
                    if (relative != entry.Relative && !File.Exists(source)) continue;
                    held.Add((relative, CortexQuiescentDatabaseSnapshot.OpenRetainedSource(source)));
                }
                foreach (var item in held)
                {
                    token.ThrowIfCancellationRequested();
                    checkpoint?.Invoke("copy");
                    var destination = Contained(Staging.DataRoot, item.Relative);
                    RequirePrivateParent(destination);
                    File.Copy(Contained(Source.DataRoot, item.Relative), destination, overwrite: false);
                    CopiedFiles++; CopiedBytes = checked(CopiedBytes + item.Stream.Length);
                }
            }
            finally { foreach (var item in held) item.Stream.Dispose(); }
        }
        token.ThrowIfCancellationRequested();
        checkpoint?.Invoke("bulk-copy");
        var bulk = CortexRobocopyTransfer.Copy(Source.DataRoot, Staging.DataRoot, token);
        CortexRobocopyTransfer.RequireComplete(bulk);
        CopiedFiles = checked(CopiedFiles + ordinaryFiles);
        CopiedBytes = checked(CopiedBytes + ordinaryBytes);
        RebindRoot(Path.Combine(Staging.InstallStateDirectory, "locations.v1.json"), "DataRoot", Source.DataRoot, Staging.DataRoot);
        _prepared = true;
        checkpoint?.Invoke("prepared");
    }

    internal CortexProductLayout Publish(CancellationToken token = default, Action<string>? checkpoint = null)
    {
        if (!Source.IsIsolatedFixture && checkpoint is not null) throw new InvalidOperationException("Fixture callback refused.");
        if (!_prepared) throw new InvalidOperationException("The data copy is incomplete.");
        token.ThrowIfCancellationRequested();
        if (_published) return Destination;
        RequirePrivateInstallLayout(Staging);
        RebindRoot(Path.Combine(Staging.InstallStateDirectory, "locations.v1.json"), "DataRoot", Staging.DataRoot, Destination.DataRoot);
        RebindRoot(Path.Combine(Staging.InstallStateDirectory, "registration.v1.json"), "DataRoot", Source.DataRoot, Destination.DataRoot);
        var users = Path.Combine(Staging.DataRoot, "users");
        if (Directory.Exists(users))
        foreach (var user in Directory.EnumerateDirectories(users))
        {
            RequirePrivateDirectory(user);
            var path = Path.Combine(user, Path.GetRelativePath(Source.UserStateDirectory, Source.UserConfigDirectory), "geometry-shadow.v1.json");
            if (Exists(path)) RebindResearch(path, Path.GetRelativePath(Staging.DataRoot, path));
        }
        checkpoint?.Invoke("publish");
        FinalizeAccess(token, checkpoint);
        if (Exists(Destination.DataRoot)) throw new IOException("The data destination appeared during preparation.");
        Directory.Move(Staging.DataRoot, Destination.DataRoot);
        _published = true;
        return Destination;
    }

    internal void RequirePublished(CortexProductLayout layout)
    {
        if (!_published || !Same(layout.DataRoot, Destination.DataRoot) || !Same(layout.InstallRoot, Source.InstallRoot))
            throw new InvalidOperationException("Only the complete published data copy may be selected.");
    }

    internal void CommitSelection(Action<CortexProductLayout> select, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(select);
        RequirePublished(Destination);
        token.ThrowIfCancellationRequested();
        // The callback is the one registry selection point. An exception may
        // follow an accepted write, so do not compensate by selecting old data.
        select(Destination);
    }

    private static IEnumerable<Entry> Inventory(string root, CancellationToken token, bool postorder = false, bool admitCollisions = false)
    {
        CortexProductStateProvisioner.RejectExistingReparseComponents(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The registered data source is absent.");
        return WalkEntries("", ReadDirectory, postorder);

        IEnumerable<Entry> ReadDirectory(string relativeDirectory)
        {
            var directory = relativeDirectory.Length == 0 ? root : Contained(root, relativeDirectory);
            HashSet<string>? names = admitCollisions ? new(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, info.FullName);
                _ = Contained(root, relative);
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || names is not null && !names.Add(info.Name))
                    throw new IOException("A data entry is redirected or collides under Windows path rules.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                yield return new Entry(relative, isDirectory, info is FileInfo file ? file.Length : 0);
            }
        }
    }

    internal static IEnumerable<Entry> WalkEntries(string directory,
        Func<string, IEnumerable<Entry>> readDirectory, bool postorder = false)
    {
        foreach (var entry in readDirectory(directory))
        {
            if (!entry.Directory || !postorder) yield return entry;
            if (!entry.Directory) continue;
            foreach (var child in WalkEntries(entry.Relative, readDirectory, postorder)) yield return child;
            if (postorder) yield return entry;
        }
    }

    internal void RequirePrivateInstallLayout(CortexProductLayout layout)
    {
        RequireInstallLayout(layout);
        RequirePrivateDirectory(Staging.DataRoot);
        // Installation may write its protected catalog and receipts, but must
        // not expose copied user leaves before all privileged fixups finish.
        foreach (var entry in CortexProductStateProvisioner.Plan(layout).Entries)
        {
            if (!Within(Staging.DataRoot, entry.Path)) continue;
            RequirePrivateParent(entry.Path);
            Directory.CreateDirectory(entry.Path);
            RequirePrivateDirectory(entry.Path);
        }
    }

    private void RequirePrivateParent(string path)
    {
        if (!Within(Staging.DataRoot, path)) throw new IOException("A staged write escaped its private root.");
        CortexProductStateProvisioner.RejectExistingReparseComponents(path);
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            RequirePrivateDirectory(parent);
            if (Same(parent, Staging.DataRoot)) return;
        }
        throw new IOException("A staged write has no private parent.");
    }

    private void RequirePrivateDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new IOException("A staged directory is absent or redirected.");
        if (!OperatingSystem.IsWindows()) return;
        var security = new DirectoryInfo(path).GetAccessControl();
        if (Source.IsIsolatedFixture)
        {
            // Fixtures run without elevation in their own current-user root.
            using var identity = WindowsIdentity.GetCurrent();
            CortexUserStateDirectories.RequireWindowsUserStateAccess(security, identity.User!);
        }
        else CortexProductStateProvisioner.RequireServiceEntryAccess(security);
    }

    private sealed record PublishedSecurity(byte[] Owner, byte[] Dacl, uint Protection);

    private void FinalizeAccess(CancellationToken token, Action<string>? checkpoint)
    {
        RequirePrivateInstallLayout(Staging);
        var roles = CortexProductStateProvisioner.Plan(Staging).Entries
            .Where(entry => Same(entry.Path, Staging.DataRoot) || Within(Staging.DataRoot, entry.Path))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // Postorder finishes each private subtree before granting access to its
        // root. Never re-enter a published subtree; keep memory proportional to
        // path depth instead of retaining every path/security descriptor.
        using var privilege = Source.IsIsolatedFixture ? null : CortexProductStateProvisioner.BeginSystemOwnedCreation();
        var users = new Dictionary<string, SecurityIdentifier>(StringComparer.Ordinal);
        var userFiles = new Dictionary<string, PublishedSecurity>(StringComparer.Ordinal);
        var userDirectories = new Dictionary<string, PublishedSecurity>(StringComparer.Ordinal);
        var pending = new List<(string Path, PublishedSecurity Security, bool WritableDatabase)>(8);
        foreach (var entry in Inventory(Staging.DataRoot, token, postorder: true).Append(new Entry("", true)))
        {
            token.ThrowIfCancellationRequested();
            var target = entry.Relative.Length == 0 ? Staging.DataRoot : Contained(Staging.DataRoot, entry.Relative);
            PublishedSecurity published;
            var userEntry = TryUser(entry.Relative, out var user, users);
            if (userEntry)
            {
                var logicalUser = user ?? throw new InvalidDataException("A copied user entry has no logical owner.");
                var cache = entry.Directory ? userDirectories : userFiles;
                if (!cache.TryGetValue(logicalUser.Value, out published!))
                {
                    published = PrepareSecurity(entry.Directory ? CortexUserStateDirectories.CreateWindowsUserDirectorySecurity(logicalUser)
                        : CortexUserStateDirectories.CreateWindowsUserFileSecurity(logicalUser));
                    cache.Add(logicalUser.Value, published);
                }
            }
            else
            {
                FileSystemSecurity security;
                if (entry.Directory && roles.TryGetValue(target, out var role)) security = CortexProductStateProvisioner.DescribeDirectorySecurity(role);
                else
                {
                    var original = entry.Relative.Length == 0 ? Source.DataRoot : Contained(Source.DataRoot, entry.Relative);
                    var observed = Exists(original) ? original : target;
                    security = entry.Directory ? new DirectoryInfo(observed).GetAccessControl() : new FileInfo(observed).GetAccessControl();
                }
                if (!Source.IsIsolatedFixture && (entry.Relative.Equals("service", StringComparison.OrdinalIgnoreCase) ||
                    entry.Relative.StartsWith("service" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    CortexProductStateProvisioner.RequireServiceEntryAccess(security);
                if (PublicInstallFile(target)) security = PublicReadFileSecurity();
                published = PrepareSecurity(security);
            }
            if (entry.Directory)
            {
                FlushFiles(); // No assignment survives the subtree's publication barrier.
                checkpoint?.Invoke("directory-acl");
                AssignExactSecurity(target, published);
            }
            else
            {
                pending.Add((target, published, userEntry && CortexRobocopyTransfer.IsDatabaseFile(target)));
                if (pending.Count == 8) FlushFiles();
            }
        }

        void FlushFiles()
        {
            if (pending.Count == 0) return;
            Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, item =>
            {
                checkpoint?.Invoke("file-acl-start");
                try
                {
                    if (item.WritableDatabase) File.SetAttributes(item.Path, File.GetAttributes(item.Path) & ~FileAttributes.ReadOnly);
                    AssignExactSecurity(item.Path, item.Security);
                }
                finally { checkpoint?.Invoke("file-acl-end"); }
            });
            pending.Clear();
        }
    }

    private static bool TryUser(string relative, out SecurityIdentifier? user, Dictionary<string, SecurityIdentifier> cache)
    {
        user = null;
        var prefix = "users" + Path.DirectorySeparatorChar;
        if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = relative.AsSpan(prefix.Length);
        var separator = rest.IndexOf(Path.DirectorySeparatorChar);
        var sid = (separator >= 0 ? rest[..separator] : rest).ToString();
        if (cache.TryGetValue(sid, out user)) return true;
        user = new SecurityIdentifier(sid);
        if (!user.IsAccountSid() || user.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            user.IsWellKnown(WellKnownSidType.LocalServiceSid) || user.IsWellKnown(WellKnownSidType.NetworkServiceSid))
            throw new InvalidDataException("A copied user directory has no ordinary account SID.");
        cache.Add(sid, user);
        return true;
    }

    private bool PublicInstallFile(string path) => Same(path, Staging.ActiveCatalogPath);

    private static FileSecurity PublicReadFileSecurity()
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),
            FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    private PublishedSecurity PrepareSecurity(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("A copied entry has no owner or access list.");
        // A medium fixture cannot assign SYSTEM/Admin owners; it still exercises
        // the exact-handle DACL publication, using its real current-user owner.
        var owner = descriptor.Owner;
        if (Source.IsIsolatedFixture)
        {
            using var identity = WindowsIdentity.GetCurrent();
            owner = identity.User!;
        }
        var sid = new byte[owner.BinaryLength]; owner.GetBinaryForm(sid, 0);
        var acl = new byte[descriptor.DiscretionaryAcl.BinaryLength]; descriptor.DiscretionaryAcl.GetBinaryForm(acl, 0);
        var isProtected = (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;
        if (!isProtected)
        {
            // The destination parent is still private here. Materialize source
            // inherited grants before Windows recomputes destination inheritance,
            // or its public reader can be lost. Keep the unprotected setting.
            var explicitAcl = new RawAcl(acl, 0);
            for (var index = 0; index < explicitAcl.Count; index++)
            {
                var ace = explicitAcl[index];
                ace.AceFlags &= ~AceFlags.Inherited;
                explicitAcl[index] = ace;
            }
            explicitAcl.GetBinaryForm(acl, 0);
        }
        return new PublishedSecurity(sid, acl, isProtected ? 0x80000000u : 0x20000000u);
    }

    private static void AssignExactSecurity(string path, PublishedSecurity security)
    {
        using var handle = CortexQuiescentDatabaseSnapshot.OpenRetainedEntry(path, 0x02000000);
        // MAXIMUM_ALLOWED makes SetSecurityInfo non-propagating: an ancestor's
        // final ACL must never recurse through a child already exposed to its user.
        var error = SetSecurityInfo(handle, 1, 5u | security.Protection, security.Owner, 0, security.Dacl, 0);
        if (error != 0) throw new IOException("A copied entry's access could not be published.", new System.ComponentModel.Win32Exception((int)error));
    }

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, uint objectType, uint information,
        byte[] owner, nint group, byte[] dacl, nint sacl);

    private static CortexProductLayout Select(CortexProductLayout source, string root) => source.IsIsolatedFixture
        ? CortexProductLayout.ForIsolatedRoots(source.InstallRoot, root, source.OwnerSid) : source.WithDataRoot(root);

    private void RebindRoot(string path, string field, string oldRoot, string newRoot)
    {
        RequirePrivateParent(path);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("A location record exceeds its bound.");
        var node = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? throw new InvalidDataException("A location record is invalid.");
        if (node[field] is { } prior && !Same(prior.GetValue<string>(), oldRoot))
            throw new InvalidDataException("A copied location record does not bind the expected source.");
        node[field] = newRoot;
        WriteJson(path, node);
    }

    private void RebindResearch(string path, string relative)
    {
        RequirePrivateParent(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("The research configuration exceeds its bound.");
        JsonObject? node;
        try { node = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject; }
        catch (JsonException) { return; } // Preserve pre-existing optional invalid configuration.
        if (node?["researchRoot"] is not JsonValue value || !value.TryGetValue<string>(out var old) ||
            node["generation"] is not JsonValue generation || !generation.TryGetValue<string>(out var text) ||
            !Guid.TryParse(text, out var id)) return;
        var directory = Path.GetDirectoryName(relative)!;
        if (Same(old, Path.Combine(Source.DataRoot, directory, "geometry-shadow", id.ToString("N"))))
        {
            node["researchRoot"] = Path.Combine(Destination.DataRoot, directory, "geometry-shadow", id.ToString("N"));
            WriteJson(path, node);
        }
    }

    private void WriteJson(string path, JsonNode node)
    {
        RequirePrivateParent(path);
        var attributes = File.Exists(path) ? File.GetAttributes(path) : FileAttributes.Normal;
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, node); stream.Flush(true);
        }
        finally { if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes); }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(root, path) || path.Length > CortexProductLayout.MaximumWindowsPathCharacters)
            throw new IOException("A data-copy path escaped its root or exceeded the Windows path budget.");
        return path;
    }
    private static bool Same(string? left, string? right) => left is not null && right is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string root, string path) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
