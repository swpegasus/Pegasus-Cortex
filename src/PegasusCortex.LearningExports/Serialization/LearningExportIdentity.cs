using System.Security.Cryptography;

namespace Pegasus.Cortex.LearningExports.Serialization;

/// <summary>
/// Creates unambiguous deterministic identities from canonical structured fields.
/// </summary>
public static class LearningExportIdentity
{
    public static string ComputeHash(
        string domain,
        params KeyValuePair<string, string>[] fields) =>
        Convert.ToHexString(ComputeHashBytes(domain, fields)).ToLowerInvariant();

    internal static byte[] ComputeHashBytes(
        string domain,
        IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(fields);
        var material = new IdentityMaterial(
            domain,
            fields.Select(field => new IdentityField(field.Key, field.Value)).ToArray());
        return SHA256.HashData(LearningExportJson.Serialize(material));
    }

    private sealed record IdentityMaterial(string Domain, IReadOnlyList<IdentityField> Fields);
    private sealed record IdentityField(string Name, string Value);
}
