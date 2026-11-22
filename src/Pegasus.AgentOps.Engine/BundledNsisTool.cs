using System.Text.Json;
using System.Text.Json.Serialization;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

/// <summary>
/// Admits the immutable product-local NSIS compiler. This definition is not
/// persisted in the user tool registry and therefore cannot collide with or
/// silently replace an owner-managed tool definition.
/// </summary>
public static class BundledNsisTool
{
    public static async Task<RegisteredToolDefinition> AdmitAsync(
        string installRoot,
        string userExecutionStateRoot,
        CancellationToken cancellationToken = default,
        IToolInvocationRunner? runner = null)
    {
        var canonicalRoot = RequireCanonicalLocalRoot(installRoot);
        var compiler = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            CortexProductIdentity.NsisCompilerRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        RequireContained(canonicalRoot, compiler);
        if (!File.Exists(compiler) || Directory.Exists(compiler))
            throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(compiler);
        var admissionPath = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            CortexProductIdentity.NsisAdmissionRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        RequireContained(canonicalRoot, admissionPath);
        var admission = ReadAdmission(admissionPath);
        ToolRecipeValidation.VerifyFileHash(compiler, admission.AdmittedCompilerSha256);

        var stateRoot = Path.GetFullPath(userExecutionStateRoot);
        RequireContained(canonicalRoot, stateRoot);
        PathNormalizer.RejectAbsoluteReparseComponents(stateRoot);
        var temporary = Path.Combine(stateRoot, "tmp");
        Directory.CreateDirectory(temporary);
        PathNormalizer.RejectAbsoluteReparseComponents(temporary);

        var nsisRoot = Path.GetDirectoryName(Path.GetDirectoryName(compiler)) ??
            throw new AgentOpsException("integrity");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NSISDIR"] = nsisRoot,
            ["TEMP"] = temporary,
            ["TMP"] = temporary
        };
        foreach (var name in new[] { "SystemRoot", "WINDIR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value &&
                Path.IsPathFullyQualified(value))
                environment[name] = Path.GetFullPath(value);
        }
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
            commandProcessor = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (Path.IsPathFullyQualified(commandProcessor) && File.Exists(commandProcessor))
            environment["ComSpec"] = Path.GetFullPath(commandProcessor);

        var definition = new RegisteredToolDefinition(
            "nsis",
            31200,
            compiler,
            admission.AdmittedCompilerSha256,
            ToolExecutionMode.User,
            ToolWorkingDirectoryRule.Caller,
            TimeoutSeconds: 3600,
            ExpectedExitCodes: [0],
            MaxArguments: 16,
            MaxArgumentUtf8Bytes: 32 * 1024,
            MaxTotalArgumentUtf8Bytes: 128 * 1024,
            MaxOutputBytes: 64 * 1024,
            VersionProbe: new ToolProbeDefinition(["/VERSION"], [0], "v3.12"),
            EnvironmentMode: ToolEnvironmentMode.Replace,
            Environment: environment)
        {
            AdmissionSha256 = admission.AdmittedCompilerSha256
        };

        var invocation = new ToolInvocation(
            Guid.Empty,
            definition.Name,
            definition.ExecutablePath,
            definition.VersionProbe!.Arguments,
            Path.GetDirectoryName(definition.ExecutablePath)!,
            TimeSpan.FromSeconds(30),
            [0],
            ToolExecutionMode.User,
            ToolWindowMode.Hidden,
            "none",
            "utf8",
            "utf8",
            definition.MaxOutputBytes,
            RegisteredToolRegistry.CreateEnvironment(definition),
            [new InvocationFileAdmission(definition.ExecutablePath, definition.AdmissionSha256)]);
        var result = await (runner ?? new AgentOpsInvocationRunner())
            .RunAsync(invocation, cancellationToken)
            .ConfigureAwait(false);
        if (result.TimedOut || result.ExitCode != 0 ||
            (!result.Stdout.Contains("v3.12", StringComparison.Ordinal) &&
             !result.Stderr.Contains("v3.12", StringComparison.Ordinal)))
            throw new AgentOpsException(result.TimedOut ? "timeout" : "precondition");

        ToolRecipeValidation.VerifyFileHash(compiler, admission.AdmittedCompilerSha256);
        return definition with { ProbeVerified = true };
    }

    private static BundledNsisAdmissionV1 ReadAdmission(string path)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new AgentOpsException("not_found");
        PathNormalizer.RejectAbsoluteReparseComponents(path);
        BundledNsisAdmissionV1 value;
        try
        {
            value = JsonSerializer.Deserialize(
                        File.ReadAllBytes(path),
                        BundledNsisAdmissionJsonContext.Default.BundledNsisAdmissionV1) ??
                    throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new AgentOpsException("integrity", inner: exception);
        }

        if (value.Contract != BundledNsisAdmissionV1.ExpectedContract ||
            value.Version != 1 ||
            value.NsisVersion != CortexProductIdentity.NsisVersion ||
            value.ArchiveSha256 != CortexProductIdentity.NsisArchiveSha256 ||
            value.CompilerRelativePath != "Bin/makensis.exe" ||
            value.UpstreamCompilerSha256 != CortexProductIdentity.NsisUpstreamCompilerSha256 ||
            !IsSha256(value.AdmittedCompilerSha256))
            throw new AgentOpsException("integrity");
        return value;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string RequireCanonicalLocalRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.IsPathFullyQualified(value) ||
            value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("//", StringComparison.Ordinal))
            throw new AgentOpsException("invalid");
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!string.Equals(
                canonical,
                Path.TrimEndingDirectorySeparator(value),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AgentOpsException("invalid");
        PathNormalizer.RejectAbsoluteReparseComponents(canonical);
        return canonical;
    }

    private static void RequireContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".") return;
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new AgentOpsException("precondition");
    }
}

internal sealed record BundledNsisAdmissionV1(
    string Contract,
    int Version,
    string NsisVersion,
    string ArchiveSha256,
    string CompilerRelativePath,
    string UpstreamCompilerSha256,
    string AdmittedCompilerSha256)
{
    public const string ExpectedContract = "pegasus-cortex-bundled-nsis";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BundledNsisAdmissionV1))]
internal sealed partial class BundledNsisAdmissionJsonContext : JsonSerializerContext;
