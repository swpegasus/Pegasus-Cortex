using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Pegasus.AgentOps.Engine;

if (args is ["--desktop-fixture"])
    return RunDesktopFixture();
if (args is ["--verify-packaged-host", var packageRoot])
    return await VerifyPackagedHostAsync(packageRoot);

var tests = new (string Name, Func<Task> Run)[]
{
    ("status_and_eof_use_bounded_protocol_frames", StatusFrameAsync),
    ("unknown_fields_and_oversized_frames_fail_closed", InvalidFramesAsync),
    ("public_click_and_move_bounds_are_exact", PublicBoundsAsync),
    ("fixed_find_and_control_contract_preserves_identity", QueryContractAsync),
    ("optional_query_text_omission_and_empty_are_equivalent", OptionalQueryTextAsync),
    ("set_text_is_ephemeral_and_never_echoed", EphemeralTextAsync),
    ("uncertain_mutation_returns_partial_recovery", UncertainMutationAsync),
    ("provider_missing_runtime_reports_unavailable", MissingRuntimeAsync),
    ("staged_autoitx_loads_registration_free", StagedRuntimeAsync),
    ("real_desktop_control_and_window_capture_are_registration_free", DesktopControlAsync),
    ("surface_contains_no_generic_or_prohibited_operations", ClosedSurfaceAsync)
};

if (args.Any(name => !tests.Any(test => test.Name == name)))
{
    Console.Error.WriteLine("Select exact advertised test names or omit arguments for the complete suite.");
    return 2;
}
var selectedTests = args.Length == 0 ? tests : tests.Where(test => args.Contains(test.Name, StringComparer.Ordinal)).ToArray();
var failed = 0;
foreach (var test in selectedTests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name} {ex}");
    }
}
Console.WriteLine($"RESULT total={selectedTests.Length} passed={selectedTests.Length - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static async Task StatusFrameAsync()
{
    using var provider = new FakeProvider();
    var response = await ExchangeAsync(new(1, "status-1", UiOperation.Status), provider);
    Equal(true, response.GetProperty("ok").GetBoolean());
    Equal("status", response.GetProperty("operation").GetString());
    Equal("fake", response.GetProperty("data").GetProperty("provider").GetString());
}

static async Task<int> VerifyPackagedHostAsync(string packageRoot)
{
    var host = Path.Combine(Path.GetFullPath(packageRoot), "bin", "ui", "Pegasus.AgentOps.UiHost.exe");
    if (!File.Exists(host)) throw new Exception("Packaged UiHost is absent.");
    var start = new ProcessStartInfo(host)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add("--stdio-v1");
    start.Environment.Clear();
    using var process = Process.Start(start) ?? throw new Exception("Packaged UiHost did not start.");
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) } };
    var payload = JsonSerializer.SerializeToUtf8Bytes(new UiHostRequest(1, "package-status", UiOperation.Status), options);
    var header = new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,payload.Length);
    await process.StandardInput.BaseStream.WriteAsync(header);await process.StandardInput.BaseStream.WriteAsync(payload);await process.StandardInput.BaseStream.FlushAsync();process.StandardInput.Close();
    await ReadExactlyAsync(process.StandardOutput.BaseStream,header);var length=BinaryPrimitives.ReadInt32LittleEndian(header);Check(length is >0 and <=UiHostProtocol.MaxResponseBytes,"packaged response frame invalid");var response=new byte[length];await ReadExactlyAsync(process.StandardOutput.BaseStream,response);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Equal(0,process.ExitCode);using var json=JsonDocument.Parse(response);Equal(true,json.RootElement.GetProperty("ok").GetBoolean());Equal(true,json.RootElement.GetProperty("data").GetProperty("available").GetBoolean());Console.WriteLine("PACKAGE_UI status=ready provider=autoitx-registration-free");return 0;
}

static async Task ReadExactlyAsync(Stream stream,byte[] buffer)
{
    var offset=0;while(offset<buffer.Length){var read=await stream.ReadAsync(buffer.AsMemory(offset));if(read==0)throw new EndOfStreamException();offset+=read;}
}

