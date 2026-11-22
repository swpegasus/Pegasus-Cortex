using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pegasus.AgentOps.Certificates;
using Pegasus.AgentOps.Protocol;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Engine;

public sealed partial class AgentOpsEngine
{
    private CertificateOwnerBinding? _certificateOwner;

    internal void BindCertificateOwner(CertificateOwnerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_directOnly || _userHostAdmission is null || !binding.IsCurrentOwner() ||
            Interlocked.CompareExchange(ref _certificateOwner, binding, null) is not null)
            throw new AgentOpsException("auth");
    }

    // Only the engine's original-envelope admission may supply these generated
    // fields. The public parser never has a relaxed or recovery execution mode.
    private static CertificateInvocation CertificateTrustedInvocation(OperationRequest request) =>
        CertificateOperationContract.Parse(request with
        {
            PathRoots = null,
            Arg = request.Arg.Where(pair => pair.Key != OperationRequest.BodyOmittedKey)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });

    private static void RequireCertificateFileRoot(OperationRequest request)
    {
        if (request.Op != "certificate") return;
        foreach (var field in CertificateOperationContract.PathFields(request.Arg.GetValueOrDefault("op")))
            if (request.PathRoots is null || !request.PathRoots.ContainsKey(field)) throw new AgentOpsException("precondition");
    }

    private OperationReceipt ExecuteCertificateReceipt(OperationRequest request, string jid, string rid,
        long sequence, CancellationToken token, bool persist)
    {
        OperationReceipt receipt;
        if (request.Body is null && request.Arg.ContainsKey(OperationRequest.BodyOmittedKey))
            receipt = CertificateReceiptAdapter.LostBody(request, jid, rid);
        else
        {
            try
            {
                RequireCertificateFileRoot(request);
                var invocation = CertificateTrustedInvocation(request);
                string? root = null;
                if (CertificateOperationContract.IsFileAction(invocation.Action))
                {
                    // Revalidate the registered workspace and exact frozen path;
                    // an absolute filename alone never supplies a signing root.
                    var field = CertificateOperationContract.PathFields(invocation.Action).Single();
                    _ = ResolvePath(request, field);
                    root = request.PathRoots![field];
                }
                receipt = CertificateReceiptAdapter.Execute(invocation, _certificateOwner, root, jid, rid, token);
            }
            catch (CertificateContractException failure)
            { receipt = CertificateReceiptAdapter.Invalid(request, jid, rid, failure.Field); }
            catch (AgentOpsException failure)
            { receipt = CertificateReceiptAdapter.Failed(request, jid, rid, failure.Code); }
        }
        receipt = receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>()) { ["seq"] = sequence } };
        return Finish(receipt, request, sequence, persist);
    }

    private static bool HasPrivateReceipt(OperationReceipt receipt) =>
        receipt.Op.StartsWith("registry_", StringComparison.Ordinal) || receipt.Op.StartsWith("certificate_", StringComparison.Ordinal) ||
        receipt.Op.StartsWith("connection_profile_", StringComparison.Ordinal);
    private static bool RequiresPrivateReplayMatch(OperationRequest request, OperationReceipt receipt) =>
        request.Op is "registry" or "certificate" or "connection_profile" || HasPrivateReceipt(receipt);
    private static bool PrivateReplayMatches(OperationRequest request, OperationReceipt receipt) => request.Op switch
    {
        "registry" => RegistryReplayMatches(request, receipt),
        "certificate" => CertificateReplayMatches(request, receipt),
        "connection_profile" => ConnectionProfileReplayMatches(request, receipt),
        _ => false
    };
    private static OperationReceipt PrivateInitialReceipt(OperationReceipt receipt, OperationRequest request)
    {
        receipt = RegistryInitialReceipt(receipt, request);
        if (request.Op == "connection_profile")
            return receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>())
                { ["requestIdentity"] = ConnectionInvocation(request).RequestIdentity } };
        if (request.Op != "certificate") return receipt;
        return receipt with { Data = new Dictionary<string, object?>(receipt.Data ?? new Dictionary<string, object?>())
            { ["requestIdentity"] = CertificateTrustedInvocation(request).RequestIdentity } };
    }
    private static OperationRequest PrivateDurableRequest(OperationRequest request) => request.Op is "certificate" or "connection_profile"
        ? new(request.Op, new Dictionary<string, string>(StringComparer.Ordinal)
            { ["op"] = request.Arg["op"], [OperationRequest.BodyOmittedKey] = "1" }, request.Rid)
        : request with { Body = null };
    private OperationReceipt PrivateDurableReceiptForStore(OperationReceipt receipt, OperationRequest? request) =>
        ConnectionProfileDurableReceipt(CertificateDurableReceiptForStore(RegistryDurableReceiptForStore(receipt, request), request), request);

    private static bool CertificateReplayMatches(OperationRequest request, OperationReceipt receipt)
    {
        if (request.Op != "certificate") return false;
        try
        {
            var invocation = CertificateTrustedInvocation(request);
            return receipt.Op == CertificateOperationContract.PublicName(invocation.Action) &&
                ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity")) == invocation.RequestIdentity;
        }
        catch (CertificateContractException) { return false; }
    }

    private OperationReceipt CertificateDurableReceiptForStore(OperationReceipt receipt, OperationRequest? request)
    {
        if (request is not null && request.Op != "certificate" ||
            request is null && !receipt.Op.StartsWith("certificate_", StringComparison.Ordinal) || IsHistoryPruned(receipt)) return receipt;
        var action = request?.Arg.GetValueOrDefault("op") ?? receipt.Op["certificate_".Length..];
        if (!CertificateOperationContract.IsKnownAction(action)) return receipt;
        if (receipt.State == "recovery_required") receipt = receipt with { Partial = true };
        var data = new Dictionary<string, object?>();
        foreach (var key in new[] { "seq", "certificate_issued", "published", "private_export_issued", "key_persistence_issued", "key_creation_issued", "key_finalized", "key_security_observed", "store_action_issued", "cleanup_issued", "cleanup_observed", "postcondition_observed", "outcome_unknown", "windows_status", "administrator_payload_issued", "administrator_exit" })
            if (RegistryMetadataScalar(receipt.Data?.GetValueOrDefault(key)) is { } value) data[key] = value;
        PreserveHelperGeneration(data, receipt, receipt.State == "recovery_required" ? _store.FindReceipt(receipt.Jid) : null,
            "administrator_pid", "administrator_start");
        if (receipt.State == "recovery_required") data["outcome_unknown"] = true;
        foreach (var key in new[] { "reference", "issuerReference", "certificateId", "issuerCertificateId", "sha_before", "sha_after", "store_scope", "store_kind", "certificate_status", "certificate_code" })
            if (ScalarString(receipt.Data?.GetValueOrDefault(key)) is { } value && RegistryReceiptAdapter.IsSafeCode(value) && value.Length <= 96)
                data[key] = value;
        var identity = ScalarString(receipt.Data?.GetValueOrDefault("requestIdentity"));
        if (!ValidRegistryIdentity(identity) && request?.Body is not null)
        {
            try { identity = CertificateTrustedInvocation(request).RequestIdentity; }
            catch (CertificateContractException) { identity = null; }
        }
        if (!ValidRegistryIdentity(identity))
        {
            var previous = _store.Find(receipt.Jid)?.Receipt;
            if (previous is not null && previous.Jid == receipt.Jid && previous.Rid == receipt.Rid && previous.Op == receipt.Op)
            {
                if (IsHistoryPruned(previous)) return previous;
                identity = ScalarString(previous.Data?.GetValueOrDefault("requestIdentity"));
            }
        }
        if (ValidRegistryIdentity(identity)) data["requestIdentity"] = identity;
        data["log_available"] = false;
        // References/outcomes are durable. Subjects, public exports, request
        // bodies, file paths, key containers and native diagnostics are not.
        return receipt with { Data = data, Detail = null, Failure = CertificateReceiptAdapter.DurableFailure(receipt) };
    }
}

