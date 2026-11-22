using System.Security.Cryptography;
using System.Text.Json;
using Pegasus.Cortex.Product;

internal static class McpConnectionApprovalTests
{
    internal static void Run()
    {
        var priorCapture = Environment.GetEnvironmentVariable("PEGASUS_AGENTOPS_CAPTURED");
        try
        {
            Environment.SetEnvironmentVariable("PEGASUS_AGENTOPS_CAPTURED", "1");
            try
            {
                _ = CortexMcpClientCredentials.ReadHeaderJson(new string('1', 32));
                throw new InvalidOperationException("Captured output reached the credential helper.");
            }
            catch (UnauthorizedAccessException) { }
        }
        finally { Environment.SetEnvironmentVariable("PEGASUS_AGENTOPS_CAPTURED", priorCapture); }
        var secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var connection = new CortexMcpConnectionRequest(CortexMcpConnections.RequestSchema,
            new string('1', 32), Convert.ToHexString(SHA256.HashData(secret)).ToLowerInvariant(),
            "Codex development", "Codex", "My project chat");
        var grant = new CortexMcpConnectionGrant(connection, new string('2', 32), 1, true, false);
        var store = new CortexMcpConnectionStore(CortexMcpConnections.StoreSchema, new string('3', 32), 1, [grant]);
        var header = "Bearer " + connection.ConnectionId + "." + CortexMcpConnections.Base64Url(secret);
        Check(CortexMcpConnections.Validate(store) == store, "valid registry refused");
        var authority = CortexMcpConnections.Authenticate(store, header) ?? throw new Exception("credential refused");
        Equal(authority, CortexMcpConnections.Authenticate(store, header), "reconnect changed persistent identity");
        Check(CortexMcpConnections.Find(store, authority) is not null, "ordinary approval unavailable");
        Check(CortexMcpConnections.Find(store, authority, true) is null, "ordinary grant gained Full control");
        var fullGrant = grant with { FullControl = true, Revision = 2 };
        var full = store with { Revision = 2, Connections = [fullGrant] };
        var fullAuthority = CortexMcpConnections.Authenticate(full, header)!;
        Check(CortexMcpConnections.Find(full, fullAuthority, true) is not null, "explicit Full control unavailable");
        Check(CortexMcpConnections.Find(full, authority) is null, "old admitted revision survived approval change");
        var revokedFull = full with { Revision = 3, Connections = [fullGrant with { FullControl = false, Revision = 3 }] };
        Check(CortexMcpConnections.Find(revokedFull, fullAuthority, true) is null, "revoked Full control survived");
        Check(CortexMcpConnections.Authenticate(revokedFull, header) is not null, "Full-control-only revoke removed ordinary access");
        var revoked = full with { Revision = 4, Connections = [fullGrant with { Enabled = false, FullControl = false, Revision = 4 }] };
        Check(CortexMcpConnections.Authenticate(revoked, header) is null, "revoked credential authenticated");
        Check(CortexMcpConnections.Find(revoked, fullAuthority, true) is null, "queued old authority survived revocation");
        var rotated = full with { Connections = [fullGrant with { CredentialGeneration = new string('4', 32) }] };
        Check(CortexMcpConnections.Find(rotated, fullAuthority) is null, "old credential generation accepted");
        Check(CortexMcpConnections.Find(full with { Authority = new string('5', 32) }, fullAuthority) is null, "other registry authority accepted");
        foreach (var invalid in new string?[] { null, "", "Bearer invalid", header.ToLowerInvariant(), header + " ",
            header.Replace(connection.ConnectionId, new string('0', 32)), header[..^1] + (header[^1] == 'A' ? "B" : "A"),
            "Bearer " + connection.ConnectionId + "." + new string('_', 43) })
            Check(CortexMcpConnections.Authenticate(store, invalid) is null, "invalid presented credential was rescued");
        Check(CortexMcpConnections.Authenticate(null, header) is null, "missing approvals authenticated");
        var setup = CortexMcpClientConfiguration.Codex(@"C:\Program Files\Pegasus Cortex\PegasusCortex.exe", connection.ConnectionId);
        Check(setup.Contains("http_headers_helper = ") && setup.Contains(connection.ConnectionId) &&
            !setup.Contains("Bearer ") && !setup.Contains(CortexMcpConnections.Base64Url(secret)),
            "client setup disclosed a credential or omitted the explicit helper");
        Reject(() => CortexMcpClientConfiguration.Codex(@"C:\unsafe%NAME%\PegasusCortex.exe", connection.ConnectionId));
        Reject(() => CortexMcpClientConfiguration.Codex(@"C:\Program Files\Pegasus Cortex\PegasusCortex.exe",
            connection.ConnectionId, "http://localhost:47831/mcp/cortex"));
        Reject(() => CortexMcpConnections.Validate(store with { Connections = [grant, grant] }));
        Reject(() => CortexMcpConnections.Validate(store with { Connections = [grant with { Enabled = false, FullControl = true }] }));
        Reject(() => CortexMcpConnections.Validate(connection with { Name = "bad\r\nlabel" }));
        Reject(() => CortexMcpConnections.Validate(connection with { CredentialSha256 = "1234" }));
        Reject(() => CortexMcpConnections.Validate(connection with { ConnectionId = "chat-id" }));
        var root = Path.Combine(Path.GetTempPath(), "pcx-connection-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "connections.json");
            Check(CortexMcpConnections.Read(path, _ => { }) is null, "missing store fabricated approvals");
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(store, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Check(CortexMcpConnections.Read(path, _ => { })!.Connections.Length == 1, "bounded fixture read failed");
            File.WriteAllText(path, "{\"schema\":\"one\",\"schema\":\"two\"}");
            Reject(() => CortexMcpConnections.Read(path, _ => { }));
            using (var stream = new FileStream(path, FileMode.Create)) stream.SetLength(CortexMcpConnections.MaximumBytes + 1L);
            Reject(() => CortexMcpConnections.Read(path, _ => { }));
            var requestPath = Path.Combine(root, "request.json");
            CortexMcpClientCredentials.WritePublicRequest(requestPath, connection);
            Equal(connection, CortexMcpConnections.ReadRequest(requestPath), "public request changed");
            var publicText = File.ReadAllText(requestPath);
            Check(!publicText.Contains("Bearer ") && !publicText.Contains(CortexMcpConnections.Base64Url(secret)),
                "public enrollment exported client credential");
        }
        finally { Directory.Delete(root, true); CryptographicOperations.ZeroMemory(secret); }
        Console.WriteLine("PASS MCP connection approval fixtures: persistent identity, explicit Full control, revisions, revocation, rotation and bounded public metadata.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) { Check(EqualityComparer<T>.Default.Equals(expected, actual), message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException) { return; }
        throw new InvalidOperationException("Unsafe connection metadata accepted.");
    }
}