static async Task InvalidFramesAsync()
{
    using var provider = new FakeProvider();
    var payload = Encoding.UTF8.GetBytes("{\"version\":1,\"id\":\"x\",\"operation\":\"status\",\"surprise\":1}");
    var response = await ExchangeRawAsync(payload, provider);
    Equal(false, response.GetProperty("ok").GetBoolean());
    Equal("invalid", response.GetProperty("err").GetString());

    using var input = new MemoryStream();
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, UiHostProtocol.MaxRequestBytes + 1);
    input.Write(header);
    input.Position = 0;
    using var output = new MemoryStream();
    var exit = await UiHostProtocol.RunAsync(input, output, provider, CancellationToken.None);
    Equal(8, exit);
    Equal(0L, output.Length);
}

static async Task PublicBoundsAsync()
{
    using var provider = new FakeProvider();
    var click = await ExchangeAsync(new(
        1, "click-bound", UiOperation.ControlClick,
        Window: provider.Identity,
        ControlHandle: 222,
        Clicks: 4), provider);
    Equal("clicks", click.GetProperty("field").GetString());

    var move = await ExchangeAsync(new(
        1, "move-bound", UiOperation.Move,
        Window: provider.Identity,
        Bounds: new UiBounds(32_769, 0, 100, 100)), provider);
    Equal("bounds", move.GetProperty("field").GetString());

    var accepted = await ExchangeAsync(new(
        1, "move-edge", UiOperation.Move,
        Window: provider.Identity,
        Bounds: new UiBounds(-32_768, 32_768, 16_384, 1)), provider);
    Equal(true, accepted.GetProperty("ok").GetBoolean());
}

static async Task QueryContractAsync()
{
    using var provider = new FakeProvider();
    var query = await ExchangeAsync(new(
        1, "find", UiOperation.FindWindow,
        Query: new UiWindowQuery("Pegasus Ω", "ready & waiting")), provider);
    Equal(42, query.GetProperty("data").GetProperty("processId").GetInt32());
    Equal("Pegasus Ω", provider.LastQuery!.Title);

    var response = await ExchangeAsync(new(
        1, "control", UiOperation.ControlResolve,
        Window: provider.Identity,
        Control: "[CLASS:Edit; INSTANCE:1]"), provider);
    Equal(222L, response.GetProperty("data").GetInt64());
    Equal(provider.Identity, provider.LastWindow);
}

static async Task OptionalQueryTextAsync()
{
    foreach (var match in new[] { "exact", "contains" })
    {
        using var provider = new FakeProvider();
        // Exercise actual wire omission as well as explicit JSON null and empty.
        var omitted = await ExchangeRawAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, id = "title-only", operation = "find_window",
            query = new { title = "Pegasus Ω", match }
        }), provider);
        Equal(true, omitted.GetProperty("ok").GetBoolean());
        Equal<string?>(null, provider.LastQuery!.Text);
        Equal(match, provider.LastQuery.Match);
        var identity = omitted.GetProperty("data").GetRawText();
        Equal(provider.Identity.Handle, omitted.GetProperty("data").GetProperty("handle").GetInt64());
        Equal(provider.Identity.ProcessId, omitted.GetProperty("data").GetProperty("processId").GetInt32());
        Equal(provider.Identity.ProcessStartUtcTicks, omitted.GetProperty("data").GetProperty("processStartUtcTicks").GetInt64());

        foreach (var text in new string?[] { null, string.Empty })
        {
            var response = await ExchangeAsync(new(1, "optional-text", UiOperation.FindWindow,
                Query: new UiWindowQuery("Pegasus Ω", text, match)), provider);
            Equal(true, response.GetProperty("ok").GetBoolean());
            Equal(identity, response.GetProperty("data").GetRawText());
            Equal(text, provider.LastQuery!.Text);
        }

        foreach (var code in new[] { "ui_not_found", "ui_ambiguous" })
        {
            using var failureProvider = new FakeProvider { FindError = code };
            foreach (var text in new string?[] { null, string.Empty })
            {
                var response = await ExchangeAsync(new(1, "find-failure", UiOperation.FindWindow,
                    Query: new UiWindowQuery("Pegasus Ω", text, match)), failureProvider);
                Equal(false, response.GetProperty("ok").GetBoolean());
                Equal(code, response.GetProperty("err").GetString());
                Equal("query", response.GetProperty("field").GetString());
            }
        }
    }

    using var bounded = new FakeProvider();
    var accepted = await ExchangeAsync(new(1, "text-edge", UiOperation.FindWindow,
        Query: new UiWindowQuery("Pegasus", new string('é', 4096))), bounded);
    Equal(true, accepted.GetProperty("ok").GetBoolean());
    foreach (var text in new[] { new string('é', 4096) + "a", "unsafe\0text" })
    {
        var calls = bounded.FindCalls;
        var response = await ExchangeAsync(new(1, "invalid-text", UiOperation.FindWindow,
            Query: new UiWindowQuery("Pegasus", text)), bounded);
        Equal("invalid", response.GetProperty("err").GetString());
        Equal("query.text", response.GetProperty("field").GetString());
        Equal(calls, bounded.FindCalls);
    }
    foreach (var request in new[]
    {
        new UiHostRequest(1, "missing-query", UiOperation.FindWindow),
        new UiHostRequest(1, "empty-title", UiOperation.FindWindow, Query: new UiWindowQuery(string.Empty)),
        new UiHostRequest(1, "invalid-match", UiOperation.FindWindow, Query: new UiWindowQuery("Pegasus", Match: "regex")),
        new UiHostRequest(1, "missing-set-text", UiOperation.ControlSetText, Window: bounded.Identity, ControlHandle: 222)
    })
    {
        var response = await ExchangeAsync(request, bounded);
        Equal(false, response.GetProperty("ok").GetBoolean());
        Equal("invalid", response.GetProperty("err").GetString());
        Equal(request.Id switch
        {
            "missing-query" => "query", "empty-title" => "query.title",
            "invalid-match" => "query.match", _ => "text"
        }, response.GetProperty("field").GetString());
    }
}

