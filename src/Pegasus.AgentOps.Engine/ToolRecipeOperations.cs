using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public enum ToolExecutionMode { User, System }
public enum ToolWorkingDirectoryRule { Caller, Workspace, ExecutableDirectory, Fixed }
public enum ToolWindowMode { Hidden, Visible }
public enum ToolOutputBehavior { Capture, Discard, StatusOnly }
public enum ToolEnvironmentMode { Replace, InheritWithOverlay }
public enum RecipeActionKind { Tool, SevenZip, Git, Dotnet, PowerShell }
public enum RecipeVariableType { Text, Integer, Boolean, RelativePath, Choice }
public enum RecipePostconditionKind { FileExists, DirectoryExists }

public sealed record ToolProbeDefinition(
    IReadOnlyList<string> Arguments,
    IReadOnlyList<int>? ExpectedExitCodes = null,
    string? RequiredText = null);

public sealed record RegisteredToolDefinition(
    string Name,
    int DefinitionVersion,
    string ExecutablePath,
    string? ExecutableSha256 = null,
    ToolExecutionMode Mode = ToolExecutionMode.User,
    ToolWorkingDirectoryRule WorkingDirectoryRule = ToolWorkingDirectoryRule.Caller,
    string? FixedWorkingDirectory = null,
    int TimeoutSeconds = 30,
    IReadOnlyList<int>? ExpectedExitCodes = null,
    int MaxArguments = 128,
    int MaxArgumentUtf8Bytes = 16 * 1024,
    int MaxTotalArgumentUtf8Bytes = 128 * 1024,
    string StdinEncoding = "none",
    string StdoutEncoding = "utf8",
    string StderrEncoding = "utf8",
    ToolWindowMode Window = ToolWindowMode.Hidden,
    int MaxOutputBytes = 64 * 1024,
    ToolProbeDefinition? VersionProbe = null,
    bool ProbeVerified = false,
    ToolEnvironmentMode EnvironmentMode = ToolEnvironmentMode.Replace,
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    [JsonInclude]
    public string? AdmissionSha256 { get; internal init; }

    // Only bootstrap-owned system PowerShell follows its registered installed
    // location. Public tool registration cannot opt out of executable admission
    // by submitting this marker; custom tools retain their existing pin policy.
    [JsonInclude]
    public bool UsesInstalledPowerShell { get; internal init; }
}

public sealed record RecipeVariableDefinition(
    string Name,
    RecipeVariableType Type = RecipeVariableType.Text,
    bool Required = true,
    string? DefaultValue = null,
    int MaxUtf8Bytes = 4096,
    long? Minimum = null,
    long? Maximum = null,
    IReadOnlyList<string>? Choices = null);

public sealed record RecipePostcondition(
    RecipePostconditionKind Kind,
    string PathTemplate);

public sealed record OneActionRecipeDefinition(
    string Name,
    int DefinitionVersion,
    RecipeActionKind Action,
    string ToolName,
    IReadOnlyList<string>? FixedArguments = null,
    IReadOnlyList<RecipeVariableDefinition>? Variables = null,
    ToolExecutionMode Mode = ToolExecutionMode.User,
    string? Workspace = null,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<int>? ExpectedExitCodes = null,
    string? ExecutableSha256 = null,
    string? ScriptPath = null,
    string? ScriptSha256 = null,
    ToolOutputBehavior OutputBehavior = ToolOutputBehavior.Capture,
    int? MaxOutputBytes = null,
    RecipePostcondition? Postcondition = null);

public sealed record ResolvedRecipePostcondition(
    RecipePostconditionKind Kind,
    string Path,
    string AdmissionRoot);

internal sealed record InvocationFileAdmission(string Path, string Sha256);

/// <summary>
/// An immutable invocation admitted by <see cref="ToolRecipeOperations"/>.
/// The constructor and admission evidence are intentionally internal so callers
/// cannot manufacture a registry-bypassing executable request.
/// </summary>
public sealed class ToolInvocation
{
    internal ToolInvocation(
        Guid admissionOwner,
        string toolName,
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyList<int> expectedExitCodes,
        ToolExecutionMode mode,
        ToolWindowMode window,
        string stdinEncoding,
        string stdoutEncoding,
        string stderrEncoding,
        int maxOutputBytes,
        ProcessEnvironment environment,
        IReadOnlyList<InvocationFileAdmission> fileAdmissions,
        ToolOutputBehavior outputBehavior = ToolOutputBehavior.Capture,
        ResolvedRecipePostcondition? postcondition = null,
        string? recipeName = null)
    {
        AdmissionOwner = admissionOwner;
        ToolName = toolName;
        ExecutablePath = executablePath;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        WorkingDirectory = workingDirectory;
        Timeout = timeout;
        ExpectedExitCodes = Array.AsReadOnly(expectedExitCodes.ToArray());
        Mode = mode;
        Window = window;
        StdinEncoding = stdinEncoding;
        StdoutEncoding = stdoutEncoding;
        StderrEncoding = stderrEncoding;
        MaxOutputBytes = maxOutputBytes;
        Environment = environment;
        FileAdmissions = Array.AsReadOnly(fileAdmissions.ToArray());
        OutputBehavior = outputBehavior;
        Postcondition = postcondition;
        RecipeName = recipeName;
    }

    public string ToolName { get; }
    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string WorkingDirectory { get; }
    public TimeSpan Timeout { get; }
    public IReadOnlyList<int> ExpectedExitCodes { get; }
    public ToolExecutionMode Mode { get; }
    public ToolWindowMode Window { get; }
    public string StdinEncoding { get; }
    public string StdoutEncoding { get; }
    public string StderrEncoding { get; }
    public int MaxOutputBytes { get; }
    public ToolOutputBehavior OutputBehavior { get; }
    public ResolvedRecipePostcondition? Postcondition { get; }
    public string? RecipeName { get; }

    internal Guid AdmissionOwner { get; }
    internal ProcessEnvironment Environment { get; }
    internal IReadOnlyList<InvocationFileAdmission> FileAdmissions { get; }

    internal ToolInvocation AddFileAdmission(string path, string sha256) =>
        Copy(fileAdmissions: [.. FileAdmissions, new InvocationFileAdmission(path, sha256)]);

    internal ToolInvocation WithEnvironment(ProcessEnvironment environment) =>
        Copy(environment: environment);

    internal ToolInvocation WithOutputCapacity(int maximumBytes)
    {
        if (maximumBytes is < 1 or > 1024 * 1024) throw new AgentOpsException("invalid");
        return Copy(maxOutputBytes: maximumBytes);
    }

    internal ToolInvocation WithRecipe(
        TimeSpan timeout,
        IReadOnlyList<int> expectedExitCodes,
        int maxOutputBytes,
        ToolOutputBehavior outputBehavior,
        ResolvedRecipePostcondition? postcondition,
        string recipeName) =>
        Copy(timeout, expectedExitCodes, maxOutputBytes, outputBehavior, postcondition, recipeName);

    private ToolInvocation Copy(
        TimeSpan? timeout = null,
        IReadOnlyList<int>? expectedExitCodes = null,
        int? maxOutputBytes = null,
        ToolOutputBehavior? outputBehavior = null,
        ResolvedRecipePostcondition? postcondition = null,
        string? recipeName = null,
        IReadOnlyList<InvocationFileAdmission>? fileAdmissions = null,
        ProcessEnvironment? environment = null) =>
        new(
            AdmissionOwner,
            ToolName,
            ExecutablePath,
            Arguments,
            WorkingDirectory,
            timeout ?? Timeout,
            expectedExitCodes ?? ExpectedExitCodes,
            Mode,
            Window,
            StdinEncoding,
            StdoutEncoding,
            StderrEncoding,
            maxOutputBytes ?? MaxOutputBytes,
            environment ?? Environment,
            fileAdmissions ?? FileAdmissions,
            outputBehavior ?? OutputBehavior,
            postcondition ?? Postcondition,
            recipeName ?? RecipeName);
}

public sealed record ToolExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    ToolExecutionMode Mode,
    string ToolName,
    string? RecipeName);

/// <summary>
/// A process started and returned bounded execution evidence, but the operation
/// did not satisfy its timeout, exit-code, or postcondition contract.
/// Admission failures that occur before process start remain
/// <see cref="AgentOpsException"/> and do not manufacture process evidence.
/// </summary>
public sealed class ToolExecutionException : Exception
{
    internal ToolExecutionException(
        string code,
        ToolExecutionResult result,
        bool timedOut,
        Exception? inner = null)
        : base(code, inner)
    {
        Code = code;
        Result = result;
        TimedOut = timedOut;
    }

