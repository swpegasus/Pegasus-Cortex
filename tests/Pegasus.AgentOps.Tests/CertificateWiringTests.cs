using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Windows.Registry;

// Engine integration fixtures: owned scratch SQLite, an in-memory repository,
// fake identities and fake signing. These do not qualify DPAPI, OS stores,
// native Authenticode, trust, deployment or a real process crash.
internal static class CertificateWiringTests
{
    private const string OwnerSid = "S-1-5-21-100-200-300-1001";
    private const string PrivateSubject = "Certificate wiring subject must not enter history";
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ZeroHash = new('0', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task RunAsync()
    {
        // Real cryptographic creation is memory-only; no persisted key or OS
        // certificate store is consulted. Resolve returns an owned key handle.
        var operations = new CertificateOperations(new FixedClock());
        using var authority = operations.CreateAuthority(new("Certificate wiring authority", Now.AddDays(-2), Now.AddDays(365), 2048));
        using var signer = operations.IssueCodeSigning(new("Certificate wiring signer", Now.AddDays(-1), Now.AddDays(30), 2048), authority);
        await WireAdmissionAsync(authority, signer);
        await AdmissionAsync(authority, signer);
        await SnapshotsAndDurabilityAsync(authority, signer);
        await ServerActionsAsync(authority, signer);
        await InFlightSignAsync(authority, signer);
        await UncertainEffectsAsync(authority, signer);
        await CrossFamilyCollisionsAsync(authority, signer);
        await BodyLossRecoveryAsync(authority, signer);
    }

    private static async Task WireAdmissionAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using var fixture = new Fixture(authority, signer);
        await using var engine = fixture.Create();
        var original = Request("inspect", Body("inspect", fixture.SignerId), "wire-inspect");
        var decoded = await WireAsync(original);
        Check(decoded.Body == original.Body && decoded.Rid == original.Rid && decoded.Arg.Count == 1 &&
            decoded.Arg.GetValueOrDefault("op") == "inspect" && !decoded.Arg.ContainsKey(OperationRequest.BodyOmittedKey),
            "live transport preserves the body without injecting durable-only arguments");
        Check((await engine.ExecuteAsync(decoded)).Ok && fixture.Rows() == 0,
            "wire-decoded certificate inspection reaches closed Engine admission without durable history");
        using (var durable = JsonDocument.Parse(JsonSerializer.Serialize(decoded, Json)))
            Check(!durable.RootElement.TryGetProperty("body", out _), "default durable JSON still omits the live request body");

        var calls = fixture.Repository.Calls;
        foreach (var field in new[] { "unexpected", "env0", "__execution_context_system" })
        {
            var malformed = await WireAsync(original with { Arg = new Dictionary<string, string>(original.Arg) { [field] = "fixture" } });
            Check(malformed.Arg.ContainsKey(field) && !(await engine.ExecuteAsync(malformed)).Ok && fixture.Repository.Calls == calls,
                "transport does not erase unknown public arguments before closed admission");
        }
        var suppliedMarker = await WireAsync(original with
        { Arg = new Dictionary<string, string>(original.Arg) { [OperationRequest.BodyOmittedKey] = "caller" } });
        Check(!suppliedMarker.Arg.ContainsKey(OperationRequest.BodyOmittedKey) && (await engine.ExecuteAsync(suppliedMarker)).Ok,
            "caller-supplied omission marker is removed without changing live-body authority");
        calls = fixture.Repository.Calls;
        var missingBody = await WireAsync(original with
        { Body = null, Arg = new Dictionary<string, string>(original.Arg) { [OperationRequest.BodyOmittedKey] = "caller" } });
        Check(!missingBody.Arg.ContainsKey(OperationRequest.BodyOmittedKey) && !(await engine.ExecuteAsync(missingBody)).Ok &&
            fixture.Repository.Calls == calls, "caller marker cannot manufacture a live or recoverable body");

        var create = await WireAsync(Request("create_ca", Body("create_ca"), "wire-create"));
        var created = await engine.ExecuteAsync(create);
        Check(created.Ok && fixture.Repository.Publishes == 1, "wire-decoded certificate mutation executes once");
        await RetiredAsync(engine);
        AssertMetadata(fixture.Stored(create.Rid!).Receipt, create, PrivateSubject);
        Check((await engine.ExecuteAsync(await WireAsync(create))).Jid == created.Jid && fixture.Repository.Publishes == 1,
            "wire-decoded RID replay retains metadata without repeating publication");
    }

    private static async Task<OperationRequest> WireAsync(OperationRequest request)
    {
        using var stream = new MemoryStream();
        await BinaryProtocol.WriteAsync(stream, request);
        stream.Position = 0;
        return await BinaryProtocol.ReadAsync<OperationRequest>(stream);
    }

    private static async Task ServerActionsAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using var fixture = new Fixture(authority, signer);
        await using var engine = fixture.Create();
        var issue = Request("issue_server", Body("issue_server", reference: "server", issuerId: fixture.IssuerId), "server-issue");
        var issued = await engine.ExecuteAsync(issue);
        Check(issued.Ok && Flag(issued, "published") == true, "server leaf was not published through the owner repository");
        await RetiredAsync(engine);
        var serverId = fixture.Repository.Peek("server").CertificateId;
        using (var leaf = fixture.Repository.Resolve(new("server", serverId)))
        {
            var names = ServerCertificateMaterial.Require(leaf, serverId, Now, privateKey: true);
            Check(names.DnsNames.SequenceEqual(new[] { "localhost" }), "issued server SANs differ from the request");
        }
        AssertMetadata(fixture.Stored(issue.Rid!).Receipt, issue, PrivateSubject, "localhost", "::1");
        var changed = issue with { Body = issue.Body!.Replace("localhost", "changed.localhost", StringComparison.Ordinal) };
        var publications = fixture.Repository.Publishes;
        Check(!(await engine.ExecuteAsync(changed)).Ok && fixture.Repository.Publishes == publications,
            "changed server SANs reused an already published RID");

