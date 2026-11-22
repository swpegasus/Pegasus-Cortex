using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

internal static class CertificateElevationContractTests
{
    private static readonly string Ticket = new('c', 64);
    private static readonly byte[] PublicDerFixture = [0x30, 0x00]; // Hash/shape fixture only, not an X.509 claim.
    private static readonly string Pin = Convert.ToHexStringLower(SHA256.HashData(PublicDerFixture));

    internal static Task RunAsync()
    {
        Payload();
        ControlEnvelope();
        Completion();
        Console.WriteLine("PASS certificate elevation closed payload/ticket/result contracts, pins/bounds/duplicates/outcome consistency (pure fixtures; no UAC, pipe, store or key calls)");
        return Task.CompletedTask;
    }

    private static void Payload()
    {
        var payload = Request();
        CertificateStoreElevationContract.ValidatePayload(payload);
        var parsed = CertificateStoreElevationContract.ParsePayload(CertificateStoreElevationContract.SerializePayload(payload));
        Check(parsed.Action == payload.Action && parsed.Store == payload.Store && parsed.CertificateId == Pin &&
            parsed.RequestIdentity == payload.RequestIdentity && parsed.Jid == payload.Jid && parsed.Rid == payload.Rid &&
            parsed.PublicDer!.SequenceEqual(PublicDerFixture), "canonical bounded payload round-trip");
        foreach (var invalid in new[]
        {
            payload with { Version = 0 }, payload with { Action = "run" }, payload with { Store = "HKLM\\Root" },
            payload with { Action = "store_add", Store = "root" }, payload with { Action = "store_trust", Store = "personal" },
            payload with { CertificateId = new string('A', 64) }, payload with { RequestIdentity = "invalid" },
            payload with { Jid = "not-a-generation" }, payload with { Jid = new string('A', 32) },
            payload with { Rid = "" }, payload with { Rid = "line\nrid" }, payload with { Rid = new string('r', 129) },
            payload with { PublicDer = null }, payload with { PublicDer = [] },
            payload with { PublicDer = [0x30, 0x01] }, payload with { PublicDer = new byte[65537] }
        }) Refused(() => CertificateStoreElevationContract.ValidatePayload(invalid));
        foreach (var store in new[] { "personal", "intermediate", "root", "trusted_publisher", "trusted_people" })
        {
            var remove = payload with { Action = "store_remove", Store = store, PublicDer = null };
            var roundTrip = CertificateStoreElevationContract.ParsePayload(CertificateStoreElevationContract.SerializePayload(remove));
            Check(roundTrip.PublicDer is null && roundTrip.Store == store, "remove carries a pin and no DER");
            Refused(() => CertificateStoreElevationContract.ValidatePayload(remove with { PublicDer = [] }));
            if (store is "root" or "trusted_publisher" or "trusted_people")
                CertificateStoreElevationContract.ValidatePayload(payload with { Action = "store_trust", Store = store });
        }
        var maximum = new byte[65536];
        var maximumPayload = payload with { PublicDer = maximum, CertificateId = Convert.ToHexStringLower(SHA256.HashData(maximum)) };
        Check(CertificateStoreElevationContract.ParsePayload(CertificateStoreElevationContract.SerializePayload(maximumPayload)).PublicDer!.Length == 65536,
            "maximum public material fits the bounded internal payload frame");
        var json = CertificateStoreElevationContract.SerializePayload(payload);
        foreach (var invalid in new[]
        {
            json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            json[..^1] + ",\"scope\":\"local_machine\"}", json[..^1] + ",\"executable\":\"anything\"}",
            json[..^1] + ",\"passwordReference\":\"anything\"}", json[..^1] + ",\"root\":\"anything\"}",
            json.Replace("\"publicDer\":\"MAA=\"", "\"publicDer\":\"MAB=\"", StringComparison.Ordinal),
            json.Replace("\"version\":1", "\"version\":1.0", StringComparison.Ordinal), "[]", "null", json + "{}",
            json + new string(' ', CertificateStoreElevationContract.MaximumPayloadBytes)
        }) Refused(() => CertificateStoreElevationContract.ParsePayload(invalid));
        var noDer = JsonNode.Parse(json)!.AsObject(); noDer.Remove("publicDer");
        Refused(() => CertificateStoreElevationContract.ParsePayload(noDer.ToJsonString()));
        Check(PublicDerFixture.SequenceEqual(new byte[] { 0x30, 0 }), "validation never mutates caller-owned public bytes");
    }

    private static void ControlEnvelope()
    {
        var redeem = Envelope(CertificateStoreElevationContract.RedeemOperation);
        Check(CertificateStoreElevationContract.IsControlRequest(redeem) && CertificateStoreElevationContract.ParseTicket(redeem) == Ticket,
            "one exact control ticket without an effect payload");
        Check(CertificateStoreElevationContract.ParseTicket(redeem with { Rid = "correlation:only-1" }) == Ticket,
            "bounded optional RID is correlation, not authority");
        foreach (var invalid in new[]
        {
            redeem with { Op = "certificate" }, redeem with { Body = "" }, redeem with { Root = "" },
            redeem with { Ws = "" }, redeem with { Cwd = "" }, redeem with { PathRoots = new Dictionary<string, string>() },
            redeem with { Quiet = true }, redeem with { Detail = true }, redeem with { Rid = "bad\nrid" },
            redeem with { Arg = new Dictionary<string, string>() },
            With(redeem, "ticket", new string('A', 64)), With(redeem, "ticket", new string('c', 63)),
            With(redeem, "op", "redeem"), With(redeem, "env0", "anything"), With(redeem, "expectedCertificateId", Pin),
            With(redeem, "rid", "anything"), With(redeem, OperationRequest.BodyOmittedKey, "1")
        }) Refused(() => CertificateStoreElevationContract.ParseTicket(invalid));
        var completion = Envelope(CertificateStoreElevationContract.CompleteOperation);
        Refused(() => CertificateStoreElevationContract.ParseTicket(completion));
        Refused(() => CertificateStoreElevationContract.ParseTicket(completion with
            { Body = new string(' ', CertificateStoreElevationContract.MaximumCompletionBytes + 1) }));
        Check(!CertificateStoreElevationContract.IsControlRequest(redeem with { Op = "__certificate_store_shell" }),
            "reserved names do not confer a generic control route");
    }

