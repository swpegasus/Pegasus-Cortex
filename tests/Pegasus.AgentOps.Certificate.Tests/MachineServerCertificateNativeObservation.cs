using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Pegasus.AgentOps.Certificates;

/// <summary>
/// Explicit administrator diagnostic, never part of the ordinary fixture suite.
/// It observes one fresh unbound key using the unchanged production native path;
/// it neither evaluates/relaxes policy nor creates a CSR or certificate binding.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MachineServerCertificateNativeObservation
{
    internal const string Selector = "--machine-server-native-observe";
    internal const string FileName = "machine-server-native-observation.json";
    private const int MaximumOutputBytes = 16384, MaximumAces = 32;

    internal static int Run(string expectedOwnerSid, string observationDirectory)
    {
        FileStream output;
        try
        {
            RequireExpectedOwner(expectedOwnerSid);
            output = ReserveOutput(observationDirectory, expectedOwnerSid);
        }
        catch (Exception error) when (Recoverable(error))
        {
            Summary(false, false, false, false, "observation_admission_failed");
            return 2;
        }
        using var reservedOutput = output;

        Observation? held = null, reopened = null;
        string? failureCode = null, failurePhase = null;
        int? nativeStatus = null;
        var phase = "key_creation";
        var creationIssued = false;
        var keyCreated = false;
        var finalized = false;
        var creationUnknown = false;
        var cleanupIssued = false;
        var cleanupObserved = false;
        var handlesDisposed = false;
        var backend = new MachineServerCertificateNative();
        try
        {
            RequireExpectedOwner(expectedOwnerSid);
            backend.Create(Guid.NewGuid(), 3072, () => creationIssued = true);
            keyCreated = backend.KeyOwned;
            phase = "key_finalization";
            RequireExpectedOwner(expectedOwnerSid);
            backend.ConfigureAndFinalize();
            finalized = true;
            phase = "held_observation";
            RequireExpectedOwner(expectedOwnerSid);
            held = Project(backend.ObserveKey(reopen: false));
            phase = "reopened_observation";
            RequireExpectedOwner(expectedOwnerSid);
            reopened = Project(backend.ObserveKey(reopen: true));
        }
        catch (Exception error) when (Recoverable(error))
        {
            failurePhase = phase;
            failureCode = ErrorCode(error);
            nativeStatus = (error as MachineServerCertificateBackendException)?.NativeError;
            creationUnknown = error is MachineServerCertificateBackendException { OutcomeUnknown: true };
        }
        finally
        {
            // Only this exact backend's original owned key is eligible. The
            // native method verifies absence by reopening after successful Delete.
            if (backend.KeyOwned)
            {
                try
                {
                    RequireExpectedOwner(expectedOwnerSid);
                    cleanupIssued = true;
                    backend.DeleteUnboundKey();
                    cleanupObserved = true;
                }
                catch (Exception error) when (Recoverable(error))
                {
                    failurePhase = "key_cleanup";
                    failureCode = ErrorCode(error);
                    nativeStatus = (error as MachineServerCertificateBackendException)?.NativeError;
                }
            }
            try { backend.Dispose(); handlesDisposed = true; }
            catch (Exception error) when (Recoverable(error))
            {
                failurePhase = "handle_disposal";
                failureCode = "observation_disposal_failed";
            }
        }

        var succeeded = failureCode is null && keyCreated && finalized && held is not null &&
            reopened is not null && cleanupIssued && cleanupObserved && handlesDisposed;
        var report = new
        {
            contract = "pegasus-machine-server-native-observation", version = 1,
            diagnosticObservationSucceeded = succeeded, policyEvaluation = "not_performed",
            sameExpectedHighOwner = true,
            keyCreationIssued = creationIssued, keyCreated, keyFinalized = finalized,
            held, reopened, cleanupIssued, cleanupObserved, handlesDisposed,
            outcomeUnknown = creationUnknown || keyCreated && !cleanupObserved,
            failurePhase, failureCode, nativeStatus,
            csrCreated = false, certificateStoreAccessed = false, privateMaterialExported = false
        };
        var reportWritten = false;
        try
        {
            // The create-new stream was reserved before any key effect. Only
            // closed public metadata is serialized, after cleanup and disposal.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report);
            if (bytes.Length > MaximumOutputBytes) throw new InvalidDataException("observation_output_bound");
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            output.Dispose();
            reportWritten = true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            try { output.Dispose(); }
            catch (Exception cleanup) when (Recoverable(cleanup)) { }
            Summary(false, cleanupObserved, handlesDisposed, false, "observation_report_failed");
            return 1;
        }
        Summary(succeeded, cleanupObserved, handlesDisposed, reportWritten, failureCode);
        return succeeded ? 0 : 1;
    }

    private static Observation Project(MachineServerCertificateKeyObservation value)
    {
        if (value.SecurityDescriptor.Length is < 1 or > 65536)
            throw new InvalidDataException("observation_descriptor_bound");
        try
        {
            var descriptor = new RawSecurityDescriptor(value.SecurityDescriptor, 0);
            var acl = descriptor.DiscretionaryAcl;
            var aces = new List<AceObservation>();
            if (acl is not null)
                for (var index = 0; index < Math.Min(acl.Count, MaximumAces); index++)
                {
                    var ace = acl[index];
                    var known = ace as KnownAce;
                    var qualified = ace as QualifiedAce;
                    aces.Add(new(index, Principal(known?.SecurityIdentifier), ace.AceType.ToString(),
                        ((byte)ace.AceFlags).ToString("x2", CultureInfo.InvariantCulture),
                        known is null ? null : unchecked((uint)known.AccessMask).ToString("x8", CultureInfo.InvariantCulture),
                        qualified?.AceQualifier.ToString(), qualified?.IsCallback, qualified?.OpaqueLength));
                }
            return new(value.MachineKey, value.ExportPolicy, value.RsaBits, value.KeyUsage,
                descriptor.Owner?.Value == "S-1-5-32-544",
                ((ushort)descriptor.ControlFlags).ToString("x4", CultureInfo.InvariantCulture),
                acl is not null, acl?.Count ?? 0, acl?.Count > MaximumAces, aces.ToArray());
        }
        finally
        {
            // ObserveKey necessarily queried public SPKI internally; neither it
            // nor the raw descriptor is part of this diagnostic's retained data.
            Array.Clear(value.PublicKeyInfo);
            Array.Clear(value.SecurityDescriptor);
        }
    }

    private static string Principal(SecurityIdentifier? sid) => sid?.Value switch
    {
        "S-1-5-18" => "SY", "S-1-5-32-544" => "BA", _ => "other"
    };

    private static FileStream ReserveOutput(string directory, string expectedOwnerSid)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(directory) || directory.StartsWith("\\\\", StringComparison.Ordinal) ||
            directory.StartsWith("//", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(directory), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("observation_directory_invalid");
        var path = Path.Combine(directory, FileName);
        if (path.Length > 240 || !Directory.Exists(directory))
            throw new InvalidDataException("observation_directory_invalid");
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (!current.Exists || current.LinkTarget is not null ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("observation_directory_invalid");
        var owner = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner?.Value != expectedOwnerSid)
            throw new UnauthorizedAccessException("observation_directory_owner_invalid");
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    }

    private static void RequireExpectedOwner(string expectedOwnerSid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (string.IsNullOrWhiteSpace(expectedOwnerSid) || expectedOwnerSid.Length > 184 ||
            new SecurityIdentifier(expectedOwnerSid).Value != expectedOwnerSid || identity.User?.Value != expectedOwnerSid ||
            expectedOwnerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || process.SessionId <= 0 ||
            !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("observation_owner_invalid");
        _ = GetTokenInformation(identity.AccessToken, 25, 0, 0, out var size); // TokenIntegrityLevel.
        if (size is < 16 or > 65536) throw new UnauthorizedAccessException("observation_integrity_unavailable");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 25, buffer, size, out var written) || written < 16 || written > size ||
                Marshal.ReadIntPtr(buffer) == 0 || new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value != "S-1-16-12288")
                throw new UnauthorizedAccessException("observation_high_integrity_required");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool Recoverable(Exception error) => error is not OutOfMemoryException and not StackOverflowException;
    private static string ErrorCode(Exception error) => error is MachineServerCertificateBackendException native
        ? native.Code : error is CertificateOperationException certificate ? certificate.Code : "observation_failed";
    private static void Summary(bool succeeded, bool cleanupObserved, bool handlesDisposed, bool reportWritten, string? failureCode)
    {
        var text = JsonSerializer.Serialize(new { diagnosticObservationSucceeded = succeeded, cleanupObserved, handlesDisposed,
            reportWritten, fileName = FileName, policyEvaluation = "not_performed", failureCode });
        if (succeeded) Console.WriteLine(text); else Console.Error.WriteLine(text);
    }

    private sealed record Observation(bool MachineKey, int ExportPolicy, int RsaBits, int KeyUsage,
        bool OwnerIsAdministrators, string ControlFlagsHex, bool NonNullDacl, int AceCount, bool AcesTruncated, AceObservation[] Aces);
    private sealed record AceObservation(int Index, string Principal, string Type, string FlagsHex, string? MaskHex,
        string? Qualifier, bool? Callback, int? OpaqueLength);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        nint information, int length, out int returnLength);
}
