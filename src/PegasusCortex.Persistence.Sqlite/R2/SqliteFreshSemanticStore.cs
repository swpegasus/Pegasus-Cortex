using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Pegasus.Cortex.Contracts.R2;
using Pegasus.Cortex.Persistence.Sqlite.Configuration;

namespace Pegasus.Cortex.Persistence.Sqlite.R2;

/// <summary>
/// Explicit Windows-local R2 bootstrap. There is no installed-root lookup, V2 initializer,
/// migration, payload writer, or transport registration in this type.
/// </summary>
public sealed class SqliteFreshSemanticStore : IAsyncDisposable
{
    public const string DatabaseFileName = "cortex-r2.db";
    private const string ClaimName = ".cortex-r2-create.lock";
    private const long MaximumBootstrapBytes = 1024 * 1024;
    private readonly SqliteConnection _connection;
    private readonly FileStream _custody;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CortexSemanticBaselineV1 _expected;
    private int _disposed;

    private SqliteFreshSemanticStore(string path, SqliteConnection connection,
        FileStream custody, CortexSemanticBaselineV1 expected)
    {
        DatabasePath = path;
        _connection = connection;
        _custody = custody;
        _expected = expected;
    }

    public string DatabasePath { get; }
    public CortexSemanticBaselineV1 Baseline => _expected;

