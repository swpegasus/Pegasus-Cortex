using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Mcp.Hosting;

internal static class CortexMcpSetupWorkflowTests
{
    private const string Ca = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string App = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Service = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Scope = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    internal static async Task RunAsync()
    {
        await FirstTimeAsync();
        await ExistingReferencesAsync();
        await IssuerAdmissionAsync();
        await OriginalOutcomesAsync();
        await ReviewedCancellationAsync();
        await SettingsAndFinalProofAsync();
        await CancellationAndBusyAsync();
        Console.WriteLine("PASS guided MCP setup workflow (injected dispatch, durable-receipt models and final-helper results; no certificates, stores, UAC, network, services, files or settings writes)");
    }

    private static async Task FirstTimeAsync()
    {
        foreach (var owner in new[] { CortexMcpOwner.Application, CortexMcpOwner.Service })
        {
            var fixture = new Fixture();
            var original = CortexMcpSetupWorkflow.Fingerprint(fixture.Saved);
            var result = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, owner), CancellationToken.None);
            Check(result.Ok && result.TlsQualified && !result.RequiresReview && fixture.Completions == 1,
                "Fresh setup did not reach the injected final qualification/save.");
            Check(fixture.Actions.SequenceEqual(owner == CortexMcpOwner.Application
                ? ["create_ca", "inspect", "issue_server", "store_bind_server", "store_admin_trust"]
                : ["create_ca", "inspect", "issue_machine_server", "store_admin_trust"]), "Role-specific action sequence changed.");
            Check(fixture.CompleteCa == Ca && fixture.CompleteLeaf == (owner == CortexMcpOwner.Application ? App : Service) &&
                fixture.CompleteRequest is { Enabled: true } selected && selected.Owner == owner &&
                CortexMcpSetupWorkflow.Fingerprint(selected.Baseline) == original &&
                CortexMcpSetupWorkflow.Fingerprint(fixture.Saved) == original, "Workflow changed settings or selected wrong role/pins.");
            var requests = fixture.Calls.Where(request => request.Op == "certificate").Select(CertificateOperationContract.Parse).ToArray();
            Check(requests[0].Days == 365 && requests[0].KeyBits == 3072 && requests[^1].Store is { Scope: "local_machine", Store: "root" },
                "First-time CA defaults or system-wide trust scope changed.");
            Check(requests[1].Action == "inspect" && requests[1].CertificateId == Ca &&
                requests[1].Reference == requests[0].Reference, "Managed CA publication/replay was not followed by a pinned inspection.");
            var leaf = requests.Single(request => request.Action is "issue_server" or "issue_machine_server");
            Check(leaf.Days == 30 && leaf.KeyBits == 3072 && leaf.ServerNames is { } names &&
                names.DnsNames.Contains("localhost") && names.IpAddresses.Contains("127.0.0.1") && names.IpAddresses.Contains("::1"),
                "Leaf validity/key/name defaults changed.");
            Check(fixture.MutationTokens.All(canCancel => !canCancel), "An issued key/store operation was abandoned to UI cancellation.");
        }
    }

    private static async Task ExistingReferencesAsync()
    {
        var fixture = new Fixture
        {
            Saved = CortexMcpSettingsStore.Default with
            {
                BindAddress = "192.168.1.10", Port = 49001, ServerName = "cortex.example.test", AllowPrivateNetwork = true,
                AllowedPeerCidrs = ["192.168.1.0/24"],
                CertificateAuthority = new("LocalMachine", "Root", Ca),
                ApplicationCertificate = new("CurrentUser", "My", App), ServiceCertificate = new("LocalMachine", "My", Service)
            }
        };
        var baseline = CortexMcpSetupWorkflow.Fingerprint(fixture.Saved);
        var result = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Service), CancellationToken.None);
        Check(result.Ok && fixture.Calls.Count == 0 && fixture.Completions == 1 && fixture.CompleteLeaf == Service,
            "Complete references required an issuer or repeated an issuance/trust operation.");
        Check(CortexMcpSetupWorkflow.Fingerprint(fixture.Saved) == baseline &&
            fixture.CompleteRequest!.Baseline.AllowedPeerCidrs.SequenceEqual(["192.168.1.0/24"]), "Existing network/opposite-role configuration was reset.");

        var userCa = fixture.Saved with { CertificateAuthority = new("CurrentUser", "Root", Ca) };
        var noIssuer = new Fixture { Saved = userCa };
        var notPrepared = await noIssuer.Workflow().SetupAsync(new(userCa, true, CortexMcpOwner.Service), CancellationToken.None);
        Check(!notPrepared.Ok && noIssuer.Calls.Count == 0 && noIssuer.Completions == 0,
            "Complete nonmachine CA references bypassed explicit machine-wide trust preparation.");
        var publicIssuer = new Fixture { Saved = userCa };
        publicIssuer.Edit = (request, receipt) => request.Arg.GetValueOrDefault("op") != "inspect" ? receipt : receipt with
        {
            Data = Changed(receipt, "certificate", new { certificateId = Ca, reference = "existing-issuer",
                subject = "CN=Fixture MCP Root", issuer = "CN=Fixture MCP Root",
                notBefore = DateTimeOffset.UtcNow.AddDays(-1), notAfter = DateTimeOffset.UtcNow.AddDays(365),
                isAuthority = true, protectedMaterialPresent = false, keyAvailability = "public_only" })
        };
        var prepared = await publicIssuer.Workflow().SetupAsync(new(userCa, true, CortexMcpOwner.Service, "existing-issuer"), CancellationToken.None);
        Check(prepared.Ok && publicIssuer.Actions.SequenceEqual(["inspect", "store_admin_trust"]) &&
            publicIssuer.Completions == 1 && publicIssuer.CompleteLeaf == Service && publicIssuer.CompleteCa == Ca,
            "An existing leaf could not prepare machine trust from its explicit matching public-only authority reference.");

        var naming = new Fixture { Saved = CortexMcpSettingsStore.Default with { ServerName = "cortex.example.test" } };
        Check((await naming.Workflow().SetupAsync(new(naming.Saved, false, CortexMcpOwner.Application), CancellationToken.None)).Ok,
            "Configured name setup failed.");
        var leaf = CertificateOperationContract.Parse(naming.Calls.Single(request => request.Arg.GetValueOrDefault("op") == "issue_server"));
        Check(leaf.ServerNames!.DnsNames.Contains("cortex.example.test") && leaf.ServerNames.DnsNames.Contains("localhost") &&
            naming.CompleteRequest!.Enabled == false, "Configured name or disabled selection was changed.");
    }

    private static async Task IssuerAdmissionAsync()
    {
        var selectedCa = CortexMcpSettingsStore.Default with { CertificateAuthority = new("LocalMachine", "Root", Ca) };
        var missing = new Fixture { Saved = selectedCa };
        var blocked = await missing.Workflow().SetupAsync(new(selectedCa, true, CortexMcpOwner.Service), CancellationToken.None);
        Check(!blocked.Ok && missing.Calls.Count == 0 && missing.Completions == 0, "Configured public CA silently became a signer or was replaced.");
        foreach (var invalid in new[] { "public-only", "wrong-pin", "not-authority", "wrong-reference" })
        {
            var fixture = new Fixture { Saved = selectedCa };
            fixture.Edit = (request, receipt) => request.Arg.GetValueOrDefault("op") != "inspect" ? receipt : receipt with
            {
                Data = new Dictionary<string, object?>(receipt.Data!)
                {
                    ["certificateId"] = invalid == "wrong-pin" ? App : Ca,
                    ["certificate"] = new { certificateId = invalid == "wrong-pin" ? App : Ca,
                        reference = invalid == "wrong-reference" ? "other" : "existing-issuer",
                        subject = "CN=Fixture MCP Root", issuer = "CN=Fixture MCP Root",
                        notBefore = DateTimeOffset.UtcNow.AddDays(-1), notAfter = DateTimeOffset.UtcNow.AddDays(365),
                        isAuthority = invalid != "not-authority", protectedMaterialPresent = invalid != "public-only" }
                }
            };
            var result = await fixture.Workflow().SetupAsync(new(selectedCa, true, CortexMcpOwner.Service, "existing-issuer"), CancellationToken.None);
            Check(!result.Ok && fixture.Actions.SequenceEqual(["inspect"]) && fixture.Completions == 0,
                "An unusable/mismatched issuer reached a mutation: " + invalid);
        }
        foreach (var invalid in new[] { "expired", "future", "intermediate", "missing-validity" })
        {
            var fixture = new Fixture { Saved = selectedCa };
            fixture.Edit = (request, receipt) =>
            {
                if (request.Arg.GetValueOrDefault("op") != "inspect") return receipt;
                var summary = new Dictionary<string, object?>
                {
                    ["certificateId"] = Ca, ["reference"] = "existing-issuer", ["isAuthority"] = true,
                    ["protectedMaterialPresent"] = true, ["subject"] = "CN=Fixture MCP Root",
                    ["issuer"] = invalid == "intermediate" ? "CN=Other issuer" : "CN=Fixture MCP Root",
                    ["notBefore"] = DateTimeOffset.UtcNow.AddDays(invalid == "future" ? 1 : -2),
                    ["notAfter"] = DateTimeOffset.UtcNow.AddDays(invalid == "expired" ? -1 : 365)
                };
                if (invalid == "missing-validity") summary.Remove("notAfter");
                return receipt with { Data = Changed(receipt, "certificate", summary) };
            };
            var result = await fixture.Workflow().SetupAsync(new(selectedCa, true, CortexMcpOwner.Service, "existing-issuer"), CancellationToken.None);
            Check(!result.Ok && fixture.Actions.SequenceEqual(["inspect"]) && fixture.Completions == 0,
                "Invalid root metadata reached issuance or machine-wide trust: " + invalid);
        }
        var valid = new Fixture { Saved = selectedCa, TrustUnchanged = true };
        var completed = await valid.Workflow().SetupAsync(new(selectedCa, true, CortexMcpOwner.Service, "existing-issuer"), CancellationToken.None);
        Check(completed.Ok && valid.Actions.SequenceEqual(["inspect", "issue_machine_server", "store_admin_trust"]),
            "Explicit matching issuer was not reused or triggered replacement CA creation.");
    }

    private static async Task OriginalOutcomesAsync()
    {
        var uncertain = new Fixture();
        uncertain.Edit = (request, receipt) => request.Arg.GetValueOrDefault("op") == "create_ca"
            ? receipt with { Ok = false, State = "recovery_required", Err = "recovery_required", Partial = true } : receipt;
        var workflow = uncertain.Workflow();
        var first = await workflow.SetupAsync(new(uncertain.Saved, true, CortexMcpOwner.Application), CancellationToken.None);
        var second = await workflow.SetupAsync(new(uncertain.Saved, true, CortexMcpOwner.Application), CancellationToken.None);
        Check(!first.Ok && first.RequiresReview && first.JobId is not null && !second.Ok && uncertain.Completions == 0 &&
            uncertain.Calls.Count == 2 && uncertain.Calls[0].Rid == uncertain.Calls[1].Rid && uncertain.NewMutationExecutions == 1,
            "Uncertain issuance advanced/replayed with a new RID.");

        var unledgered = new Fixture { ExistingUnledgeredReference = true };
        var unadopted = await unledgered.Workflow().SetupAsync(new(unledgered.Saved, true, CortexMcpOwner.Application), CancellationToken.None);
        Check(!unadopted.Ok && unledgered.Actions.SequenceEqual(["create_ca"]) && unledgered.Completions == 0,
            "Unledgered default reference was silently inspected/adopted.");

        foreach (var invalid in new[] { "rid", "op", "jid", "identity", "postcondition", "unknown-missing", "pin" })
        {
            var fixture = new Fixture();
            fixture.Edit = (request, receipt) => invalid switch
            {
                "rid" => receipt with { Rid = "wrong" },
                "op" => receipt with { Op = "certificate_inspect" },
                "jid" => receipt with { Jid = "not-a-job" },
                "identity" => receipt with { Data = Changed(receipt, "requestIdentity", App) },
                "postcondition" => receipt with { Data = Changed(receipt, "postcondition_observed", false) },
                "unknown-missing" => receipt with { Data = Changed(receipt, "outcome_unknown", null) },
                "pin" => receipt with { Data = Changed(receipt, "certificateId", "invalid") },
                _ => receipt
            };
            var result = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Application), CancellationToken.None);
            Check(!result.Ok && result.RequiresReview && fixture.Calls.Count == 1 && fixture.Completions == 0,
                "Invalid required receipt metadata advanced: " + invalid);
        }
        foreach (var mismatch in new[] { false, true })
        {
            var fixture = new Fixture { QueueAction = "create_ca", WrongWaitJob = mismatch };
            var result = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Application), CancellationToken.None);
            var wait = fixture.Calls.Single(request => request.Op == "job");
            Check(wait.Arg["jid"] == fixture.FirstQueuedJob && wait.Arg["op"] == "wait" &&
                (mismatch ? !result.Ok && result.RequiresReview && fixture.Completions == 0 : result.Ok),
                "Same-JID observation did not preserve the original operation identity.");
        }
    }

    private static async Task ReviewedCancellationAsync()
    {
        foreach (var action in new[] { "issue_machine_server", "store_admin_trust" })
        {
            var fixture = new Fixture();
            var canceled = false;
            fixture.Edit = (request, receipt) =>
            {
                if (request.Arg.GetValueOrDefault("op") != action || canceled) return receipt;
                canceled = true;
                return NoEffectCancellation(receipt);
            };
            var original = new CortexMcpSetupRequest(fixture.Saved, true, CortexMcpOwner.Service);
            var first = await fixture.Workflow().SetupAsync(original, CancellationToken.None);
            Check(!first.Ok && first.RequiresReview && first.Retry is { } step && step.Action == action &&
                step.JobId == first.JobId && fixture.Completions == 0, "Known cancellation did not require explicit review: " + action);
            var descriptor = first.Retry!;
            var reviewed = original with { ReviewedCanceledJobIds = [descriptor.JobId] };
            var completed = await fixture.Workflow().SetupAsync(reviewed, CancellationToken.None);
            Check(completed.Ok && completed.TlsQualified && fixture.Completions == 1,
                "Reviewed canceled step did not complete: " + action);
            var attempts = fixture.Calls.Where(call => call.Arg.GetValueOrDefault("op") == action).ToArray();
            Check(attempts.Length == 3 && attempts[0].Rid == attempts[1].Rid && attempts[2].Rid != attempts[0].Rid &&
                descriptor.RequestId == attempts[0].Rid &&
                descriptor.RequestIdentity == CertificateOperationContract.Parse(attempts[0]).RequestIdentity,
                "Retry did not replay the exact original before one deterministic successor.");
            Check(fixture.Calls.Where(call => call.Arg.GetValueOrDefault("op") == "create_ca").Select(call => call.Rid).Distinct().Count() == 1 &&
                (action != "store_admin_trust" || fixture.Calls.Where(call => call.Arg.GetValueOrDefault("op") == "issue_machine_server")
                    .Select(call => call.Rid).Distinct().Count() == 1), "Retry changed completed CA/leaf identities.");
            var executions = fixture.NewMutationExecutions;
            var afterRestart = await fixture.Workflow().SetupAsync(original, CancellationToken.None);
            Check(afterRestart.Retry == descriptor && fixture.NewMutationExecutions == executions,
                "Reopening setup lost the original cancellation or re-executed completed work.");
            var replay = await fixture.Workflow().SetupAsync(reviewed, CancellationToken.None);
            Check(replay.Ok && fixture.NewMutationExecutions == executions &&
                fixture.Calls.Last(call => call.Arg.GetValueOrDefault("op") == action).Rid == attempts[2].Rid,
                "Reopened reviewed chain created a new attempt instead of replaying its successor.");
        }

        var repeated = new Fixture();
        repeated.Edit = (request, receipt) => request.Arg.GetValueOrDefault("op") == "issue_machine_server"
            ? NoEffectCancellation(receipt) : receipt;
        var chain = new List<string>();
        for (var index = 0; index <= CortexMcpSetupWorkflow.MaximumReviewedCancellations; index++)
        {
            var result = await repeated.Workflow().SetupAsync(new(repeated.Saved, true, CortexMcpOwner.Service,
                ReviewedCanceledJobIds: chain.ToArray()), CancellationToken.None);
            Check(!result.Ok && repeated.Completions == 0, "Repeated cancellation advanced setup.");
            if (index == CortexMcpSetupWorkflow.MaximumReviewedCancellations)
                Check(result.Retry is null && result.Error!.Contains("limit", StringComparison.Ordinal), "Bounded review chain offered a ninth review.");
            else
            {
                Check(result.Retry is not null && !chain.Contains(result.Retry.JobId), "Repeated cancellation lost its exact new original.");
                chain.Add(result.Retry!.JobId);
            }
        }
        Check(repeated.Calls.Where(call => call.Arg.GetValueOrDefault("op") == "issue_machine_server")
            .Select(call => call.Rid).Distinct().Count() == CortexMcpSetupWorkflow.MaximumReviewedCancellations + 1,
            "Repeated review did not stay within its deterministic attempt bound.");

        var unknown = new Fixture();
        var firstAttempt = true;
        unknown.Edit = (request, receipt) =>
        {
            if (request.Arg.GetValueOrDefault("op") != "issue_machine_server") return receipt;
            if (firstAttempt) { firstAttempt = false; return NoEffectCancellation(receipt); }
            return receipt with { Ok = false, State = "recovery_required", Err = "recovery_required", Partial = true,
                Data = Changed(receipt, "outcome_unknown", true) };
        };
        var stopped = await unknown.Workflow().SetupAsync(new(unknown.Saved, true, CortexMcpOwner.Service), CancellationToken.None);
        var knownReview = new CortexMcpSetupRequest(unknown.Saved, true, CortexMcpOwner.Service,
            ReviewedCanceledJobIds: [stopped.Retry!.JobId]);
        var uncertain = await unknown.Workflow().SetupAsync(knownReview, CancellationToken.None);
        var mutationCount = unknown.NewMutationExecutions;
        var retained = await unknown.Workflow().SetupAsync(knownReview, CancellationToken.None);
        Check(!uncertain.Ok && uncertain.Retry is null && !retained.Ok && retained.Retry is null &&
            unknown.NewMutationExecutions == mutationCount && unknown.Completions == 0,
            "Unknown successor was retried or offered as a known-no-effect cancellation.");

        foreach (var invalid in new[] { "partial", "unknown", "missing-unknown", "payload", "missing-payload", "published",
            "missing-published", "effect", "malformed-effect", "attention", "failed", "pruned", "wrong-reference", "wrong-rid", "wrong-op", "wrong-identity" })
        {
            var fixture = new Fixture();
            fixture.Edit = (request, receipt) =>
            {
                if (request.Arg.GetValueOrDefault("op") != "issue_machine_server") return receipt;
                var canceledReceipt = NoEffectCancellation(receipt);
                return invalid switch
                {
                    "partial" => canceledReceipt with { Partial = true },
                    "unknown" => canceledReceipt with { Data = Changed(canceledReceipt, "outcome_unknown", true) },
                    "missing-unknown" => canceledReceipt with { Data = Changed(canceledReceipt, "outcome_unknown", null) },
                    "payload" => canceledReceipt with { Data = Changed(canceledReceipt, "administrator_payload_issued", true) },
                    "missing-payload" => canceledReceipt with { Data = Changed(canceledReceipt, "administrator_payload_issued", null) },
                    "published" => canceledReceipt with { Data = Changed(canceledReceipt, "published", true) },
                    "missing-published" => canceledReceipt with { Data = Changed(canceledReceipt, "published", null) },
                    "effect" => canceledReceipt with { Data = Changed(canceledReceipt, "key_creation_issued", true) },
                    "malformed-effect" => canceledReceipt with { Data = Changed(canceledReceipt, "key_creation_issued", "false") },
                    "attention" => canceledReceipt with { State = "attention" },
                    "failed" => canceledReceipt with { State = "failed" },
                    "pruned" => canceledReceipt with { State = "failed", Err = "not_found", Data = null },
                    "wrong-reference" => canceledReceipt with { Data = Changed(canceledReceipt, "reference", "other") },
                    "wrong-rid" => canceledReceipt with { Rid = "other" },
                    "wrong-op" => canceledReceipt with { Op = "certificate_inspect" },
                    _ => canceledReceipt with { Data = Changed(canceledReceipt, "requestIdentity", App) }
                };
            };
            var result = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Service), CancellationToken.None);
            Check(!result.Ok && result.Retry is null && fixture.Completions == 0,
                "Incomplete/contradictory cancellation proof offered a retry: " + invalid);
        }
        foreach (var storeAction in new object?[] { null, true, "false" })
        {
            var fixture = new Fixture();
            fixture.Edit = (request, receipt) =>
            {
                if(request.Arg.GetValueOrDefault("op") != "store_admin_trust")return receipt;
                var canceledReceipt=NoEffectCancellation(receipt);
                return canceledReceipt with { Data=Changed(canceledReceipt,"store_action_issued",storeAction) };
            };
            var result = await fixture.Workflow().SetupAsync(new(fixture.Saved,true,CortexMcpOwner.Service),CancellationToken.None);
            Check(!result.Ok && result.Retry is null && fixture.Completions == 0,
                "Trust retry did not require explicit false store-action proof.");
        }
        var finalCanceled = new Fixture { FinalResult = new(false,"Final Windows approval canceled",RequiresReview:true) };
        var finalRequest = new CortexMcpSetupRequest(finalCanceled.Saved,true,CortexMcpOwner.Service);
        var finalOriginal = await finalCanceled.Workflow().SetupAsync(finalRequest,CancellationToken.None);
        var finalMutationCount = finalCanceled.NewMutationExecutions;
        finalCanceled.FinalResult = new(true,TlsQualified:true);
        var finalRetried = await finalCanceled.Workflow().SetupAsync(finalRequest,CancellationToken.None);
        Check(!finalOriginal.Ok && finalOriginal.Retry is null && finalRetried.Ok && finalCanceled.Completions == 2 &&
            finalCanceled.NewMutationExecutions == finalMutationCount,
            "Final helper cancellation repeated certificate mutations or incorrectly required a canceled-certificate review.");
        foreach (var invalid in new IReadOnlyList<string>[] { ["bad"], [new string('a', 32), new string('a', 32)],
            Enumerable.Range(0, 9).Select(index => index.ToString("x32")).ToArray() })
        {
            var fixture = new Fixture();
            var result = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Service,
                ReviewedCanceledJobIds: invalid), CancellationToken.None);
            Check(!result.Ok && fixture.Calls.Count == 0, "Invalid/over-budget review chain reached dispatch.");
        }
    }

    private static OperationReceipt NoEffectCancellation(OperationReceipt receipt)
    {
        var data = new Dictionary<string, object?>(receipt.Data!)
        {
            ["administrator_payload_issued"] = false, ["outcome_unknown"] = false, ["postcondition_observed"] = false
        };
        foreach (var field in new[] { "published", "certificate_issued", "key_creation_issued", "key_finalized",
            "key_persistence_issued", "store_action_issued" }) data[field] = false;
        return receipt with { Ok = false, State = "canceled", Err = "canceled", Partial = false, Data = data };
    }

    private static async Task SettingsAndFinalProofAsync()
    {
        var changed = new Fixture();
        var baseline = changed.Saved;
        changed.Saved = baseline with { Port = 49000 };
        Check(!(await changed.Workflow().SetupAsync(new(baseline, true, CortexMcpOwner.Application), CancellationToken.None)).Ok &&
            changed.Calls.Count == 0, "Stale baseline reached a certificate operation.");
        var mid = new Fixture();
        mid.Edit = (request, receipt) => { mid.Saved = mid.Saved with { Port = 49000 }; return receipt; };
        Check(!(await mid.Workflow().SetupAsync(new(mid.Saved, true, CortexMcpOwner.Application), CancellationToken.None)).Ok &&
            mid.Calls.Count == 1 && mid.Completions == 0, "Settings drift between steps was ignored.");
        foreach (var result in new[] { new CortexMcpSetupResult(true), new(false, "Settings fingerprint changed.", RequiresReview: true) })
        {
            var fixture = new Fixture { FinalResult = result };
            var completed = await fixture.Workflow().SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Application), CancellationToken.None);
            Check(!completed.Ok && completed.RequiresReview && !completed.TlsQualified && fixture.Completions == 1,
                "Incomplete TLS proof or failed final CAS was presented as saved setup.");
        }
    }

    private static async Task CancellationAndBusyAsync()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new CortexMcpSetupWorkflow(Scope, () => fixture.Saved, async (request, token) =>
        {
            Check(!token.CanBeCanceled, "Submitted mutation used the caller cancellation token.");
            var receipt = await fixture.Dispatch(request, token);
            entered.SetResult();
            await release.Task;
            return receipt;
        }, fixture.Complete);
        using var cancellation = new CancellationTokenSource();
        var pending = workflow.SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Application), cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        Check(!pending.IsCompleted, "Cancellation abandoned the actual original callback.");
        Check(!(await workflow.SetupAsync(new(fixture.Saved, true, CortexMcpOwner.Application), CancellationToken.None)).Ok &&
            fixture.Calls.Count == 1, "Concurrent setup entered a second operation.");
        release.SetResult();
        var result = await pending;
        Check(!result.Ok && result.RequiresReview && fixture.Calls.Count == 1 && fixture.Completions == 0,
            "Cancellation issued another step or released the original callback early.");
    }

    private static Dictionary<string, object?> Changed(OperationReceipt receipt, string key, object? value) =>
        new(receipt.Data!) { [key] = value };
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture
    {
        internal CortexMcpSettings Saved = CortexMcpSettingsStore.Default;
        internal List<OperationRequest> Calls { get; } = [];
        internal List<bool> MutationTokens { get; } = [];
        internal string[] Actions => Calls.Where(request => request.Op == "certificate").Select(request => request.Arg["op"]).ToArray();
        internal Func<OperationRequest, OperationReceipt, OperationReceipt>? Edit;
        internal string? QueueAction, FirstQueuedJob;
        internal bool WrongWaitJob, ExistingUnledgeredReference, TrustUnchanged;
        internal int Completions, NewMutationExecutions;
        internal string? CompleteLeaf, CompleteCa;
        internal CortexMcpSetupRequest? CompleteRequest;
        internal CortexMcpSetupResult FinalResult = new(true, TlsQualified: true);
        private readonly Dictionary<string, OperationReceipt> _ledger = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OperationReceipt> _pending = new(StringComparer.Ordinal);
        private int _sequence;

        internal CortexMcpSetupWorkflow Workflow() => new(Scope, () => Saved, Dispatch, Complete);
        internal Task<CortexMcpSetupResult> Complete(CortexMcpSetupRequest request, string leaf, string ca, CancellationToken token)
        {
            Check(!token.CanBeCanceled, "Final helper ownership was abandoned to caller cancellation.");
            Completions++; CompleteRequest = request; CompleteLeaf = leaf; CompleteCa = ca;
            return Task.FromResult(FinalResult);
        }
        internal Task<OperationReceipt> Dispatch(OperationRequest request, CancellationToken token)
        {
            Calls.Add(request);
            if (request.Op == "job")
            {
                var waited = _pending[request.Arg["jid"]];
                return Task.FromResult(WrongWaitJob ? waited with { Jid = new string('f', 32) } : waited);
            }
            var invocation = CertificateOperationContract.Parse(request);
            if (invocation.IsMutation)
            {
                MutationTokens.Add(token.CanBeCanceled);
                if (_ledger.TryGetValue(request.Rid!, out var original)) return Task.FromResult(original);
                NewMutationExecutions++;
            }
            var pin = invocation.Action switch { "create_ca" or "inspect" or "store_admin_trust" => Ca,
                "issue_machine_server" => Service, _ => App };
            var data = new Dictionary<string, object?>
            {
                ["requestIdentity"] = invocation.RequestIdentity, ["reference"] = invocation.Reference,
                ["certificateId"] = pin, ["postcondition_observed"] = true, ["outcome_unknown"] = false,
                ["published"] = true, ["certificate_issued"] = true,
                ["issuerReference"] = invocation.Issuer?.Reference, ["issuerCertificateId"] = invocation.Issuer?.CertificateId,
                ["key_creation_issued"] = true, ["key_finalized"] = true, ["key_security_observed"] = true,
                ["key_persistence_issued"] = true, ["store_action_issued"] = true,
                ["store_scope"] = invocation.Action == "store_bind_server" ? "current_user" : "local_machine",
                ["store_kind"] = invocation.Action == "store_admin_trust" ? "root" : "personal",
                ["certificate_status"] = invocation.Action == "issue_machine_server" ? "bound" :
                    invocation.Action == "store_admin_trust" ? TrustUnchanged ? "unchanged" : "added" : "completed",
                ["certificate"] = new { certificateId = pin, reference = invocation.Reference, isAuthority = true,
                    subject = "CN=Fixture MCP Root", issuer = "CN=Fixture MCP Root",
                    notBefore = DateTimeOffset.UtcNow.AddDays(-1), notAfter = DateTimeOffset.UtcNow.AddDays(365),
                    protectedMaterialPresent = true, keyAvailability = "protected_material_not_opened" }
            };
            if (invocation.Action == "store_admin_trust" && TrustUnchanged) data["store_action_issued"] = false;
            var receipt = new OperationReceipt(true, (++_sequence).ToString("x32"), request.Rid!, "done",
                CertificateOperationContract.PublicName(invocation.Action), Data: data);
            if (ExistingUnledgeredReference && invocation.Action == "create_ca")
                receipt = receipt with { Ok = false, State = "failed", Err = "certificate_failed" };
            if (Edit is not null) receipt = Edit(request, receipt);
            if (invocation.IsMutation) _ledger.Add(request.Rid!, receipt);
            if (invocation.Action == QueueAction)
            {
                FirstQueuedJob = receipt.Jid; _pending.Add(receipt.Jid, receipt);
                return Task.FromResult(receipt with { State = "queued" });
            }
            return Task.FromResult(receipt);
        }
    }
}
