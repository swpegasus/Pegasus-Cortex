using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexMcpConnectionsSnapshot(CortexMcpConnectionStore? Store,
    CortexMcpConnectionRequest[] Pending);
internal sealed record CortexMcpConnectionDraft(CortexMcpConnectionRequest Request, string? Warning = null);
internal sealed record CortexMcpConnectionControlResult(bool Confirmed, string? Error = null);

internal interface ICortexMcpConnectionsController
{
    CortexMcpConnectionsSnapshot Read();
    CortexMcpConnectionDraft Create(string name, string application, string? chatLabel);
    CortexMcpConnectionDraft Import(string path);
    Task<CortexMcpConnectionControlResult> ApproveAsync(CortexMcpConnectionRequest request,
        long expectedRevision, bool fullControl, CancellationToken cancellationToken);
    Task<CortexMcpConnectionControlResult> RevokeAsync(string connectionId,
        long expectedRevision, bool fullControlOnly, CancellationToken cancellationToken);
}

internal sealed record CortexMcpConnectionControlSelection(string Action, string Target,
    string Sha256, string ConnectionId, long ExpectedRevision, bool FullControl, string OwnerSid);

/// <summary>One fixed installed-image UAC route. Public enrollment files never contain bearer material.</summary>
internal sealed class CortexMcpConnectionsControl : ICortexMcpConnectionsController
{
    public CortexMcpConnectionsSnapshot Read()
    {
        var installed = CortexMcpSettingsControl.ResolveInstalledHost();
        var store = CortexMcpConnections.ReadForInstallRoot(installed.Root);
        var layout = CurrentLayout(installed.Root);
        var directory = RequestDirectory(layout);
        if (!Directory.Exists(directory)) return new(store, []);
        RequirePrivateDirectory(directory, layout.OwnerSid);
        var files = Directory.EnumerateFileSystemEntries(directory)
            .Take(CortexMcpConnections.MaximumConnections + 1).ToArray();
        if (files.Length > CortexMcpConnections.MaximumConnections)
            throw new InvalidDataException("Too many pending MCP connection requests.");
        var pending = new List<CortexMcpConnectionRequest>();
        foreach (var path in files.Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!CortexMcpConnections.IsId(id) || Path.GetFileName(path) != id + ".json")
                throw new InvalidDataException("Unexpected pending MCP connection entry.");
            RequirePrivateFile(path, layout.OwnerSid);
            var pinned = CortexMcpConnections.ReadPinnedRequest(path);
            if (pinned.Request.ConnectionId != id)
                throw new InvalidDataException("Pending MCP connection identity does not match its filename.");
            pending.Add(pinned.Request);
        }
        return new(store, pending.ToArray());
    }

    public CortexMcpConnectionDraft Create(string name, string application, string? chatLabel)
    {
        var snapshot = Read();
        if ((snapshot.Store?.Connections.Select(item => item.Request.ConnectionId) ?? [])
            .Concat(snapshot.Pending.Select(item => item.ConnectionId)).Distinct(StringComparer.Ordinal).Count()
            >= CortexMcpConnections.MaximumConnections)
            throw new InvalidDataException("The MCP connection limit has been reached.");
        var request = CortexMcpClientCredentials.Create(name, application, chatLabel);
        try { using var retained = Retain(request); return new(request); }
        catch (Exception error) when (Expected(error))
        {
            // The already-created vault credential must not be regenerated after a
            // public-file failure. Return its public coordinates for explicit recovery.
            return new(request, "The local credential was created, but its public request was not retained. " +
                "Keep this window open and copy its connection ID. " + error.GetType().Name);
        }
    }

    public CortexMcpConnectionDraft Import(string path)
    {
        var pinned = CortexMcpConnections.ReadPinnedRequest(path);
        using var retained = Retain(pinned.Request);
        return new(pinned.Request);
    }

    public async Task<CortexMcpConnectionControlResult> ApproveAsync(
        CortexMcpConnectionRequest request, long expectedRevision, bool fullControl,
        CancellationToken cancellationToken)
    {
        try
        {
            request = CortexMcpConnections.Validate(request);
            var installed = CortexMcpSettingsControl.ResolveInstalledHost();
            using var retained = Retain(request);
            var selection = ParseArguments("approve", retained.Path, retained.Sha256,
                request.ConnectionId, expectedRevision.ToString(CultureInfo.InvariantCulture),
                fullControl ? "full" : "ordinary", CurrentOwnerSid());
            var result = await ExecuteAsync(installed.Host, selection, cancellationToken).ConfigureAwait(false);
            if (!result.Confirmed) return result;
            var saved = CortexMcpConnections.ReadForInstallRoot(installed.Root)?.Connections
                .SingleOrDefault(item => item.Request.ConnectionId == request.ConnectionId);
            return saved is { Enabled: true } && saved.Revision == checked(expectedRevision + 1) &&
                saved.FullControl == fullControl && saved.Request == request
                ? new(true)
                : new(false, "The approval result could not be confirmed. Refresh before another action.");
        }
        catch (OperationCanceledException) { return new(false, "Cancelled before the approval helper started."); }
        catch (Exception error) when (Expected(error))
        { return new(false, "Approval was not confirmed: " + error.GetType().Name + ". Refresh before another action."); }
    }

    public async Task<CortexMcpConnectionControlResult> RevokeAsync(
        string connectionId, long expectedRevision, bool fullControlOnly, CancellationToken cancellationToken)
    {
        try
        {
            var installed = CortexMcpSettingsControl.ResolveInstalledHost();
            var before = CortexMcpConnections.ReadForInstallRoot(installed.Root)?.Connections
                .SingleOrDefault(item => item.Request.ConnectionId == connectionId);
            if (before is null || before.Revision != expectedRevision)
                return new(false, "The selected approval changed. Refresh before another action.");
            var selection = ParseArguments(fullControlOnly ? "revoke-full" : "revoke", connectionId,
                "-", connectionId, expectedRevision.ToString(CultureInfo.InvariantCulture),
                fullControlOnly ? "full" : "ordinary", CurrentOwnerSid());
            var result = await ExecuteAsync(installed.Host, selection, cancellationToken).ConfigureAwait(false);
            if (!result.Confirmed) return result;
            var saved = CortexMcpConnections.ReadForInstallRoot(installed.Root)?.Connections
                .SingleOrDefault(item => item.Request.ConnectionId == connectionId);
            return saved is not null && saved.Revision == checked(expectedRevision + 1) &&
                !saved.FullControl && saved.Enabled == (fullControlOnly && before.Enabled)
                ? new(true)
                : new(false, "The revocation result could not be confirmed. Refresh before another action.");
        }
        catch (OperationCanceledException) { return new(false, "Cancelled before the revocation helper started."); }
        catch (Exception error) when (Expected(error))
        { return new(false, "Revocation was not confirmed: " + error.GetType().Name + ". Refresh before another action."); }
    }

    private static async Task<CortexMcpConnectionControlResult> ExecuteAsync(string host,
        CortexMcpConnectionControlSelection selection, CancellationToken token)
    {
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };
        foreach (var argument in Arguments(selection)) start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new(false, "The Windows approval helper did not start.");
            // Page disposal cannot retract a delivered action. Observe this
            // original helper naturally; never cancel it or submit a replacement.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return process.ExitCode == 0 ? new(true) :
                new(false, "Connection change was not confirmed (helper exit " + process.ExitCode + "). Refresh.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { return new(false, "Windows approval was cancelled. The same public request is retained; refresh before choosing again."); }
    }

    internal static string[] Arguments(CortexMcpConnectionControlSelection selection) =>
        ["--mcp-connection-control", selection.Action, selection.Target, selection.Sha256,
            selection.ConnectionId, selection.ExpectedRevision.ToString(CultureInfo.InvariantCulture),
            selection.FullControl ? "full" : "ordinary", selection.OwnerSid];

    internal static CortexMcpConnectionControlSelection ParseArguments(string action, string target,
        string sha, string connectionId, string expectedRevision, string mode, string ownerSid)
    {
        if (!CortexMcpConnections.IsId(connectionId) ||
            !long.TryParse(expectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision is < 0 or long.MaxValue || mode is not ("ordinary" or "full") ||
            ownerSid.Length > 184 || ownerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new InvalidDataException("Invalid MCP connection control selection.");
        try
        {
            if (new SecurityIdentifier(ownerSid).Value != ownerSid)
                throw new InvalidDataException("Invalid owner identity.");
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid owner identity.", error); }
        if (action == "approve")
        {
            if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal) ||
                Path.GetFullPath(target) != target || !Hash(sha))
                throw new InvalidDataException("Invalid public enrollment selection.");
            CortexProductLayout.RequirePathBudget(target);
        }
        else if (action is "revoke" or "revoke-full")
        {
            if (target != connectionId || sha != "-" || revision < 1 ||
                mode != (action == "revoke-full" ? "full" : "ordinary"))
                throw new InvalidDataException("Invalid revocation selection.");
        }
        else throw new InvalidDataException("Unknown MCP connection control action.");
        return new(action, target, sha, connectionId, revision, mode == "full", ownerSid);
    }

    internal static int RunElevated(string action, string target, string sha, string connectionId,
        string expectedRevision, string mode, string ownerSid)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 12;
            var selection = ParseArguments(action, target, sha, connectionId, expectedRevision, mode, ownerSid);
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 4;
            if (identity.User?.Value != selection.OwnerSid || Process.GetCurrentProcess().SessionId <= 0) return 12;
            var installed = CortexMcpSettingsControl.ResolveInstalledHost();
            if (action == "approve")
            {
                var pinned = CortexMcpConnections.ReadPinnedRequest(selection.Target);
                var request = RequirePinnedRequest(selection, pinned.Request, pinned.Sha256);
                CortexMcpConnections.ApproveForInstallRoot(installed.Root, request,
                    selection.FullControl, selection.ExpectedRevision);
            }
            else CortexMcpConnections.RevokeForInstallRoot(installed.Root, selection.ConnectionId,
                selection.ExpectedRevision, action == "revoke-full");
            return 0;
        }
        catch (UnauthorizedAccessException) { return 12; }
        catch (Exception error) when (Expected(error)) { return 8; }
    }

    internal static CortexMcpConnectionRequest RequirePinnedRequest(CortexMcpConnectionControlSelection selection,
        CortexMcpConnectionRequest request, string actualSha256)
    {
        request = CortexMcpConnections.Validate(request);
        if (selection.Action != "approve" || actualSha256 != selection.Sha256 ||
            request.ConnectionId != selection.ConnectionId)
            throw new UnauthorizedAccessException("The selected public request changed.");
        return request;
    }

    private static RetainedRequest Retain(CortexMcpConnectionRequest request)
    {
        request = CortexMcpConnections.Validate(request);
        var installed = CortexMcpSettingsControl.ResolveInstalledHost();
        var layout = CurrentLayout(installed.Root);
        CortexUserStateDirectories.EnsureExecution(layout);
        var directory = RequestDirectory(layout);
        CortexUserStateDirectories.EnsureCurrentUserDirectory(directory);
        RequirePrivateDirectory(directory, layout.OwnerSid);
        var path = Path.Combine(directory, request.ConnectionId + ".json");
        CortexProductLayout.RequirePathBudget(path);
        if (!File.Exists(path))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Take(CortexMcpConnections.MaximumConnections).Count()
                >= CortexMcpConnections.MaximumConnections)
                throw new InvalidDataException("The pending MCP connection limit has been reached.");
            // Public metadata only, private at creation; existing ACLs are only read.
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (bytes.Length > 4096) throw new InvalidDataException("Public enrollment request exceeds its bound.");
            var security = CortexUserStateDirectories.CreateWindowsUserFileSecurity(new SecurityIdentifier(layout.OwnerSid));
            using var output = new FileInfo(path).Create(FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.None, 4096, FileOptions.None, security);
            output.Write(bytes); output.Flush(true);
        }
        RequirePrivateFile(path, layout.OwnerSid);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var pinned = CortexMcpConnections.ReadPinnedRequest(path);
            if (pinned.Request != request) throw new InvalidDataException("A different public request already uses this ID.");
            return new(path, pinned.Sha256, held);
        }
        catch { held.Dispose(); throw; }
    }

    private static CortexProductLayout CurrentLayout(string installRoot)
    {
        var layout = CortexProductLayout.ResolveCurrent();
        if (!StringComparer.OrdinalIgnoreCase.Equals(layout.InstallRoot, installRoot) ||
            layout.OwnerSid != CurrentOwnerSid()) throw new UnauthorizedAccessException("MCP requests require the current owner.");
        layout.RequireExecutionPathsWithinInstallRoot();
        return layout;
    }

    private static string RequestDirectory(CortexProductLayout layout)
    {
        var directory = Path.Combine(layout.TrayStateDirectory, "mcp-requests");
        CortexProductLayout.RequirePathBudget(directory);
        // Reserve the full generated filename before even a new vault credential
        // can be created; every public connection ID has this exact fixed length.
        CortexProductLayout.RequirePathBudget(Path.Combine(directory, new string('0', 32) + ".json"));
        CortexProductLayout.RejectExistingReparseComponents(directory);
        return directory;
    }

    private static string CurrentOwnerSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("Windows owner is unavailable.");
        if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || Process.GetCurrentProcess().SessionId <= 0)
            throw new UnauthorizedAccessException("MCP requests belong to the signed-in Windows user.");
        return sid;
    }

    private static void RequirePrivateDirectory(string path, string sid)
    {
        CortexProductLayout.RequirePathBudget(path);
        CortexProductLayout.RejectExistingReparseComponents(path);
        CortexUserStateDirectories.RequireWindowsUserStateAccess(
            new DirectoryInfo(path).GetAccessControl(), new SecurityIdentifier(sid));
    }

    private static void RequirePrivateFile(string path, string sid)
    {
        CortexProductLayout.RequirePathBudget(path);
        CortexProductLayout.RejectExistingReparseComponents(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Public enrollment request must be an ordinary file.");
        CortexUserStateDirectories.RequireWindowsUserStateAccess(
            new FileInfo(path).GetAccessControl(), new SecurityIdentifier(sid));
    }

    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or Win32Exception or NotSupportedException or System.Text.Json.JsonException or
        System.Security.Cryptography.CryptographicException or OverflowException;

    private sealed record RetainedRequest(string Path, string Sha256, FileStream Held) : IDisposable
    {
        public void Dispose() => Held.Dispose();
    }
}
