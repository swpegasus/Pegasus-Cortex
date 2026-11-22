namespace Pegasus.AgentOps.Windows.Registry;

/// <summary>
/// Bounded local registry workflows. This type performs no elevation, identity
/// discovery, logging, continuity storage or retry of a mutation. All methods
/// validate before admission and before the injectable OS boundary is touched.
/// </summary>
public sealed class RegistryOperations(IRegistryNative native, IRegistryAdmission? admission = null)
{
    private readonly IRegistryNative _native = native ?? throw new ArgumentNullException(nameof(native));
    private readonly IRegistryAdmission _admission = admission ?? DenyRegistryAdmission.Instance;

    public RegistryResult<RegistryKeySnapshot> InspectKey(RegistryKeyIdentity key, CancellationToken cancellationToken = default)
    {
        if (!RegistryValidation.Key(key)) return Invalid<RegistryKeySnapshot>();
        return Read(RegistryOperation.InspectKey, key, null, cancellationToken, () => InspectRequired(key));
    }

    public RegistryResult<RegistryValueSnapshot> ReadValue(RegistryValueIdentity identity, CancellationToken cancellationToken = default)
    {
        if (!RegistryValidation.Identity(identity)) return Invalid<RegistryValueSnapshot>();
        return Read<RegistryValueSnapshot>(RegistryOperation.ReadValue, identity.Key, identity.ValueName, cancellationToken, () =>
        {
            var stable = ReadStableValue(identity);
            return new(identity, stable.Key, stable.Value is null ? null : new(stable.Value.Type, Convert.ToBase64String(stable.Value.Data)),
                stable.Value is null ? null : RegistryValidation.ValueDigest(identity, stable.Value));
        });
    }

    public RegistryResult<RegistryBrowsePage> Browse(RegistryBrowseRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !RegistryValidation.Key(request.Key) || !Enum.IsDefined(request.Kind) ||
            request.Limit is < 1 or > RegistryLimits.MaxPageEntries) return Invalid<RegistryBrowsePage>();
        var cursor = request.Cursor;
        var fingerprint = RegistryValidation.KeyFingerprint(request.Key);
        if (cursor is not null && (cursor.Version != RegistryLimits.ContractVersion || cursor.KeyFingerprint != fingerprint ||
            cursor.Kind != request.Kind || !RegistryValidation.Digest(cursor.Generation) ||
            cursor.NextIndex is < 1 or > RegistryLimits.MaxCursorIndex)) return Invalid<RegistryBrowsePage>("cursor_invalid");