    public string Code { get; }
    public ToolExecutionResult Result { get; }
    public int ExitCode => Result.ExitCode;
    public string Stdout => Result.Stdout;
    public string Stderr => Result.Stderr;
    public bool StdoutTruncated => Result.StdoutTruncated;
    public bool StderrTruncated => Result.StderrTruncated;
    public bool TimedOut { get; }
}

public interface IToolInvocationRunner
{
    Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// User-context runner for prepared invocations. System and visible work is
/// deliberately classified but not impersonated here; the service or tray must
/// dispatch those invocations in their proper context.
/// </summary>
public sealed class AgentOpsInvocationRunner : IToolInvocationRunner
{
    public async Task<ProcessResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Mode == ToolExecutionMode.System) throw new AgentOpsException("service_unavailable");
        if (invocation.Window == ToolWindowMode.Visible) throw new AgentOpsException("user_dispatch_required");
        if (invocation.StdinEncoding != "none" || invocation.StdoutEncoding != "utf8" || invocation.StderrEncoding != "utf8")
            throw new AgentOpsException("precondition");
        using var admissionLease = ToolRecipeValidation.AcquireInvocationLease(invocation);
        return await ProcessRunner.RunAsync(
            invocation.ExecutablePath,
            invocation.Arguments,
            invocation.Timeout,
            invocation.WorkingDirectory,
            invocation.Environment,
            invocation.MaxOutputBytes,
            cancellationToken);
    }
}

public sealed class RegisteredToolRegistry
{
    private const int SchemaVersion = 1;
    private const int MaxDefinitions = 256;
    private readonly string _storePath;
    private readonly object _gate;
    private readonly IToolInvocationRunner _probeRunner;
    private readonly Func<string> _installedPowerShellResolver;

    public RegisteredToolRegistry(string stateRoot, IToolInvocationRunner? probeRunner = null)
        : this(stateRoot, probeRunner, null)
    {
    }

    internal RegisteredToolRegistry(string stateRoot, IToolInvocationRunner? probeRunner,
        Func<string>? installedPowerShellResolver)
    {
        if (!Path.IsPathFullyQualified(stateRoot)) throw new AgentOpsException("invalid");
        stateRoot = Path.GetFullPath(stateRoot);
        PathNormalizer.RejectAbsoluteReparseComponents(stateRoot);
        Directory.CreateDirectory(stateRoot);
        PathNormalizer.RejectAbsoluteReparseComponents(stateRoot);
        _storePath = Path.Combine(stateRoot, "tools.v1.json");
        _gate = VersionedJsonStore.Gate(_storePath);
        _probeRunner = probeRunner ?? new AgentOpsInvocationRunner();
        _installedPowerShellResolver = installedPowerShellResolver ?? (() => OperatingSystem.IsWindows()
            ? PowerShell7ToolBootstrap.ResolveExecutable()
            : throw new PlatformNotSupportedException("Installed PowerShell discovery requires Windows."));
        lock (_gate) _ = ReadDocument();
    }

    public string StorePath => _storePath;

