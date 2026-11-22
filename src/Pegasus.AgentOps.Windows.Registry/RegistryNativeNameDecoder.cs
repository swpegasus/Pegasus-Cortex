namespace Pegasus.AgentOps.Windows.Registry;

// Pure seam: neither construction nor decoding opens the registry. The native
// character count is independent of StringBuilder's NUL-terminated marshalling.
internal static class RegistryNativeNameDecoder
{
    internal static string Decode(string materializedName, uint nativeLength, int maxNameChars)
    {
        if (maxNameChars is < 0 or > RegistryLimits.MaxValueNameChars || nativeLength > maxNameChars)
            throw new RegistryNativeException(RegistryNativeFailure.LimitExceeded, "native_name_size_limit");
        // Never expose allowed\0hidden as allowed. Check the independent count
        // before accepting any materialized identity, then reject embedded NUL,
        // controls and unpaired surrogates even when their lengths agree.
        if (materializedName.Length != nativeLength || !RegistryValidation.Text(materializedName))
            throw new RegistryNativeException(RegistryNativeFailure.Unsupported, "native_name_identity_invalid");
        // Length zero is valid for the unnamed/default value. The owning browse
        // validation separately rejects empty key names and unsafe key segments.
        return materializedName;
    }
}
