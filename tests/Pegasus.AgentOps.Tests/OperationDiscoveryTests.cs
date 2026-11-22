using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;

internal static class OperationDiscoveryTests
{
    internal static Task RunAsync()
    {
        var definition = PaoProgram.UnifiedMcpToolDefinition();
        var selectors = definition["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray()
            .Select(value => value!.GetValue<string>()).ToArray();
        Check(selectors.Length > 0 && selectors.Distinct(StringComparer.Ordinal).Count() == selectors.Length,
            "Advertised selectors must be unique.");
        foreach (var selector in selectors)
        {
            var help = PaoProgram.UnifiedMcpOperationHelp(selector)
                ?? throw new InvalidOperationException("Missing selected metadata: " + selector);
            var allowed = help["allowed"]!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var required = help["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            var schema = help["inputSchema"]!.AsObject();
            var fields = schema["properties"]!.AsObject();
            Check(help["name"]!.GetValue<string>() == selector &&
                help["availability"]!.GetValue<string>() == "unobserved" &&
                !string.IsNullOrWhiteSpace(help["description"]!.GetValue<string>()) &&
                !string.IsNullOrWhiteSpace(help["executionRoute"]!.GetValue<string>()),
                "Selected metadata must identify its static contract without claiming readiness.");
            Check(required.All(allowed.Contains) && fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal).SetEquals(allowed) &&
                fields["op"]!["const"]!.GetValue<string>() == selector &&
                schema["required"]!.AsArray().Any(value => value!.GetValue<string>() == "op") &&
                schema["additionalProperties"]!.GetValue<bool>() == false,
                "Selected schema must retain the advertised field boundary and exact operation selector.");
            help["allowed"]!.AsArray().Clear();
            fields.Clear();
            var reloaded = PaoProgram.UnifiedMcpOperationHelp(selector)!;
            Check(reloaded["allowed"]!.AsArray().Count == allowed.Count &&
                reloaded["inputSchema"]!["properties"]!.AsObject().Count == allowed.Count,
                "A caller must not mutate another caller's contract metadata.");
        }
        Check(PaoProgram.UnifiedMcpOperationHelp("unknown_operation") is null &&
            PaoProgram.UnifiedMcpOperationHelp("") is null, "Unknown operations must not acquire an invented schema.");
        var registry = PaoProgram.UnifiedMcpOperationHelp("registry_read")!;
        foreach (var action in RegistryOperationContract.Actions)
        {
            var selected = PaoProgram.UnifiedMcpOperationHelp(RegistryOperationContract.PublicName(action))!;
            var example = JsonNode.Parse(selected["mcp_example"]!.GetValue<string>())!;
            Check(example["op"]!.GetValue<string>() == RegistryOperationContract.PublicName(action) &&
                RegistryOperationContract.Parse(action, example["text"]!.GetValue<string>()).Action == action,
                "Selected registry examples must pass the real fixed contract parser.");
        }
        Check(registry["description"]!.GetValue<string>().StartsWith("Read one exact value", StringComparison.Ordinal) &&
            !registry["constraints"]!.GetValue<string>().Contains("parentGeneration", StringComparison.Ordinal) &&
            registry["inputSchema"]!["properties"]!["text"]!["maxLength"]!.GetValue<int>() == RegistryOperationContract.MaxRequestBytes,
            "A registry leaf must not substitute other actions' parameters for its own contract.");
        Check(!registry["allowed"]!.AsArray().Any(value => value!.GetValue<string>() == "root") &&
            registry["executionRoute"]!.GetValue<string>().Contains("no automatic service", StringComparison.Ordinal),
            "Registry discovery must preserve owner-only routing.");
        Check(PaoProgram.UnifiedMcpOperationHelp("certificate_issue_machine_server")!["executionRoute"]!.GetValue<string>()
                .Contains("same-owner UAC", StringComparison.Ordinal) &&
            PaoProgram.UnifiedMcpOperationHelp("certificate_issue_server")!["executionRoute"]!.GetValue<string>()
                .Contains("ordinary owner", StringComparison.Ordinal),
            "Certificate discovery must distinguish explicit machine-key elevation from ordinary leaf issuance.");
        Check(PaoProgram.UnifiedMcpOperationHelp("service_status")!["executionRoute"]!.GetValue<string>()
                .Contains("LocalSystem", StringComparison.Ordinal) &&
            PaoProgram.UnifiedMcpOperationHelp("admin_validate")!["executionRoute"]!.GetValue<string>()
                .Contains("no host execution", StringComparison.Ordinal) &&
            PaoProgram.UnifiedMcpOperationHelp("job_retry")!["executionRoute"]!.GetValue<string>()
                .Contains("System jobs only", StringComparison.Ordinal), "Discovery must not substitute execution identities.");
        Console.WriteLine($"PASS selected PAO discovery metadata for {selectors.Length} advertised operations; pure catalog, no runtime calls");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