    public IReadOnlyList<RegisteredToolDefinition> List()
    {
        lock (_gate) return ReadDocument().Tools.Select(ResolveInstalledPowerShell)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public RegisteredToolDefinition Get(string name)
    {
        ToolRecipeValidation.ValidateName(name);
        lock (_gate)
        {
            return ReadDocument().Tools.SingleOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, name))
                ?? throw new AgentOpsException("not_found");
        }
    }

    public Task<RegisteredToolDefinition> RegisterAsync(
        RegisteredToolDefinition definition,
        CancellationToken cancellationToken = default)
        => RegisterCoreAsync(definition with { UsesInstalledPowerShell = false }, cancellationToken);

    internal Task<RegisteredToolDefinition> RegisterInstalledPowerShellAsync(
        RegisteredToolDefinition definition, CancellationToken cancellationToken = default)
        => RegisterCoreAsync(definition with
        {
            UsesInstalledPowerShell = true,
            ExecutableSha256 = null,
            AdmissionSha256 = null
        }, cancellationToken);

    private async Task<RegisteredToolDefinition> RegisterCoreAsync(
        RegisteredToolDefinition definition, CancellationToken cancellationToken)
    {
        var normalized = Normalize(definition, allowInstalledPowerShell: definition.UsesInstalledPowerShell);
        normalized = ResolveInstalledPowerShell(normalized);
        lock (_gate) ValidateVersionAdmission(ReadDocument(), normalized);
        var beforeProbeSha = normalized.UsesInstalledPowerShell
            ? null : ToolRecipeValidation.ComputeFileHash(normalized.ExecutablePath);
        ToolRecipeValidation.VerifyHashValue(beforeProbeSha, normalized.ExecutableSha256);

        if (normalized.VersionProbe is not null)
        {
            var probe = normalized.VersionProbe;
            var expected = probe.ExpectedExitCodes is { Count: > 0 } ? probe.ExpectedExitCodes : [0];
            ToolRecipeValidation.ValidateArgumentVector(normalized, probe.Arguments);
            ToolRecipeValidation.ValidateExpectedExits(expected);
            var invocation = new ToolInvocation(
                Guid.Empty,
                normalized.Name,
                normalized.ExecutablePath,
                probe.Arguments.ToArray(),
                Path.GetDirectoryName(normalized.ExecutablePath)!,
                TimeSpan.FromSeconds(normalized.TimeoutSeconds),
                expected.ToArray(),
                ToolExecutionMode.User,
                ToolWindowMode.Hidden,
                "none",
                normalized.StdoutEncoding,
                normalized.StderrEncoding,
                Math.Min(normalized.MaxOutputBytes, 64 * 1024),
                CreateEnvironment(normalized),
                beforeProbeSha is null ? [] : [new InvocationFileAdmission(normalized.ExecutablePath, beforeProbeSha)]);
            var result = await _probeRunner.RunAsync(invocation, cancellationToken);
            if (result.TimedOut) throw new AgentOpsException("timeout");
            if (!expected.Contains(result.ExitCode)) throw new AgentOpsException("external_failed");
            if (probe.RequiredText is not null &&
                !result.Stdout.Contains(probe.RequiredText, StringComparison.Ordinal) &&
                !result.Stderr.Contains(probe.RequiredText, StringComparison.Ordinal))
                throw new AgentOpsException("precondition");
            normalized = normalized with { ProbeVerified = true };
        }

        var admittedSha = normalized.UsesInstalledPowerShell
            ? null : ToolRecipeValidation.ComputeFileHash(normalized.ExecutablePath);
        ToolRecipeValidation.VerifyHashValue(admittedSha, normalized.ExecutableSha256);
        if (normalized.VersionProbe is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(beforeProbeSha, admittedSha))
            throw new AgentOpsException("integrity");
        normalized = normalized with { AdmissionSha256 = admittedSha };

        using (VersionedJsonStore.AcquireMutationLock(_storePath, cancellationToken))
        lock (_gate)
        {
            var document = ReadDocument();
            var tools = document.Tools.ToList();
            var existing = tools.FindIndex(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, normalized.Name));
            ValidateVersionAdmission(document, normalized);
            if (existing >= 0)
            {
                tools[existing] = normalized;
            }
            else
            {
                if (tools.Count >= MaxDefinitions) throw new AgentOpsException("invalid");
                tools.Add(normalized);
            }
            VersionedJsonStore.Write(_storePath, new ToolRegistryDocument(SchemaVersion, tools));
        }
        return normalized;
    }

    internal RegisteredToolDefinition ResolveInstalledPowerShell(RegisteredToolDefinition definition)
    {
        if (!definition.UsesInstalledPowerShell) return definition;
        var executable = _installedPowerShellResolver();
        if (!Path.IsPathFullyQualified(executable) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable), "pwsh.exe") ||
            !File.Exists(executable))
            throw new AgentOpsException("precondition");
        executable = Path.GetFullPath(executable);
        PathNormalizer.RejectAbsoluteReparseComponents(executable);
        return definition with { ExecutablePath = executable };
    }

    internal bool Remove(string name, CancellationToken cancellationToken = default)
    {
        ToolRecipeValidation.ValidateName(name);
        using (VersionedJsonStore.AcquireMutationLock(_storePath, cancellationToken))
        lock (_gate)
        {
            var document = ReadDocument();
            var tools = document.Tools.ToList();
            var removed = tools.RemoveAll(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, name)) == 1;
            if (removed) VersionedJsonStore.Write(_storePath, new ToolRegistryDocument(SchemaVersion, tools));
            return removed;
        }
    }

    private ToolRegistryDocument ReadDocument()
    {
        var document = VersionedJsonStore.Read(_storePath, () => new ToolRegistryDocument(SchemaVersion, []));
        if (document.SchemaVersion != SchemaVersion || document.Tools is null || document.Tools.Count > MaxDefinitions)
            throw new AgentOpsException("integrity");
        var normalized = document.Tools.Select(x => Normalize(x, stored: true)).ToArray();
        if (normalized.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new AgentOpsException("integrity");
        return document with { Tools = normalized };
    }

    private static RegisteredToolDefinition Normalize(RegisteredToolDefinition value, bool stored = false,
        bool allowInstalledPowerShell = false)
    {
        try
        {
            ToolRecipeValidation.ValidateName(value.Name);
            if (value.DefinitionVersion < 1) throw new AgentOpsException("invalid");
            if (!Enum.IsDefined(value.EnvironmentMode)) throw new AgentOpsException("invalid");
            if (!Path.IsPathFullyQualified(value.ExecutablePath)) throw new AgentOpsException("invalid");
            var executable = Path.GetFullPath(value.ExecutablePath);
            var installedPowerShell = value.UsesInstalledPowerShell && (stored || allowInstalledPowerShell);
            if (installedPowerShell &&
                (!StringComparer.OrdinalIgnoreCase.Equals(value.Name, "pwsh") ||
                 value.Mode != ToolExecutionMode.User || value.ExecutableSha256 is not null ||
                 value.AdmissionSha256 is not null))
                throw new AgentOpsException("invalid");
            // A managed installation can remove its former path during servicing.
            // Resolve its current exact location only when selected for use.
            if (!installedPowerShell && !File.Exists(executable))
                throw new AgentOpsException(stored ? "integrity" : "not_found");
            PathNormalizer.RejectAbsoluteReparseComponents(executable);
            var isPowerShell = StringComparer.OrdinalIgnoreCase.Equals(value.Name, "pwsh");
            if (isPowerShell && !installedPowerShell &&
                (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable), "pwsh.exe") ||
                 FileVersionInfo.GetVersionInfo(executable).FileMajorPart < 7))
                throw new AgentOpsException(stored ? "integrity" : "precondition");
            ToolRecipeValidation.ValidateSha(value.ExecutableSha256);
            if (value.TimeoutSeconds is < 1 or > 86_400 || value.MaxArguments is < 1 or > 4096 ||
                value.MaxArgumentUtf8Bytes is < 1 or > 1024 * 1024 ||
                value.MaxTotalArgumentUtf8Bytes < value.MaxArgumentUtf8Bytes || value.MaxTotalArgumentUtf8Bytes > 4 * 1024 * 1024 ||
                value.MaxOutputBytes is < 1 or > 64 * 1024)
                throw new AgentOpsException("invalid");
            var exits = value.ExpectedExitCodes is { Count: > 0 } ? value.ExpectedExitCodes.Distinct().ToArray() : [0];
            var environment = NormalizeEnvironment(value.Environment);
            ToolRecipeValidation.ValidateExpectedExits(exits);
            ToolRecipeValidation.ValidateEncoding(value.StdinEncoding, allowNone: true);
            ToolRecipeValidation.ValidateEncoding(value.StdoutEncoding, allowNone: false);
            ToolRecipeValidation.ValidateEncoding(value.StderrEncoding, allowNone: false);
            if (!value.StdinEncoding.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                !value.StdoutEncoding.Equals("utf8", StringComparison.OrdinalIgnoreCase) ||
                !value.StderrEncoding.Equals("utf8", StringComparison.OrdinalIgnoreCase))
                throw new AgentOpsException("precondition");
            if (value.Mode == ToolExecutionMode.System && value.Window == ToolWindowMode.Visible)
                throw new AgentOpsException("precondition");

            string? fixedCwd = value.FixedWorkingDirectory;
            if (value.WorkingDirectoryRule == ToolWorkingDirectoryRule.Fixed)
            {
                if (string.IsNullOrWhiteSpace(fixedCwd) || !Path.IsPathFullyQualified(fixedCwd))
                    throw new AgentOpsException("invalid");
                fixedCwd = Path.GetFullPath(fixedCwd);
                if (!Directory.Exists(fixedCwd)) throw new AgentOpsException(stored ? "integrity" : "not_found");
                PathNormalizer.RejectAbsoluteReparseComponents(fixedCwd);
            }
            else if (fixedCwd is not null)
            {
                throw new AgentOpsException("invalid");
            }

            ToolProbeDefinition? probe = null;
            if (value.VersionProbe is not null)
            {
                if (value.VersionProbe.Arguments is null) throw new AgentOpsException("invalid");
                ToolRecipeValidation.ValidateVersionProbeArguments(value.VersionProbe.Arguments);
                var probeExits = value.VersionProbe.ExpectedExitCodes is { Count: > 0 }
                    ? value.VersionProbe.ExpectedExitCodes.Distinct().ToArray()
                    : [0];
                ToolRecipeValidation.ValidateExpectedExits(probeExits);
                if (value.VersionProbe.RequiredText is { Length: > 512 } ||
                    value.VersionProbe.RequiredText?.Contains('\0') == true)
                    throw new AgentOpsException("invalid");
                probe = value.VersionProbe with
                {
                    Arguments = value.VersionProbe.Arguments.ToArray(),
                    ExpectedExitCodes = probeExits
                };
            }
            if (isPowerShell)
                probe = new ToolProbeDefinition(["-version"], [0], installedPowerShell ? "PowerShell " : "PowerShell 7.");

            ToolRecipeValidation.ValidateSha(value.AdmissionSha256);
            if (stored)
            {
                if ((!installedPowerShell && value.AdmissionSha256 is null) ||
                    (probe is null && value.ProbeVerified) ||
                    (probe is not null && !value.ProbeVerified))
                    throw new AgentOpsException("integrity");
            }

            return value with
            {
                ExecutablePath = executable,
                ExecutableSha256 = value.ExecutableSha256?.ToLowerInvariant(),
                FixedWorkingDirectory = fixedCwd,
                ExpectedExitCodes = exits,
                StdinEncoding = value.StdinEncoding.ToLowerInvariant(),
                StdoutEncoding = value.StdoutEncoding.ToLowerInvariant(),
                StderrEncoding = value.StderrEncoding.ToLowerInvariant(),
                VersionProbe = probe,
                ProbeVerified = stored && value.ProbeVerified,
                AdmissionSha256 = stored ? value.AdmissionSha256?.ToLowerInvariant() : null,
                UsesInstalledPowerShell = installedPowerShell,
                Environment = environment
            };
        }
        catch (AgentOpsException ex) when (stored && ex.Code != "integrity")
        {
            throw new AgentOpsException("integrity", inner: ex);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new AgentOpsException(stored ? "integrity" : "invalid", inner: ex);
        }
    }

    private static void ValidateVersionAdmission(ToolRegistryDocument document, RegisteredToolDefinition definition)
    {
        var existing = document.Tools.SingleOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, definition.Name));
        if (existing is not null && definition.DefinitionVersion <= existing.DefinitionVersion)
            throw new AgentOpsException("precondition");
    }

    private sealed record ToolRegistryDocument(int SchemaVersion, IReadOnlyList<RegisteredToolDefinition> Tools);

    internal static ProcessEnvironment CreateEnvironment(RegisteredToolDefinition definition)
    {
        switch (definition.EnvironmentMode)
        {
            case ToolEnvironmentMode.Replace:
                var environment = new Dictionary<string, string?>(definition.Environment ?? EmptyEnvironment,
                    StringComparer.OrdinalIgnoreCase);
                // Old admitted definitions predate this fixed runtime default.
                // Preserve explicit policies and avoid importing ambient values.
                environment.TryAdd("PATHEXT", AgentOpsEngine.NativeExecutableExtensions);
                return ProcessEnvironment.Replace(environment);
            case ToolEnvironmentMode.InheritWithOverlay:
                return ProcessEnvironment.InheritCurrent(definition.Environment);
            default:
                throw new AgentOpsException("invalid");
        }
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyEnvironment =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> NormalizeEnvironment(IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null) return EmptyEnvironment;
        if (environment.Count > 64) throw new AgentOpsException("invalid");
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        foreach (var item in environment)
        {
            if (string.IsNullOrEmpty(item.Key) || item.Key.IndexOf('\0') >= 0 || item.Key.Contains('='))
                throw new AgentOpsException("invalid");
            var keyBytes = Encoding.UTF8.GetByteCount(item.Key);
            var valueBytes = item.Value is null ? 0 : Encoding.UTF8.GetByteCount(item.Value);
            if (keyBytes > 256 || valueBytes > 8192 || item.Value?.IndexOf('\0') >= 0 ||
                !normalized.TryAdd(item.Key, item.Value))
                throw new AgentOpsException("invalid");
            totalBytes = checked(totalBytes + keyBytes + valueBytes);
            if (totalBytes > 64 * 1024) throw new AgentOpsException("invalid");
        }
        return normalized;
    }
}