        var bind = Request("store_bind_server", Body("store_bind_server", serverId, "server"), "server-bind");
        fixture.Peer = new(OwnerSid, 7, Elevated: true);
        Check(!(await engine.ExecuteAsync(bind)).Ok && fixture.ServerBinding.Calls == 0,
            "elevated identity reached private server binding");
        fixture.Peer = new(OwnerSid, 7);
        var bound = await engine.ExecuteAsync(bind);
        Check(bound.Ok && Flag(bound, "key_persistence_issued") == true && Flag(bound, "store_action_issued") == true,
            "fixed private binding did not report its explicit effect");
        using (var locator = JsonDocument.Parse(JsonSerializer.Serialize(bound.Data!["binding"], Json)))
            Check(locator.RootElement.GetProperty("location").GetString() == "CurrentUser" &&
                locator.RootElement.GetProperty("store").GetString() == "My" &&
                locator.RootElement.GetProperty("certificateId").GetString() == serverId,
                "DevHost locator is not exact CurrentUser/My/SHA256 DER");
        await RetiredAsync(engine);
        AssertMetadata(fixture.Stored(bind.Rid!).Receipt, bind, PrivateSubject, "localhost", "::1", "CurrentUser");
        Check(fixture.Stored(bind.Rid!).Receipt.Data?.ContainsKey("binding") != true,
            "live binding object leaked into durable history");
        var calls = fixture.ServerBinding.Calls;
        Check((await engine.ExecuteAsync(bind)).Jid == bound.Jid && fixture.ServerBinding.Calls == calls,
            "RID replay entered the private-key backend again");

