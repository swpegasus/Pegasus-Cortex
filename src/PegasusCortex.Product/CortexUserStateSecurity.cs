using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Pegasus.Cortex.Product;

/// <summary>Windows access policy for one user's product state, not a workspace or service ACL policy.</summary>
[SupportedOSPlatform("windows")]
internal static class CortexUserStateSecurity
{
    private const uint FullControl = (uint)FileSystemRights.FullControl;
    private const uint DirectoryAccess = (uint)(FileSystemRights.Modify | FileSystemRights.Synchronize);
    private const uint FileAccess = (uint)(FileSystemRights.Read | FileSystemRights.Write |
        FileSystemRights.Delete | FileSystemRights.Synchronize);
    private const uint DirectoryReadAccess = (uint)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
    private const uint FileReadAccess = (uint)(FileSystemRights.Read | FileSystemRights.Synchronize);

    internal static DirectorySecurity CreateDirectory(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in CreationPrincipals(user))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateFile(SecurityIdentifier user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in CreationPrincipals(user))
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static void RequireAccess(FileSystemSecurity security, SecurityIdentifier user,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        // A direct user grant is required, so an elevated administrator token
        // cannot stand in for the ordinary user's access. Include deny-only
        // groups when considering denies on the current user's token.
        var denyPrincipals = new HashSet<string>(StringComparer.Ordinal)
        {
            user.Value, "S-1-1-0", "S-1-5-11"
        };
        using var current = WindowsIdentity.GetCurrent();
        var groupsKnown = current.User?.Equals(user) == true;
        if (groupsKnown) AddDenyGroups(current.AccessToken, denyPrincipals);
        RequireAccess(security, user, denyPrincipals, groupsKnown, requiredAccess);
    }

    // The descriptor-only seam makes token membership explicit in focused tests.
    // For a different user without its token, a deny cannot be proven harmless.
    internal static void RequireAccess(FileSystemSecurity security, SecurityIdentifier user,
        IReadOnlySet<string> denyPrincipals, bool groupsKnown,
        CortexUserStateAccess requiredAccess = CortexUserStateAccess.Modify)
    {
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(denyPrincipals);
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !Trusted(descriptor.Owner, user))
            throw new UnauthorizedAccessException("The user-state entry has an unrelated Windows owner.");
        if (descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("The user-state entry has an unrestricted access list.");

        var requiredRights = requiredAccess switch
        {
            CortexUserStateAccess.Read => security is DirectorySecurity ? DirectoryReadAccess : FileReadAccess,
            CortexUserStateAccess.Modify => security is DirectorySecurity ? DirectoryAccess : FileAccess,
            _ => throw new ArgumentOutOfRangeException(nameof(requiredAccess))
        };
        uint userAccess = 0;
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if (entry is not CommonAce ace || ace.IsCallback ||
                ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new UnauthorizedAccessException("The user-state entry contains an unverifiable access rule.");
            var rights = ExpandGenericRights(unchecked((uint)ace.AccessMask));
            if (ace.AceQualifier == AceQualifier.AccessAllowed)
            {
                // This includes read-only and inherit-only grants: private
                // data must not become visible to an unrelated ordinary user.
                if (!Trusted(ace.SecurityIdentifier, user))
                    throw new UnauthorizedAccessException("The user-state entry grants access to an unrelated principal.");
                if (ace.SecurityIdentifier.Equals(user) && (ace.AceFlags & AceFlags.InheritOnly) == 0)
                    userAccess |= rights;
            }
            else if ((rights & requiredRights) != 0 &&
                (!groupsKnown || denyPrincipals.Contains(ace.SecurityIdentifier.Value) ||
                 ace.SecurityIdentifier.Value == "S-1-3-4"))
            {
                throw new UnauthorizedAccessException("The user-state entry denies required user access.");
            }
        }
        if ((userAccess & requiredRights) != requiredRights)
            throw new UnauthorizedAccessException("The user-state entry does not grant the required ordinary user data access.");
    }

    private static bool Trusted(SecurityIdentifier sid, SecurityIdentifier user) => sid.Equals(user) ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);

    private static IEnumerable<SecurityIdentifier> CreationPrincipals(SecurityIdentifier user)
    {
        yield return user;
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    }

    private static uint ExpandGenericRights(uint rights)
    {
        if ((rights & 0x10000000) != 0) rights |= FullControl;
        if ((rights & 0x80000000) != 0) rights |= (uint)(FileSystemRights.Read | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        if ((rights & 0x40000000) != 0) rights |= (uint)(FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        if ((rights & 0x20000000) != 0) rights |= (uint)(FileSystemRights.ReadAndExecute | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        return rights & 0x0fffffff;
    }

    private static void AddDenyGroups(SafeAccessTokenHandle token, HashSet<string> principals)
    {
        // WindowsIdentity.Groups omits deny-only and logon groups. Read the
        // bounded native list so a filtered Administrator SID cannot hide a deny.
        const int maximumBytes = 1024 * 1024;
        if (GetTokenInformation(token, 2, IntPtr.Zero, 0, out var length) ||
            Marshal.GetLastWin32Error() != 122 || length < 4 || length > maximumBytes)
            throw new UnauthorizedAccessException("The user's deny-group membership could not be verified.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 2, buffer, length, out var returned) || returned < 4 || returned > length)
                throw new UnauthorizedAccessException("The user's deny-group membership could not be verified.");
            var count = Marshal.ReadInt32(buffer);
            var offset = Marshal.OffsetOf<TokenGroupsHeader>(nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
            var stride = Marshal.SizeOf<SidAndAttributes>();
            if (count < 0 || count > 4096 || (long)offset + (long)count * stride > returned)
                throw new UnauthorizedAccessException("The user's deny-group membership exceeds its supported bounds.");
            for (var index = 0; index < count; index++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(buffer, offset + index * stride));
                if ((group.Attributes & 0x14) == 0) continue; // enabled or SE_GROUP_USE_FOR_DENY_ONLY
                var sidOffset = group.Sid.ToInt64() - buffer.ToInt64();
                if (sidOffset < 0 || sidOffset + 8 > returned)
                    throw new UnauthorizedAccessException("The user's deny-group SID is invalid.");
                var subAuthorities = Marshal.ReadByte(group.Sid, 1);
                if (subAuthorities > 15 || sidOffset + 8 + 4 * subAuthorities > returned)
                    throw new UnauthorizedAccessException("The user's deny-group SID is invalid.");
                principals.Add(new SecurityIdentifier(group.Sid).Value);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader { public uint Count; public SidAndAttributes FirstGroup; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