public sealed class OneActionRecipeRegistry
{
    private const int SchemaVersion = 1;
    private const int MaxDefinitions = 256;
    private readonly string _storePath;
    private readonly object _gate;
    private readonly RegisteredToolRegistry _tools;

    public OneActionRecipeRegistry(string stateRoot, RegisteredToolRegistry tools)
    {
        if (!Path.IsPathFullyQualified(stateRoot)) throw new AgentOpsException("invalid");
        stateRoot = Path.GetFullPath(stateRoot);
        PathNormalizer.RejectAbsoluteReparseComponents(stateRoot);
        Directory.CreateDirectory(stateRoot);
        PathNormalizer.RejectAbsoluteReparseComponents(stateRoot);
        _storePath = Path.Combine(stateRoot, "recipes.v1.json");
        _gate = VersionedJsonStore.Gate(_storePath);
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        lock (_gate) _ = ReadDocument();
    }

    public string StorePath => _storePath;

    public IReadOnlyList<OneActionRecipeDefinition> List()
    {
        lock (_gate) return ReadDocument().Recipes.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public OneActionRecipeDefinition Get(string name)
    {
        ToolRecipeValidation.ValidateName(name);
        lock (_gate)
        {
            return ReadDocument().Recipes.SingleOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, name))
                ?? throw new AgentOpsException("not_found");
        }
    }

    public OneActionRecipeDefinition Register(OneActionRecipeDefinition definition)
    {
        var normalized = Normalize(definition);
        using (VersionedJsonStore.AcquireMutationLock(_storePath, CancellationToken.None))
        lock (_gate)
        {
            var document = ReadDocument();
            var recipes = document.Recipes.ToList();
            var existing = recipes.FindIndex(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, normalized.Name));
            if (existing >= 0)
            {
                if (normalized.DefinitionVersion <= recipes[existing].DefinitionVersion)
                    throw new AgentOpsException("precondition");
                recipes[existing] = normalized;
            }
            else
            {
                if (recipes.Count >= MaxDefinitions) throw new AgentOpsException("invalid");
                recipes.Add(normalized);
            }
            VersionedJsonStore.Write(_storePath, new RecipeRegistryDocument(SchemaVersion, recipes));
        }
        return normalized;
    }

    internal bool Remove(string name)
    {
        ToolRecipeValidation.ValidateName(name);
        using (VersionedJsonStore.AcquireMutationLock(_storePath, CancellationToken.None))
        lock (_gate)
        {
            var document = ReadDocument();
            var recipes = document.Recipes.ToList();
            var removed = recipes.RemoveAll(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, name)) == 1;
            if (removed) VersionedJsonStore.Write(_storePath, new RecipeRegistryDocument(SchemaVersion, recipes));
            return removed;
        }
    }

    private RecipeRegistryDocument ReadDocument()
    {
        var document = VersionedJsonStore.Read(_storePath, () => new RecipeRegistryDocument(SchemaVersion, []));
        if (document.SchemaVersion != SchemaVersion || document.Recipes is null || document.Recipes.Count > MaxDefinitions)
            throw new AgentOpsException("integrity");
        var normalized = document.Recipes.Select(x => Normalize(x, stored: true)).ToArray();
        if (normalized.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new AgentOpsException("integrity");
        return document with { Recipes = normalized };
    }

    private OneActionRecipeDefinition Normalize(OneActionRecipeDefinition value, bool stored = false)
    {
        try
        {
            ToolRecipeValidation.ValidateName(value.Name);
            ToolRecipeValidation.ValidateName(value.ToolName);
            if (value.DefinitionVersion < 1) throw new AgentOpsException("invalid");
            if (value.Action is not (RecipeActionKind.Tool or RecipeActionKind.PowerShell or RecipeActionKind.Dotnet))
                throw new AgentOpsException("precondition");
            var tool = _tools.Get(value.ToolName);
            if (tool.Mode != value.Mode) throw new AgentOpsException("precondition");

            var fixedArguments = value.FixedArguments?.ToArray() ?? [];
            if (fixedArguments.Length > tool.MaxArguments) throw new AgentOpsException("invalid");
            ToolRecipeValidation.ValidatePersistedTemplates(fixedArguments);

            var variables = value.Variables?.ToArray() ?? [];
            if (variables.Length > 64 || variables.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != variables.Length)
                throw new AgentOpsException("invalid");
            foreach (var variable in variables) ToolRecipeValidation.ValidateVariable(variable);
            ValidatePlaceholders(fixedArguments, variables);
            if (value.Action == RecipeActionKind.Dotnet)
                ToolRecipeValidation.ValidateDotnetReleaseRecipe(value, tool, fixedArguments, variables);

            if (value.TimeoutSeconds is <= 0 || value.TimeoutSeconds > tool.TimeoutSeconds)
                throw new AgentOpsException("invalid");
            var exits = value.ExpectedExitCodes?.Distinct().ToArray();
            if (exits is { Length: > 0 })
            {
                ToolRecipeValidation.ValidateExpectedExits(exits);
                if (exits.Any(x => !tool.ExpectedExitCodes!.Contains(x))) throw new AgentOpsException("precondition");
            }
            else
            {
                exits = null;
            }
            if (value.MaxOutputBytes is <= 0 || value.MaxOutputBytes > tool.MaxOutputBytes)
                throw new AgentOpsException("invalid");

            ToolRecipeValidation.ValidateSha(value.ExecutableSha256);
            if (value.ExecutableSha256 is not null)
                ToolRecipeValidation.VerifyFileHash(tool.ExecutablePath, value.ExecutableSha256);

            string? workspace = value.Workspace;
            if (workspace is not null) ToolRecipeValidation.ValidateName(workspace);
            if (value.WorkingDirectory is not null)
            {
                if (workspace is null || !ToolRecipeValidation.IsSafeRelativePath(value.WorkingDirectory))
                    throw new AgentOpsException("precondition");
                if (tool.WorkingDirectoryRule is not (ToolWorkingDirectoryRule.Workspace or ToolWorkingDirectoryRule.Caller))
                    throw new AgentOpsException("precondition");
            }

            string? script = value.ScriptPath;
            string? scriptSha = value.ScriptSha256;
            ToolRecipeValidation.ValidateSha(scriptSha);
            if (value.Action == RecipeActionKind.PowerShell)
            {
                if (string.IsNullOrWhiteSpace(script) || !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                    throw new AgentOpsException("precondition");
                ValidatePlaceholders([script], variables);
                if (Path.IsPathFullyQualified(script))
                {
                    script = Path.GetFullPath(script);
                    if (!File.Exists(script)) throw new AgentOpsException(stored ? "integrity" : "not_found");
                    PathNormalizer.RejectAbsoluteReparseComponents(script);
                    ToolRecipeValidation.VerifyFileHash(script, scriptSha);
                }
                else if (workspace is null || !ToolRecipeValidation.IsSafeRelativeTemplatePath(script, variables))
                {
                    throw new AgentOpsException("precondition");
                }
            }
            else if (script is not null || scriptSha is not null)
            {
                throw new AgentOpsException("invalid");
            }

            RecipePostcondition? postcondition = value.Postcondition;
            if (postcondition is not null)
            {
                ValidatePlaceholders([postcondition.PathTemplate], variables);
                if (!ToolRecipeValidation.IsSafeRelativeTemplatePath(postcondition.PathTemplate, variables))
                    throw new AgentOpsException("precondition");
            }

            return value with
            {
                FixedArguments = fixedArguments,
                Variables = variables,
                ExpectedExitCodes = exits,
                ExecutableSha256 = value.ExecutableSha256?.ToLowerInvariant(),
                ScriptPath = script,
                ScriptSha256 = scriptSha?.ToLowerInvariant(),
                Postcondition = postcondition
            };
        }
        catch (AgentOpsException ex) when (stored && ex.Code != "integrity")
        {
            throw new AgentOpsException("integrity", inner: ex);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new AgentOpsException(stored ? "integrity" : "invalid", inner: ex);
        }
    }

    private static void ValidatePlaceholders(IEnumerable<string> templates, IReadOnlyList<RecipeVariableDefinition> variables)
    {
        var known = variables.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            if (template.Contains('\0')) throw new AgentOpsException("invalid");
            foreach (Match match in ToolRecipeValidation.Placeholder.Matches(template))
                if (!known.Contains(match.Groups[1].Value)) throw new AgentOpsException("precondition");
            var withoutKnown = ToolRecipeValidation.Placeholder.Replace(template, string.Empty);
            if (withoutKnown.Contains("{{", StringComparison.Ordinal) || withoutKnown.Contains("}}", StringComparison.Ordinal))
                throw new AgentOpsException("invalid");
        }
    }

    private sealed record RecipeRegistryDocument(int SchemaVersion, IReadOnlyList<OneActionRecipeDefinition> Recipes);
}