    /// <summary>
    /// The caller supplies an existing, empty dedicated directory. A private staging file
    /// is published without replacement only after its transaction and verification finish.
    /// Cancellation is honored before publication; a published creation is never reported as canceled.
    /// </summary>
    public static async Task<SqliteFreshSemanticStore> CreateNewAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        RequireWindows();
        cancellationToken.ThrowIfCancellationRequested();
        var root = ValidateRoot(directory);
        var target = Path.Combine(root, DatabaseFileName);
        var stagePath = Path.Combine(root, ".r2-" + Guid.NewGuid().ToString("N") + ".db");
        SqliteLocalPathPolicy.ValidateNewBackupPath(target);
        SqliteLocalPathPolicy.ValidateNewBackupPath(stagePath);
        RequireEntries(root);
        // DeleteOnClose removes this exact claim object, not a later file at the same path.
        using var claim = new FileStream(Path.Combine(root, ClaimName), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        RequireEntries(root, ClaimName);
        var baseline = new CortexSemanticBaselineV1(CortexSemanticBaselineV1.ContractName,
            CortexSemanticBaselineV1.Generation, CortexSemanticBaselineV1.Version,
            Guid.NewGuid(), DateTimeOffset.UtcNow, SqliteFreshSemanticSchema.Fingerprint);
        FileStream? stage = null;
        var published = false;
        var promoted = false;
        try
        {
            stage = Native.CreateStage(stagePath);
            var physicalIdentity = Native.Identity(stage.SafeFileHandle);
            await using (var connection = Connection(stagePath, SqliteOpenMode.ReadWrite))
            {
                // The retained initial handle shares DELETE to remain compatible with SQLite's VFS.
                // Once SQLite opens, its non-delete-sharing handle prevents replacement. Verify
                // it reached our original staged object before issuing any schema/configuration SQL.
                Native.RequirePathIdentity(stagePath, physicalIdentity);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                Native.RequirePathIdentity(stagePath, physicalIdentity);
                await ExecuteAsync(connection, null,
                    "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;",
                    cancellationToken).ConfigureAwait(false);
                using var transaction = connection.BeginTransaction();
                await ExecuteAsync(connection, transaction,
                    $"PRAGMA application_id={SqliteFreshSemanticSchema.ApplicationId}; PRAGMA user_version={CortexSemanticBaselineV1.Version};",
                    cancellationToken).ConfigureAwait(false);
                foreach (var sql in SqliteFreshSemanticSchema.Definitions.Values)
                    await ExecuteAsync(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO cortex_r2_meta
                        (singleton, contract, schema_generation, schema_version, store_instance_id, created_utc, schema_sha256)
                    VALUES (1, $contract, $generation, $version, $instance, $created, $pin);
                    """;
                insert.Parameters.AddWithValue("$contract", baseline.Contract);
                insert.Parameters.AddWithValue("$generation", baseline.SchemaGeneration);
                insert.Parameters.AddWithValue("$version", baseline.SchemaVersion);
                insert.Parameters.AddWithValue("$instance", baseline.StoreInstanceId.ToString("N"));
                insert.Parameters.AddWithValue("$created", baseline.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$pin", baseline.SchemaSha256);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await VerifyAsync(connection, baseline, cancellationToken).ConfigureAwait(false);
            }
            // ReOpenFile acquires DELETE on the same retained object, never by a pathname
            // that could now refer to another file. SQLite is closed before this promotion.
            var publicationCustody = Native.Promote(stage.SafeFileHandle);
            stage.Dispose();
            stage = publicationCustody;
            promoted = true;
            if (Native.Identity(stage.SafeFileHandle) != physicalIdentity)
                throw new IOException("The staged R2 object identity changed.");
            Native.RequirePathIdentity(stagePath, physicalIdentity);
            stage.Flush(flushToDisk: true);
            RequireNoSidecars(stagePath);
            ValidateRoot(root);
            RequireEntries(root, ClaimName, Path.GetFileName(stagePath));
            cancellationToken.ThrowIfCancellationRequested();
            // Rename by our retained DELETE-capable handle: never move/adopt a substituted path.
            Native.Publish(stage.SafeFileHandle, target);
            published = true;
            stage.Dispose();
            stage = null;
            // Publication is the commit boundary. Finish admission even if cancellation now arrives.
            return await OpenCoreAsync(target, baseline, physicalIdentity, allowOwnClaim: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            if (published)
                throw new CortexSemanticBaselineCreationException(
                    published: true, cleanupCompleted: false, baseline, error);
            if (stage is not null)
            {
                try
                {
                    // Preserve an abnormal SQLite journal as recovery evidence; never sweep the directory.
                    RequireNoSidecars(stagePath);
                    if (!promoted)
                    {
                        var cleanupCustody = Native.Promote(stage.SafeFileHandle);
                        stage.Dispose();
                        stage = cleanupCustody;
                    }
                    Native.DeleteOwned(stage.SafeFileHandle);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    throw new CortexSemanticBaselineCreationException(
                        published: false, cleanupCompleted: false, baseline, new AggregateException(error, cleanup));
                }
            }
            throw;
        }
        finally
        {
            stage?.Dispose();
        }
    }

    /// <summary>
    /// Opens only the explicit expected R2 generation, read-only. No configuration or repair
    /// pragma is issued against an existing file, and no connection can create a missing file.
    /// </summary>
    public static Task<SqliteFreshSemanticStore> OpenVerifiedAsync(
        string databasePath, CortexSemanticBaselineV1 expected,
        CancellationToken cancellationToken = default) =>
        OpenCoreAsync(databasePath, expected, physicalIdentity: null, allowOwnClaim: false, cancellationToken);

    private static async Task<SqliteFreshSemanticStore> OpenCoreAsync(string databasePath,
        CortexSemanticBaselineV1 expected, Native.FileIdentity? physicalIdentity, bool allowOwnClaim,
        CancellationToken cancellationToken)
    {
        RequireWindows();
        ArgumentNullException.ThrowIfNull(expected);
        expected.Validate();
        if (expected.SchemaSha256 != SqliteFreshSemanticSchema.Fingerprint)
            throw new InvalidDataException("The expected R2 schema fingerprint is unsupported.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetFileName(databasePath) != DatabaseFileName)
            throw new ArgumentException("Verified reopen requires the explicit R2 database filename.", nameof(databasePath));
        // Existing generic helpers can probe File.Exists before their own drive check.
        // R2 therefore performs string/root/local-drive admission first in BOTH entry paths.
        var path = PreflightLocalPath(databasePath);
        var root = ValidateRoot(Path.GetDirectoryName(path)!);
        SqliteLocalPathPolicy.ValidateExistingBackupPath(path);
        RequireNoSidecars(path);
        RequireEntries(root, allowOwnClaim ? [DatabaseFileName, ClaimName] : [DatabaseFileName]);
        // Windows sharing denies replacement AND writes while SQLite verifies and uses this file.
        var custody = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        SqliteConnection? connection = null;
        try
        {
            var observed = Native.Identity(custody.SafeFileHandle);
            if (physicalIdentity is not null && observed != physicalIdentity.Value)
                throw new IOException("The published R2 file identity changed.");
            if (custody.Length is < 100 or > MaximumBootstrapBytes)
                throw new InvalidDataException("The R2 bootstrap file exceeds its admitted size.");
            // ReadOnly SQLite may still create WAL/SHM for WAL-mode input. Admit the
            // rollback-journal header and generation BEFORE letting SQLite open any candidate.
            RequireHeader(custody);
            RequireNoSidecars(path);
            connection = Connection(path, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Both settings are connection-local, including on a read-only connection.
            await ExecuteAsync(connection, null, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;",
                cancellationToken).ConfigureAwait(false);
            await VerifyAsync(connection, expected, cancellationToken).ConfigureAwait(false);
            return new(path, connection, custody, expected);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            custody.Dispose();
            throw;
        }
    }

    public async ValueTask<CortexSemanticBaselineSnapshotV1> InspectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await VerifyAsync(_connection, _expected, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        finally { _custody.Dispose(); _gate.Release(); }
    }

    private static async Task<CortexSemanticBaselineSnapshotV1> VerifyAsync(SqliteConnection connection,
        CortexSemanticBaselineV1 expected, CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, "PRAGMA application_id;", cancellationToken).ConfigureAwait(false) !=
                SqliteFreshSemanticSchema.ApplicationId ||
            await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false) !=
                CortexSemanticBaselineV1.Version)
            throw new InvalidDataException("The file is not the supported R2 bootstrap generation.");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, type, sql, tbl_name FROM sqlite_schema ORDER BY name LIMIT 33;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var objects = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (++objects > 32) throw new InvalidDataException("The R2 schema object count exceeded its bound.");
                var name = reader.GetString(0);
                // Do not use LIKE 'sqlite_%': '_' is a wildcard and hides ordinary extra tables.
                // Only SQLite's automatic backing indexes for our exact DDL are internal objects here.
                if (reader.GetString(1) == "index" && name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal) &&
                    reader.IsDBNull(2) && SqliteFreshSemanticSchema.Definitions.ContainsKey(reader.GetString(3)))
                    continue;
                if (!names.Add(name) || !SqliteFreshSemanticSchema.Definitions.TryGetValue(name, out var sql) ||
                    reader.GetString(1) != "table" || reader.IsDBNull(2) || reader.GetString(2) != sql.Trim())
                    throw new InvalidDataException("Unexpected or changed R2 schema object.");
            }
            if (names.Count != SqliteFreshSemanticSchema.Definitions.Count)
                throw new InvalidDataException("The R2 schema is incomplete.");
        }
        CortexSemanticBaselineV1 actual;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT singleton, contract, schema_generation, schema_version, store_instance_id, created_utc, schema_sha256
                FROM cortex_r2_meta LIMIT 2;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 1 ||
                !Guid.TryParseExact(reader.GetString(4), "N", out var instance) ||
                !DateTimeOffset.TryParseExact(reader.GetString(5), "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var created))
                throw new InvalidDataException("The R2 baseline identity is missing or malformed.");
            actual = new(reader.GetString(1), reader.GetString(2), reader.GetInt32(3), instance,
                created, reader.GetString(6));
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The R2 baseline has multiple identities.");
        }
        try { actual.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Malformed stored R2 identity.", error); }
        if (actual != expected)
            throw new InvalidDataException("The R2 store identity does not match the expected generation.");
        var snapshot = new CortexSemanticBaselineSnapshotV1(actual,
            await ScalarAsync(connection, "SELECT count(*) FROM cortex_r2_projects;", cancellationToken).ConfigureAwait(false),
            await ScalarAsync(connection, "SELECT count(*) FROM cortex_r2_chats;", cancellationToken).ConfigureAwait(false),
            await ScalarAsync(connection, "SELECT count(*) FROM cortex_r2_tasks;", cancellationToken).ConfigureAwait(false),
            await ScalarAsync(connection, "SELECT count(*) FROM cortex_r2_record_identities;", cancellationToken).ConfigureAwait(false));
        // No phase-one API writes these rows. Populated stores require a later explicit contract.
        if (!snapshot.IsEmpty)
            throw new InvalidDataException("Phase-one reopen does not admit populated semantic identity tables.");
        return snapshot;
    }

    private static void RequireHeader(FileStream custody)
    {
        Span<byte> header = stackalloc byte[100];
        custody.Position = 0;
        custody.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8) || header[18] != 1 || header[19] != 1 ||
            BinaryPrimitives.ReadUInt32BigEndian(header[60..64]) != CortexSemanticBaselineV1.Version ||
            BinaryPrimitives.ReadUInt32BigEndian(header[68..72]) != SqliteFreshSemanticSchema.ApplicationId)
            throw new InvalidDataException("The candidate is not a supported rollback-journal R2 header; no SQLite open occurred.");
    }

    private static SqliteConnection Connection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 5
    }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string PreflightLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != path ||
            path.Length > 240)
            throw new ArgumentException("An explicit, canonical, bounded local path is required.", nameof(path));
        var volumeRoot = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(volumeRoot))
            throw new ArgumentException("A local volume root is required.", nameof(path));
        var relative = path[volumeRoot.Length..];
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            var basename = part.Split('.')[0];
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                Encoding.UTF8.GetByteCount(part) > 255 || part.Any(character =>
                    char.IsControl(character) || "<>:\"|?*".Contains(character)) ||
                new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(basename, StringComparer.OrdinalIgnoreCase) ||
                basename.Length == 4 && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && basename[3] is >= '1' and <= '9')
                throw new ArgumentException("The R2 path contains an unsafe Windows segment.", nameof(path));
        }
        // Match existing local-store drive policy, before Directory.Exists/File.Exists
        // or any helper that can probe the filesystem. Do not experimentally contact mapped drives.
        var driveType = new DriveInfo(volumeRoot).DriveType;
        if (driveType is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
            throw new ArgumentException("R2 storage requires local storage.", nameof(path));
        return path;
    }

    private static string ValidateRoot(string directory)
    {
        var root = PreflightLocalPath(directory);
        if (Path.Combine(root, ".r2-" + new string('0', 32) + ".db-journal").Length > 240)
            throw new ArgumentException("The R2 storage path exceeds its complete staging/sidecar path budget.", nameof(directory));
        if (!Directory.Exists(root))
            throw new ArgumentException("The caller must supply an existing empty dedicated directory.", nameof(directory));
        SqliteLocalPathPolicy.EnsureExistingAncestorsAreNotReparsePoints(root);
        return root;
    }

    private static void RequireEntries(string root, params string[] allowed)
    {
        var entries = Directory.EnumerateFileSystemEntries(root).Take(allowed.Length + 1).ToArray();
        if (entries.Any(path => !allowed.Contains(Path.GetFileName(path), StringComparer.Ordinal)) ||
            entries.Length > allowed.Length)
            throw new IOException("R2 storage requires its own empty or exactly qualified directory.");
    }

    private static void RequireNoSidecars(string path)
    {
        SqliteLocalPathPolicy.EnsureSidecarsAbsent(path, "The R2 bootstrap");
        SqliteLocalPathPolicy.EnsureFileEntryIsSafe(path + "-journal", "The R2 bootstrap journal");
        if (File.Exists(path + "-journal"))
            throw new IOException("The R2 bootstrap has an unresolved journal; no recovery is automatic.");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This local provider requires Windows file-handle custody.");
    }

    private static class Native
    {
        internal readonly record struct FileIdentity(uint Volume, uint High, uint Low);
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { internal uint Low; internal uint High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal FileTime Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Disposition { internal uint Delete; }

        internal static FileStream CreateStage(string path)
        {
            // SQLite's Windows VFS does not share DELETE access. Initial custody is RW,
            // sharing R/W/Delete; promotion happens only after SQLite closes.
            var handle = CreateFile(path, 0xC0000000, 7, IntPtr.Zero, 1, 0x80, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Error("R2 staging creation failed."); handle.Dispose(); throw error; }
            try { return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false); }
            catch { handle.Dispose(); throw; }
        }

        internal static FileStream Promote(SafeFileHandle original)
        {
            var handle = ReopenFile(original, 0xC0010000, 3, 0);
            if (handle.IsInvalid) { var error = Error("R2 exact-object custody promotion failed."); handle.Dispose(); throw error; }
            try { return new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false); }
            catch { handle.Dispose(); throw; }
        }

        internal static void RequirePathIdentity(string path, FileIdentity expected)
        {
            using var observation = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            if (Identity(observation.SafeFileHandle) != expected)
                throw new IOException("The R2 staging pathname no longer selects its created file.");
        }

        internal static FileIdentity Identity(SafeFileHandle handle)
        {
            if (!GetInformation(handle, out var information)) throw Error("R2 file identity is unavailable.");
            if ((information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                information.Links != 1)
                throw new IOException("R2 storage requires an ordinary, single-link file.");
            return new(information.Volume, information.IndexHigh, information.IndexLow);
        }

        internal static void Publish(SafeFileHandle handle, string target)
        {
            var name = Encoding.Unicode.GetBytes(target);
            var lengthOffset = 2 * IntPtr.Size;
            var nameOffset = lengthOffset + sizeof(uint);
            var size = checked(nameOffset + name.Length + sizeof(char));
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buffer, size);
                // ReplaceIfExists/Flags=0 and RootDirectory=null: never replace a destination.
                Marshal.WriteInt32(buffer, lengthOffset, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
                if (!SetInformation(handle, 3, buffer, (uint)size)) throw Error("R2 atomic publication was refused.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static void DeleteOwned(SafeFileHandle handle)
        {
            var disposition = new Disposition { Delete = 1 };
            if (!SetDisposition(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
                throw Error("R2 staging cleanup was not confirmed.");
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "ReOpenFile", SetLastError = true)]
        private static extern SafeFileHandle ReopenFile(SafeFileHandle original, uint access, uint sharing, uint flags);
        private static Win32Exception Error(string message) => new(Marshal.GetLastWin32Error(), message);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetInformation(SafeFileHandle handle, out FileInformation information);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformation(SafeFileHandle handle, int kind, IntPtr information, uint size);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref Disposition information, uint size);
    }
}

/// <summary>Distinguishes an unpublished cleanup problem from a published creation needing inspection.</summary>
public sealed class CortexSemanticBaselineCreationException : IOException
{
    public CortexSemanticBaselineCreationException(bool published, bool cleanupCompleted,
        CortexSemanticBaselineV1 baseline, Exception inner)
        : base(published
            ? "An R2 baseline was published but final admission failed; inspect it without replaying creation."
            : "R2 staging cleanup is unconfirmed; preserve the isolated directory for explicit inspection.", inner)
    {
        Published = published;
        CleanupCompleted = cleanupCompleted;
        Baseline = baseline;
    }

    public bool Published { get; }
    public bool CleanupCompleted { get; }
    public CortexSemanticBaselineV1 Baseline { get; }
}