        var again = await engine.ExecuteAsync(bind with { Rid = "server-bind-again" });
        Check(again.Ok && Flag(again, "key_persistence_issued") == false && fixture.ServerBinding.Mutations == 1,
            "fresh explicit binding did not observe the already usable identity unchanged");
        await RetiredAsync(engine);

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { _ = await engine.ExecuteAsync(bind with { Rid = "server-bind-canceled" }, canceled.Token); }
            catch (OperationCanceledException) { }
            Check(fixture.ServerBinding.Calls == calls + 1, "pre-issue cancellation entered the binding backend");
        }
        fixture.ServerBinding.Failure = new("server_binding_precondition_failed", false, false, false, new CryptographicException());
        var preEffect = await engine.ExecuteAsync(bind with { Rid = "server-bind-pre-effect" });
        Check(!preEffect.Ok && preEffect.State == "failed" && !preEffect.Partial && Flag(preEffect, "outcome_unknown") == false &&
            Flag(preEffect, "key_persistence_issued") == false && Flag(preEffect, "store_action_issued") == false,
            "known backend pre-persistence failure was mislabeled as an unknown effect");
        await RetiredAsync(engine);
        fixture.ServerBinding.Failure = new("server_binding_outcome_unknown", true, false, true);
        var uncertainRequest = bind with { Rid = "server-bind-unknown" };
        var uncertain = await engine.ExecuteAsync(uncertainRequest);
        AssertUnknown(uncertain);
        await RetiredAsync(engine);
        Check(Flag(fixture.Stored(uncertainRequest.Rid!).Receipt, "key_persistence_issued") == true,
            "key-persistence uncertainty lost its durable effect flag");
        calls = fixture.ServerBinding.Calls;
        Check((await engine.ExecuteAsync(uncertainRequest)).Jid == uncertain.Jid && fixture.ServerBinding.Calls == calls,
            "unknown private binding was automatically replayed");
        fixture.ServerBinding.Failure = null;
        fixture.ServerBinding.GenericFailure = new CertificateOperationException("certificate_time_invalid");
        AssertUnknown(await engine.ExecuteAsync(bind with { Rid = "server-bind-unclassified-backend" }));
        await RetiredAsync(engine);
        fixture.ServerBinding.GenericFailure = null;
        fixture.ServerBinding.Override = new("bound", ZeroHash, true, true, true, false);
        AssertUnknown(await engine.ExecuteAsync(bind with { Rid = "server-bind-bad-result" }));
        await RetiredAsync(engine);
        fixture.AssertHistoryExcludes(PrivateSubject, "localhost", "::1", "protectedPfx", "protectedPassword");
    }

    private static async Task AdmissionAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using var fixture = new Fixture(authority, signer);
        var inspect = Request("inspect", Body("inspect", fixture.SignerId), "denied-read");
        var create = Request("create_ca", Body("create_ca"), "denied-create");
        await using (var engine = fixture.Create(bind: false))
            foreach (var request in new[] { inspect, create })
                Check(!(await engine.ExecuteAsync(request)).Ok && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0,
                    "unbound engine refuses before repository/native factory or key access");
        foreach (var direct in new[] { false, true })
        {
            await using var engine = fixture.Create(bind: false, direct: direct, admission: direct);
            var refused = false;
            try { engine.BindCertificateOwner(fixture.Binding()); }
            catch (AgentOpsException error) when (error.Code == "auth") { refused = true; }
            Check(refused && !(await engine.ExecuteAsync(inspect)).Ok && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0,
                direct ? "direct engine cannot bind certificate owner" : "engine without user admission cannot bind certificate owner");
        }
        await using (var engine = fixture.Create())
        {
            Check(!(await engine.ExecuteAuthenticatedFrozenDirectAsync(inspect)).Ok && fixture.FactoryCalls == 0,
                "service-frozen route cannot use an owner certificate binding");
            Check(!(await engine.ExecuteAuthenticatedFrozenDirectAsync(create)).Ok && fixture.FactoryCalls == 0,
                "service-frozen route cannot generate or publish a certificate");
            foreach (var peer in new[]
            {
                new LocalPeer("S-1-5-18", 0), new LocalPeer("S-1-5-21-100-200-300-1002", 7),
                new LocalPeer(OwnerSid, 0), new LocalPeer(OwnerSid, 8), new LocalPeer(OwnerSid, 7, Elevated: true)
            })
            {
                fixture.Peer = peer;
                foreach (var request in new[] { inspect, create })
                    Check(!(await engine.ExecuteAsync(request)).Ok && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0,
                        "foreign SID/session/elevation denied before factories or keys");
            }
            fixture.Peer = new(OwnerSid, 7);
            var malformed = new List<OperationRequest>
            {
                inspect with { Root = "AB12" }, inspect with { Ws = "invented" },
                inspect with { PathRoots = new Dictionary<string, string> { ["src"] = fixture.Workspace } },
                inspect with { Body = Body("inspect", fixture.SignerId).Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal) },
                Request("create_ca", Body("create_ca"), null)
            };
            foreach (var field in new[] { "root", "stdin", "env0", "wait", "cwd", "ws", "__execution_context_system", "__authenticated_frozen", OperationRequest.BodyOmittedKey })
                malformed.Add(inspect with { Arg = new Dictionary<string, string>(inspect.Arg) { [field] = "fixture" } });
            foreach (var request in malformed)
                Check(!(await engine.ExecuteAsync(request)).Ok && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0 && fixture.Rows() == 0,
                    "original forbidden/ambiguous fields refused before generic projection can erase them");

            var sign = fixture.FileRequest("sign", "workspace-missing");
            foreach (var request in new[] { sign with { Ws = null }, sign with { Ws = "not-registered" } })
                Check(!(await engine.ExecuteAsync(request)).Ok && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0,
                    "absolute source parent never supplies an implicit certificate workspace");
        }
        var dispatches = 0;
        var state = fixture.Directory("broker");
        await using var broker = new AuthenticatedServiceBroker(state, OwnerSid,
            null, false,
            (_, _, _, _) => { dispatches++; throw new InvalidOperationException("certificate broker delegated to user"); },
            (_, _) => { dispatches++; throw new InvalidOperationException("certificate broker delegated to system"); }, maintenanceMode: fixture.Mode);
        var user = await broker.HandleUserAsync(inspect, fixture.Peer, CancellationToken.None);
        var system = await broker.HandleSystemAsync(inspect with { Root = "FAKE" }, fixture.Peer, CancellationToken.None);
        var application = await broker.HandleApplicationAsync(inspect, CancellationToken.None);
        Check(!user.Ok && !system.Ok && !application.Ok && dispatches == 0 && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0,
            "broker user/system/application routes refuse without delegation or certificate access");
    }

    private static async Task SnapshotsAndDurabilityAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using var fixture = new Fixture(authority, signer);
        await using var engine = fixture.Create();
        var inspect = Request("inspect", Body("inspect", fixture.SignerId), "snapshot-read");
        var initial = await engine.ExecuteAsync(inspect with { Cwd = fixture.Workspace });
        Check(initial.Ok && Live(initial).Contains("Certificate wiring signer", StringComparison.Ordinal) && fixture.Rows() == 0,
            "inspect returns a live public summary without durable history");
        var exported = await engine.ExecuteAsync(Request("export_public", Body("export_public", fixture.SignerId), "snapshot-export"));
        Check(exported.Ok && Live(exported).Contains(Convert.ToBase64String(fixture.Repository.Peek("signer").PublicDer), StringComparison.Ordinal) &&
            fixture.Rows() == 0 && fixture.Repository.ResolveCalls == 0, "public export is live/nondurable and never resolves private material");
        var verify = fixture.FileRequest("verify", "snapshot-verify");
        await fixture.RegisterAsync(engine);
        var baseline = fixture.Rows();
        var verified = await engine.ExecuteAsync(verify);
        Check(verified.Ok && verified.Data?.GetValueOrDefault("certificate") is AuthenticodeVerificationResult
            { Signature: "valid", Trust: "untrusted", Revocation: "not_checked" } verification &&
            verification.CertificateId == fixture.SignerId && verification.WindowsStatus == unchecked((int)0x800b010a) &&
            fixture.Native.Verifies == 1 && fixture.Repository.ResolveCalls == 0 &&
            fixture.Rows() == baseline && fixture.LastAdapterRoot == fixture.Workspace,
            "fake verify exposes independent integrity/trust/revocation on the registered workspace, without key resolution or history");

        var unrelated = await engine.ExecuteAsync(new("mkdir", new Dictionary<string, string> { ["dst"] = Path.Combine(fixture.Workspace, "old-rid") }, "old-other-rid"));
        Check(unrelated.Ok, "owned scratch operation establishes an unrelated RID");
        await RetiredAsync(engine);
        baseline = fixture.Rows();
        var snapshot = await engine.ExecuteAsync(inspect with { Rid = unrelated.Rid });
        Check(snapshot.Ok && snapshot.Op == "certificate_inspect" && snapshot.Jid != unrelated.Jid && fixture.Rows() == baseline,
            "certificate snapshot never adopts an unrelated old RID receipt");
        var calls = fixture.Repository.Calls;
        using (fixture.Admission.TryClose() ?? throw new InvalidOperationException("fixture close failed"))
        {
            var stopped = await engine.ExecuteAsync(inspect with { Rid = unrelated.Rid });
            Check(!stopped.Ok && stopped.Err == "host_stopping" && stopped.Jid != unrelated.Jid && fixture.Repository.Calls == calls,
                "closed host cannot rescue a fresh read through unrelated durable RID");
        }

        var request = Request("create_ca", Body("create_ca"), "create-live-only");
        var live = await engine.ExecuteAsync(request);
        Check(live.Ok && Live(live).Contains(PrivateSubject, StringComparison.Ordinal) && fixture.Repository.Publishes == 1,
            "original creation caller receives its live public summary exactly once");
        await RetiredAsync(engine);
        var stored = fixture.Stored(request.Rid!);
        var entry = fixture.Repository.Peek("created");
        AssertMetadata(stored.Receipt, request, PrivateSubject, Convert.ToBase64String(entry.PublicDer));
        Check(stored.Request is null, "completed certificate request is cleared after its metadata receipt is stored");
        fixture.AssertHistoryExcludes(PrivateSubject, request.Body!, Convert.ToBase64String(entry.PublicDer), "BEGIN CERTIFICATE", "PRIVATE KEY", "protectedPfx", "protectedPassword");
        var factories = fixture.FactoryCalls;
        var replay = await engine.ExecuteAsync(request);
        Check(replay.Jid == live.Jid && fixture.Repository.Publishes == 1 && fixture.FactoryCalls == factories,
            "lost creation reply is observed as original metadata without key creation/publication again");
        AssertMetadata(replay, request, PrivateSubject, Convert.ToBase64String(entry.PublicDer));
        var changed = request with { Body = Body("create_ca", subject: PrivateSubject + " changed") };
        Check((await engine.ExecuteAsync(changed)).Err == "precondition" && fixture.Repository.Publishes == 1,
            "same creation RID cannot change its immutable body");
        using (fixture.Admission.TryClose() ?? throw new InvalidOperationException("fixture close failed"))
        {
            var exact = await engine.ExecuteAsync(request);
            Check(!exact.Ok && exact.Err == "host_stopping" && fixture.FactoryCalls == factories && fixture.Repository.Publishes == 1,
                "closed admission refuses exact certificate resubmission without repository use");
            var changedWhileClosed = await engine.ExecuteAsync(changed);
            Check(!changedWhileClosed.Ok && changedWhileClosed.Err == "host_stopping" &&
                fixture.FactoryCalls == factories && fixture.Repository.Publishes == 1,
                "closed admission does not rescue changed certificate identity");
        }
        var before = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        var pruned = await engine.ExecuteAsync(new("job", new Dictionary<string, string>
            { ["op"] = "prune", ["mode"] = "details", ["before"] = before, ["max"] = "32" }, "certificate-prune"));
        Check(pruned.Ok, "actual isolated history pruning completed");
        await RetiredAsync(engine);
        Check(AgentOpsEngine.IsHistoryPruned(fixture.Stored(request.Rid!).Receipt), "certificate receipt became an actual SQLite tombstone");
        var compact = await engine.ExecuteAsync(request);
        Check(compact.Jid == live.Jid && AgentOpsEngine.IsHistoryPruned(compact) && Identity(compact) == Identity(stored.Receipt) &&
            fixture.Repository.Publishes == 1 && fixture.FactoryCalls == factories, "pruned RID remains metadata-only with its immutable identity");
        Check((await engine.ExecuteAsync(changed)).Err == "precondition" && fixture.Repository.Publishes == 1,
            "changed creation body cannot cross a pruned RID barrier");

        var issuer = fixture.Repository.Peek("issuer");
        var issueBody = JsonSerializer.Serialize(new
        {
            version = 1, reference = "issued-fixture", subject = "Certificate wiring issued leaf", days = 1, keyBits = 2048,
            issuer = new { reference = "issuer", certificateId = issuer.CertificateId }
        });
        var resolveBefore = fixture.Repository.ResolveCalls;
        var issuedRequest = Request("issue_code", issueBody, "pinned-issuer-issuance");
        var issued = await engine.ExecuteAsync(issuedRequest);
        Check(issued.Ok && Live(issued).Contains("Certificate wiring issued leaf", StringComparison.Ordinal) &&
            fixture.Repository.Publishes == 2 && fixture.Repository.ResolveCalls == resolveBefore + 1,
            "explicit pinned issuer resolves one owned ephemeral key and publishes one code-signing leaf");
        await RetiredAsync(engine);
        AssertMetadata(fixture.Stored(issued.Rid).Receipt, issuedRequest, "Certificate wiring issued leaf",
            Convert.ToBase64String(fixture.Repository.Peek("issued-fixture").PublicDer));
    }

    private static async Task InFlightSignAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using var fixture = new Fixture(authority, signer);
        await using var engine = fixture.Create();
        await fixture.RegisterAsync(engine);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Native.BeforeSign = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fake sign fence timed out");
        };
        var request = fixture.FileRequest("sign", "in-flight-sign");
        var originalTask = engine.ExecuteAsync(request);
        Task<OperationReceipt>? waiting = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running = fixture.Stored(request.Rid!);
            Check(running.State == "running" && running.Request?.Body is null && Identity(running.Receipt) == CertificateOperationContract.Parse(request).RequestIdentity,
                "running sign stores only immutable metadata and omitted body");
            AssertStoredRequestMetadata(running);
            Check(!RawContains(running.RawRequest, fixture.Source),
                "running certificate history must not persist its raw file path");
            var factories = fixture.FactoryCalls;
            var replay = await engine.ExecuteAsync(request).WaitAsync(TimeSpan.FromSeconds(2));
            Check(replay.State == "running" && replay.Jid == running.Receipt.Jid && fixture.Native.Signs == 1 &&
                fixture.FactoryCalls == factories && !originalTask.IsCompleted, "duplicate in-flight RID neither joins live response nor signs again");
            AssertMetadata(replay, request, "Certificate wiring signer", fixture.Source);
            waiting = engine.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "wait", ["jid"] = replay.Jid }, "certificate-wait"));
            Check(!waiting.IsCompleted, "explicit job wait observes the fenced original completion");
        }
        finally { release.Set(); fixture.Native.BeforeSign = null; }
        var original = await originalTask.WaitAsync(TimeSpan.FromSeconds(5));
        var observed = await waiting!.WaitAsync(TimeSpan.FromSeconds(5));
        Check(original.Ok && original.Data?.GetValueOrDefault("certificate") is AuthenticodeSignResult { State: "signed", Timestamped: false } signing &&
            signing.CertificateId == fixture.SignerId && signing.ContentSha256 == fixture.Sha &&
            observed.Jid == original.Jid && fixture.Native.Signs == 1,
            "one fake native invocation supplies original result and later metadata observation");
        AssertMetadata(observed, request, "Certificate wiring signer", fixture.Source);
        await RetiredAsync(engine);
        AssertMetadata(fixture.Stored(request.Rid!).Receipt, request, "Certificate wiring signer", fixture.Source);
        var calls = fixture.FactoryCalls;
        var changed = request with { Arg = new Dictionary<string, string>(request.Arg) { ["sha"] = ZeroHash } };
        Check(CertificateOperationContract.Parse(changed).RequestIdentity != CertificateOperationContract.Parse(request).RequestIdentity &&
            (await engine.ExecuteAsync(changed)).Err == "precondition" && fixture.Native.Signs == 1 && fixture.FactoryCalls == calls,
            "same sign RID with changed file hash cannot invoke the native boundary");
        var replayed = await engine.ExecuteAsync(request);
        Check(replayed.Jid == original.Jid && fixture.Native.Signs == 1 && fixture.FactoryCalls == calls,
            "completed sign replay never re-signs after its original reply is lost");
        AssertMetadata(replayed, request, "Certificate wiring signer");
    }

    private static async Task UncertainEffectsAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        foreach (var (issued, unknown, postcondition) in new[] { (false, false, false), (true, true, false), (true, false, true) })
        {
            using var fixture = new Fixture(authority, signer);
            await using var engine = fixture.Create();
            await fixture.RegisterAsync(engine);
            fixture.Native.SignFailure = new(unknown ? "fixture_sign_reply_lost" : "fixture_sign_native_failure", issued, unknown, postcondition, 5);
            var request = fixture.FileRequest("sign", unknown ? "issued-unknown-sign" : issued ? "issued-known-postcondition" : "not-issued-sign");
            var result = await engine.ExecuteAsync(request);
            Check(!result.Ok && result.State == (unknown ? "recovery_required" : issued ? "attention" : "failed") && result.Partial == issued &&
                Flag(result, "certificate_issued") == issued && Flag(result, "outcome_unknown") == unknown && Flag(result, "postcondition_observed") == postcondition,
                "fake native issued/unknown facts survive the Engine boundary without success or cancel acknowledgement");
            if (unknown) AssertUnknown(result);
            await RetiredAsync(engine);
            var stored = fixture.Stored(request.Rid!);
            AssertMetadata(stored.Receipt, request, "Certificate wiring signer");
            if (unknown) AssertUnknown(stored.Receipt);
            else if (issued) Check(stored.Receipt.State == "attention" && stored.Receipt.Partial &&
                Flag(stored.Receipt, "outcome_unknown") is false && Flag(stored.Receipt, "postcondition_observed") is true &&
                stored.Receipt.Failure is { Execution.Value: OperationExecutionState.Started, Retry.Value: OperationRetryDisposition.NotSupported },
                "known postcondition failure remains distinct from unknown execution after durable projection");
            var replay = await engine.ExecuteAsync(request);
            Check(replay.Jid == result.Jid && replay.State == result.State && fixture.Native.Signs == 1,
                "failed/uncertain native sign is never implicitly replayed");
            var retry = await engine.ExecuteAsync(new("job", new Dictionary<string, string> { ["op"] = "retry", ["jid"] = result.Jid }, "sign-retry-refused"));
            Check(!retry.Ok && fixture.Native.Signs == 1, "job retry cannot restore or replay an issued certificate body");
            if (unknown)
                foreach (var action in new[] { "start", "status" })
                {
                    var app = await engine.ExecuteAsync(new("app", new Dictionary<string, string>
                        { ["op"] = action, ["name"] = "must-not-start", ["cfg"] = "not-registered" }, result.Rid));
                    Check(app.Err == "precondition" && fixture.Native.Signs == 1,
                        "application cannot adopt an uncertain certificate RID");
                }
        }
        using (var fixture = new Fixture(authority, signer))
        {
            await using var engine = fixture.Create();
            fixture.Repository.AfterPublish = () => throw new OwnerCertificateRepositoryException("certificate_publish_outcome_unknown", published: true, outcomeUnknown: true);
            var request = Request("create_ca", Body("create_ca"), "published-reply-lost");
            var result = await engine.ExecuteAsync(request);
            AssertUnknown(result);
            await RetiredAsync(engine);
            var entry = fixture.Repository.Peek("created");
            AssertMetadata(fixture.Stored(request.Rid!).Receipt, request, PrivateSubject, Convert.ToBase64String(entry.PublicDer));
            fixture.Repository.AfterPublish = null;
            var diagnosed = await engine.ExecuteAsync(Request("inspect", Body("inspect", reference: "created"), "diagnose-created"));
            Check(diagnosed.Ok && Live(diagnosed).Contains(entry.CertificateId, StringComparison.Ordinal) && fixture.Repository.Publishes == 1 &&
                fixture.Repository.ResolveCalls == 0, "explicit unpinned public inspection can diagnose unknown creation without private-key adoption");
            var mismatch = await engine.ExecuteAsync(Request("inspect", Body("inspect", ZeroHash, "created"), "diagnose-wrong-pin"));
            Check(!mismatch.Ok && fixture.Repository.Publishes == 1, "explicit inspect pin is enforced when supplied");
            var replay = await engine.ExecuteAsync(request);
            Check(replay.Jid == result.Jid && replay.State == "recovery_required" && fixture.Repository.Publishes == 1,
                "public diagnosis does not turn unknown publication into success or replay/adopt its key");
        }
    }

    private static async Task CrossFamilyCollisionsAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using var fixture = new Fixture(authority, signer);
        foreach (var (family, op) in new[] { ("registry", "registry_set"), ("app", "app_start"), ("mkdir", "mkdir") })
            fixture.SeedOld(family, op, "old-" + family);
        await using var engine = fixture.Create();
        var rows = fixture.Rows();
        foreach (var family in new[] { "registry", "app", "mkdir" })
        {
            var old = fixture.Stored("old-" + family);
            var refused = await engine.ExecuteAsync(Request("create_ca", Body("create_ca"), old.Receipt.Rid));
            Check(!refused.Ok && refused.Err == "precondition" && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0 &&
                fixture.Rows() == rows && fixture.Stored(old.Receipt.Rid).RawReceipt == old.RawReceipt,
                "certificate cannot adopt existing " + family + " RID or invoke a factory");
        }
        var createdRequest = Request("create_ca", Body("create_ca"), "certificate-original-for-collisions");
        var created = await engine.ExecuteAsync(createdRequest);
        Check(created.Ok, "isolated certificate receipt establishes reverse collision barrier");
        await RetiredAsync(engine);
        var original = fixture.Stored(created.Rid);
        var registry = new UnusedRegistry();
        engine.BindRegistryOwner(new(OwnerSid, () => fixture.Peer, admission => new RegistryOperations(registry, admission)));
        var registryBody = JsonSerializer.Serialize(new
        {
            version = 1, key = new { hive = "current_user", view = 64, keyPath = "Software\\Certificate wiring fixture" },
            valueName = "never-write", value = new { type = "binary", base64 = "AQ==" },
            preimage = new { keyGeneration = ZeroHash, valueDigest = (string?)null }
        });
        var target = Path.Combine(fixture.Workspace, "must-not-create");
        foreach (var request in new[]
        {
            new OperationRequest("registry", new Dictionary<string, string> { ["op"] = "set" }, created.Rid, Body: registryBody),
            new OperationRequest("app", new Dictionary<string, string> { ["op"] = "start", ["name"] = "must-not-start", ["cfg"] = "not-registered" }, created.Rid),
            new OperationRequest("app", new Dictionary<string, string> { ["op"] = "status", ["name"] = "must-not-start" }, created.Rid),
            new OperationRequest("mkdir", new Dictionary<string, string> { ["dst"] = target }, created.Rid)
        })
        {
            var refused = await engine.ExecuteAsync(request);
            Check(!refused.Ok && refused.Err == "precondition" && registry.Calls == 0 && fixture.Repository.Publishes == 1 &&
                fixture.Stored(created.Rid).RawReceipt == original.RawReceipt && !Directory.Exists(target),
                "registry/app/other request cannot adopt certificate RID or replace its original outcome");
        }
    }

    private static async Task BodyLossRecoveryAsync(X509Certificate2 authority, X509Certificate2 signer)
    {
        using (var fixture = new Fixture(authority, signer))
        {
            var engine = fixture.Create();
            var request = await WireAsync(Request("create_ca", Body("create_ca"), "actual-queued-certificate-body-loss"));
            fixture.Mode.HoldAfterNextRead();
            var pending = engine.ExecuteAsync(request);
            try
            {
                await UntilAsync(() => fixture.Find(request.Rid!)?.State == "queued");
                var queued = fixture.Stored(request.Rid!);
                AssertStoredRequestMetadata(queued);
                Check(queued.Request?.Body is null && queued.Request?.Arg.GetValueOrDefault(OperationRequest.BodyOmittedKey) == "1" &&
                    Identity(queued.Receipt) == CertificateOperationContract.Parse(request).RequestIdentity && fixture.FactoryCalls == 0,
                    "actual accepted queued certificate row drops its body before any factory is used");
            }
            finally
            {
                await engine.DisposeAsync();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }
            fixture.Mode.Open();
            await using var reopened = fixture.Create();
            var recovered = fixture.Stored(request.Rid!);
            Check(recovered.State == "recovery_required" && recovered.Request is null && fixture.FactoryCalls == 0 && fixture.Repository.Calls == 0,
                "queued lost-body restart never regenerates or publishes a certificate");
            AssertUnknown(recovered.Receipt);
            var replay = await reopened.ExecuteAsync(request);
            Check(replay.Jid == recovered.Receipt.Jid && replay.State == "recovery_required" && fixture.FactoryCalls == 0,
                "queued body-loss RID returns retained uncertainty without original key generation");
            var changed = request with { Body = Body("create_ca", subject: PrivateSubject + " changed") };
            Check((await reopened.ExecuteAsync(changed)).Err == "precondition" && fixture.FactoryCalls == 0,
                "recovered body-loss identity cannot be replaced with a fresh certificate request");
        }
        using (var fixture = new Fixture(authority, signer))
        {
            StoredRow captured;
            var request = fixture.FileRequest("sign", "captured-running-certificate-body-loss");
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Native.BeforeSign = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("fake recovery sign fence timed out");
            };
            await using (var engine = fixture.Create())
            {
                await fixture.RegisterAsync(engine);
                var pending = engine.ExecuteAsync(request);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    captured = fixture.Stored(request.Rid!);
                    AssertStoredRequestMetadata(captured);
                    Check(captured.State == "running" && captured.Request?.Body is null &&
                        Identity(captured.Receipt) == CertificateOperationContract.Parse(request).RequestIdentity,
                        "captured actual running row carries no recoverable signing body");
                }
                finally { release.Set(); fixture.Native.BeforeSign = null; }
                Check((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Ok && fixture.Native.Signs == 1, "original fake sign completes once");
                await RetiredAsync(engine);
            }
            // Restoring a captured durable snapshot models crash recovery. No
            // process is killed, and this does not claim a real crash was tested.
            fixture.Restore(captured);
            var factories = fixture.FactoryCalls;
            await using var reopened = fixture.Create();
            var recovered = fixture.Stored(request.Rid!);
            Check(recovered.State == "recovery_required" && recovered.Request is null && fixture.FactoryCalls == factories && fixture.Native.Signs == 1,
                "running lost-body snapshot becomes uncertainty without re-signing");
            AssertUnknown(recovered.Receipt);
            Check((await reopened.ExecuteAsync(request)).Jid == captured.Receipt.Jid && fixture.Native.Signs == 1 && fixture.FactoryCalls == factories,
                "running recovery keeps original RID identity and does not invoke an adapter");
        }
    }

    private static OperationRequest Request(string action, string body, string? rid) =>
        new("certificate", new Dictionary<string, string> { ["op"] = action }, rid, Body: body);

    private static string Body(string action, string? certificateId = null, string reference = "signer", string subject = PrivateSubject, string? issuerId = null)
    {
        var body = new Dictionary<string, object?> { ["version"] = 1 };
        if (action != "verify") body["reference"] = action == "create_ca" ? "created" : reference;
        if (action is "create_ca" or "issue_code" or "issue_server")
        { body["subject"] = subject; body["days"] = 1; body["keyBits"] = 2048; }
        if (action is "issue_code" or "issue_server")
            body["issuer"] = new { reference = "issuer", certificateId = issuerId ?? ZeroHash };
        if (action == "issue_server")
        {
            body["dnsNames"] = new[] { "localhost" };
            body["ipAddresses"] = new[] { "127.0.0.1", "::1" };
        }
        if (certificateId is not null) body["certificateId"] = certificateId;
        if (action == "export_public") body["format"] = "der";
        return JsonSerializer.Serialize(body);
    }
    private static string Live(OperationReceipt receipt)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(receipt.Data, Json));
        return string.Join('\n', JsonStrings(document.RootElement));
    }
    private static string? Identity(OperationReceipt receipt) => receipt.Data?.GetValueOrDefault("requestIdentity") switch
    { string value => value, JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(), _ => null };
    private static bool? Flag(OperationReceipt receipt, string name) => receipt.Data?.GetValueOrDefault(name) switch
    { bool value => value, JsonElement { ValueKind: JsonValueKind.True } => true, JsonElement { ValueKind: JsonValueKind.False } => false, _ => null };
    private static void AssertMetadata(OperationReceipt receipt, OperationRequest request, params string[] forbidden)
    {
        Check(Identity(receipt) == CertificateOperationContract.Parse(request).RequestIdentity, "durable certificate request identity is exact");
        var text = JsonSerializer.Serialize(receipt, Json);
        foreach (var value in forbidden) Check(!RawContains(text, value), "public live certificate output entered metadata history/replay");
        foreach (var key in new[] { "certificate", "summary", "publicDer", "publicPem", "der", "pem", "body", "pfx", "secret", "protectedPfx", "protectedPassword" })
            Check(receipt.Data?.ContainsKey(key) != true, "durable certificate receipt contains a forbidden payload field");
        Check(receipt.Detail is null, "durable certificate detail must not retain payload text");
    }
    private static void AssertStoredRequestMetadata(StoredRow row)
    {
        Check(row.Request is { Op: "certificate", Body: null, Ws: null, Cwd: null, Root: null, PathRoots: null } request &&
            request.Rid == row.Receipt.Rid && request.Arg.Count == 2 && request.Arg.ContainsKey("op") &&
            request.Arg.GetValueOrDefault(OperationRequest.BodyOmittedKey) == "1",
            "queued/running stored certificate request retains only selector, omitted-body marker and RID; no paths, pins, workspace or payload");
    }
    private static bool RawContains(string? text, string value)
    {
        if (string.IsNullOrEmpty(text)) return false;
        using var document = JsonDocument.Parse(text);
        return JsonStrings(document.RootElement).Any(part => part.Contains(value, StringComparison.Ordinal));
    }
    private static IEnumerable<string> JsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var text in JsonStrings(property.Value)) yield return text;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var text in JsonStrings(item)) yield return text;
    }
    private static void AssertUnknown(OperationReceipt receipt) => Check(!receipt.Ok && receipt.State == "recovery_required" && receipt.Partial &&
        receipt.Failure is { Execution.Value: OperationExecutionState.Unknown, Retry.Value: OperationRetryDisposition.NotSupported },
        "unknown certificate effect is preserved without false success/cancel acknowledgement or retry");
    private static Task RetiredAsync(AgentOpsEngine engine) => UntilAsync(() => engine.ResidentCompletionCount == 0 && engine.ResidentJobCount == 0);
    private static async Task UntilAsync(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed record StoredRow(string State, OperationReceipt Receipt, OperationRequest? Request, string RawReceipt, string? RawRequest);

    private sealed class Fixture : IDisposable
    {
        private readonly TempRoot _root = new();
        private int _repositoryFactories, _adapterFactories, _bindingFactories;
        internal readonly FakeRepository Repository = new();
        internal readonly FakeAuthenticode Native;
        internal readonly FakeServerBinding ServerBinding = new();
        internal readonly UserHostAdmission Admission = new();
        internal readonly OpenMode Mode = new();
        internal LocalPeer Peer = new(OwnerSid, 7);
        internal string State { get; }
        internal string Workspace { get; }
        internal string Source { get; }
        internal string Sha { get; }
        internal string SignerId => Repository.Peek("signer").CertificateId;
        internal string IssuerId => Repository.Peek("issuer").CertificateId;
        internal string? LastAdapterRoot { get; private set; }
        internal int FactoryCalls => Volatile.Read(ref _repositoryFactories) + Volatile.Read(ref _adapterFactories) + Volatile.Read(ref _bindingFactories);
        internal Fixture(X509Certificate2 authority, X509Certificate2 signer)
        {
            State = _root.Dir("certificate-engine"); Workspace = _root.Dir("certificate-workspace");
            Source = Path.Combine(Workspace, "owned-fixture.bin");
            File.WriteAllText(Source, "Owned synthetic bytes; never native-signed by this fixture.");
            Sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Source)));
            Repository.Seed("issuer", authority); Repository.Seed("signer", signer); Native = new(Repository);
        }
        internal string Directory(string name) => _root.Dir(name);
        internal CertificateOwnerBinding Binding() => new(OwnerSid, () => Peer,
            () => { Interlocked.Increment(ref _repositoryFactories); return Repository; },
            (root, repository) =>
            {
                Interlocked.Increment(ref _adapterFactories); LastAdapterRoot = root;
                Check(ReferenceEquals(repository, Repository), "adapter received a substituted repository");
                return Native;
            }, new FixedClock(), serverBinding: authorize =>
            {
                Interlocked.Increment(ref _bindingFactories);
                ServerBinding.Authorize = authorize;
                return ServerBinding;
            });
        internal AgentOpsEngine Create(bool bind = true, bool direct = false, bool admission = true)
        {
            var engine = new AgentOpsEngine(State, null, direct, null, false, null,
                maintenanceMode: Mode, userHostAdmission: admission ? Admission : null);
            if (bind) engine.BindCertificateOwner(Binding());
            return engine;
        }
        internal OperationRequest FileRequest(string action, string rid) => Request(action, Body(action, SignerId), rid) with
        { Ws = "certificate-fixture", Arg = new Dictionary<string, string> { ["op"] = action, ["src"] = Source, ["sha"] = Sha } };
        internal async Task RegisterAsync(AgentOpsEngine engine)
        {
            var result = await engine.ExecuteAsync(new("ctx", new Dictionary<string, string> { ["register"] = "certificate-fixture:" + Workspace }));
            Check(result.Ok, "scratch certificate workspace registration failed"); await RetiredAsync(engine);
        }
        internal long Rows()
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM jobs"; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        internal StoredRow Stored(string rid) => Find(rid) ?? throw new InvalidOperationException("missing owned certificate fixture row");
        internal StoredRow? Find(string rid)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,receipt,request FROM jobs WHERE rid=$rid"; command.Parameters.AddWithValue("$rid", rid);
            using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            var receipt = reader.GetString(1); var request = reader.IsDBNull(2) ? null : reader.GetString(2);
            return new(reader.GetString(0), JsonSerializer.Deserialize<OperationReceipt>(receipt, Json)!,
                request is null ? null : JsonSerializer.Deserialize<OperationRequest>(request, Json), receipt, request);
        }
        internal void SeedOld(string family, string op, string rid)
        {
            using var store = new SqliteJobStore(State);
            store.Claim(new(false, Guid.NewGuid().ToString("N"), rid, "failed", op, "precondition",
                Data: new Dictionary<string, object?> { ["requestIdentity"] = ZeroHash }),
                new(family, new Dictionary<string, string> { ["op"] = family == "registry" ? "set" : "start" }, rid), true);
        }
        internal void Restore(StoredRow row)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET state=$state,receipt=$receipt,request=$request WHERE jid=$jid AND rid=$rid";
            command.Parameters.AddWithValue("$state", row.State); command.Parameters.AddWithValue("$receipt", row.RawReceipt);
            command.Parameters.AddWithValue("$request", (object?)row.RawRequest ?? DBNull.Value);
            command.Parameters.AddWithValue("$jid", row.Receipt.Jid); command.Parameters.AddWithValue("$rid", row.Receipt.Rid);
            Check(command.ExecuteNonQuery() == 1, "captured fixture receipt was not restored exactly once");
        }
        internal void AssertHistoryExcludes(params string[] forbidden)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT receipt,COALESCE(request,'') FROM jobs";
            using var reader = command.ExecuteReader();
            while (reader.Read()) foreach (var text in new[] { reader.GetString(0), reader.GetString(1) })
                foreach (var value in forbidden) Check(!RawContains(text, value), "certificate payload entered owned SQLite history");
        }
        private SqliteConnection Connection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(State, "agentops.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose() { Repository.Dispose(); _root.Dispose(); }
    }

    private sealed class OpenMode : IMaintenanceModeSource
    {
        private readonly object _gate = new();
        private bool _holdAfterRead, _closed;
        internal void HoldAfterNextRead() { lock (_gate) _holdAfterRead = true; }
        internal void Open() { lock (_gate) { _holdAfterRead = false; _closed = false; } }
        public MaintenanceModeRecord? Read()
        {
            lock (_gate)
            {
                var closed = _closed;
                if (_holdAfterRead) { _holdAfterRead = false; _closed = true; }
                return closed ? new(1, "certificate-fixture", "active", "fixture-jid", "fixture-rid", "fixture-scope", "fixture-task",
                    OwnerSid, 7, "fixture-service", null, 1) : null;
            }
        }
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }

    private sealed class FakeRepository : IOwnerCertificateRepository, IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (OwnerCertificateEntry Entry, X509Certificate2 Certificate)> _entries = new(StringComparer.Ordinal);
        private int _publishes, _inspects, _resolves;
        internal int Publishes => Volatile.Read(ref _publishes);
        internal int ResolveCalls => Volatile.Read(ref _resolves);
        internal int Calls => Publishes + Volatile.Read(ref _inspects) + ResolveCalls;
        internal Action? AfterPublish;
        internal void Seed(string reference, X509Certificate2 certificate)
        {
            lock (_gate) _entries.Add(reference, (Entry(reference, certificate, ZeroHash), Copy(certificate)));
        }
        internal OwnerCertificateEntry Peek(string reference)
        { lock (_gate) { var entry = _entries[reference].Entry; return entry with { PublicDer = entry.PublicDer.ToArray() }; } }
        public OwnerCertificateEntry Publish(string reference, X509Certificate2 ownedCertificate, string requestIdentity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref _publishes);
            OwnerCertificateEntry entry;
            lock (_gate)
            {
                if (_entries.ContainsKey(reference)) throw new OwnerCertificateRepositoryException("certificate_reference_exists");
                entry = Entry(reference, ownedCertificate, requestIdentity);
                _entries.Add(reference, (entry, Copy(ownedCertificate)));
            }
            AfterPublish?.Invoke(); return entry with { PublicDer = entry.PublicDer.ToArray() };
        }
        public OwnerCertificateEntry Inspect(string reference, string? expectedCertificateId = null)
        {
            Interlocked.Increment(ref _inspects);
            lock (_gate)
            {
                if (!_entries.TryGetValue(reference, out var stored)) throw new OwnerCertificateRepositoryException("certificate_reference_missing");
                if (expectedCertificateId is not null && expectedCertificateId != stored.Entry.CertificateId)
                    throw new OwnerCertificateRepositoryException("certificate_identity_mismatch");
                return stored.Entry with { PublicDer = stored.Entry.PublicDer.ToArray() };
            }
        }
        public X509Certificate2 Resolve(CertificateKeyReference reference)
        {
            Interlocked.Increment(ref _resolves);
            lock (_gate)
            {
                if (!_entries.TryGetValue(reference.Reference, out var stored) || reference.ExpectedCertificateId != stored.Entry.CertificateId)
                    throw new OwnerCertificateRepositoryException("certificate_identity_mismatch");
                return Copy(stored.Certificate);
            }
        }
        private static OwnerCertificateEntry Entry(string reference, X509Certificate2 certificate, string requestIdentity) =>
            new(1, OwnerSid, reference, CertificateOperations.Inspect(certificate).CertificateId, requestIdentity, certificate.RawData, true);
        private static X509Certificate2 Copy(X509Certificate2 certificate)
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("fixture key missing");
            return publicOnly.CopyWithPrivateKey(key);
        }
        public void Dispose() { lock (_gate) { foreach (var entry in _entries.Values) entry.Certificate.Dispose(); _entries.Clear(); } }
    }

    private sealed class FakeAuthenticode(FakeRepository repository) : IAuthenticodeFileAdapter
    {
        private int _signs, _verifies;
        internal int Signs => Volatile.Read(ref _signs);
        internal int Verifies => Volatile.Read(ref _verifies);
        internal Action? BeforeSign;
        internal AuthenticodeAttemptException? SignFailure;
        public AuthenticodeSignResult Sign(AuthenticodeSignRequest request)
        {
            Interlocked.Increment(ref _signs); BeforeSign?.Invoke();
            if (SignFailure is not null) throw SignFailure;
            using var key = repository.Resolve(request.Signer);
            return new("signed", request.Signer.ExpectedCertificateId, request.ExpectedContentSha256, false);
        }
        public AuthenticodeVerificationResult Verify(AuthenticodeVerifyRequest request)
        { Interlocked.Increment(ref _verifies); return new("valid", "untrusted", "not_checked", request.ExpectedCertificateId, unchecked((int)0x800b010a)); }
    }

    private sealed class FakeServerBinding : ICertificateServerBinding
    {
        private readonly HashSet<string> _bound = new(StringComparer.Ordinal);
        internal Action? Authorize;
        internal int Calls, Mutations;
        internal ServerCertificateBindingException? Failure;
        internal Exception? GenericFailure;
        internal ServerCertificateBindingResult? Override;
        public ServerCertificateBindingResult BindServer(X509Certificate2 certificate, string expectedCertificateId,
            DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Authorize!();
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            _ = ServerCertificateMaterial.Require(certificate, expectedCertificateId, now, privateKey: true);
            ServerCertificateMaterial.ProvePrivateKey(certificate);
            if (Failure is not null) throw Failure;
            if (GenericFailure is not null) throw GenericFailure;
            if (Override is not null) return Override;
            if (!_bound.Add(expectedCertificateId)) return new("unchanged", expectedCertificateId, false, false, true, false);
            Mutations++;
            return new("bound", expectedCertificateId, true, true, true, false);
        }
    }

    // A collision reaching any registry method is itself the fixture failure;
    // no registry state is emulated or accessed by these certificate tests.
    private sealed class UnusedRegistry : IRegistryNative
    {
        internal int Calls;
        private Exception Unexpected() { Calls++; return new InvalidOperationException("certificate RID collision reached registry"); }
        public RegistryNativeKeyInfo? InspectKey(RegistryKeyIdentity key) => throw Unexpected();
        public RegistryNativeValue? ReadValue(RegistryValueIdentity identity, int maxBytes) => throw Unexpected();
        public RegistryBrowseEntry? ReadName(RegistryKeyIdentity key, RegistryBrowseKind kind, int index, int maxNameChars) => throw Unexpected();
        public bool CreateKey(RegistryKeyIdentity key) => throw Unexpected();
        public void SetValue(RegistryValueIdentity identity, RegistryValueType type, byte[] data) => throw Unexpected();
        public void DeleteValue(RegistryValueIdentity identity) => throw Unexpected();
        public void DeleteEmptyKey(RegistryKeyIdentity key) => throw Unexpected();
    }
}