public sealed class ToolRecipeOperations
{
    private readonly IToolInvocationRunner _runner;
    private readonly Guid _admissionOwner = Guid.NewGuid();

    public ToolRecipeOperations(string stateRoot, IToolInvocationRunner? runner = null)
        : this(stateRoot, runner, null)
    {
    }

    internal ToolRecipeOperations(string stateRoot, IToolInvocationRunner? runner,
        Func<string>? installedPowerShellResolver)
    {
        _runner = runner ?? new AgentOpsInvocationRunner();
        Tools = new RegisteredToolRegistry(stateRoot, _runner, installedPowerShellResolver);
        Recipes = new OneActionRecipeRegistry(stateRoot, Tools);
    }

    public RegisteredToolRegistry Tools { get; }
    public OneActionRecipeRegistry Recipes { get; }

    public bool RemoveRecipe(string name) => Recipes.Remove(name);

    public bool RemoveTool(string name, CancellationToken cancellationToken = default)
    {
        if (Recipes.List().Any(x => StringComparer.OrdinalIgnoreCase.Equals(x.ToolName, name)))
            throw new AgentOpsException("precondition");
        return Tools.Remove(name, cancellationToken);
    }

    public ToolInvocation PrepareTool(
        string toolName,
        IReadOnlyList<string> arguments,
        string? workspaceRoot = null,
        string? cwd = null,
        int? timeoutSeconds = null,
        IReadOnlyList<int>? expectedExitCodes = null)
        => PrepareAdmittedTool(
            Tools.Get(toolName),
            arguments,
            workspaceRoot,
            cwd,
            timeoutSeconds,
            expectedExitCodes);