static async Task EphemeralTextAsync()
{
    using var provider = new FakeProvider();
    const string secretText = "ephemeral Ω & must-not-echo";
    var json = await ExchangeRawTextAsync(new(
        1, "text", UiOperation.ControlSetText,
        Window: provider.Identity,
        ControlHandle: 222,
        Text: secretText), provider);
    Check(!json.Contains(secretText, StringComparison.Ordinal), "set-text body leaked into response");
    Equal(secretText, provider.LastText);
    using var response = JsonDocument.Parse(json);
    Equal(true, response.RootElement.GetProperty("ok").GetBoolean());
}

static async Task UncertainMutationAsync()
{
    using var provider = new FakeProvider { FailMutationUncertain = true };
    var response = await ExchangeAsync(new(
        1, "click", UiOperation.ControlClick,
        Window: provider.Identity,
        ControlHandle: 222,
        Button: "left",
        Clicks: 1), provider);
    Equal(false, response.GetProperty("ok").GetBoolean());
    Equal("recovery_required", response.GetProperty("state").GetString());
    Equal(true, response.GetProperty("partial").GetBoolean());
    Equal(true, response.GetProperty("recoveryRequired").GetBoolean());
}

static Task MissingRuntimeAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "pao-uihost-missing-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        using var provider = new AutoItXUiAutomationProvider(root);
        var status = provider.Status();
        Equal(false, status.Available);
        try { provider.FindWindow(new("missing")); }
        catch (UiAutomationException ex)
        {
        Equal("ui_unavailable", ex.Code);
            Check(ex.Remedy?.Contains("Stage AutoItX3.Assembly.dll", StringComparison.Ordinal) == true, "missing runtime remedy");
            return Task.CompletedTask;
        }
        throw new Exception("Missing provider runtime did not fail closed.");
    }
    finally
    {
        Directory.Delete(root);
    }
}

static Task StagedRuntimeAsync()
{
    var repo = FindRepoRoot();
    var runtime = Path.Combine(repo, "third_party", "autoitx", "3.3.18.0");
    using var provider = new AutoItXUiAutomationProvider(runtime);
    var status = provider.Status();
    Equal(true, status.Available);
    Equal("autoitx-registration-free", status.Provider);
    Equal("x64", status.Architecture);
    try
    {
        provider.FindWindow(new UiWindowQuery("pao-uihost-no-window-" + Guid.NewGuid().ToString("N")));
    }
    catch (UiAutomationException ex)
    {
        Equal("ui_not_found", ex.Code);
        return Task.CompletedTask;
    }
    throw new Exception("Impossible-title lookup unexpectedly matched a window.");
}

