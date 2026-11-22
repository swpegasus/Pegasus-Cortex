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

if (args is ["mcp", "headers", "--connection", var connectionId])
{
    try
    {
        Console.WriteLine(Pegasus.Cortex.Product.CortexMcpClientCredentials.ReadHeaderJson(connectionId));
        return 0;
    }
    catch (Exception error) when (error is UnauthorizedAccessException or IOException or
        System.ComponentModel.Win32Exception or InvalidDataException or PlatformNotSupportedException)
    {
        // Header-helper errors never echo headers, credentials, paths or native messages.
        Console.Error.WriteLine("Cortex MCP client credential is unavailable.");
        return 12;
    }
}

if (args is ["mcp","enroll","--name",var connectionName,"--application",var connectionApplication,
    "--request-file",var connectionRequestPath])
{
    Pegasus.Cortex.Product.CortexMcpConnectionRequest? request=null;
    try
    {
        if(File.Exists(connectionRequestPath)||Directory.Exists(connectionRequestPath))
            throw new IOException("The public enrollment destination is occupied.");
        request=Pegasus.Cortex.Product.CortexMcpClientCredentials.Create(connectionName,connectionApplication);
        Pegasus.Cortex.Product.CortexMcpClientCredentials.WritePublicRequest(connectionRequestPath,request);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {state="pending_approval",
            connectionId=request.ConnectionId,requestFile=Path.GetFullPath(connectionRequestPath)}));
        return 0;
    }
    catch(Exception error)when(error is IOException or UnauthorizedAccessException or
        System.ComponentModel.Win32Exception or InvalidDataException or PlatformNotSupportedException)
    {
        // Public recovery coordinates are safe; the vault credential is never emitted.
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {state=request is null?"not_created":"public_request_not_saved",
            request, error=error.GetType().Name}));
        return 12;
    }
}

if (args is ["mcp", "config", "--connection", var configurationConnectionId])
{
    try
    {
        var product = Pegasus.Cortex.Product.CortexProductLayout.ResolveCurrent();
        var settings = Pegasus.Cortex.Mcp.Hosting.CortexMcpSettingsStore.ReadForInstallRoot(product.InstallRoot);
        var endpoint = new UriBuilder("https", settings.ServerName, settings.Port, "/mcp/cortex").Uri.AbsoluteUri;
        Console.Write(Pegasus.Cortex.Product.CortexMcpClientConfiguration.Codex(
            Path.Combine(product.InstallRoot, "PegasusCortex.exe"), configurationConnectionId, endpoint));
        return 0;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine("Cortex MCP client configuration is unavailable: " + error.GetType().Name);
        return 12;
    }
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