    internal ToolInvocation PrepareAdmittedTool(
        RegisteredToolDefinition tool,
        IReadOnlyList<string> arguments,
        string? workspaceRoot = null,
        string? cwd = null,
        int? timeoutSeconds = null,
        IReadOnlyList<int>? expectedExitCodes = null)
    {
        tool = Tools.ResolveInstalledPowerShell(tool);
        if (!tool.ProbeVerified || (!tool.UsesInstalledPowerShell && string.IsNullOrWhiteSpace(tool.AdmissionSha256)))
            throw new AgentOpsException("precondition");
        if (!tool.UsesInstalledPowerShell)
            ToolRecipeValidation.VerifyFileHash(tool.ExecutablePath, tool.AdmissionSha256);
        ToolRecipeValidation.VerifyFileHash(tool.ExecutablePath, tool.ExecutableSha256);
        ToolRecipeValidation.ValidateArgumentVector(tool, arguments);
        var timeout = timeoutSeconds ?? tool.TimeoutSeconds;
        if (timeout is < 1 || timeout > tool.TimeoutSeconds)
            throw new AgentOpsException("precondition", failure: new OperationFailureEvidence(
                FormattableString.Invariant($"The timeout must be between 1 and {tool.TimeoutSeconds} seconds for the registered tool."),
                new OperationFailureFact(OperationExecutionState.NotDispatched, "registered-tool-admission"),
                new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "registered-tool-admission"),
                new OperationFailureField("tmo"),
                new OperationFailureBound(timeout, tool.TimeoutSeconds, "seconds"),
                Next: FormattableString.Invariant($"Set tmo to a value from 1 through {tool.TimeoutSeconds} seconds, then retry the typed operation."),
                DetailAvailability: "none",
                LogAvailability: "not_retained"));
        var exits = expectedExitCodes?.Distinct().ToArray() ?? tool.ExpectedExitCodes!.ToArray();
        ToolRecipeValidation.ValidateExpectedExits(exits);
        if (exits.Any(x => !tool.ExpectedExitCodes!.Contains(x))) throw new AgentOpsException("precondition");
        var workingDirectory = ResolveWorkingDirectory(tool, workspaceRoot, cwd);
        return new ToolInvocation(
            _admissionOwner,
            tool.Name,
            tool.ExecutablePath,
            arguments.ToArray(),
            workingDirectory,
            TimeSpan.FromSeconds(timeout),
            exits,
            tool.Mode,
            tool.Window,
            tool.StdinEncoding,
            tool.StdoutEncoding,
            tool.StderrEncoding,
            tool.MaxOutputBytes,
            RegisteredToolRegistry.CreateEnvironment(tool),
            tool.UsesInstalledPowerShell ? [] : [new InvocationFileAdmission(tool.ExecutablePath, tool.AdmissionSha256!)]);
    }

    public ToolInvocation PrepareGit(
        string operation,
        IReadOnlyList<string>? arguments = null,
        string toolName = "git",
        string? workspaceRoot = null,
        string? cwd = null)
    {
        ToolRecipeValidation.ValidateAdapterOperation(operation);
        var vector = new List<string> { operation };
        if (arguments is not null) vector.AddRange(arguments);
        return PrepareTool(toolName, vector, workspaceRoot, cwd);
    }

    public ToolInvocation PrepareDotnet(
        string operation,
        string? configuration = null,
        IReadOnlyList<string>? passThrough = null,
        string toolName = "dotnet",
        string? workspaceRoot = null,
        string? cwd = null,
        string? target = null,
        string? output = null)
    {
        if (operation is not ("build" or "test" or "pack" or "publish" or "restore" or "clean"))
            throw new AgentOpsException("invalid");
        var vector = new List<string> { operation };
        if (target is not null)
        {
            if (!Path.IsPathFullyQualified(target) || !File.Exists(target))
                throw new AgentOpsException("not_found");
            PathNormalizer.RejectAbsoluteReparseComponents(target);
            vector.Add(Path.GetFullPath(target));
        }
        if (configuration is not null)
        {
            if (operation is "restore" || string.IsNullOrWhiteSpace(configuration) || configuration.Contains('\0'))
                throw new AgentOpsException("invalid");
            vector.Add("--configuration");
            vector.Add(configuration);
        }
        if (output is not null)
        {
            if (operation != "publish" || !Path.IsPathFullyQualified(output))
                throw new AgentOpsException("invalid");
            PathNormalizer.RejectAbsoluteReparseComponents(output);
            vector.Add("--output");
            vector.Add(Path.GetFullPath(output));
        }
        if (passThrough is not null) vector.AddRange(passThrough);
        return PrepareTool(toolName, vector, workspaceRoot, cwd);
    }

    public ToolInvocation PrepareSevenZip(
        string operation,
        string source,
        string? destination = null,
        string policy = "fail",
        string toolName = "7z",
        string? workspaceRoot = null,
        string? cwd = null)
    {
        var baseDirectory = workspaceRoot ?? cwd ?? Environment.CurrentDirectory;
        var canonicalSource = workspaceRoot is null
            ? Path.GetFullPath(source, baseDirectory)
            : ResolveWithinWorkspace(workspaceRoot, source);
        PathNormalizer.RejectAbsoluteReparseComponents(canonicalSource);
        if (!File.Exists(canonicalSource)) throw new AgentOpsException("not_found");
        var vector = new List<string>();
        switch (operation)
        {
            case "test":
                vector.AddRange(["t", "-bso0", "-bsp0", "--", canonicalSource]);
                break;
            case "list":
                vector.AddRange(["l", "-slt", "--", canonicalSource]);
                break;
            case "extract":
                // Safe extraction requires entry admission, expansion limits,
                // destination reparse inspection, and honest clean/partial receipts.
                // Until that orchestration calls this module, do not advertise an
                // argv-only extraction path.
                throw new AgentOpsException("precondition");
            default:
                throw new AgentOpsException("invalid");
        }
        var invocation = PrepareTool(toolName, vector, workspaceRoot, cwd);
        return invocation.AddFileAdmission(canonicalSource, ToolRecipeValidation.ComputeFileHash(canonicalSource));
    }

    public ToolInvocation PreparePowerShell(
        string scriptPath,
        IReadOnlyList<string>? arguments = null,
        string toolName = "pwsh",
        string? expectedScriptSha256 = null,
        string? workspaceRoot = null,
        string? cwd = null)
    {
        var baseDirectory = workspaceRoot ?? cwd ?? Environment.CurrentDirectory;
        var script = workspaceRoot is null
            ? Path.GetFullPath(scriptPath, baseDirectory)
            : ResolveWithinWorkspace(workspaceRoot, scriptPath);
        if (!File.Exists(script) || !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            throw new AgentOpsException("precondition");
        PathNormalizer.RejectAbsoluteReparseComponents(script);
        ToolRecipeValidation.ValidateSha(expectedScriptSha256);
        ToolRecipeValidation.VerifyFileHash(script, expectedScriptSha256);
        var scriptAdmissionSha = ToolRecipeValidation.ComputeFileHash(script);
        var vector = new List<string> { "-NoProfile", "-NonInteractive", "-File", script };
        if (arguments is not null) vector.AddRange(arguments);
        return PrepareTool(toolName, vector, workspaceRoot, cwd)
            .AddFileAdmission(script, scriptAdmissionSha);
    }

    public ToolInvocation PrepareRecipe(
        string recipeName,
        IReadOnlyDictionary<string, string>? variables = null,
        IReadOnlyDictionary<string, string>? workspaceRoots = null,
        string? callerCwd = null)
    {
        var recipe = Recipes.Get(recipeName);
        var resolvedVariables = ToolRecipeValidation.ResolveVariables(recipe.Variables!, variables);
        var arguments = recipe.FixedArguments!.Select(x => ToolRecipeValidation.Expand(x, resolvedVariables)).ToArray();
        ValidateAdapterVector(recipe.Action, arguments);

        string? workspaceRoot = null;
        if (recipe.Workspace is not null)
        {
            if (workspaceRoots is null || !workspaceRoots.TryGetValue(recipe.Workspace, out workspaceRoot))
                throw new AgentOpsException("precondition");
            workspaceRoot = PathNormalizer.NormalizeDirectory(workspaceRoot);
            if (!Directory.Exists(workspaceRoot)) throw new AgentOpsException("not_found");
            PathNormalizer.RejectAbsoluteReparseComponents(workspaceRoot);
        }
        var cwd = recipe.WorkingDirectory is null
            ? workspaceRoot ?? callerCwd
            : ResolveWorkspaceRelative(workspaceRoot!, recipe.WorkingDirectory);

        ToolInvocation invocation;
        if (recipe.Action == RecipeActionKind.PowerShell)
        {
            var script = ToolRecipeValidation.Expand(recipe.ScriptPath!, resolvedVariables);
            if (!Path.IsPathFullyQualified(script)) script = ResolveWorkspaceRelative(workspaceRoot!, script);
            invocation = PreparePowerShell(
                script,
                arguments,
                recipe.ToolName,
                recipe.ScriptSha256,
                workspaceRoot,
                cwd);
        }
        else if (recipe.Action == RecipeActionKind.Dotnet)
        {
            // This closed recipe uses the caller's already admitted directory,
            // never ambient cwd or a guessed workspace. Project is a relative
            // operand, not arbitrary executable arguments or a shell string.
            if (callerCwd is null || !Path.IsPathFullyQualified(callerCwd))
                throw new AgentOpsException("precondition");
            var admittedCwd = PathNormalizer.NormalizeDirectory(callerCwd);
            if (admittedCwd.Length > 240) throw new AgentOpsException("precondition");
            if (!Directory.Exists(admittedCwd)) throw new AgentOpsException("not_found");
            PathNormalizer.RejectAbsoluteReparseComponents(admittedCwd);
            if (Path.GetFullPath(arguments[1], admittedCwd).Length > 240)
                throw new AgentOpsException("precondition");
            var project = ResolveWorkspaceRelative(admittedCwd, arguments[1]);
            if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new AgentOpsException("precondition");
            var projectContext = new WindowsPathContextNormalizer().Normalize(project);
            WindowsPathContextNormalizer.RevalidateLocalNoFollow(projectContext, admittedCwd);
            project = projectContext.CanonicalPath;
            invocation = PrepareDotnet("build", "Release", arguments.Skip(4).ToArray(),
                recipe.ToolName, admittedCwd, admittedCwd, target: project);
        }
        else
        {
            invocation = PrepareTool(
                recipe.ToolName,
                arguments,
                workspaceRoot,
                cwd,
                recipe.TimeoutSeconds,
                recipe.ExpectedExitCodes);
        }

        ToolRecipeValidation.VerifyFileHash(invocation.ExecutablePath, recipe.ExecutableSha256);
        if (recipe.ExecutableSha256 is not null)
            invocation = invocation.AddFileAdmission(invocation.ExecutablePath, recipe.ExecutableSha256);
        ResolvedRecipePostcondition? postcondition = null;
        if (recipe.Postcondition is not null)
        {
            var expanded = ToolRecipeValidation.Expand(recipe.Postcondition.PathTemplate, resolvedVariables);
            var resolved = ResolveWorkspaceRelative(invocation.WorkingDirectory, expanded);
            postcondition = new ResolvedRecipePostcondition(
                recipe.Postcondition.Kind,
                resolved,
                invocation.WorkingDirectory);
        }

        return invocation.WithRecipe(
            TimeSpan.FromSeconds(recipe.TimeoutSeconds ?? (int)invocation.Timeout.TotalSeconds),
            recipe.ExpectedExitCodes?.ToArray() ?? invocation.ExpectedExitCodes,
            recipe.MaxOutputBytes ?? invocation.MaxOutputBytes,
            recipe.OutputBehavior,
            postcondition,
            recipe.Name);
    }

    public async Task<ToolExecutionResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (invocation is null || invocation.AdmissionOwner != _admissionOwner)
            throw new AgentOpsException("precondition");
        if (invocation.Mode == ToolExecutionMode.System)
            throw new AgentOpsException("service_unavailable");
        using var admissionLease = ToolRecipeValidation.AcquireInvocationLease(invocation);
        var result = await _runner.RunAsync(invocation, cancellationToken);
        var evidence = ApplyOutputPolicy(invocation, result);
        if (result.TimedOut)
            throw new ToolExecutionException("timeout", evidence, timedOut: true);
        if (!invocation.ExpectedExitCodes.Contains(result.ExitCode))
            throw new ToolExecutionException("external_failed", evidence, timedOut: false);
        try
        {
            ValidatePostcondition(invocation.Postcondition);
        }
        catch (AgentOpsException ex) when (ex.Code == "recovery_required")
        {
            throw new ToolExecutionException("recovery_required", evidence, timedOut: false, inner: ex);
        }
        return evidence;
    }

    private static ToolExecutionResult ApplyOutputPolicy(ToolInvocation invocation, ProcessResult result)
    {
        var (stdout, stdoutBounded) = ToolRecipeValidation.BoundUtf8(result.Stdout, invocation.MaxOutputBytes);
        var (stderr, stderrBounded) = ToolRecipeValidation.BoundUtf8(result.Stderr, invocation.MaxOutputBytes);
        if (invocation.OutputBehavior is ToolOutputBehavior.Discard or ToolOutputBehavior.StatusOnly)
        {
            stdout = string.Empty;
            stderr = string.Empty;
        }
        return new ToolExecutionResult(
            result.ExitCode,
            stdout,
            stderr,
            result.StdoutTruncated || stdoutBounded,
            result.StderrTruncated || stderrBounded,
            invocation.Mode,
            invocation.ToolName,
            invocation.RecipeName);
    }

    private static string ResolveWorkingDirectory(RegisteredToolDefinition tool, string? workspaceRoot, string? cwd)
    {
        string resolved;
        switch (tool.WorkingDirectoryRule)
        {
            case ToolWorkingDirectoryRule.ExecutableDirectory:
                resolved = Path.GetDirectoryName(tool.ExecutablePath)!;
                break;
            case ToolWorkingDirectoryRule.Fixed:
                resolved = tool.FixedWorkingDirectory!;
                break;
            case ToolWorkingDirectoryRule.Workspace:
                if (workspaceRoot is null) throw new AgentOpsException("precondition");
                resolved = cwd is null
                    ? PathNormalizer.NormalizeDirectory(workspaceRoot)
                    : ResolveWithinWorkspace(workspaceRoot, cwd);
                break;
            case ToolWorkingDirectoryRule.Caller:
                if (workspaceRoot is not null)
                {
                    var admittedWorkspace = PathNormalizer.NormalizeDirectory(workspaceRoot);
                    if (!Directory.Exists(admittedWorkspace)) throw new AgentOpsException("not_found");
                    PathNormalizer.RejectAbsoluteReparseComponents(admittedWorkspace);
                    resolved = cwd is null
                        ? admittedWorkspace
                        : ResolveWithinWorkspace(admittedWorkspace, cwd);
                }
                else
                {
                    resolved = Path.GetFullPath(cwd ?? Environment.CurrentDirectory);
                }
                break;
            default:
                throw new AgentOpsException("invalid");
        }
        if (!Directory.Exists(resolved)) throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(resolved);
        return resolved;
    }

    private static string ResolveWorkspaceRelative(string root, string relative)
    {
        root = PathNormalizer.NormalizeDirectory(root);
        if (!ToolRecipeValidation.IsSafeRelativePath(relative)) throw new AgentOpsException("precondition");
        var full = Path.GetFullPath(relative, root);
        if (!PathNormalizer.Contains(root, full)) throw new AgentOpsException("precondition");
        PathNormalizer.RejectReparseComponents(root, full);
        return full;
    }

    private static string ResolveWithinWorkspace(string root, string path)
    {
        root = PathNormalizer.NormalizeDirectory(root);
        if (!Path.IsPathFullyQualified(path)) return ResolveWorkspaceRelative(root, path);
        var full = Path.GetFullPath(path);
        if (!PathNormalizer.Contains(root, full)) throw new AgentOpsException("precondition");
        PathNormalizer.RejectReparseComponents(root, full);
        return full;
    }

    private static void ValidateAdapterVector(RecipeActionKind action, IReadOnlyList<string> arguments)
    {
        if (action is RecipeActionKind.Tool or RecipeActionKind.PowerShell or RecipeActionKind.Dotnet) return;
        throw new AgentOpsException("precondition");
    }

    private static void ValidatePostcondition(ResolvedRecipePostcondition? postcondition)
    {
        if (postcondition is null) return;
        try
        {
            PathNormalizer.RejectReparseComponents(postcondition.AdmissionRoot, postcondition.Path);
            var satisfied = postcondition.Kind switch
            {
                RecipePostconditionKind.FileExists => File.Exists(postcondition.Path),
                RecipePostconditionKind.DirectoryExists => Directory.Exists(postcondition.Path),
                _ => false
            };
            if (!satisfied) throw new AgentOpsException("recovery_required");
        }
        catch (AgentOpsException ex) when (ex.Code != "recovery_required")
        {
            throw new AgentOpsException("recovery_required", inner: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AgentOpsException("recovery_required", inner: ex);
        }
    }
}

