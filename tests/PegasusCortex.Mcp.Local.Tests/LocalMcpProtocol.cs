using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pegasus.Cortex.Contracts.Serialization;

internal static class LocalMcpProtocol
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    public static string Initialize(int id = 1) => Serialize(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "local-mcp-tests",
                ["version"] = "1.0"
            }
        }
    });

    public static string Initialized() => Serialize(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/initialized",
        ["params"] = new JsonObject()
    });

    public static string ToolsList(int id) => Serialize(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/list",
        ["params"] = new JsonObject()
    });

    public static string ToolCall(int id, string name, JsonObject arguments) =>
        Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments
            }
        });

    public static JsonNode AssertionNode<T>(T assertion) =>
        JsonNode.Parse(CortexCanonicalJson.Serialize(assertion)) ??
        throw new InvalidOperationException("The assertion did not serialize.");

    public static JsonObject SubmitArguments(
        string projectLinkId,
        string projectNamespace,
        JsonNode assertion) => new()
    {
        ["projectLinkId"] = projectLinkId,
        ["projectNamespace"] = projectNamespace,
        ["assertion"] = assertion
    };

    public static JsonObject SubmitReceiptArguments(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        JsonNode assertion) => new()
    {
        ["projectLinkId"] = projectLinkId,
        ["projectId"] = projectId,
        ["projectNamespace"] = projectNamespace,
        ["assertion"] = assertion
    };

    public static JsonObject ReadArguments(
        string projectLinkId,
        string projectId,
        string projectNamespace,
        string kind,
        string documentId,
        long revision,
        string contentHash) => new()
    {
        ["projectLinkId"] = projectLinkId,
        ["projectId"] = projectId,
        ["projectNamespace"] = projectNamespace,
        ["kind"] = kind,
        ["documentId"] = documentId,
        ["revision"] = revision,
        ["contentHash"] = contentHash
    };

    public static JsonObject ProjectBriefArguments(
        string projectLinkId,
        string knownAuthoritySetHash) => new()
    {
        ["projectLinkId"] = projectLinkId,
        ["knownAuthoritySetHash"] = knownAuthoritySetHash
    };

    public static JsonObject ParseResponse(string line) =>
        JsonNode.Parse(line)?.AsObject() ??
        throw new InvalidDataException("The MCP response is not a JSON object.");

    public static JsonObject ResponseById(
        IReadOnlyList<JsonObject> responses,
        int id) =>
        responses.Single(response =>
            response["id"] is JsonValue value &&
            value.TryGetValue<int>(out var actual) &&
            actual == id);

    public static JsonObject Structured(JsonObject response) =>
        response["result"]?.AsObject()["structuredContent"]?.AsObject() ??
        throw new InvalidDataException("The MCP response has no structured tool content.");

    public static JsonObject ToolEnvelope(JsonObject response) =>
        response["result"]?.AsObject() ??
        throw new InvalidDataException("The MCP response has no tool result.");

    public static string DiagnosticCode(JsonObject structured) =>
        structured["diagnostic"]?.AsObject()["code"]?.GetValue<string>() ??
        throw new InvalidDataException("The structured result has no diagnostic code.");

    public static string Serialize(JsonNode value) =>
        value.ToJsonString(JsonOptions);
}

internal sealed record LocalProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public IReadOnlyList<JsonObject> Responses =>
        StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(LocalMcpProtocol.ParseResponse)
            .ToArray();
}

internal static class LocalProcessHarness
{
    public static Task<LocalProcessResult> RunAsync(
        IEnumerable<string> arguments,
        IEnumerable<string> inputLines,
        TimeSpan? timeout = null) =>
        RunCoreAsync(arguments, inputLines, timeout, useOwnedProtocolChild: true);

    // Public-entry refusal checks must never take the fixture protocol route.
    public static Task<LocalProcessResult> RunPublicEntryAsync(
        IEnumerable<string> arguments,
        IEnumerable<string> inputLines,
        TimeSpan? timeout = null) =>
        RunCoreAsync(arguments, inputLines, timeout, useOwnedProtocolChild: false);

    private static async Task<LocalProcessResult> RunCoreAsync(
        IEnumerable<string> arguments,
        IEnumerable<string> inputLines,
        TimeSpan? timeout,
        bool useOwnedProtocolChild)
    {
        using var process = StartCore(arguments, useOwnedProtocolChild);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        foreach (var line in inputLines)
            await process.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        process.StandardInput.Close();

        using var deadline = new CancellationTokenSource(
            timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new LocalProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    public static Process Start(IEnumerable<string> arguments) =>
        StartCore(arguments, useOwnedProtocolChild: true);

    private static Process StartCore(
        IEnumerable<string> arguments,
        bool useOwnedProtocolChild)
    {
        var supplied = arguments.ToArray();
        // Only the existing explicit owned-profile test shape is redirected.
        // Other CLI modes and malformed/public retirement inputs use the real entry.
        var ownedProfile = useOwnedProtocolChild && supplied is
            ["stdio", "--profile", _, "--state-root", _];
        var executable = ownedProfile
            ? Path.ChangeExtension(typeof(LocalProcessHarness).Assembly.Location,
                OperatingSystem.IsWindows() ? ".exe" : null)
            : LocalTestContext.LocalExecutablePath();
        if (!File.Exists(executable))
            throw new FileNotFoundException("The owned test executable was not built.", executable);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var selected = ownedProfile
            ? new[] { "--owned-profile-protocol-child", supplied[2], supplied[4] }
            : supplied;
        foreach (var argument in selected)
            start.ArgumentList.Add(argument);
        PoisonIntegratedEnvironment(start);
        return Process.Start(start) ??
            throw new InvalidOperationException("The local MCP process did not start.");
    }

    public static void TryKill(Process process)
    {
        if (process.HasExited)
            return;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }

    private static void PoisonIntegratedEnvironment(ProcessStartInfo start)
    {
        start.Environment["PEGASUS_CORTEX_AUTHORITY_FILE"] = "poison-authority";
        start.Environment["PEGASUS_CORTEX_ROW_KEY_FILE"] = "poison-key";
        start.Environment["PEGASUS_CORTEX_RUNTIME_CONNECTION"] =
            "Host=198.51.100.10;Database=poison";
        start.Environment["PEGASUS_CORTEX_MIGRATION_CONNECTION"] =
            "Host=203.0.113.10;Database=poison";
        start.Environment["PEGASUS_CORTEX_TLS_POLICY"] = "poison";
        start.Environment["PEGASUS_CORTEX_ROOT_CA"] = "poison-root";
        start.Environment["PEGASUS_CORTEX_POSTGRESQL_SERVER_SPKI_SHA256"] =
            new string('f', 64);
    }
}

internal static class LocalAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected '{expected}', actual '{actual}'.");
        }
    }

    public static async Task ThrowsAsync<TException>(
        Func<Task> action,
        string message)
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
        throw new InvalidOperationException(message);
    }
}