    private static void Completion()
    {
        var success = new CertificateElevationResult(1, "added", Pin, true, true, false);
        foreach (var value in new[]
        {
            success, success with { State = "removed" }, success with { State = "unchanged", ActionIssued = false },
            success with { State = "absent", ActionIssued = false },
            new(1, "failed", Pin, false, false, false, "elevation_cancelled"),
            new(1, "failed", Pin, true, true, false, "native_error_postcondition_observed", 5),
            new(1, "uncertain", Pin, null, false, true, "helper_outcome_unknown"),
            new(1, "uncertain", Pin, true, false, true, "certificate_store_outcome_unknown", unchecked((int)0x80070005))
        })
        {
            CertificateStoreElevationContract.ValidateResult(value, Pin);
            var request = Envelope(CertificateStoreElevationContract.CompleteOperation,
                CertificateStoreElevationContract.SerializeResult(value));
            Check(CertificateStoreElevationContract.ParseTicket(request) == Ticket &&
                CertificateStoreElevationContract.ParseCompletion(request, Pin) == value, "complete result round-trip with exact expected pin");
            Refused(() => CertificateStoreElevationContract.ParseCompletion(request, new string('a', 64)), "certificateId");
        }
        foreach (var invalid in new[]
        {
            success with { Version = 2 }, success with { State = "done" }, success with { CertificateId = new string('A', 64) },
            success with { ActionIssued = null }, success with { ActionIssued = false }, success with { PostconditionObserved = false },
            success with { OutcomeUnknown = true }, success with { NativeError = 5 },
            success with { State = "unchanged", ActionIssued = true },
            success with { State = "failed", ActionIssued = null }, success with { State = "failed", OutcomeUnknown = true },
            success with { State = "uncertain", OutcomeUnknown = false },
            success with { State = "uncertain", ActionIssued = false, OutcomeUnknown = true },
            success with { Code = "path:or command" }, success with { Code = new string('c', 97) }
        }) Refused(() => CertificateStoreElevationContract.ValidateResult(invalid, Pin));
        Refused(() => CertificateStoreElevationContract.ValidateResult(success, new string('A', 64)), "expectedCertificateId");
        var json = CertificateStoreElevationContract.SerializeResult(success);
        foreach (var invalid in new[]
        {
            json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            json.Replace("\"actionIssued\":true", "\"actionIssued\":\"true\"", StringComparison.Ordinal),
            json.Replace("\"nativeError\":null", "\"nativeError\":1e0", StringComparison.Ordinal),
            json[..^1] + ",\"credentials\":{}}", json[..^1] + ",\"exe\":\"anything\"}",
            json + "{}", "[]", "null", "{}", json[..^1] + ",}",
            "{\"version\":1,\"state\":" + new string('[', 6) + "0" + new string(']', 6) + "}"
        }) Refused(() => CertificateStoreElevationContract.ParseCompletion(Envelope(CertificateStoreElevationContract.CompleteOperation, invalid)));
        var omitted = JsonNode.Parse(json)!.AsObject(); omitted.Remove("actionIssued");
        Refused(() => CertificateStoreElevationContract.ParseCompletion(Envelope(CertificateStoreElevationContract.CompleteOperation, omitted.ToJsonString())));
        var exact = json + new string(' ', CertificateStoreElevationContract.MaximumCompletionBytes - Encoding.UTF8.GetByteCount(json));
        Check(CertificateStoreElevationContract.ParseCompletion(Envelope(CertificateStoreElevationContract.CompleteOperation, exact), Pin) == success,
            "completion byte ceiling accepts an exact bounded document");
        Refused(() => CertificateStoreElevationContract.ParseCompletion(Envelope(CertificateStoreElevationContract.CompleteOperation, exact + " ")));
    }

    private static AdminCertificateStoreRequest Request() => new(1, "store_add", "personal", Pin, PublicDerFixture.ToArray(), new string('a', 64), new string('b', 32), "fixture:request-1");
    private static OperationRequest Envelope(string operation, string? body = null) =>
        new(operation, new Dictionary<string, string>(StringComparer.Ordinal) { ["ticket"] = Ticket }, Body: body);
    private static OperationRequest With(OperationRequest request, string name, string value) => request with
        { Arg = new Dictionary<string, string>(request.Arg, StringComparer.Ordinal) { [name] = value } };
    private static void Refused(Action action, string? field = null)
    {
        try { action(); }
        catch (CertificateElevationContractException failure)
        {
            Check(failure.Message == "certificate_elevation_contract_invalid" && (field is null || failure.Field == field),
                "bounded fixed contract diagnostic");
            return;
        }
        throw new InvalidOperationException("expected closed elevation contract refusal");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
