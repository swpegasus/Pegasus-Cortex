using System.Text.RegularExpressions;

namespace Pegasus.AgentOps.Engine;

// Trusted owner-host composition supplies the expected SID and OS sampler.
// This is an identity check, never a grant or caller-selected execution mode.
internal sealed class OwnerHostIdentity
{
    private readonly string _ownerSid;
    private readonly int _session;
    private readonly Func<LocalPeer> _current;

    internal OwnerHostIdentity(string ownerSid, Func<LocalPeer> current)
    {
        if (ownerSid is null || ownerSid.Length > 184 ||
            !Regex.IsMatch(ownerSid, "^S-[0-9]+(?:-[0-9]+){1,15}$", RegexOptions.CultureInvariant) ||
            ownerSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            throw new ArgumentException("owner_identity_invalid");
        _ownerSid = ownerSid;
        _current = current ?? throw new ArgumentNullException(nameof(current));
        try { _session = _current().Session; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { _session = 0; }
    }

    internal bool IsCurrentOwner()
    {
        try
        {
            var current = _current();
            return _session > 0 && current.Session == _session && !current.Elevated &&
                StringComparer.Ordinal.Equals(current.Sid, _ownerSid);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { return false; }
    }
}
