using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

internal static class PersonalProfileTests
{
    internal static async Task RunAsync(string fixture)
    {
        var transitions = new List<(string State, string? Issue)>();
        var availability = new PersonalProfileAvailability("  Example User  ",
            (state, issue) => transitions.Add((state, issue)));
        var initial = availability.Snapshot();
        Check(initial.State == "not_loaded" && initial.Profile is null, "Process existence loaded a personal profile.");
        availability.UiLoaded();
        var loaded = availability.Snapshot();
        Check(loaded.State == "loaded" && loaded.Profile == "Example User" && loaded.IssueCode is null &&
            loaded.Generation != initial.Generation, "Ready UI did not publish a bounded loaded profile.");
        availability.UiLoaded();
        Check(availability.Snapshot().Generation == loaded.Generation, "Repeated loaded status churned generation.");
        availability.Unload();
        var unloaded = availability.Snapshot();
        Check(unloaded.State == "not_loaded" && unloaded.Profile is null && unloaded.IssueCode == "profile_unloaded" &&
            unloaded.Generation != loaded.Generation, "Explicit unload retained personal availability.");
        availability.UiUnavailable();
        availability.UiLoaded();
        Check(availability.Snapshot().State == "not_loaded" && availability.Snapshot().IssueCode == "profile_unloaded" &&
            availability.Snapshot().Profile is null, "A transient UI restart undid explicit unload.");
        availability.Load();
        Check(availability.Snapshot().State == "loaded" && availability.Snapshot().IssueCode is null &&
            availability.Snapshot().Generation != unloaded.Generation, "Reload did not clear the active warning.");
        availability.UiUnavailable();
        var failed = availability.Snapshot();
        Check(failed.State == "not_loaded" && failed.IssueCode == "ui_unavailable", "Failed/disposed UI remained available.");
        availability.UiLoaded();
        Check(availability.Snapshot().State == "loaded" && availability.Snapshot().Generation != failed.Generation &&
            availability.Snapshot().IssueCode is null, "UI recovery did not restore a new clean generation.");
        availability.PipeUnavailable();
        Check(availability.Snapshot().IssueCode == "profile_pipe_unavailable", "Pipe failure was not observable.");
        availability.PipeAvailable();
        Check(availability.Snapshot().State == "loaded" && availability.Snapshot().IssueCode is null,
            "Pipe recovery did not clear the active warning.");
        Check(transitions.Count == 9, "Transition reporting was repeated or omitted.");
        foreach (var invalid in new[] { "", "bad\nlabel", "bad\u202elabel", "bad\\label", new string('a', 129) })
        {
            var rejected = new PersonalProfileAvailability(invalid);
            rejected.UiLoaded();
            Check(rejected.Snapshot().State == "not_loaded" && rejected.Snapshot().IssueCode == "profile_label_invalid",
                "An unsafe display label enabled the personal profile.");
        }
        Check(CortexPersonalProfilePipe.CanonicalProfileLabel("Jose\u0301") == "Jos\u00e9", "Label normalization is not canonical NFC.");
        var owner = LocalIdentity.Current();
        Check(PersonalProfilePipeHost.IsAuthorized(owner, owner), "Current user/session was rejected.");
        Check(!PersonalProfilePipeHost.IsAuthorized(owner, owner with { Session = owner.Session + 1 }), "Another session was admitted.");
        Check(!PersonalProfilePipeHost.IsAuthorized(owner, owner with { Sid = "S-1-5-18" }), "Another principal was admitted.");
        Console.WriteLine("PASS personal-profile state, generation, recovery, display-label and same-user/session policy unit fixtures");

        using (var request = new MemoryStream())
        {
            await CortexPersonalProfilePipe.WriteRequestAsync(request);
            request.Position = 0;
            await CortexPersonalProfilePipe.ReadRequestAsync(request);
        }
        using (var response = new MemoryStream())
        {
            await CortexPersonalProfilePipe.WriteResponseAsync(response, availability.Snapshot());
            response.Position = 0;
            Check(await CortexPersonalProfilePipe.ReadResponseAsync(response) == availability.Snapshot(), "Status frame lost fields.");
        }
        foreach (var json in new[]
                 {
                     "{\"version\":2,\"operation\":\"status\"}",
                     "{\"version\":1,\"operation\":\"load\"}",
                     "{\"version\":1,\"operation\":\"status\",\"body\":\"not allowed\"}",
                     "{\"version\":1,\"version\":1,\"operation\":\"status\"}",
                     "{\"version\":1}", "{\"version\":\"1\",\"operation\":\"status\"}"
                 })
        {
            using var invalid = Frame(json);
            await MustRejectAsync(() => CortexPersonalProfilePipe.ReadRequestAsync(invalid));
        }
        using (var oversized = new MemoryStream())
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, CortexPersonalProfilePipe.MaximumFrameBytes + 1);
            oversized.Write(header);
            oversized.Position = 0;
            await MustRejectAsync(() => CortexPersonalProfilePipe.ReadRequestAsync(oversized));
        }
        using (var invalidResponse = Frame("{\"version\":1,\"state\":\"loaded\",\"profile\":null,\"generation\":\"" +
                   Guid.NewGuid().ToString("N") + "\",\"issueCode\":null}"))
            await MustRejectAsync(async () => { await CortexPersonalProfilePipe.ReadResponseAsync(invalidResponse); });
        Console.WriteLine("PASS strict bounded personal-profile framing and invalid protocol fixture rejection");

        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var name = "cortex-profile-fixture-" + Guid.NewGuid().ToString("N");
            var server = PersonalProfilePipeHost.RunAsync(name, owner, availability, stop.Token);
            try
            {
                var received = await QueryAsync(name, owner, stop.Token);
                Check(received.State == "loaded", "Authenticated isolated pipe did not expose the loaded fixture.");
                availability.Unload();
                received = await QueryAsync(name, owner, stop.Token);
                Check(received.State == "not_loaded" && received.Profile is null && received.IssueCode == "profile_unloaded",
                    "Isolated pipe retained a profile after unload.");
                availability.UiUnavailable();
                availability.UiLoaded();
                received = await QueryAsync(name, owner, stop.Token);
                Check(received.State == "not_loaded" && received.IssueCode == "profile_unloaded",
                    "Isolated pipe undid explicit unload after a UI restart.");
                availability.Load();
                received = await QueryAsync(name, owner, stop.Token);
                Check(received.State == "loaded" && received.Generation != loaded.Generation && received.IssueCode is null,
                    "Isolated pipe did not report recovery.");
            }
            finally
            {
                stop.Cancel();
                await server;
            }
        }
        Console.WriteLine("PASS real isolated named-pipe status round trips with current SID/session authentication; no service or live endpoint used");

        var logs = Path.Combine(fixture, "profile-diagnostics");
        var diagnostics = new CortexPersonalProfileDiagnostics(logs);
        diagnostics.Report("not_loaded", "ui_unavailable");
        diagnostics.Report("not_loaded", "ui_unavailable");
        var currentLog = Path.Combine(logs, "personal-profile.log");
        Check(File.ReadAllLines(currentLog).Length == 1, "Repeated observations duplicated transition diagnostics.");
        Check(!File.ReadAllText(currentLog).Contains("Example User", StringComparison.Ordinal), "Diagnostics retained profile content.");
        File.WriteAllText(currentLog, new string('x', 64 * 1024));
        diagnostics.Report("loaded", null);
        Check(new FileInfo(currentLog).Length < 1024 && File.Exists(Path.Combine(logs, "personal-profile.previous.log")),
            "Transition log did not rotate to one bounded previous file.");
        Check(Directory.EnumerateFiles(logs).Count() == 2, "Diagnostics created unbounded history.");
        new CortexPersonalProfileDiagnostics(currentLog).Report("not_loaded", "ui_unavailable");
        Console.WriteLine("PASS transition-only bounded log rotation, duplicate suppression, redaction and best-effort failure fixtures");
    }

    private static async Task<CortexPersonalProfileStatusV1> QueryAsync(string name, LocalPeer owner, CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(cancellationToken);
        Check(LocalIdentity.CaptureExpectedServer(client, owner).SameIdentity(owner), "Isolated pipe server identity mismatched.");
        await CortexPersonalProfilePipe.WriteRequestAsync(client, cancellationToken);
        return await CortexPersonalProfilePipe.ReadResponseAsync(client, cancellationToken);
    }

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    private static async Task MustRejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Malformed personal-profile protocol fixture was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