internal static partial class ToolRecipeValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> UnsupportedRuntimeFields =
        new(["root", "stdin", "env"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedVersionProbeArguments =
        new(["--version", "-version", "version", "-v", "-V", "i", "/?"], StringComparer.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("\\{\\{([A-Za-z][A-Za-z0-9_-]{0,31})\\}\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    public static Regex Placeholder => PlaceholderPattern();

    public static void ValidateDotnetReleaseRecipe(OneActionRecipeDefinition recipe,
        RegisteredToolDefinition tool, IReadOnlyList<string> arguments,
        IReadOnlyList<RecipeVariableDefinition> variables)
    {
        // Enable only this versioned build profile, not the general Dotnet enum
        // surface. Registration may narrow limits, never inherit or broaden them.
        if (recipe.ToolName != "dotnet" || recipe.Mode != ToolExecutionMode.User ||
            tool.WorkingDirectoryRule != ToolWorkingDirectoryRule.Caller ||
            recipe.Workspace is not null || recipe.WorkingDirectory is not null ||
            !arguments.SequenceEqual(new[] { "build", "{{project}}", "--configuration", "Release",
                "--disable-build-servers", "--verbosity", "minimal" }, StringComparer.Ordinal) ||
            variables.Count != 1 || variables[0] is not
                { Name: "project", Type: RecipeVariableType.RelativePath, Required: true, DefaultValue: null } ||
            variables[0].MaxUtf8Bytes > 1024 ||
            recipe.TimeoutSeconds is not (>= 1 and <= 120) ||
            recipe.ExpectedExitCodes is not { Count: 1 } || recipe.ExpectedExitCodes[0] != 0 ||
            recipe.OutputBehavior != ToolOutputBehavior.Capture ||
            recipe.MaxOutputBytes is not (>= 1 and <= 16 * 1024) || recipe.Postcondition is not null)
            throw new AgentOpsException("precondition");
    }

    public static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !NamePattern().IsMatch(value)) throw new AgentOpsException("invalid");
    }

    public static void ValidateSha(string? value)
    {
        if (value is not null && (value.Length != 64 || !value.All(Uri.IsHexDigit)))
            throw new AgentOpsException("invalid");
    }

    public static void VerifyFileHash(string path, string? expected)
    {
        if (expected is null) return;
        VerifyHashValue(ComputeFileHash(path), expected);
    }

    public static string ComputeFileHash(string path)
    {
        try
        {
            PathNormalizer.RejectAbsoluteReparseComponents(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (AgentOpsException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new AgentOpsException("integrity", inner: ex);
        }
    }

    public static void VerifyHashValue(string? actual, string? expected)
    {
        if (expected is not null && !StringComparer.OrdinalIgnoreCase.Equals(expected, actual))
            throw new AgentOpsException("integrity");
    }

    public static IDisposable AcquireInvocationLease(ToolInvocation invocation)
    {
        if (!Directory.Exists(invocation.WorkingDirectory)) throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(invocation.WorkingDirectory);
        var streams = new List<FileStream>(invocation.FileAdmissions.Count);
        try
        {
            // Installed-path tools still acquire an ordinary executable handle;
            // only their executable byte comparison is omitted. Script and
            // explicit recipe admissions below remain exact and locked.
            if (!invocation.FileAdmissions.Any(admission =>
                    StringComparer.OrdinalIgnoreCase.Equals(admission.Path, invocation.ExecutablePath)))
            {
                PathNormalizer.RejectAbsoluteReparseComponents(invocation.ExecutablePath);
                streams.Add(new FileStream(invocation.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            foreach (var admission in invocation.FileAdmissions)
            {
                PathNormalizer.RejectAbsoluteReparseComponents(admission.Path);
                var stream = new FileStream(admission.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                VerifyHashValue(actual, admission.Sha256);
            }
            return new InvocationAdmissionLease(streams);
        }
        catch (AgentOpsException)
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            foreach (var stream in streams) stream.Dispose();
            throw new AgentOpsException("integrity", inner: ex);
        }
    }

    private sealed class InvocationAdmissionLease(IReadOnlyList<FileStream> streams) : IDisposable
    {
        public void Dispose()
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    public static void ValidateExpectedExits(IReadOnlyCollection<int> exits)
    {
        if (exits.Count is < 1 or > 64) throw new AgentOpsException("invalid");
    }

    public static void ValidateEncoding(string value, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new AgentOpsException("invalid");
        var normalized = value.ToLowerInvariant();
        if (allowNone && normalized == "none") return;
        if (normalized is "ascii" or "utf8" or "utf8bom" or "utf16le" or "utf16be") return;
        if (normalized.StartsWith("codepage:", StringComparison.Ordinal) &&
            int.TryParse(normalized[9..], NumberStyles.None, CultureInfo.InvariantCulture, out var codePage) && codePage > 0)
            return;
        throw new AgentOpsException("invalid");
    }

    public static void ValidateArgumentVector(RegisteredToolDefinition tool, IReadOnlyList<string> arguments)
    {
        if (arguments is null) throw ArgumentFailure("The argument vector is required.", "arg", null, null, "Supply the registered tool arguments as a bounded string array.");
        if (arguments.Count > tool.MaxArguments)
            throw ArgumentFailure("The argument vector contains too many entries.", "arg", null,
                new OperationFailureBound(arguments.Count, tool.MaxArguments, "items"),
                "Reduce the argument count and retry.");
        long total = 0;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is null || argument.Contains('\0'))
                throw ArgumentFailure("An argument is null or contains a NUL character.", "arg", index, null,
                    "Replace the invalid argument with ordinary text and retry.");
            int bytes;
            try { bytes = StrictUtf8.GetByteCount(argument); }
            catch (EncoderFallbackException ex)
            {
                throw ArgumentFailure("An argument is not valid UTF-8 text.", "arg", index, null,
                    "Replace the invalid Unicode sequence and retry.", ex);
            }
            if (bytes > tool.MaxArgumentUtf8Bytes)
                throw ArgumentFailure("An argument exceeds the registered per-argument UTF-8 limit.", "arg", index,
                    new OperationFailureBound(bytes, tool.MaxArgumentUtf8Bytes, "utf8_bytes"),
                    "Write large content to a file and pass its admitted path, or reduce this argument.");
            total += bytes;
            if (total > tool.MaxTotalArgumentUtf8Bytes)
                throw ArgumentFailure("The argument vector exceeds the registered total UTF-8 limit.", "arg", index,
                    new OperationFailureBound(total, tool.MaxTotalArgumentUtf8Bytes, "utf8_bytes"),
                    "Reduce the argument vector or pass large content through an admitted file.");
        }
    }

    private static AgentOpsException ArgumentFailure(string message, string field, int? index,
        OperationFailureBound? bound, string next, Exception? inner = null) =>
        new("invalid", inner: inner, failure: new OperationFailureEvidence(
            message,
            new OperationFailureFact(OperationExecutionState.NotDispatched, "argument-vector-validation"),
            new OperationFailureFact(OperationRetryDisposition.AfterCorrection, "argument-vector-validation"),
            new OperationFailureField(field, index),
            bound,
            Next: next,
            DetailAvailability: "none",
            LogAvailability: "not_retained"));

    public static void ValidatePersistedTemplates(IReadOnlyList<string> arguments)
    {
        long total = 0;
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Contains('\0')) throw new AgentOpsException("invalid");
            int bytes;
            try { bytes = StrictUtf8.GetByteCount(argument); }
            catch (EncoderFallbackException ex) { throw new AgentOpsException("invalid", inner: ex); }
            if (bytes > 64 * 1024) throw new AgentOpsException("invalid");
            total += bytes;
            if (total > 256 * 1024) throw new AgentOpsException("invalid");
        }
    }

    public static void ValidateVersionProbeArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1 || arguments[0] is null || !AllowedVersionProbeArguments.Contains(arguments[0]))
            throw new AgentOpsException("precondition");
    }

    public static void ValidateVariable(RecipeVariableDefinition variable)
    {
        ValidateName(variable.Name);
        if (UnsupportedRuntimeFields.Contains(variable.Name))
            throw new AgentOpsException("precondition");
        // Registry documents are plaintext definitions. Runtime values are supplied
        // only when preparing an invocation, so persisted defaults are unsupported.
        if (variable.DefaultValue is not null) throw new AgentOpsException("precondition");
        if (variable.MaxUtf8Bytes is < 1 or > 64 * 1024) throw new AgentOpsException("invalid");
        if (variable.Minimum > variable.Maximum) throw new AgentOpsException("invalid");
        if (variable.Type != RecipeVariableType.Integer && (variable.Minimum is not null || variable.Maximum is not null))
            throw new AgentOpsException("invalid");
        if (variable.Type == RecipeVariableType.Choice && variable.Choices is not { Count: > 0 })
            throw new AgentOpsException("invalid");
        if (variable.Type != RecipeVariableType.Choice && variable.Choices is not null)
            throw new AgentOpsException("invalid");
        if (variable.Choices is { Count: > 64 } || variable.Choices?.Any(x => x is null || x.Contains('\0')) == true)
            throw new AgentOpsException("invalid");
        if (variable.Choices is not null)
        {
            foreach (var choice in variable.Choices) _ = NormalizeVariable(variable, choice);
        }
    }

    public static IReadOnlyDictionary<string, string> ResolveVariables(
        IReadOnlyList<RecipeVariableDefinition> definitions,
        IReadOnlyDictionary<string, string>? supplied)
    {
        supplied ??= new Dictionary<string, string>(StringComparer.Ordinal);
        var known = definitions.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (supplied.Keys.Any(x => !known.Contains(x))) throw new AgentOpsException("precondition");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!supplied.TryGetValue(definition.Name, out var value)) value = definition.DefaultValue;
            if (value is null)
            {
                if (definition.Required) throw new AgentOpsException("precondition");
                value = string.Empty;
            }
            result[definition.Name] = NormalizeVariable(definition, value);
        }
        return result;
    }

    public static string Expand(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder.Replace(template, match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? value
            : throw new AgentOpsException("precondition"));

    public static bool IsSafeRelativeTemplatePath(string value, IReadOnlyList<RecipeVariableDefinition> variables)
    {
        var sample = Placeholder.Replace(value, match =>
        {
            var definition = variables.SingleOrDefault(x => x.Name == match.Groups[1].Value);
            return definition?.Type == RecipeVariableType.RelativePath ? "safe" : "value";
        });
        return IsSafeRelativePath(sample);
    }

    public static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || Regex.IsMatch(value, "^[A-Za-z]:", RegexOptions.CultureInvariant))
            return false;
        var invalid = Path.GetInvalidFileNameChars();
        var components = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return components.Length > 0 && components.All(x => x is not ("." or "..") && x.IndexOfAny(invalid) < 0);
    }

    public static void ValidateAdapterOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Contains('\0') || operation.Any(char.IsWhiteSpace))
            throw new AgentOpsException("invalid");
    }

    public static (string Text, bool Truncated) BoundUtf8(string text, int maxBytes)
    {
        try
        {
            if (StrictUtf8.GetByteCount(text) <= maxBytes) return (text, false);
            var index = 0;
            var bytes = 0;
            while (index < text.Length)
            {
                var charCount = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                if (char.IsSurrogate(text[index]) && charCount == 1) throw new AgentOpsException("invalid");
                var runeBytes = StrictUtf8.GetByteCount(text.AsSpan(index, charCount));
                if (bytes + runeBytes > maxBytes) break;
                bytes += runeBytes;
                index += charCount;
            }
            return (text[..index], true);
        }
        catch (EncoderFallbackException ex)
        {
            throw new AgentOpsException("invalid", inner: ex);
        }
    }

    private static string NormalizeVariable(RecipeVariableDefinition definition, string value)
    {
        if (value.Contains('\0')) throw new AgentOpsException("invalid");
        int bytes;
        try { bytes = StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException ex) { throw new AgentOpsException("invalid", inner: ex); }
        if (bytes > definition.MaxUtf8Bytes) throw new AgentOpsException("invalid");
        return definition.Type switch
        {
            RecipeVariableType.Text => value,
            RecipeVariableType.Integer => NormalizeInteger(definition, value),
            RecipeVariableType.Boolean when bool.TryParse(value, out var parsed) => parsed ? "true" : "false",
            RecipeVariableType.RelativePath when IsSafeRelativePath(value) => value,
            RecipeVariableType.Choice when definition.Choices!.Contains(value, StringComparer.Ordinal) => value,
            _ => throw new AgentOpsException("precondition")
        };
    }

    private static string NormalizeInteger(RecipeVariableDefinition definition, string value)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < (definition.Minimum ?? long.MinValue) || parsed > (definition.Maximum ?? long.MaxValue))
            throw new AgentOpsException("precondition");
        return parsed.ToString(CultureInfo.InvariantCulture);
    }
}