internal sealed class CertificateOwnerBinding
{
    private readonly OwnerHostIdentity _identity;
    private readonly Func<IOwnerCertificateRepository> _repository;
    private readonly Func<string, IOwnerCertificateRepository, IAuthenticodeFileAdapter> _authenticode;
    private readonly Func<ICertificateStoreAdmission, CertificateStoreOperations>? _stores;
    private readonly Func<ICertificateSecretResolver>? _secrets;
    private readonly Func<string, IOwnerCertificateRepository, ICertificateSecretResolver, IOwnerCertificateFileExchange>? _exchange;
    private readonly Func<Action, ICertificateServerBinding>? _serverBinding;
    internal CertificateOperations Operations { get; }
    internal TimeProvider Clock { get; }

    internal CertificateOwnerBinding(string ownerSid, Func<LocalPeer> current,
        Func<IOwnerCertificateRepository> repository,
        Func<string, IOwnerCertificateRepository, IAuthenticodeFileAdapter> authenticode, TimeProvider? clock = null,
        Func<ICertificateStoreAdmission, CertificateStoreOperations>? stores = null,
        Func<ICertificateSecretResolver>? secrets = null,
        Func<string, IOwnerCertificateRepository, ICertificateSecretResolver, IOwnerCertificateFileExchange>? exchange = null,
        Func<Action, ICertificateServerBinding>? serverBinding = null)
    {
        _identity = new OwnerHostIdentity(ownerSid, current);
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _authenticode = authenticode ?? throw new ArgumentNullException(nameof(authenticode));
        _stores = stores; _secrets = secrets; _exchange = exchange; _serverBinding = serverBinding;
        Clock = clock ?? TimeProvider.System;
        Operations = new(Clock);
    }

