# Knowledge.Windows.Identity.SIDAndProfileScope

Object: Knowledge.Windows.Identity.SIDAndProfileScope
Version: 1.0.0
Triggers: per-user data layout, ACLs, copied profiles/databases, domain or Microsoft-account sign-in, user migration
Rendered budget: 4096 UTF-8 bytes

## Core model

A Windows SID identifies a security principal issued by a security authority. Display names, folder names, email addresses, and login labels are not equivalent identities.

Local accounts are issued by the individual computer and their SIDs are machine-specific. Recreating the same account name—or using the same Microsoft-account email—on another computer does not by itself establish the same local SID. Domain-issued principals retain the domain SID for that principal across domain computers, subject to the principal still existing and the machine resolving that authority. Deleted and recreated principals receive different SIDs.

## Application data design

- Use the live token SID for local access control and current-user isolation.
- Store a separate product-owned portable profile identity when application data must move between machines.
- Preserve source owner metadata during backup/export, but perform an explicit verified rebind on import.
- Do not derive portable identity solely from username, email, profile-folder name, or a user-edited directory name.
- Do not assume copied ACL entries will resolve usefully on another computer.
- Keep shared machine data, per-user semantic data, service state, and interactive-session state in separately owned locations.

## Migration

A safe migration verifies the source profile identity, destination signed-in principal, existing destination state, collision policy, and explicit mapping. It reapplies destination ACLs for the destination principal rather than trusting stale numeric SID entries.

## Boundary

A portable application identity helps locate and rebind data; it does not replace Windows authentication or authorize access to another user's records.

References:
- https://learn.microsoft.com/en-us/windows/win32/secauthz/security-identifiers
- https://learn.microsoft.com/en-us/windows/security/identity-protection/access-control/local-accounts