        return Read<RegistryBrowsePage>(RegistryOperation.Browse, request.Key, null, cancellationToken, () =>
        {
            var before = InspectRequired(request.Key);
            if (cursor is not null && before.Generation != cursor.Generation) throw Fault(RegistryStatus.Stale, "cursor_stale");
            var index = cursor?.NextIndex ?? 0;
            var count = request.Kind == RegistryBrowseKind.Keys ? before.SubKeyCount : before.ValueCount;
            if (index > count) throw Fault(RegistryStatus.Stale, "cursor_position_stale");
            var entries = new List<RegistryBrowseEntry>(request.Limit);
            var nameChars = 0;
            var hasMore = false;
            while (entries.Count < request.Limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index > RegistryLimits.MaxCursorIndex) throw Fault(RegistryStatus.LimitExceeded, "enumeration_position_limit");
                var entry = ReadName(request, index);
                if (entry is null) break;
                if (nameChars + entry.Name.Length > RegistryLimits.MaxPageNameChars) { hasMore = true; break; }
                entries.Add(entry);
                nameChars += entry.Name.Length;
                index++;
            }
            if (!hasMore && entries.Count == request.Limit)
                hasMore = index > RegistryLimits.MaxCursorIndex ? index < count : ReadName(request, index) is not null;
            var after = InspectRequired(request.Key);
            if (before.Generation != after.Generation) throw Fault(RegistryStatus.Stale, "key_changed_during_browse");
            if (hasMore && index > RegistryLimits.MaxCursorIndex) throw Fault(RegistryStatus.LimitExceeded, "enumeration_position_limit");
            return new(before, request.Kind, entries.AsReadOnly(), hasMore,
                hasMore ? new(RegistryLimits.ContractVersion, fingerprint, request.Kind, before.Generation, index) : null);
        });
    }

    public RegistryResult<RegistryMutationObservation> CreateKey(RegistryCreateKeyRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !MutableKey(request.Key) || !RegistryValidation.Digest(request.ParentGeneration))
            return Invalid<RegistryMutationObservation>();
        var parent = RegistryValidation.Parent(request.Key);
        return Mutate(RegistryOperation.CreateKey, request.Key, null, parent, cancellationToken, invoke =>
        {
            var before = InspectRequired(parent);
            if (before.Generation != request.ParentGeneration) throw Fault(RegistryStatus.PreconditionFailed, "parent_preimage_changed");
            if (_native.InspectKey(request.Key) is not null) throw Fault(RegistryStatus.PreconditionFailed, "key_already_exists");
            if (InspectRequired(parent).Generation != before.Generation) throw Fault(RegistryStatus.PreconditionFailed, "parent_preimage_changed");
            invoke();
            if (!_native.CreateKey(request.Key)) throw Fault(RegistryStatus.Uncertain, "key_create_raced");
            return InspectRequired(request.Key).Generation;
        });
    }

    public RegistryResult<RegistryMutationObservation> SetValue(RegistrySetValueRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !RegistryValidation.Identity(request.Identity) || !MutableKey(request.Identity.Key) ||
            !RegistryValidation.Preimage(request.Preimage) || !RegistryValidation.Data(request.Value, out var bytes))
            return Invalid<RegistryMutationObservation>();
        return Mutate(RegistryOperation.SetValue, request.Identity.Key, request.Identity.ValueName, null, cancellationToken, invoke =>
        {
            GuardValue(request.Identity, request.Preimage);
            invoke();
            _native.SetValue(request.Identity, request.Value.Type, bytes);
            var after = ReadStableValue(request.Identity);
            if (after.Value is null || after.Value.Type != request.Value.Type || !after.Value.Data.AsSpan().SequenceEqual(bytes))
                throw Fault(RegistryStatus.Uncertain, "value_postcondition_not_observed");
            return after.Key.Generation;
        });
    }

    public RegistryResult<RegistryMutationObservation> DeleteValue(RegistryDeleteValueRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !RegistryValidation.Identity(request.Identity) || !MutableKey(request.Identity.Key) ||
            !RegistryValidation.Preimage(request.Preimage) || request.Preimage.ValueDigest is null)
            return Invalid<RegistryMutationObservation>();
        return Mutate(RegistryOperation.DeleteValue, request.Identity.Key, request.Identity.ValueName, null, cancellationToken, invoke =>
        {
            GuardValue(request.Identity, request.Preimage);
            invoke();
            _native.DeleteValue(request.Identity);
            var after = ReadStableValue(request.Identity);
            if (after.Value is not null) throw Fault(RegistryStatus.Uncertain, "value_postcondition_not_observed");
            return after.Key.Generation;
        });
    }

    public RegistryResult<RegistryMutationObservation> DeleteEmptyKey(RegistryDeleteEmptyKeyRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !MutableKey(request.Key) || !RegistryValidation.Digest(request.KeyGeneration))
            return Invalid<RegistryMutationObservation>();
        return Mutate(RegistryOperation.DeleteEmptyKey, request.Key, null, RegistryValidation.Parent(request.Key), cancellationToken, invoke =>
        {
            var before = InspectRequired(request.Key);
            if (before.Generation != request.KeyGeneration) throw Fault(RegistryStatus.PreconditionFailed, "key_preimage_changed");
            if (before.SubKeyCount != 0 || before.ValueCount != 0) throw Fault(RegistryStatus.PreconditionFailed, "key_not_empty");
            invoke();
            _native.DeleteEmptyKey(request.Key);
            if (_native.InspectKey(request.Key) is not null) throw Fault(RegistryStatus.Uncertain, "key_postcondition_not_observed");
            return null;
        });
    }

    private RegistryBrowseEntry? ReadName(RegistryBrowseRequest request, int index)
    {
        var max = request.Kind == RegistryBrowseKind.Keys ? RegistryLimits.MaxKeySegmentChars : RegistryLimits.MaxValueNameChars;
        var entry = _native.ReadName(request.Key, request.Kind, index, max);
        if (entry is not null && (entry.Name is null || entry.Name.Length > max || !RegistryValidation.Text(entry.Name) ||
            (request.Kind == RegistryBrowseKind.Keys && (entry.Name.Length == 0 || entry.Name is "." or ".." ||
                entry.Name.Contains('\\') || entry.Name.Contains('/')))))
            throw Fault(RegistryStatus.LimitExceeded, "enumerated_name_outside_contract");
        return entry;
    }

    private void GuardValue(RegistryValueIdentity identity, RegistryValuePreimage guard)
    {
        var current = ReadStableValue(identity);
        var digest = current.Value is null ? null : RegistryValidation.ValueDigest(identity, current.Value);
        if (current.Key.Generation != guard.KeyGeneration || digest != guard.ValueDigest)
            throw Fault(RegistryStatus.PreconditionFailed, "value_preimage_changed");
    }

    private (RegistryKeySnapshot Key, RegistryNativeValue? Value) ReadStableValue(RegistryValueIdentity identity)
    {
        var before = InspectRequired(identity.Key);
        var value = _native.ReadValue(identity, RegistryLimits.MaxValueBytes);
        if (value is not null && (value.Data is null || value.Data.Length > RegistryLimits.MaxValueBytes || !Enum.IsDefined(value.Type)))
            throw Fault(RegistryStatus.Unsupported, "value_outside_contract");
        var after = InspectRequired(identity.Key);
        if (before.Generation != after.Generation) throw Fault(RegistryStatus.Stale, "key_changed_during_read");
        return (after, value);
    }

    private RegistryKeySnapshot InspectRequired(RegistryKeyIdentity key) => _native.InspectKey(key) is { } info
        ? RegistryValidation.Snapshot(key, info) : throw Fault(RegistryStatus.NotFound, "key_not_found");

    private RegistryResult<T> Read<T>(RegistryOperation operation, RegistryKeyIdentity key, string? name, CancellationToken token, Func<T> read)
    {
        if (!Admitted(operation, key, name, null)) return new(RegistryStatus.Denied, "registry_access_denied");
        try
        {
            token.ThrowIfCancellationRequested();
            var result = read();
            token.ThrowIfCancellationRequested();
            return new(RegistryStatus.Ok, "observed", result);
        }
        catch (RegistryWorkflowException failure) { return new(failure.Status, failure.Message); }
        catch (RegistryNativeException failure) { return new(Status(failure.Failure), failure.Code, NativeError: failure.NativeError); }
        catch (OperationCanceledException) { return new(RegistryStatus.Cancelled, "cancelled_before_result"); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { return new(RegistryStatus.Failed, "registry_boundary_failed"); }
    }

    private RegistryResult<RegistryMutationObservation> Mutate(RegistryOperation operation, RegistryKeyIdentity key, string? name,
        RegistryKeyIdentity? parent, CancellationToken token, Func<Action, string?> write)
    {
        if (!Admitted(operation, key, name, parent)) return new(RegistryStatus.Denied, "registry_access_denied");
        var invoked = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var generation = write(() => { token.ThrowIfCancellationRequested(); invoked = true; });
            // Cancellation after the native call cannot establish that it did not
            // execute. Keep the consumed attempt uncertain instead of replaying.
            token.ThrowIfCancellationRequested();
            return new(RegistryStatus.Ok, "postcondition_observed_non_atomic", new(operation, true, true, generation));
        }
        catch (RegistryWorkflowException failure)
        { return Failure(invoked ? RegistryStatus.Uncertain : failure.Status, failure.Message); }
        catch (RegistryNativeException failure)
        { return Failure(invoked ? RegistryStatus.Uncertain : Status(failure.Failure), failure.Code, failure.NativeError); }
        catch (OperationCanceledException)
        { return Failure(invoked ? RegistryStatus.Uncertain : RegistryStatus.Cancelled, invoked ? "cancelled_after_write_invocation" : "cancelled_before_write"); }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        { return Failure(invoked ? RegistryStatus.Uncertain : RegistryStatus.Failed, "registry_boundary_failed"); }

        RegistryResult<RegistryMutationObservation> Failure(RegistryStatus status, string code, int? nativeError = null) =>
            new(status, code, new(operation, invoked, false, null), nativeError);
    }

    private bool Admitted(RegistryOperation operation, RegistryKeyIdentity key, string? name, RegistryKeyIdentity? parent)
    {
        try { return _admission.IsAllowed(new(RegistryLimits.ContractVersion, operation, key, name, parent)); }
        catch { return false; } // Policy errors must never result in an OS call.
    }

    private static bool MutableKey(RegistryKeyIdentity? key) => RegistryValidation.Key(key) && key!.KeyPath.Length != 0;
    private static RegistryResult<T> Invalid<T>(string code = "registry_request_invalid") => new(RegistryStatus.InvalidRequest, code);
    private static RegistryWorkflowException Fault(RegistryStatus status, string code) => new(status, code);
    private sealed class RegistryWorkflowException(RegistryStatus status, string code) : Exception(code)
    { internal RegistryStatus Status { get; } = status; }

    private static RegistryStatus Status(RegistryNativeFailure failure) => failure switch
    {
        RegistryNativeFailure.AccessDenied => RegistryStatus.Denied,
        RegistryNativeFailure.NotFound => RegistryStatus.NotFound,
        RegistryNativeFailure.LimitExceeded => RegistryStatus.LimitExceeded,
        RegistryNativeFailure.Unsupported => RegistryStatus.Unsupported,
        RegistryNativeFailure.Conflict => RegistryStatus.PreconditionFailed,
        _ => RegistryStatus.Failed
    };
}