    [SupportedOSPlatform("windows")]
    internal static CertificateOwnerBinding ForOwner(CortexProductLayout layout) => new(layout.OwnerSid, LocalIdentity.Current,
        () => new OwnerCertificateRepository(Path.Combine(layout.UserExecutionDirectory, "certificates"), layout.OwnerSid),
        (root, repository) => new WindowsAuthenticodeFileAdapter(layout.OwnerSid, root, repository),
        stores: admission => new(new WindowsCertificateStoreBackend(), admission),
        secrets: () => new OwnerCertificateSecrets(layout.OwnerSid),
        exchange: (root, repository, secrets) => new OwnerCertificateFileExchange(root, layout.OwnerSid, repository, secrets),
        serverBinding: requireOwner => new WindowsCertificateServerBinding(requireOwner));

    internal bool IsCurrentOwner() => _identity.IsCurrentOwner();
    internal IOwnerCertificateRepository Repository()
    {
        if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        return _repository() ?? throw new AgentOpsException("certificate_unavailable");
    }
    internal IAuthenticodeFileAdapter Authenticode(string root, IOwnerCertificateRepository repository)
    {
        if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        return _authenticode(root, repository) ?? throw new AgentOpsException("certificate_unavailable");
    }
    internal void RequireStoreTarget(CertificateStoreTarget target, CertificateStoreAction action)
    {
        if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        if (!Enum.IsDefined(target.Scope) || !Enum.IsDefined(target.Store) || !Enum.IsDefined(action))
            throw new CertificateOperationException("store_target_invalid");
        if (target.Scope == CertificateStoreScope.LocalMachine && action != CertificateStoreAction.Inventory)
            throw new CertificateOperationException("certificate_machine_administration_required");
    }
    internal CertificateStoreOperations Stores()
    {
        if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        return _stores?.Invoke(new StoreAdmission(this)) ?? throw new AgentOpsException("certificate_unavailable");
    }
    internal ICertificateServerBinding ServerBinding()
    {
        if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        return _serverBinding?.Invoke(() =>
        {
            if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        }) ?? throw new AgentOpsException("certificate_unavailable");
    }
    internal IOwnerCertificateFileExchange Exchange(string root, IOwnerCertificateRepository repository)
    {
        if (!IsCurrentOwner()) throw new AgentOpsException("auth");
        var secrets = _secrets?.Invoke() ?? throw new AgentOpsException("certificate_secret_provider_unavailable");
        return _exchange?.Invoke(root, repository, secrets) ?? throw new AgentOpsException("certificate_unavailable");
    }
    private sealed class StoreAdmission(CertificateOwnerBinding owner) : ICertificateStoreAdmission
    { public void Authorize(CertificateStoreTarget target, CertificateStoreAction action) => owner.RequireStoreTarget(target, action); }
}

