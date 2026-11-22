using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Mcp.Hosting;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexMcpSetupRequest(CortexMcpSettings Baseline, bool Enabled, CortexMcpOwner Owner,
    string? IssuerReference = null, IReadOnlyList<string>? ReviewedCanceledJobIds = null);
internal sealed record CortexMcpSetupRetry(string Action, string JobId, string RequestId, string RequestIdentity);
internal sealed record CortexMcpSetupResult(bool Ok, string? Error = null, string? JobId = null,
    bool RequiresReview = false, bool TlsQualified = false, CortexMcpSetupRetry? Retry = null);
internal interface ICortexMcpSetupController
{
    Task<CortexMcpSetupResult> SetupAsync(CortexMcpSetupRequest request, CancellationToken token);
}

/// <summary>
/// Composes the existing owner-host certificate operations; owns no store, key,
/// process, listener or settings writer. The production wrapper retains owner
/// admission through the actual callbacks and supplies the fixed final helper.
/// </summary>
internal sealed class CortexMcpSetupWorkflow : ICortexMcpSetupController
{
    internal const int MaximumReviewedCancellations = 8;
    private readonly string _scopeIdentity;
    private readonly Func<CortexMcpSettings> _read;
    private readonly Func<OperationRequest, CancellationToken, Task<OperationReceipt>> _dispatch;
    private readonly Func<CortexMcpSetupRequest, string, string, CancellationToken, Task<CortexMcpSetupResult>> _complete;
    private int _running;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal CortexMcpSetupWorkflow(string scopeIdentity, Func<CortexMcpSettings> read,
        Func<OperationRequest, CancellationToken, Task<OperationReceipt>> dispatch,
        Func<CortexMcpSetupRequest, string, string, CancellationToken, Task<CortexMcpSetupResult>> complete)
    {
        if (!Digest(scopeIdentity)) throw new ArgumentException("A stable owner/install scope hash is required.", nameof(scopeIdentity));
        _scopeIdentity = scopeIdentity;
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
    }

    internal static string Fingerprint(CortexMcpSettings settings) =>
        Convert.ToHexStringLower(SHA256.HashData(CortexMcpSettingsStore.Serialize(settings)));

