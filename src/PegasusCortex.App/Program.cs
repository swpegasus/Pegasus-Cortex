using System.Text;
using Pegasus.Cortex.Mcp.Local.Offline;
using Pegasus.Cortex.Mcp.Local.Runtime;

if (args.Length > 0 && PegasusCortexMcpLocalProgram.IsRetiredStdioInvocation(args))
{
    await Console.Error.WriteLineAsync(PegasusCortexMcpLocalProgram.StdioRetirementMessage).ConfigureAwait(false);
    return 2;
}

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var launched = await CortexStableLauncher.TryRunSelectedVersionAsync(
    args,
    CancellationToken.None).ConfigureAwait(false);
if (launched is not null)
    return launched.Value;

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync(PegasusCortexMcpLocalProgram.StdioRetirementMessage).ConfigureAwait(false);
    return 2;
}

if (args is ["version"])
{
    Console.WriteLine(CortexLocalProductCommands.Version);
    return 0;
}

if (args.Length > 0 && args[0] == "assets")
{
    var result = await CortexAssetProductCommandsV2.RunAsync(args, CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args.Length > 0 && args[0] == "connections")
{
    var result = CortexConnectionCommands.Run(args);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["status"])
{
    Console.WriteLine(await CortexLocalProductCommands.StatusAsync(CancellationToken.None));
    return 0;
}

if (args is ["doctor"])
{
    var result = await CortexLocalProductCommands.DoctorAsync(CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args.Length is 1 or 3 && args[0] == "metrics")
{
    var days = 7;
    if (args.Length == 3 && (args[1] != "--days" || !int.TryParse(args[2], out days) || days is < 1 or > 90))
        return 2;
    var result = await CortexLocalProductCommands.UtilizationAsync(days, CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["backup"])
{
    var result = await CortexLocalProductCommands.BackupAsync(CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["migrate-knowledge-spaces"])
{
    var result = await CortexLocalProductCommands.MigrateKnowledgeSpacesAsync(
        CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["seed-defaults"])
{
    var result = await CortexLocalProductCommands.ImportStarterCorpusAsync(
        Path.Combine(AppContext.BaseDirectory, "behavior-packs"),
        onlyIfEmpty: false,
        CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["import-behavior-pack", "--pack-root", var packRoot, "--namespace", var targetNamespace])
{
    var result = await CortexLocalProductCommands.ImportBehaviorPackAsync(
        packRoot,
        targetNamespace,
        CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["import-behavior-pack", "--pack-root", var adoptingPackRoot, "--namespace", var adoptingNamespace,
    "--adoption-manifest", var adoptionManifest])
{
    var result = await CortexLocalProductCommands.ImportBehaviorPackAsync(
        adoptingPackRoot,
        adoptingNamespace,
        adoptionManifest,
        CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["warmup"])
{
    var starter = await CortexLocalProductCommands.ImportStarterCorpusAsync(
        Path.Combine(AppContext.BaseDirectory, "behavior-packs"),
        onlyIfEmpty: true,
        CancellationToken.None);
    if (starter.ExitCode != 0)
    {
        Console.WriteLine(starter.Json);
        return starter.ExitCode;
    }
    var result = await CortexOfflineProductCommands.WarmupAsync(CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args.Length >= 2 && string.Equals(args[0], "offline", StringComparison.Ordinal))
{
    var result = await CortexOfflineProductCommands.RunAsync(
        args,
        cancellationToken: CancellationToken.None);
    Console.WriteLine(result.Json);
    return result.ExitCode;
}

if (args is ["geometry-shadow-status"])
{
    return await PegasusCortexMcpLocalProgram.RunAsync(args).ConfigureAwait(false);
}

Console.Error.WriteLine("Usage: PegasusCortex.exe [connections describe|connections list|connections register/rotate --client <id> --application <exe> --transport native/mcp --defaults-file <json>|connections revoke <id>|connections rotate-codex|version|status|doctor|metrics [--days 1..90]|backup|migrate-knowledge-spaces|seed-defaults|import-behavior-pack --pack-root <absolute-directory> --namespace knowledge.shared [--adoption-manifest <absolute-file>]|assets import --file <absolute-file> --metadata-file <absolute-json> [--expected-ref <sha256>]|assets import-icons --pack-root <absolute-directory> --namespace <namespace> --catalog-sha <sha256> --content-sha <sha256>|assets import-visual-library --manifest <absolute-file> --sha256 <sha256>|assets expose --namespace <namespace>|warmup|offline ...]");
Console.Error.WriteLine(PegasusCortexMcpLocalProgram.StdioRetirementMessage);
return 2;
