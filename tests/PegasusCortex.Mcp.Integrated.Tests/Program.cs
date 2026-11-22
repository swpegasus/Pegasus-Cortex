using System.Diagnostics;
using System.Reflection;
using Pegasus.Cortex.Core.Query;
using Pegasus.Cortex.Mcp.Integrated.Runtime;
using Pegasus.Cortex.Mcp.Knowledge;
using Pegasus.Cortex.Service.Configuration;

if(args is ["--metadata-read-bounds-only"])
{
    await ProtectedLocalFileReadBoundsTests.RunAsync();
    Console.WriteLine("PASS integrated protected metadata opened-handle bounds.");
    return 0;
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("protected integrated authority separates review and authenticates rows",
        CortexMcpSecurityTests.ProtectedAuthorityAndRowKeyAsync),
    ("protected receipt authority signs verifies and rejects changed proofs",
        CortexMcpSecurityTests.ProtectedReceiptAuthorityAsync),
    ("integrated project brief is capability gated and sync stays off MCP",
        IntegratedCapabilityTests.ToolAdvertisementIsFailClosedAsync),
    ("integrated transfer importer pins canonical protected bundle bytes",
        CortexIntegratedTransferImportTests.BundlePathHashAndCanonicalityAsync),
    ("integrated transfer importer requires exact server project authority",
        CortexIntegratedTransferImportTests.ServerProjectAuthorizationAsync),
    ("integrated transfer importer verifies regenerated brief fields",
        CortexIntegratedTransferImportTests.BriefRegenerationReadbackAsync),
    ("integrated sync gateway preserves exact replay scope proof and full brief fields",
        CortexIntegratedSyncGatewayTests.ExactReplayScopeAndProofAsync),
    ("integrated sync gateway rejects oversize failure conflict and converges concurrency",
        CortexIntegratedSyncGatewayTests.LimitsFailuresAndConcurrencyAsync),
    ("explicit integrated profile fails closed without complete configuration",
        IntegratedProfileTests.MissingAndIncompleteConfigurationFailsClosedAsync),
    ("integrated profile parses the explicit trusted-loopback policy",
        IntegratedProfileTests.TrustedLoopbackPolicyParsingAsync),
    ("integrated executable never falls back when configuration is missing",
        IntegratedProfileTests.ExecutableMissingConfigurationFailsClosedAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run().ConfigureAwait(false);
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

if (failures.Count != 0)
{
    Console.Error.WriteLine(
        $"{failures.Count}/{tests.Length} Pegasus Cortex integrated MCP checks failed.");
    return 1;
}

Console.WriteLine(
    $"Pegasus Cortex integrated MCP checks passed ({tests.Length}/{tests.Length}).");
return 0;

internal static class IntegratedProfileTests
{
    private static readonly string[] RequiredConfigurationVariables =
    [
        "PEGASUS_CORTEX_AUTHORITY_FILE",
        "PEGASUS_CORTEX_ROW_KEY_FILE",
        "PEGASUS_CORTEX_RUNTIME_CONNECTION"
    ];

    private static readonly string[] ConfigurationVariables =
    [
        .. RequiredConfigurationVariables,
        "PEGASUS_CORTEX_MIGRATION_CONNECTION"
    ];

    public static async Task MissingAndIncompleteConfigurationFailsClosedAsync()
    {
        var captured = ConfigurationVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        try
        {
            foreach (var name in ConfigurationVariables)
                Environment.SetEnvironmentVariable(name, null);

            var missing = await CortexMcpIntegratedHost.TryCreateAsync(
                CancellationToken.None);
            Assert.False(missing.Available, "Missing configuration activated a runtime.");
            Assert.Equal<CortexMcpIntegratedRuntime?>(null, missing.Runtime);
            Assert.Equal<CortexMcpRuntimeComposition?>(null, missing.Composition);
            Assert.Equal("not-configured", missing.State);
            Assert.Equal("cortex_hybrid_not_configured", missing.IssueCode);

            Environment.SetEnvironmentVariable(
                RequiredConfigurationVariables[0],
                Path.Combine(Path.GetTempPath(), "missing-authority.json"));
            var incomplete = await CortexMcpIntegratedHost.TryCreateAsync(
                CancellationToken.None);
            Assert.False(incomplete.Available, "Incomplete configuration activated a runtime.");
            Assert.Equal<CortexMcpIntegratedRuntime?>(null, incomplete.Runtime);
            Assert.Equal<CortexMcpRuntimeComposition?>(null, incomplete.Composition);
            Assert.Equal("not-ready", incomplete.State);
            Assert.Equal("cortex_hybrid_configuration_incomplete", incomplete.IssueCode);

            Environment.SetEnvironmentVariable(
                RequiredConfigurationVariables[1],
                Path.Combine(Path.GetTempPath(), "missing-row-key.json"));
            Environment.SetEnvironmentVariable(
                RequiredConfigurationVariables[2],
                "Host=127.0.0.1;Port=5432;Database=unused;Username=runtime;Password=unused");
            var runtimeOnly = await CortexMcpIntegratedHost.TryCreateAsync(
                CancellationToken.None);
            Assert.False(runtimeOnly.Available, "Invalid protected files activated a runtime.");
            Assert.Equal("not-ready", runtimeOnly.State);
            Assert.Equal(
                "cortex_hybrid_configuration_or_readiness_failed",
                runtimeOnly.IssueCode);
        }
        finally
        {
            foreach (var entry in captured)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }

    public static Task TrustedLoopbackPolicyParsingAsync()
    {
        Assert.Equal(
            CortexPostgreSqlTlsPolicy.TrustedLoopback,
            CortexMcpIntegratedHost.ParseTlsPolicy("trusted-loopback"));
        Assert.Equal(
            CortexPostgreSqlTlsPolicy.LocalLoopbackBootstrap,
            CortexMcpIntegratedHost.ParseTlsPolicy("loopback-bootstrap"));
        Assert.Throws<InvalidOperationException>(() =>
            CortexMcpIntegratedHost.ParseTlsPolicy("localhost"));
        return Task.CompletedTask;
    }

    public static async Task ExecutableMissingConfigurationFailsClosedAsync()
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows()
                ? "PegasusCortex.Mcp.Integrated.exe"
                : "PegasusCortex.Mcp.Integrated");
        Assert.True(
            File.Exists(executable),
            "The integrated MCP executable is unavailable in the test closure.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var name in ConfigurationVariables)
            start.Environment.Remove(name);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The integrated MCP process did not start.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

        Assert.Equal(1, process.ExitCode);
        Assert.Equal(string.Empty, await standardOutput.ConfigureAwait(false));
        var diagnostic = await standardError.ConfigureAwait(false);
        Assert.True(
            diagnostic.Contains(
                "code=cortex_hybrid_not_configured",
                StringComparison.Ordinal),
            "The integrated executable did not report missing configuration.");
        Assert.True(
            diagnostic.Contains(
                "no local fallback was started",
                StringComparison.Ordinal),
            "The integrated executable did not make fail-closed selection explicit.");
    }
}

internal static class IntegratedCapabilityTests
{
    public static Task ToolAdvertisementIsFailClosedAsync()
    {
        var mainCapabilities =
            CortexMcpRuntimeCapability.Read |
            CortexMcpRuntimeCapability.Correlate |
            CortexMcpRuntimeCapability.Propose |
            CortexMcpRuntimeCapability.Review |
            CortexMcpRuntimeCapability.DelegateReview |
            CortexMcpRuntimeCapability.RetainedRead |
            CortexMcpRuntimeCapability.RetainedWrite |
            CortexMcpRuntimeCapability.ProjectBrief |
            CortexMcpRuntimeCapability.Sync;
        var ready = new CortexMcpRuntimeComposition(
            new CortexMcpRuntimeDescriptor(
                "integrated-postgresql",
                "ready",
                null,
                "endpoint-1",
                "principal-1",
                "subject-1",
                mainCapabilities,
                UsesNetwork: true,
                UsesDatabase: true,
                UsesAuthentication: true,
                UsesLiveServices: true,
                "Nexus gateway only",
                StorageKind: CortexMcpStorageKinds.PostgreSql,
                SupportsProvisionalPublication: true),
            ProjectKnowledge: Port<ICortexProjectKnowledgeRuntime>(),
            ProjectCorrelation: Port<ICortexProjectCorrelationRuntime>(),
            ProposalSubmission: Port<ICortexProposalSubmissionRuntime>(),
            ProposalQuery: Port<ICortexProposalQueryRuntime>(),
            ProposalReview: Port<ICortexProposalReviewRuntime>(),
            ReviewGrant: Port<ICortexReviewGrantRuntime>(),
            ProjectBrief: Port<ICortexProjectBriefRuntime>(),
            RetainedAssertionReader: Port<ICortexRetainedAssertionReaderRuntime>(),
            RetainedAssertionWriter: Port<ICortexRetainedAssertionWriterRuntime>(),
            Sync: Port<ICortexSyncRuntime>(),
            PublicationChange: Port<ICortexPublicationChangeRuntime>());
        var readyNames = CortexMcpContract.ListTools(ready)
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(value => value["name"]?.GetValue<string>())
            .ToArray();
        Assert.True(
            readyNames.SequenceEqual(
                [
                    "cortex_discover",
                    "cortex_status",
                    "cortex_search",
                    "cortex_read",
                    "cortex_project_brief",
                    "cortex_read_retained_record",
                    "cortex_submit_task_snapshot",
                    "cortex_submit_work_packet",
                    "cortex_submit_work_packet_handoff_receipt",
                    "cortex_submit_work_packet_acceptance_receipt",
                    "cortex_correlate_project",
                    "cortex_submit_proposal",
                    "cortex_list_proposals",
                    "cortex_change_publication",
                    "cortex_review_proposal",
                    "cortex_manage_review_grant"
                ],
                StringComparer.Ordinal),
            "The main integrated principal did not receive its exact 16-tool surface.");
        Assert.False(
            readyNames.Any(name => name?.Contains("sync", StringComparison.Ordinal) == true),
            "The Nexus-only synchronization port leaked into MCP tool discovery.");

        var auditor = new CortexMcpRuntimeComposition(
            ready.Descriptor with
            {
                PrincipalName = "auditor",
                Capabilities = CortexMcpRuntimeCapability.Audit
            },
            ProposalQuery: Port<ICortexProposalQueryRuntime>(),
            ProposalAudit: Port<ICortexProposalAuditRuntime>());
        var auditorNames = CortexMcpContract.ListTools(auditor)
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(value => value["name"]?.GetValue<string>())
            .ToArray();
        Assert.True(
            auditorNames.SequenceEqual(
                [
                    "cortex_discover",
                    "cortex_status",
                    "cortex_list_proposals",
                    "cortex_submit_audit"
                ],
                StringComparer.Ordinal),
            "The auditor principal did not receive its exact four-tool surface.");

        var notReady = ready with
        {
            Descriptor = ready.Descriptor with { State = "not-ready" }
        };
        var notReadyNames = CortexMcpContract.ListTools(notReady)
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(value => value["name"]?.GetValue<string>())
            .ToArray();
        Assert.False(
            notReadyNames.Skip(2).Any(),
            "A not-ready integrated runtime advertised a data or mutation tool.");
        return Task.CompletedTask;
    }

    private static T Port<T>() where T : class =>
        DispatchProxy.Create<T, IntegratedRuntimePortProxy>();
}

internal class IntegratedRuntimePortProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException(
            $"The contract-only runtime port cannot invoke '{targetMethod?.Name}'.");
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) =>
        True(!condition, message);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"Expected '{expected}', received '{actual}'.");
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name}.");
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name}.");
    }
}
