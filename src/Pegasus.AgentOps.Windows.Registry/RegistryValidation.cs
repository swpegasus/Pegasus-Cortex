using System.Security.Cryptography;
using System.Text;

namespace Pegasus.AgentOps.Windows.Registry;

internal static class RegistryValidation
{
    internal static bool Key(RegistryKeyIdentity? key)
    {
        if (key is null || !Enum.IsDefined(key.Hive) || !Enum.IsDefined(key.View) || key.KeyPath is null ||
            key.KeyPath.Length > RegistryLimits.MaxKeyPathChars || !Text(key.KeyPath)) return false;
        if (key.KeyPath.Length == 0) return true;
        var segments = key.KeyPath.Split('\\');
        return segments.Length <= RegistryLimits.MaxKeyDepth && segments.All(segment =>
            segment.Length is > 0 and <= RegistryLimits.MaxKeySegmentChars && segment is not "." and not ".." && !segment.Contains('/'));
    }

    internal static bool Identity(RegistryValueIdentity? identity) => identity is not null && Key(identity.Key) &&
        identity.ValueName is not null && identity.ValueName.Length <= RegistryLimits.MaxValueNameChars && Text(identity.ValueName);

    internal static bool Text(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsControl(current)) return false;
            if (!char.IsSurrogate(current)) continue;
            if (!char.IsHighSurrogate(current) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i])) return false;
        }
        return true;
    }

    internal static bool Digest(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool Preimage(RegistryValuePreimage? guard) => guard is not null && Digest(guard.KeyGeneration) &&
        (guard.ValueDigest is null || Digest(guard.ValueDigest));

    internal static bool Data(RegistryValueData? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || !Enum.IsDefined(value.Type) || value.Base64 is null ||
            value.Base64.Length > ((RegistryLimits.MaxValueBytes + 2) / 3) * 4) return false;
        try { bytes = Convert.FromBase64String(value.Base64); }
        catch (FormatException) { return false; }
        // A canonical encoding prevents aliases, whitespace and silent truncation.
        return bytes.Length <= RegistryLimits.MaxValueBytes && Convert.ToBase64String(bytes) == value.Base64 &&
            (value.Type is not (RegistryValueType.DWord or RegistryValueType.DWordBigEndian) || bytes.Length == 4) &&
            (value.Type != RegistryValueType.QWord || bytes.Length == 8);
    }

    internal static RegistryKeyIdentity Parent(RegistryKeyIdentity key)
    {
        var split = key.KeyPath.LastIndexOf('\\');
        return key with { KeyPath = split < 0 ? "" : key.KeyPath[..split] };
    }

    internal static string Leaf(RegistryKeyIdentity key) => key.KeyPath[(key.KeyPath.LastIndexOf('\\') + 1)..];

    internal static string KeyFingerprint(RegistryKeyIdentity key) => Hash(writer =>
    {
        writer.Write(RegistryLimits.ContractVersion);
        writer.Write((int)key.Hive);
        writer.Write((int)key.View);
        writer.Write(key.KeyPath.ToUpperInvariant());
    });

    internal static RegistryKeySnapshot Snapshot(RegistryKeyIdentity key, RegistryNativeKeyInfo info)
    {
        if (info.SubKeyCount < 0 || info.ValueCount < 0)
            throw new RegistryNativeException(RegistryNativeFailure.Failed, "native_key_info_invalid");
        return new(key, Hash(writer =>
        {
            writer.Write(KeyFingerprint(key));
            writer.Write(info.LastWriteFileTime);
            writer.Write(info.SubKeyCount);
            writer.Write(info.ValueCount);
        }), info.SubKeyCount, info.ValueCount);
    }

    internal static string ValueDigest(RegistryValueIdentity identity, RegistryNativeValue value) => Hash(writer =>
    {
        writer.Write(KeyFingerprint(identity.Key));
        writer.Write(identity.ValueName.ToUpperInvariant());
        writer.Write((uint)value.Type);
        writer.Write(value.Data.Length);
        writer.Write(value.Data);
    });

    private static string Hash(Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) body(writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}
