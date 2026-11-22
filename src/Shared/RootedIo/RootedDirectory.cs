using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.RootedIo;

/// <summary>Handle-relative I/O. A child retains its complete admitted ancestor chain.</summary>
internal sealed partial class RootedDirectory : IDisposable
{
    private Node? _node;
    private RootedDirectory(Node node) => _node = node;
    internal string Path => Current.Path;
    internal string Name => Current.Name;
    internal FileIdentity Identity => Current.Identity;
    internal RootedDirectory Retain() => new(Current.Retain());
    private Node Current => _node ?? throw new ObjectDisposedException(nameof(RootedDirectory));
    internal static Action<string, string>? TestObserver { get => Observer.Value; set => Observer.Value = value; }
    private static readonly AsyncLocal<Action<string, string>?> Observer = new();

    internal static RootedDirectory OpenRoot(string path, bool createMissing = false)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var root = System.IO.Path.GetPathRoot(path) ?? throw new IOException("The directory has no filesystem root.");
        Node current = NativeOpenRoot(root);
        try
        {
            var relative = System.IO.Path.GetRelativePath(root, path);
            if (relative != ".")
                foreach (var part in Parts(relative))
                {
                    var next = OpenChild(current, part, directory: true, createMissing, exclusive: false, writable: false);
                    current.Release(); current = next;
                }
            var result = new RootedDirectory(current);
            current = null!;
            try { Observe("root_pinned", result.Path); }
            catch { result.Dispose(); throw; }
            return result;
        }
        finally { current?.Release(); }
    }

    internal RootedDirectory OpenDirectory(string relative, bool createMissing = false, bool exclusive = false,
        UnixFileMode mode = (UnixFileMode)0x1ff)
    {
        var current = Current.Retain();
        try
        {
            foreach (var part in Parts(relative))
            {
                var next = OpenChild(current, part, directory: true, createMissing, exclusive, writable: false, mode: (uint)mode);
                current.Release(); current = next;
            }
            var result = new RootedDirectory(current); current = null!;
            return result;
        }
        finally { current?.Release(); }
    }

    internal RootedFile OpenRead(string relative) => OpenFile(relative, create: false, exclusive: false, writable: false, 0x1b6);
    internal RootedFile CreateNew(string relative, UnixFileMode mode = (UnixFileMode)0x1b6) =>
        OpenFile(relative, create: true, exclusive: true, writable: true, (uint)mode);
    internal RootedFile OpenLock(string relative) => OpenFile(relative, create: true, exclusive: false, writable: true, 0x1b6);

    private RootedFile OpenFile(string relative, bool create, bool exclusive, bool writable, uint mode)
    {
        var parts = Parts(relative);
        var current = Current.Retain();
        try
        {
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var next = OpenChild(current, parts[index], directory: true, create: false, exclusive: false, writable: false);
                current.Release(); current = next;
            }
            Observe("parent_pinned", System.IO.Path.Combine(current.Path, parts[^1]));
            return new RootedFile(OpenChild(current, parts[^1], directory: false, create, exclusive, writable, mode: mode), writable);
        }
        finally { current.Release(); }
    }

    internal IReadOnlyList<Entry> Enumerate() => NativeEnumerate(Current);
    internal void SetMode(UnixFileMode mode) => NativeSetMode(Current, mode);
    internal UnixFileMode GetMode() => NativeGetMode(Current);

    internal void MoveNoReplace(string newName)
    {
        ValidateName(newName);
        var node = Current;
        if (node.Parent is null) throw new IOException("A filesystem root cannot be published.");
        Observe("before_publish", node.Path);
        NativeRename(node, newName, replace: false);
        node.Name = newName;
    }

    internal void DeleteOwnedTree(IReadOnlyDictionary<string, FileIdentity> ownedEntries, Action<string>? beforeDelete = null) =>
        DeleteOwnedTree(ownedEntries, string.Empty, beforeDelete);

    private void DeleteOwnedTree(IReadOnlyDictionary<string, FileIdentity> ownedEntries, string prefix, Action<string>? beforeDelete)
    {
        var node = Current;
        foreach (var entry in NativeEnumerate(node))
        {
            var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            if (!ownedEntries.TryGetValue(relative, out var identity) || identity != entry.Identity)
                throw new IOException("Cleanup refused an unowned or replaced archive entry.");
            // Open every child without following links, relative to the retained directory.
            if (entry.Reparse)
            {
                beforeDelete?.Invoke(System.IO.Path.Combine(node.Path, entry.Name));
                NativeDeleteLink(node, entry);
                continue;
            }
            using var child = new RootedDirectory(OpenChild(node, entry.Name, entry.Directory,
                create: false, exclusive: false, writable: false, deletion: true));
            if (child.Identity != identity) throw new IOException("The cleanup child identity changed.");
            if (entry.Directory) child.DeleteOwnedTree(ownedEntries, relative, beforeDelete);
            else
            {
                beforeDelete?.Invoke(child.Path);
                NativeDelete(child.Current);
            }
        }
        beforeDelete?.Invoke(node.Path);
        NativeDelete(node);
    }

    public void Dispose() => Interlocked.Exchange(ref _node, null)?.Release();

    private static Node OpenChild(Node parent, string name, bool directory, bool create, bool exclusive,
        bool writable, bool deletion = false, uint mode = 0x1ff)
    {
        ValidateName(name);
        var node = NativeOpenChild(parent, name, directory, create, exclusive, writable, deletion, mode);
        try { Observe("entry_opened", node.Path); return node; }
        catch { node.Release(); throw; }
    }

    private static string[] Parts(string relative)
    {
        if (string.IsNullOrEmpty(relative) || System.IO.Path.IsPathRooted(relative))
            throw new IOException("A child path must be relative and nonempty.");
        var parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        foreach (var part in parts) ValidateName(part);
        return parts;
    }

    private static void ValidateName(string name)
    {
        if (name is "" or "." or ".." || name.IndexOf('\0') >= 0 ||
            name.IndexOf(System.IO.Path.DirectorySeparatorChar) >= 0 ||
            name.IndexOf(System.IO.Path.AltDirectorySeparatorChar) >= 0 ||
            OperatingSystem.IsWindows() && name.IndexOf(':') >= 0)
            throw new IOException("The child name is not one filesystem component.");
    }

    private static void Observe(string point, string path) => Observer.Value?.Invoke(point, path);
    internal static bool IsMissing(IOException error) => error is FileNotFoundException or DirectoryNotFoundException;
    internal static bool IsCollision(IOException error) => OperatingSystem.IsWindows()
        ? (error.HResult & 0xffff) is 80 or 183 : (error.HResult & 0xffff) == 17;
    internal sealed record Entry(string Name, bool Directory, bool Reparse, FileIdentity Identity);

    internal sealed class Node
    {
        private int _references = 1;
        internal SafeFileHandle Handle { get; }
        internal Node? Parent { get; }
        internal string Name { get; set; }
        internal bool Directory { get; }
        internal FileIdentity Identity { get; }
        internal bool Published { get; set; }
        internal string Path => Parent is null ? Name : System.IO.Path.Combine(Parent.Path, Name);
        internal Node(SafeFileHandle handle, Node? parent, string name, bool directory, FileIdentity identity)
        { Handle = handle; Parent = parent?.Retain(); Name = name; Directory = directory; Identity = identity; }
        internal Node Retain() { Interlocked.Increment(ref _references); return this; }
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            try { Handle.Dispose(); }
            finally { Parent?.Release(); }
        }
    }

    internal readonly record struct FileIdentity(ulong Device, ulong File);

    internal sealed class RootedFile : IDisposable
    {
        private Node? _node;
        private readonly bool _writable;
        private Node Current => _node ?? throw new ObjectDisposedException(nameof(RootedFile));
        internal RootedFile(Node node, bool writable) { _node = node; _writable = writable; }
        internal FileStream OpenStream() => new RetainedStream(Current, _writable ? FileAccess.ReadWrite : FileAccess.Read);
        internal bool WasPublished => Current.Published;
        internal FileIdentity Identity => Current.Identity;
        internal void RequireNamedIdentity() => NativeRequireBinding(Current);
        internal void SetMode(UnixFileMode mode) => NativeSetMode(Current, mode);
        internal UnixFileMode GetMode() => NativeGetMode(Current);
        internal void Publish(string name, bool replace)
        {
            ValidateName(name);
            var node = Current;
            Observe("before_publish", node.Path);
            NativeRename(node, name, replace);
            node.Name = name;
        }
        internal void DeleteIfOwned() { Observe("before_delete", Current.Path); NativeDelete(Current); }
        public void Dispose() => Interlocked.Exchange(ref _node, null)?.Release();
    }

    private sealed class RetainedStream : FileStream
    {
        private Node? _retained;
        internal RetainedStream(Node node, FileAccess access) : base(NativeDuplicate(node.Handle), access, 4096, isAsync: false)
        { _retained = node.Retain(); }
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) Interlocked.Exchange(ref _retained, null)?.Release(); }
        }
        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _retained, null)?.Release(); }
        }
    }
}