    public async Task<CortexMcpSetupResult> SetupAsync(CortexMcpSetupRequest request, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new(false, "MCP setup is already in progress.");
        string? lastJob = null;
        var mutationSubmitted = false;
        var finalHelperEntered = false;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            var baseline = CortexMcpSettingsStore.Validate(request.Baseline);
            if (!Enum.IsDefined(request.Owner)) return new(false, "Select an existing MCP owner before setup.");
            if (request.ReviewedCanceledJobIds is { Count: > MaximumReviewedCancellations })
                return new(false, "The reviewed setup steps are invalid. Review the original work.", RequiresReview: true);
            var reviewed = request.ReviewedCanceledJobIds?.ToArray() ?? [];
            if (reviewed.Length > MaximumReviewedCancellations || reviewed.Any(jid => !Hex(jid, 32)) ||
                reviewed.Distinct(StringComparer.Ordinal).Count() != reviewed.Length)
                return new(false, "The reviewed setup steps are invalid. Review the original work.", RequiresReview: true);
            request = request with { Baseline = baseline, ReviewedCanceledJobIds = reviewed };
            var reviewedJobs = reviewed.ToHashSet(StringComparer.Ordinal);
            var followedJobs = new HashSet<string>(StringComparer.Ordinal);
            var fingerprint = Fingerprint(baseline);
            RequireCurrent();
            token.ThrowIfCancellationRequested();
            var serverPin = (request.Owner == CortexMcpOwner.Application
                ? baseline.ApplicationCertificate : baseline.ServiceCertificate)?.CertificateId;
            var caPin = baseline.CertificateAuthority?.CertificateId;

            // Existing complete references need validation/qualification only;
            // public CA trust does not imply that its signing key is available.
            if (serverPin is not null && baseline.CertificateAuthority is { Location: "LocalMachine", Store: "Root" })
                return await CompleteAsync(serverPin, caPin!);
            var issuerReference = string.IsNullOrWhiteSpace(request.IssuerReference) ? null : request.IssuerReference;
            if (issuerReference is null && (caPin is not null || serverPin is not null))
                return new(false, "An authority is already selected or certificate references are incomplete. " +
                    "Supply its matching owner-held issuer reference; setup will not replace the authority.");

            JsonElement authoritySummary;
            if (issuerReference is not null)
            {
                var fields = Fields(("reference", issuerReference));
                if (caPin is not null) fields.Add("certificateId", caPin);
                var inspected = await StepAsync("inspect", fields);
                var observedPin = Pin(inspected);
                if (caPin is not null && observedPin != caPin) throw Stop("The issuer reference does not match the selected authority.");
                authoritySummary = Object(inspected.Data?.GetValueOrDefault("certificate"));
                caPin = observedPin;
            }
            else
            {
                // Never inspect-and-adopt this deterministic default reference.
                // Existing original RID metadata can prove prior completion;
                // without that ledger, create-only publication must refuse it.
                issuerReference = "mcp-ca-v1-" + Hash(_scopeIdentity + "\n" + baseline.ServerName)[..32];
                var created = await StepAsync("create_ca", Fields(("reference", issuerReference),
                    ("subject", "Pegasus Cortex MCP local authority"), ("days", 365), ("keyBits", 3072)));
                RequireTrue(created, "published", "certificate_issued");
                caPin = Pin(created);
                var inspected = await StepAsync("inspect", Fields(("reference", issuerReference), ("certificateId", caPin)));
                RequirePin(inspected, caPin);
                authoritySummary = Object(inspected.Data?.GetValueOrDefault("certificate"));
            }

            RequireAuthority(authoritySummary, issuerReference, caPin, serverPin is null);
            if (serverPin is null)
            {
                var names = Names(baseline.ServerName);
                var role = request.Owner == CortexMcpOwner.Application ? "app" : "svc";
                var leafReference = "mcp-" + role + "-v1-" + Hash(_scopeIdentity + "\n" + baseline.ServerName + "\n" + caPin)[..32];
                var action = request.Owner == CortexMcpOwner.Application ? "issue_server" : "issue_machine_server";
                var issued = await StepAsync(action, Fields(("reference", leafReference),
                    ("subject", "Pegasus Cortex MCP server"), ("days", 30), ("keyBits", 3072),
                    ("issuer", new { reference = issuerReference, certificateId = caPin }),
                    ("dnsNames", names.DnsNames), ("ipAddresses", names.IpAddresses)));
                RequireTrue(issued, "published");
                if (ScalarText(issued.Data?.GetValueOrDefault("issuerReference")) != issuerReference ||
                    ScalarText(issued.Data?.GetValueOrDefault("issuerCertificateId")) != caPin)
                    throw Stop("The issued certificate did not retain the selected issuer identity.");
                serverPin = Pin(issued);
                if (request.Owner == CortexMcpOwner.Service)
                {
                    RequireTrue(issued, "key_creation_issued", "key_finalized", "key_security_observed", "store_action_issued");
                    RequireStore(issued, "local_machine", "personal");
                    if (ScalarText(issued.Data?.GetValueOrDefault("certificate_status")) != "bound")
                        throw Stop("The service certificate binding was not confirmed.");
                }
                else
                {
                    RequireTrue(issued, "certificate_issued");
                    var bound = await StepAsync("store_bind_server", Fields(("reference", leafReference), ("certificateId", serverPin)));
                    RequirePin(bound, serverPin);
                    RequireStore(bound, "current_user", "personal");
                    var keyIssued = ScalarBoolean(bound.Data?.GetValueOrDefault("key_persistence_issued"));
                    if (keyIssued is null || ScalarBoolean(bound.Data?.GetValueOrDefault("store_action_issued")) != keyIssued)
                        throw Stop("The application certificate binding was not confirmed.");
                }
            }
            // A selected existing leaf needs no CA signing key. When a new leaf
            // is needed, finish its issuance/binding before installing trust so
            // an unavailable private issuer does not install an unused authority.
            // Recheck the retained validity interval against the current time;
            // a long issuance/UAC interaction must not outlive its CA admission.
            RequireAuthority(authoritySummary, issuerReference, caPin, signingRequired: false);
            var trusted = await StepAsync("store_admin_trust", Fields(("reference", issuerReference),
                ("certificateId", caPin), ("scope", "local_machine"), ("store", "root")));
            RequirePin(trusted, caPin);
            RequireStore(trusted, "local_machine", "root");
            var trustState = ScalarText(trusted.Data?.GetValueOrDefault("certificate_status"));
            if (trustState is not ("added" or "unchanged") ||
                ScalarBoolean(trusted.Data?.GetValueOrDefault("store_action_issued")) != (trustState == "added"))
                throw Stop("Machine-wide authority trust was not confirmed.");
            return await CompleteAsync(serverPin, caPin);

            void RequireCurrent()
            {
                if (Fingerprint(_read()) != fingerprint)
                    throw Stop("MCP settings changed. Refresh and review the current configuration before continuing.");
            }

            SetupStopped Stop(string error) => new(error);

            async Task<OperationReceipt> StepAsync(string action, Dictionary<string, object?> fields)
            {
                RequireCurrent();
                token.ThrowIfCancellationRequested();
                var body = JsonSerializer.Serialize(fields, Json);
                var provisional = new OperationRequest("certificate", new Dictionary<string, string> { ["op"] = action },
                    "mcp-setup-v1-pending", Body: body);
                var invocation = CertificateOperationContract.Parse(provisional);
                var rid = "mcp-setup-v1-" + Hash(_scopeIdentity + "\n" + invocation.RequestIdentity);
                if (invocation.IsMutation) mutationSubmitted = true;
                // Once submitted, retain the actual original completion. UI
                // cancellation stops the next step, not an already delivered key/store action.
                var observationToken = invocation.IsMutation ? CancellationToken.None : token;
                while (true)
                {
                    RequireCurrent();
                    token.ThrowIfCancellationRequested();
                    var receipt = await _dispatch(provisional with { Rid = rid }, observationToken).ConfigureAwait(false);
                    ValidateIdentity(receipt, rid, invocation.Action, invocation.RequestIdentity, null);
                    lastJob = receipt.Jid;
                    if (receipt.Ok && !receipt.Partial && receipt.Err is null && receipt.Failure is null &&
                        receipt.State is ("queued" or "running"))
                    {
                        var jid = receipt.Jid;
                        receipt = await _dispatch(new("job", new Dictionary<string, string> { ["op"] = "wait", ["jid"] = jid }, rid),
                            observationToken).ConfigureAwait(false);
                        ValidateIdentity(receipt, rid, invocation.Action, invocation.RequestIdentity, jid);
                    }
                    if (KnownNoEffectCancellation(receipt, invocation))
                    {
                        if (!reviewedJobs.Contains(receipt.Jid))
                            throw new SetupStopped("Windows approval was canceled before this certificate step changed anything. " +
                                (reviewed.Length < MaximumReviewedCancellations
                                    ? "Refresh, then explicitly review and retry this step."
                                    : "The setup retry limit was reached. Review the original work manually."),
                                reviewed.Length < MaximumReviewedCancellations
                                    ? new(action, receipt.Jid, rid, invocation.RequestIdentity) : null);
                        if (!followedJobs.Add(receipt.Jid) || followedJobs.Count > MaximumReviewedCancellations)
                            throw Stop("The reviewed setup chain is invalid. Review the original work.");
                        // Only a reviewed, proven no-effect original advances this one step.
                        // Reopening setup reconstructs the same successor and replays any
                        // existing success/unknown result rather than creating a new attempt.
                        rid = "mcp-setup-v1-" + Hash(rid + "\n" + receipt.Jid);
                        continue;
                    }
                    if (!receipt.Ok || receipt.State != "done" || receipt.Partial || receipt.Err is not null || receipt.Failure is not null)
                        throw Stop("Certificate setup stopped at " + action + ". Review the original job before continuing.");
                    if (ScalarText(receipt.Data?.GetValueOrDefault("reference")) != invocation.Reference)
                        throw Stop("Certificate setup received mismatched reference metadata.");
                    if (invocation.IsMutation && (ScalarBoolean(receipt.Data?.GetValueOrDefault("postcondition_observed")) != true ||
                        ScalarBoolean(receipt.Data?.GetValueOrDefault("outcome_unknown")) != false))
                        throw Stop("Certificate setup received an unconfirmed operation outcome.");
                    return receipt;
                }
            }

            async Task<CortexMcpSetupResult> CompleteAsync(string leaf, string ca)
            {
                RequireCurrent();
                token.ThrowIfCancellationRequested();
                if (followedJobs.Count != reviewedJobs.Count)
                    throw Stop("The reviewed canceled jobs no longer match this setup. Review the original work.");
                if (!Digest(leaf) || !Digest(ca)) throw Stop("Selected certificate identities are invalid.");
                finalHelperEntered = true;
                // The fixed helper performs the fresh-settings CAS, role-specific
                // key/chain checks and real ephemeral TLS proof before saving.
                // Its result is qualification as that owner, never SYSTEM live readiness.
                var result = await _complete(request, leaf, ca, CancellationToken.None).ConfigureAwait(false);
                if (result.Ok && (!result.TlsQualified || result.RequiresReview || result.Error is not null))
                    throw Stop("The final setup helper did not confirm TLS qualification and saved settings.");
                return result with { JobId = result.JobId ?? lastJob };
            }
        }
        catch (OperationCanceledException)
        { return new(false, "Setup stopped before its next step. Completed actions were retained; review before continuing.", lastJob,
            mutationSubmitted || finalHelperEntered); }
        catch (SetupStopped error)
        { return new(false, error.Message, lastJob, mutationSubmitted || finalHelperEntered, Retry: error.Retry); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return new(false, "MCP setup could not confirm its current step. Review the original operation before continuing.",
            lastJob, mutationSubmitted || finalHelperEntered); }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static void ValidateIdentity(OperationReceipt receipt, string rid, string action, string requestIdentity, string? jid)
    {
        if (receipt is null || !Hex(receipt.Jid, 32) || receipt.Rid != rid ||
            receipt.Op != CertificateOperationContract.PublicName(action) || jid is not null && receipt.Jid != jid ||
            ScalarText(receipt.Data?.GetValueOrDefault("requestIdentity")) != requestIdentity)
            throw new SetupStopped("Certificate setup received an unverified job identity. Review the original operation.");
    }