internal static class CertificateReceiptAdapter
{
    internal static OperationReceipt Execute(CertificateInvocation invocation, CertificateOwnerBinding? owner,
        string? root, string jid, string rid, CancellationToken token)
    {
        var op = CertificateOperationContract.PublicName(invocation.Action);
        var data = new Dictionary<string, object?> { ["requestIdentity"] = invocation.RequestIdentity, ["log_available"] = false };
        if (invocation.Reference is not null) data["reference"] = invocation.Reference;
        if (invocation.CertificateId is not null) data["certificateId"] = invocation.CertificateId;
        if (invocation.Issuer is { } issuer)
        { data["issuerReference"] = issuer.Reference; data["issuerCertificateId"] = issuer.CertificateId; }
        if (invocation.Sha is not null) data["sha_before"] = invocation.Sha;
        try
        {
            if (owner is null || !owner.IsCurrentOwner()) throw new AgentOpsException("auth");
            token.ThrowIfCancellationRequested();
            CertificateStoreTarget? storeTarget = null;
            if (invocation.Store is { } store)
            {
                storeTarget = StoreTarget(store);
                // Reject machine writes before any store/repository/key/secret
                // factory. This route never substitutes an administrator token.
                owner.RequireStoreTarget(storeTarget, StoreAction(invocation.Action));
                data["store_scope"] = store.Scope; data["store_kind"] = store.Store;
            }
            IOwnerCertificateRepository? repository = null;
            IOwnerCertificateRepository Repository() => repository ??= owner.Repository();
            switch (invocation.Action)
            {
                case "create_ca":
                case "issue_code":
                case "issue_server":
                {
                    var now = owner.Clock.GetUtcNow();
                    var start = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero).AddMinutes(-1);
                    using var authority = invocation.Issuer is { } issuing
                        ? Repository().Resolve(new(issuing.Reference, issuing.CertificateId)) : null;
                    if (authority is not null && start < new DateTimeOffset(authority.NotBefore.ToUniversalTime()))
                        start = new DateTimeOffset(authority.NotBefore.ToUniversalTime());
                    var specification = new CertificateSpec(invocation.Subject!, start, start.AddDays(invocation.Days!.Value), invocation.KeyBits!.Value);
                    if (invocation.Action != "create_ca" && authority is null) throw new CertificateOperationException("issuer_private_key_required");
                    using var certificate = authority is null ? owner.Operations.CreateAuthority(specification) :
                        invocation.Action == "issue_server" ? owner.Operations.IssueServer(specification, authority, invocation.ServerNames!) :
                        owner.Operations.IssueCodeSigning(specification, authority);
                    token.ThrowIfCancellationRequested();
                    if (!owner.IsCurrentOwner()) throw new AgentOpsException("auth");
                    var entry = Repository().Publish(invocation.Reference!, certificate, invocation.RequestIdentity, token);
                    // Publication is the durable effect. No late cancellation or
                    // lost acknowledgement can convert it into a fresh attempt.
                    data["published"] = true; data["certificate_issued"] = true; data["postcondition_observed"] = true;
                    data["outcome_unknown"] = false; data["certificateId"] = entry.CertificateId;
                    data["certificate"] = PublicSummary(entry);
                    break;
                }
                case "inspect":
                {
                    var entry = Repository().Inspect(invocation.Reference!, invocation.CertificateId);
                    data["certificateId"] = entry.CertificateId;
                    data["certificate"] = PublicSummary(entry);
                    break;
                }
                case "export_public":
                {
                    var entry = Repository().Inspect(invocation.Reference!, invocation.CertificateId);
                    using var certificate = CertificateExchange.ImportPublic(entry.PublicDer, PublicCertificateFormat.Der);
                    var format = invocation.Format == "pem" ? PublicCertificateFormat.Pem : PublicCertificateFormat.Der;
                    var exported = CertificateOperations.ExportPublic(certificate, format);
                    data["certificate"] = new { certificateId = entry.CertificateId, format = invocation.Format, bytes = exported.Length,
                        content = format == PublicCertificateFormat.Pem ? Encoding.ASCII.GetString(exported) : Convert.ToBase64String(exported) };
                    break;
                }
                case "sign":
                {
                    var adapter = owner.Authenticode(root ?? throw new AgentOpsException("precondition"), Repository());
                    var request = new AuthenticodeSignRequest(invocation.Src!, invocation.Sha!, new(invocation.Reference!, invocation.CertificateId!));
                    var result = OperatingSystem.IsWindows() && adapter is WindowsAuthenticodeFileAdapter windows
                        ? windows.Sign(request, token) : adapter.Sign(request);
                    data["certificate"] = result; data["sha_after"] = result.ContentSha256;
                    data["certificate_issued"] = true; data["postcondition_observed"] = true; data["outcome_unknown"] = false;
                    break;
                }
                case "verify":
                {
                    var adapter = owner.Authenticode(root ?? throw new AgentOpsException("precondition"), Repository());
                    var request = new AuthenticodeVerifyRequest(invocation.Src!, invocation.Sha!, invocation.CertificateId!);
                    // Verification is a completed observation even for invalid or
                    // untrusted signatures; each result dimension stays explicit.
                    data["certificate"] = OperatingSystem.IsWindows() && adapter is WindowsAuthenticodeFileAdapter windows
                        ? windows.Verify(request, token) : adapter.Verify(request);
                    break;
                }
                case "import_public":
                {
                    var bytes = invocation.Format == "pem" ? Encoding.UTF8.GetBytes(invocation.Content!) : Convert.FromBase64String(invocation.Content!);
                    try
                    {
                        using var certificate = CertificateExchange.ImportPublic(bytes,
                            invocation.Format == "pem" ? PublicCertificateFormat.Pem : PublicCertificateFormat.Der);
                        if (CertificateOperations.Inspect(certificate).CertificateId != invocation.CertificateId)
                            throw new CertificateOperationException("certificate_identity_mismatch");
                        token.ThrowIfCancellationRequested();
                        var entry = Repository().Publish(invocation.Reference!, certificate, invocation.RequestIdentity, token);
                        data["published"] = true; data["certificate_issued"] = true; data["postcondition_observed"] = true;
                        data["outcome_unknown"] = false; data["certificate"] = PublicSummary(entry);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    break;
                }
                case "import_private":
                {
                    var exchange = owner.Exchange(root ?? throw new AgentOpsException("precondition"), Repository());
                    using var certificate = exchange.Import(invocation.Src!, invocation.Sha!, invocation.PasswordReference!, invocation.CertificateId!, token);
                    if (CertificateOperations.Inspect(certificate).CertificateId != invocation.CertificateId)
                        throw new CertificateOperationException("certificate_identity_mismatch");
                    token.ThrowIfCancellationRequested();
                    var entry = Repository().Publish(invocation.Reference!, certificate, invocation.RequestIdentity, token);
                    data["published"] = true; data["certificate_issued"] = true; data["postcondition_observed"] = true;
                    data["outcome_unknown"] = false; data["certificate"] = PublicSummary(entry);
                    break;
                }
                case "export_private":
                {
                    var exchange = owner.Exchange(root ?? throw new AgentOpsException("precondition"), Repository());
                    var result = exchange.Export(invocation.Dst!, new(invocation.Reference!, invocation.CertificateId!), invocation.PasswordReference!, token);
                    data["private_export_issued"] = true; data["published"] = true; data["postcondition_observed"] = true;
                    if (result.CertificateId != invocation.CertificateId ||
                        !string.Equals(result.Destination, invocation.Dst, StringComparison.OrdinalIgnoreCase) ||
                        result.Bytes is < 1 or > 1024 * 1024 || !ValidDigest(result.Sha256))
                        throw new OwnerCertificateFileExchangeException("certificate_export_identity_unknown", true, true, false);
                    data["outcome_unknown"] = false; data["sha_after"] = result.Sha256; data["certificate"] = result;
                    break;
                }
                case "store_bind_server":
                {
                    data["store_scope"] = "current_user"; data["store_kind"] = "personal";
                    var binding = owner.ServerBinding();
                    using var certificate = Repository().Resolve(new(invocation.Reference!, invocation.CertificateId!));
                    _ = ServerCertificateMaterial.Require(certificate, invocation.CertificateId!, owner.Clock.GetUtcNow(), privateKey: true);
                    token.ThrowIfCancellationRequested();
                    ServerCertificateBindingResult result;
                    try { result = binding.BindServer(certificate, invocation.CertificateId!, owner.Clock.GetUtcNow(), token); }
                    catch (Exception failure) when (failure is not (ServerCertificateBindingException or OutOfMemoryException or StackOverflowException))
                    { throw new ServerCertificateBindingException("server_binding_outcome_unknown", false, false, true, failure); }
                    data["key_persistence_issued"] = result.KeyPersistenceIssued;
                    data["store_action_issued"] = result.StoreActionIssued;
                    var issued = result.KeyPersistenceIssued || result.StoreActionIssued;
                    if (result.CertificateId != invocation.CertificateId || !result.PostconditionObserved || result.OutcomeUnknown ||
                        result.State is not ("bound" or "unchanged") ||
                        result.State == "unchanged" && issued ||
                        result.State == "bound" && (!result.KeyPersistenceIssued || !result.StoreActionIssued))
                        return Failure("server_binding_result_unconfirmed", issued, true);
                    data["binding"] = new { location = "CurrentUser", store = "My", certificateId = result.CertificateId };
                    data["postcondition_observed"] = true; data["outcome_unknown"] = false;
                    break;
                }
                case "store_list":
                    data["certificate"] = owner.Stores().Inventory(storeTarget!, invocation.Max!.Value, invocation.AfterCertificateId);
                    break;
                case "store_add":
                case "store_trust":
                case "store_remove":
                {
                    var operations = owner.Stores();
                    CertificateStoreMutation result;
                    if (invocation.Action == "store_remove") result = operations.RemovePublic(storeTarget!, invocation.CertificateId!);
                    else
                    {
                        var entry = Repository().Inspect(invocation.Reference!, invocation.CertificateId);
                        result = invocation.Action == "store_trust" ? operations.InstallTrust(storeTarget!, entry.PublicDer)
                            : operations.AddPublic(storeTarget!, entry.PublicDer);
                    }
                    data["store_action_issued"] = result.ActionIssued;
                    if (result.CertificateId != invocation.CertificateId)
                    {
                        data["outcome_unknown"] = result.ActionIssued;
                        return Failure("certificate_store_identity_changed", result.ActionIssued, result.ActionIssued);
                    }
                    data["certificate"] = result; data["postcondition_observed"] = true; data["outcome_unknown"] = false;
                    break;
                }
                default: throw new AgentOpsException("invalid");
            }
            data["certificate_status"] = "completed";
            return new(true, jid, rid, "done", op, Data: data);
        }
        catch (AuthenticodeAttemptException failure)
        {
            data["certificate_issued"] = failure.Issued; data["postcondition_observed"] = failure.PostconditionObserved;
            data["outcome_unknown"] = failure.OutcomeUnknown;
            if (failure.WindowsStatus is { } status) data["windows_status"] = status;
            return Failure(failure.Code, failure.Issued, failure.OutcomeUnknown);
        }
        catch (ServerCertificateBindingException failure)
        {
            data["key_persistence_issued"] = failure.KeyPersistenceIssued;
            data["store_action_issued"] = failure.StoreActionIssued;
            data["postcondition_observed"] = false; data["outcome_unknown"] = failure.OutcomeUnknown;
            return Failure(failure.Code, failure.KeyPersistenceIssued || failure.StoreActionIssued, failure.OutcomeUnknown);
        }
        catch (OwnerCertificateRepositoryException failure)
        {
            data["published"] = failure.Published; data["postcondition_observed"] = failure.PostconditionObserved;
            data["outcome_unknown"] = failure.OutcomeUnknown;
            return Failure(failure.Code, failure.Published, failure.OutcomeUnknown);
        }
        catch (OwnerCertificateFileExchangeException failure)
        {
            data["published"] = failure.Published; data["private_export_issued"] = failure.Published || failure.OutcomeUnknown;
            data["postcondition_observed"] = failure.PostconditionObserved; data["outcome_unknown"] = failure.OutcomeUnknown;
            return Failure(failure.Code, failure.Published || failure.OutcomeUnknown, failure.OutcomeUnknown);
        }
        catch (CertificateOperationException failure)
        {
            if (failure.Code == "certificate_store_outcome_unknown")
            {
                data["store_action_issued"] = true; data["outcome_unknown"] = true;
                return Failure(failure.Code, true, true);
            }
            return Failure(failure.Code, EffectObserved(), false);
        }
        catch (AgentOpsException failure) { return Failure(failure.Code, EffectObserved(), false); }
        catch (OperationCanceledException) { return Failure("canceled", EffectObserved(), false); }
        catch (UnauthorizedAccessException) { return Failure("access", EffectObserved(), false); }
        catch (Exception failure) when (failure is IOException or CryptographicException or ArgumentException)
        { return Failure("certificate_failed", EffectObserved(), false); }

        bool EffectObserved() => data.GetValueOrDefault("published") is true || data.GetValueOrDefault("certificate_issued") is true ||
            data.GetValueOrDefault("private_export_issued") is true || data.GetValueOrDefault("store_action_issued") is true;

        OperationReceipt Failure(string cause, bool issued, bool unknown)
        {
            var code = cause is "auth" or "access" or "precondition" or "invalid" or "not_found" or "canceled" ? cause :
                cause == "certificate_machine_administration_required" ? "precondition" :
                cause.Contains("canceled_before", StringComparison.Ordinal) ? "canceled" : "certificate_failed";
            if (unknown) code = "recovery_required";
            data["certificate_code"] = RegistryReceiptAdapter.IsSafeCode(cause) ? cause : "certificate_failed";
            data["certificate_status"] = unknown ? "uncertain" : issued ? "failed_after_issue" : "failed";
            return new(false, jid, rid, unknown ? "recovery_required" : issued ? "attention" : code == "canceled" ? "canceled" : "failed",
                op, code, data, Partial: issued || unknown, Failure: Evidence(code, issued, unknown));
        }
    }

