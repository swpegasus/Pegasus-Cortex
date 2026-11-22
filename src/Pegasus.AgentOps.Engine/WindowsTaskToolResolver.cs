using System.Security.Cryptography;
using System.Text.Json;

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Creation-time registered-tool admission only. Task Scheduler supplies the later
/// environment and launches directly; this resolver does not install a runner or
/// promise a future image hash check, registered environment replay, or job control.
/// </summary>
internal sealed class WindowsTaskToolResolver : IWindowsTaskToolResolver
{
    private readonly ToolRecipeOperations _tools;
    private readonly Func<string, string> _resolveWorkspace;
    private readonly OwnerHostIdentity _owner;

    internal WindowsTaskToolResolver(ToolRecipeOperations tools, Func<string, string> resolveWorkspace,
        string ownerSid, Func<LocalPeer> current)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _resolveWorkspace = resolveWorkspace ?? throw new ArgumentNullException(nameof(resolveWorkspace));
        _owner = new OwnerHostIdentity(ownerSid, current);
    }

    public WindowsTaskPreparedTool Prepare(WindowsTaskToolRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        RequireOwner();
        if (request.EnvironmentPolicy != "scheduler" || request.Argv is null ||
            (request.DefinitionVersion is null) != (request.DefinitionSha is null) ||
            request.DefinitionVersion is < 1 || request.DefinitionSha is not null && !IsSha(request.DefinitionSha))
            throw new AgentOpsException("invalid");
        var definition = _tools.Tools.Get(request.Tool);
        if (definition.Mode != ToolExecutionMode.User || definition.UsesInstalledPowerShell ||
            !definition.ProbeVerified || !IsSha(definition.AdmissionSha256))
            throw new AgentOpsException("precondition");
        var digest = DefinitionDigest(definition);
        if (request.DefinitionVersion is not null &&
            (request.DefinitionVersion != definition.DefinitionVersion || request.DefinitionSha != digest))
            throw new AgentOpsException("precondition");

        // These values are subsequently interpreted by Task Scheduler, which expands
        // environment and event-value references even though no shell is involved.
        RequireLiteral(definition.ExecutablePath);
        ToolRecipeValidation.ValidateArgumentVector(definition, request.Argv);
        var arguments = request.Argv.ToArray();
        foreach (var argument in arguments) RequireLiteral(argument);
        string? workspace = null, cwd = null;
        switch (definition.WorkingDirectoryRule)
        {
            case ToolWorkingDirectoryRule.Caller:
                if (request.Workspace is not null) throw new AgentOpsException("invalid");
                cwd = AbsoluteDirectory(request.Cwd);
                break;
            case ToolWorkingDirectoryRule.Workspace:
                if (request.Workspace is null || request.Workspace.Length is < 1 or > 32 ||
                    request.Workspace.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                    throw new AgentOpsException("invalid");
                workspace = AbsoluteDirectory(_resolveWorkspace(request.Workspace));
                cwd = request.Cwd is null ? null : AbsoluteDirectory(request.Cwd);
                break;
            case ToolWorkingDirectoryRule.Fixed:
            case ToolWorkingDirectoryRule.ExecutableDirectory:
                if (request.Workspace is not null || request.Cwd is not null) throw new AgentOpsException("invalid");
                break;
            default:
                throw new AgentOpsException("invalid");
        }
        token.ThrowIfCancellationRequested();
        RequireOwner();
        var invocation = _tools.PrepareAdmittedTool(definition, arguments, workspaceRoot: workspace, cwd: cwd);
        RequireLiteral(invocation.WorkingDirectory);
        token.ThrowIfCancellationRequested();
        RequireOwner();
        IDisposable? lease = ToolRecipeValidation.AcquireInvocationLease(invocation);
        try
        {
            token.ThrowIfCancellationRequested();
            RequireOwner();
            // Do not publish evidence for a definition replaced while admission ran.
            if (DefinitionDigest(_tools.Tools.Get(request.Tool)) != digest) throw new AgentOpsException("precondition");
            token.ThrowIfCancellationRequested();
            RequireOwner();
            var prepared = new WindowsTaskPreparedTool(invocation,
                new WindowsTaskToolPin(definition.Name, definition.DefinitionVersion, digest), lease);
            lease = null;
            return prepared;
        }
        finally { lease?.Dispose(); }
    }

    private void RequireOwner()
    {
        if (!_owner.IsCurrentOwner()) throw new AgentOpsException("owner_denied");
    }

    private static string AbsoluteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new AgentOpsException("invalid");
        RequireLiteral(path);
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { throw new AgentOpsException("invalid"); }
    }

    private static void RequireLiteral(string? value)
    {
        if (value is null || value.Contains('\0') || WindowsTaskOperationContract.HasSchedulerExpansion(value))
            throw new AgentOpsException("invalid");
    }

    private static bool IsSha(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string DefinitionDigest(RegisteredToolDefinition definition)
    {
        // Explicit versioned projection includes every definition policy. Environment
        // names and unordered exit sets are canonical; argv order remains meaningful.
        // Canonical bytes remain private in memory and are cleared after hashing.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            name = definition.Name.ToLowerInvariant(), definition.DefinitionVersion, definition.ExecutablePath,
            definition.ExecutableSha256, definition.AdmissionSha256, definition.Mode, definition.WorkingDirectoryRule,
            definition.FixedWorkingDirectory, definition.TimeoutSeconds,
            expectedExitCodes = definition.ExpectedExitCodes?.Order().ToArray(),
            definition.MaxArguments, definition.MaxArgumentUtf8Bytes, definition.MaxTotalArgumentUtf8Bytes,
            definition.StdinEncoding, definition.StdoutEncoding, definition.StderrEncoding,
            definition.Window, definition.MaxOutputBytes,
            versionProbe = definition.VersionProbe is null ? null : new
            {
                definition.VersionProbe.Arguments,
                expectedExitCodes = definition.VersionProbe.ExpectedExitCodes?.Order().ToArray(),
                definition.VersionProbe.RequiredText
            },
            definition.ProbeVerified, definition.EnvironmentMode, definition.UsesInstalledPowerShell,
            environment = definition.Environment?.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new { key = item.Key.ToUpperInvariant(), item.Value }).ToArray()
        });
        try { return Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