    private static bool KnownNoEffectCancellation(OperationReceipt receipt, CertificateInvocation invocation)
    {
        if (receipt.Ok || receipt.State != "canceled" || receipt.Err != "canceled" || receipt.Partial ||
            ScalarBoolean(receipt.Data?.GetValueOrDefault("outcome_unknown")) != false ||
            ScalarBoolean(receipt.Data?.GetValueOrDefault("administrator_payload_issued")) != false ||
            ScalarText(receipt.Data?.GetValueOrDefault("reference")) != invocation.Reference)
            return false;
        foreach (var effect in new[] { "published", "certificate_issued", "key_creation_issued", "key_finalized",
            "key_persistence_issued", "store_action_issued", "postcondition_observed" })
            if (receipt.Data is { } data && data.TryGetValue(effect, out var value) && ScalarBoolean(value) != false) return false;
        return invocation.Action switch
        {
            "issue_machine_server" => ScalarBoolean(receipt.Data?.GetValueOrDefault("published")) == false,
            "store_admin_trust" => ScalarBoolean(receipt.Data?.GetValueOrDefault("store_action_issued")) == false,
            _ => false
        };
    }

    private static Dictionary<string, object?> Fields(params (string Key, object? Value)[] values)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["version"] = 1 };
        foreach (var value in values) fields.Add(value.Key, value.Value);
        return fields;
    }

    private static ServerCertificateNames Names(string serverName)
    {
        var dns = new List<string> { "localhost" };
        var ips = new List<string> { "127.0.0.1", "::1" };
        if (IPAddress.TryParse(serverName, out var address))
        { if (!ips.Contains(address.ToString(), StringComparer.Ordinal)) ips.Add(address.ToString()); }
        else if (!dns.Contains(serverName, StringComparer.OrdinalIgnoreCase)) dns.Add(serverName);
        return new(dns, ips);
    }

    private static void RequireTrue(OperationReceipt receipt, params string[] names)
    {
        if (names.Any(name => ScalarBoolean(receipt.Data?.GetValueOrDefault(name)) != true))
            throw new SetupStopped("Certificate setup received incomplete publication or binding evidence.");
    }
    private static void RequireStore(OperationReceipt receipt, string scope, string store)
    {
        if (ScalarText(receipt.Data?.GetValueOrDefault("store_scope")) != scope ||
            ScalarText(receipt.Data?.GetValueOrDefault("store_kind")) != store)
            throw new SetupStopped("Certificate setup received a mismatched store scope.");
    }
    private static void RequireAuthority(JsonElement summary, string reference, string pin, bool signingRequired)
    {
        var subject = Text(summary, "subject");
        var issuer = Text(summary, "issuer");
        var before = Field(summary, "notBefore");
        var after = Field(summary, "notAfter");
        var now = DateTimeOffset.UtcNow;
        if (Text(summary, "certificateId") != pin || Text(summary, "reference") != reference ||
            Boolean(summary, "isAuthority") != true || string.IsNullOrWhiteSpace(subject) ||
            !string.Equals(subject, issuer, StringComparison.Ordinal) ||
            before.ValueKind != JsonValueKind.String || !before.TryGetDateTimeOffset(out var notBefore) ||
            after.ValueKind != JsonValueKind.String || !after.TryGetDateTimeOffset(out var notAfter) ||
            notBefore > now || notAfter <= now)
            throw new SetupStopped("The selected authority must return matching, currently valid self-issued root metadata before issuance or machine-wide trust.");
        if (signingRequired && Boolean(summary, "protectedMaterialPresent") != true)
            throw new SetupStopped("The selected authority has no owner-held signing material. " +
                "Existing certificates can still be checked; issuing a new leaf needs its private issuer reference.");
    }
    private static string Pin(OperationReceipt receipt)
    {
        var pin = ScalarText(receipt.Data?.GetValueOrDefault("certificateId"));
        return Digest(pin) ? pin! : throw new SetupStopped("Certificate setup did not return an exact public certificate identity.");
    }
    private static void RequirePin(OperationReceipt receipt, string expected)
    { if (Pin(receipt) != expected) throw new SetupStopped("Certificate setup returned a different public certificate identity."); }

    private static JsonElement Object(object? value) => value is null ? default : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement Field(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return default;
        // Direct injected/native objects and their camel-case wire form are the
        // same existing summary contract. Reject duplicate/ambiguous spellings.
        var alternate = char.ToUpperInvariant(name[0]) + name[1..];
        var matches = value.EnumerateObject().Where(p => p.Name == name || p.Name == alternate).ToArray();
        return matches.Length == 1 ? matches[0].Value : default;
    }
    private static string? Text(JsonElement value, string name) => ScalarText(Field(value, name));
    private static bool? Boolean(JsonElement value, string name) => ScalarBoolean(Field(value, name));
    private static string? ScalarText(object? value) => value switch
    { string text => text, JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(), _ => null };
    private static bool? ScalarBoolean(object? value) => value switch
    { bool flag => flag, JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false, _ => null };
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool Digest(string? text) => Hex(text, 64);
    private static bool Hex(string? text, int length) => text is not null && text.Length == length &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed class SetupStopped(string message, CortexMcpSetupRetry? retry = null) : Exception(message)
    { internal CortexMcpSetupRetry? Retry { get; } = retry; }
}