static async Task DesktopControlAsync()
{
    var dotnet = FindDotnet();
    var testAssembly = Assembly.GetExecutingAssembly().Location;
    var start = new ProcessStartInfo(dotnet)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    start.ArgumentList.Add(testAssembly);
    start.ArgumentList.Add("--desktop-fixture");
    using var fixture = Process.Start(start) ?? throw new Exception("Desktop fixture did not start.");
    try
    {
        var lineTask = fixture.StandardOutput.ReadLineAsync();
        var completed = await Task.WhenAny(lineTask, Task.Delay(TimeSpan.FromSeconds(10)));
        if (completed != lineTask)
            throw new Exception("Desktop fixture did not publish its bounded identity before timeout.");
        var line = await lineTask;
        if (string.IsNullOrWhiteSpace(line))
            throw new Exception("Desktop fixture exited before publishing its identity: " + Bound(await fixture.StandardError.ReadToEndAsync(), 1024));
        using var identity = JsonDocument.Parse(line);
        var root = identity.RootElement;
        var hwnd = root.GetProperty("hwnd").GetInt64();
        var control = root.GetProperty("control").GetInt64();
        fixture.Refresh();
        var window = new UiTargetIdentity(hwnd, fixture.Id, fixture.StartTime.ToUniversalTime().Ticks);
        var runtime = Path.Combine(FindRepoRoot(), "third_party", "autoitx", "3.3.18.0");
        using var provider = new AutoItXUiAutomationProvider(runtime);
        var before = provider.GetControlInfo(window, control);
        Equal("initial", before.Text);
        const string replacement = "registration-free Ω & [verified]";
        var changed = provider.SetControlText(window, control, replacement);
        Check(changed.ActionIssued && changed.Changed, "AutoItX did not report the verified set-text postcondition.");
        Equal(replacement, provider.GetControlInfo(window, control).Text);
        var captureDirectory = Path.Combine(Path.GetTempPath(), "pao-ui-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(captureDirectory);
        try
        {
            var destination = Path.Combine(captureDirectory, "window.png");
            var capture = provider.CaptureWindow(window, destination, "png");
            Equal(destination, capture.Destination);
            Check(capture.Width > 0 && capture.Height > 0 && capture.Bytes > 8, "window capture metadata was invalid");
            Check(capture.Sha256.Length == 64, "window capture hash was invalid");
            var header = new byte[8];
            using var image = File.OpenRead(destination);
            Equal(header.Length, image.Read(header));
            Check(header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "window capture was not PNG");
        }
        finally { Directory.Delete(captureDirectory, recursive: true); }
    }
    finally
    {
        if (!fixture.HasExited)
        {
            fixture.Kill(entireProcessTree: true);
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

static int RunDesktopFixture()
{
    var result = 0;
    var thread = new Thread(() =>
    {
        try
        {
            ApplicationConfiguration.Initialize();
            using var form = new Form { Text = "Pegasus AgentOps owned UI fixture", Width = 480, Height = 180 };
            using var input = new TextBox { Text = "initial", Left = 24, Top = 36, Width = 400 };
            form.Controls.Add(input);
            form.Shown += (_, _) =>
            {
                var payload = JsonSerializer.Serialize(new { hwnd = form.Handle.ToInt64(), control = input.Handle.ToInt64() });
                Console.Out.WriteLine(payload);
                Console.Out.Flush();
            };
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name);
            result = 1;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return result;
}

static Task ClosedSurfaceAsync()
{
    var actual = Enum.GetNames<UiOperation>();
    string[] expected =
    [
        "Status", "FindWindow", "WindowInfo", "ControlResolve", "ControlInfo",
        "Activate", "Move", "ControlFocus", "ControlClick", "ControlSetText", "CaptureWindow"
    ];
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new Exception("UI operation surface changed.");
    var providerMethods = typeof(IUiAutomationProvider).GetMethods().Select(item => item.Name).ToArray();
    string[] prohibited = ["Run", "RunWait", "RunAs", "WinKill", "Send", "Mouse", "Clipboard", "Screenshot", "Ocr", "Shutdown"];
    Check(!providerMethods.Any(name => prohibited.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase))),
        "prohibited provider surface was exposed");
    return Task.CompletedTask;
}

static async Task<JsonElement> ExchangeAsync(UiHostRequest request, IUiAutomationProvider provider)
{
    var json = await ExchangeRawTextAsync(request, provider);
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

static async Task<JsonElement> ExchangeRawAsync(byte[] payload, IUiAutomationProvider provider)
{
    var json = await ExchangePayloadAsync(payload, provider);
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

static async Task<string> ExchangeRawTextAsync(UiHostRequest request, IUiAutomationProvider provider)
{
    var json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    return await ExchangePayloadAsync(JsonSerializer.SerializeToUtf8Bytes(request, json), provider);
}

static async Task<string> ExchangePayloadAsync(byte[] payload, IUiAutomationProvider provider)
{
    using var input = new MemoryStream();
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
    input.Write(header);
    input.Write(payload);
    input.Position = 0;
    using var output = new MemoryStream();
    Equal(0, await UiHostProtocol.RunAsync(input, output, provider, CancellationToken.None));
    output.Position = 0;
    Equal(4, await output.ReadAsync(header));
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    Check(length is > 0 and <= UiHostProtocol.MaxResponseBytes, "response frame was not bounded");
    var response = new byte[length];
    Equal(length, await output.ReadAsync(response));
    Equal(output.Length, output.Position);
    return Encoding.UTF8.GetString(response);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static string FindRepoRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
            Directory.Exists(Path.Combine(current.FullName, "third_party", "autoitx")))
            return current.FullName;
        current = current.Parent;
    }
    throw new Exception("AgentOps repository root was not found.");
}

static string FindDotnet()
{
    var explicitHost = Environment.GetEnvironmentVariable("PAO_TEST_DOTNET");
    if (!string.IsNullOrWhiteSpace(explicitHost) && File.Exists(explicitHost)) return Path.GetFullPath(explicitHost);
    var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    if (!string.IsNullOrWhiteSpace(root))
    {
        var candidate = Path.Combine(root, "dotnet.exe");
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
    }
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var candidate = Path.Combine(directory, "dotnet.exe");
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
    }
    throw new Exception("A .NET 10 host is required. Set PAO_TEST_DOTNET or DOTNET_ROOT, or place dotnet.exe on PATH.");
}

static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

sealed class FakeProvider : IUiAutomationProvider
{
    public UiTargetIdentity Identity { get; } = new(111, 42, 638_900_000_000_000_000);
    public UiWindowQuery? LastQuery { get; private set; }
    public UiTargetIdentity? LastWindow { get; private set; }
    public string? LastText { get; private set; }
    public bool FailMutationUncertain { get; init; }
    public string? FindError { get; init; }
    public int FindCalls { get; private set; }

    public UiProviderStatus Status() => new(true, "fake", "1.0", "x64", true);
    public UiTargetIdentity FindWindow(UiWindowQuery query)
    {
        LastQuery = query;
        FindCalls++;
        if (FindError is not null) throw new UiAutomationException(FindError, "query");
        return Identity;
    }
    public UiWindowInfo GetWindowInfo(UiTargetIdentity window) => new(window, "title", "class", new(1, 2, 3, 4), 7);
    public long ResolveControl(UiTargetIdentity window, string selector) { LastWindow = window; return 222; }
    public UiControlInfo GetControlInfo(UiTargetIdentity window, long controlHandle) => new(window, controlHandle, "text", new(1, 2, 3, 4));
    public UiMutationResult Activate(UiTargetIdentity window) => Mutation();
    public UiMutationResult Move(UiTargetIdentity window, UiBounds bounds) => Mutation();
    public UiMutationResult FocusControl(UiTargetIdentity window, long controlHandle) => Mutation();
    public UiMutationResult ClickControl(UiTargetIdentity window, long controlHandle, string button, int clicks, int x, int y) => Mutation();
    public UiMutationResult SetControlText(UiTargetIdentity window, long controlHandle, string text) { LastText = text; return Mutation(); }
    public UiCaptureResult CaptureWindow(UiTargetIdentity window, string destination, string format) => new(destination, format, 320, 200, 1024, new string('a', 64));
    public void Dispose() { }

    private UiMutationResult Mutation()
    {
        if (FailMutationUncertain)
            throw new UiAutomationException("recovery_required", partial: true, recoveryRequired: true);
        return new(true, true);
    }
}