    private static CertificateStoreTarget StoreTarget(CertificateStoreIdentity store) => new(
        store.Scope switch { "current_user" => CertificateStoreScope.CurrentUser, "local_machine" => CertificateStoreScope.LocalMachine,
            _ => throw new CertificateOperationException("store_target_invalid") },
        store.Store switch
        {
            "personal" => CertificateStoreKind.Personal, "intermediate" => CertificateStoreKind.IntermediateAuthority,
            "root" => CertificateStoreKind.Root, "trusted_publisher" => CertificateStoreKind.TrustedPublisher,
            "trusted_people" => CertificateStoreKind.TrustedPeople, _ => throw new CertificateOperationException("store_target_invalid")
        });
    private static CertificateStoreAction StoreAction(string action) => action switch
    {
        "store_list" => CertificateStoreAction.Inventory, "store_add" => CertificateStoreAction.AddPublic,
        "store_trust" => CertificateStoreAction.InstallTrust, "store_remove" => CertificateStoreAction.RemovePublic,
        _ => throw new CertificateOperationException("store_target_invalid")
    };

    private static object PublicSummary(OwnerCertificateEntry entry)
    {
        using var certificate = CertificateExchange.ImportPublic(entry.PublicDer, PublicCertificateFormat.Der);
        var summary = CertificateOperations.Inspect(certificate);
        return new { summary.CertificateId, entry.Reference, summary.Subject, summary.Issuer, summary.NotBefore, summary.NotAfter,
            summary.IsAuthority, entry.ProtectedMaterialPresent,
            keyAvailability = entry.ProtectedMaterialPresent ? "protected_material_not_opened" : "public_only" };
    }
    private static bool ValidDigest(string value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static OperationReceipt Invalid(OperationRequest request, string jid, string rid, string field) =>
        new(false, jid, rid, "failed", OperationNames.Public(request), "invalid", Failure: Evidence("invalid", false, false, field));
    internal static OperationReceipt Failed(OperationRequest request, string jid, string rid, string code) =>
        new(false, jid, rid, "failed", OperationNames.Public(request), code, Failure: Evidence(code, false, false));
    internal static OperationReceipt LostBody(OperationRequest request, string jid, string rid) =>
        new(false, jid, rid, "recovery_required", OperationNames.Public(request), "recovery_required", Partial: true,
            Failure: Evidence("recovery_required", true, true));
    internal static OperationFailureEvidence? DurableFailure(OperationReceipt receipt) => receipt.Ok ? null :
        Evidence(receipt.Err ?? "certificate_failed", receipt.Failure?.Execution.Value is not OperationExecutionState.NotDispatched,
            receipt.State == "recovery_required" || receipt.Data?.GetValueOrDefault("outcome_unknown") is true ||
            receipt.Data?.GetValueOrDefault("outcome_unknown") is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True });
    internal static OperationFailureEvidence Evidence(string code, bool issued, bool unknown, string? field = null) => new(
        code == "auth" ? "Certificate work requires the verified ordinary signed-in owner host." :
        unknown ? "The original certificate attempt needs reconciliation; its outcome or private request body is unavailable." :
        issued ? "The certificate attempt failed after an effect was issued; inspect its recorded outcome." :
        "The closed certificate operation failed before publication or signing was issued.",
        new(unknown ? OperationExecutionState.Unknown : issued ? OperationExecutionState.Started : OperationExecutionState.NotDispatched, "certificate-adapter"),
        new(issued || unknown ? OperationRetryDisposition.NotSupported : OperationRetryDisposition.AfterCorrection, "certificate-adapter"),
        Field: field is null ? null : new(field), DetailAvailability: "none", LogAvailability: "not_retained",
        Next: "Inspect the original reference or exact file and receipt. Never replay an issued or uncertain attempt; a deliberate new change requires a fresh preimage and RID.");
}