internal static class VersionedJsonStore
{
    private const int MaxStoreBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan MutationLockTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = CreateOptions();

    public static object Gate(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    public static IDisposable AcquireMutationLock(string storePath, CancellationToken cancellationToken)
    {
        var lockPath = Path.GetFullPath(storePath) + ".lock";
        PathNormalizer.RejectAbsoluteReparseComponents(lockPath);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException) when (timer.Elapsed < MutationLockTimeout)
            {
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(20)))
                    cancellationToken.ThrowIfCancellationRequested();
            }
            catch (IOException ex)
            {
                throw new AgentOpsException("timeout", inner: ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new AgentOpsException("access", inner: ex);
            }
        }
    }

    public static T Read<T>(string path, Func<T> fallback)
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            PathNormalizer.RejectAbsoluteReparseComponents(path);
            var info = new FileInfo(path);
            if (info.Length is < 1 or > MaxStoreBytes) throw new AgentOpsException("integrity");
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new AgentOpsException("integrity");
        }
        catch (AgentOpsException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new AgentOpsException("integrity", inner: ex);
        }
    }

    public static void Write<T>(string path, T value)
    {
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxStoreBytes) throw new AgentOpsException("invalid");
        var directory = Path.GetDirectoryName(path) ?? throw new AgentOpsException("invalid");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: File.Exists(path));
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
