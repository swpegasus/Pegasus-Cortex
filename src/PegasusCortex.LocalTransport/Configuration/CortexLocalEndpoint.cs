using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Pegasus.Cortex.LocalTransport.Configuration;

/// <summary>
/// Derives a current-OS-user endpoint without consulting environment variables.
/// The profile is explicit and cannot silently select an integrated transport.
/// </summary>
public sealed record CortexLocalEndpoint
{
    public const int MaximumProfileCharacters = 64;

    private CortexLocalEndpoint(string profileId, string pipeName)
    {
        ProfileId = profileId;
        PipeName = pipeName;
    }

    public string ProfileId { get; }
    public string PipeName { get; }

    public static CortexLocalEndpoint Create(string profileId)
    {
        if (!IsPortableIdentifier(profileId))
            throw new ArgumentException("A bounded portable local profile is required.", nameof(profileId));
        var identity = CaptureOsUserIdentity();
        var material = Encoding.UTF8.GetBytes($"{identity}\n{profileId}");
        var suffix = Convert.ToHexStringLower(SHA256.HashData(material))[..32];
        return new CortexLocalEndpoint(profileId, $"pegasus-cortex-{suffix}");
    }

    private static bool IsPortableIdentifier(string? value) =>
        value is { Length: > 0 } &&
        value.Length <= MaximumProfileCharacters &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        !value.EndsWith('.') &&
        !value.EndsWith(' ') &&
        !value.Contains("..", StringComparison.Ordinal);

    private static string CaptureOsUserIdentity()
    {
        if (OperatingSystem.IsWindows())
            return CaptureWindowsUserIdentity();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return $"uid:{GetEffectiveUserId()}";
        throw new PlatformNotSupportedException("Standalone Cortex local IPC requires a supported OS user boundary.");
    }

    [SupportedOSPlatform("windows")]
    private static string CaptureWindowsUserIdentity() =>
        WindowsIdentity.GetCurrent().User?.Value ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint GetEffectiveUserId();
}
