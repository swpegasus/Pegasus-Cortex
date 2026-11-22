using Pegasus.Cortex.Product;
using System.Runtime.Versioning;

namespace Pegasus.AgentOps.Engine;

[SupportedOSPlatform("windows")]
internal sealed class SmbOwnerBinding
{
    private readonly OwnerHostIdentity _owner;
    private readonly Lazy<OwnerSmbConnectionProfiles> _profiles;
    private readonly Lazy<WindowsSmbConnections> _connections;
    internal SmbOwnerBinding(string ownerSid, Func<LocalPeer> current, Func<OwnerSmbConnectionProfiles> profiles,
        Func<WindowsSmbConnections> connections)
    {
        _owner = new(ownerSid, current);
        _profiles = new(profiles ?? throw new ArgumentNullException(nameof(profiles)));
        _connections = new(connections ?? throw new ArgumentNullException(nameof(connections)));
    }
    [SupportedOSPlatform("windows")]
    internal static SmbOwnerBinding ForOwner(CortexProductLayout layout) => new(layout.OwnerSid, LocalIdentity.Current,
        () => new OwnerSmbConnectionProfiles(Path.Combine(layout.UserExecutionDirectory, "connections"), layout.OwnerSid),
        () => new WindowsSmbConnections(layout.OwnerSid));
    internal bool IsCurrentOwner() => _owner.IsCurrentOwner();
    internal OwnerSmbConnectionProfiles Profiles()
    { RequireOwner(); return _profiles.Value; }
    internal WindowsSmbConnections Connections()
    { RequireOwner(); return _connections.Value; }
    private void RequireOwner()
    { if (!IsCurrentOwner()) throw new SmbConnectionException("smb_owner_denied"); }
}
