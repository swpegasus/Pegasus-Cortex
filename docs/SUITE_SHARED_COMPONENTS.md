# Pegasus suite shared-component boundary

Pegasus products may reduce duplicate payloads only through an explicit,
versioned machine-wide tools catalog. The intended default root is
`%ProgramFiles%\Pegasus\Tools`; products must not discover another product's
private `bin`, `tools`, plug-in, or state directories by probing paths.

## Admission contract

A shared component must be immutable, portable between participating products,
ABI-stable for its declared version, independently licensed for redistribution,
and admitted by exact version, architecture, publisher/signature, inventory,
and content digest. Registration records owners and reference counts so repair,
upgrade, and uninstall change only entries they own. Discovery returns a
versioned typed identity and canonical path; it never returns a generic command
transport.

Only narrow contracts and self-contained portable tools belong in the shared
root. Product-private .NET dependency closures, host-specific providers,
mutable settings, databases, caches, logs, receipts, plug-in data, policy, and
credentials remain isolated. A shared tool receives explicit arguments and
state/output paths from the owning product; it never infers them from the
caller's installation.

## Current release decision

This Cortex release does not move or share an installed binary. The bundled
NSIS 3.12 payload remains private under `<CortexInstallRoot>\tools` because no
cross-product ownership, reference-count, or rollback contract has yet been
installed with Cerebral. Moving it prematurely would couple independent repair
and uninstall lifecycles. Cortex and Cerebral may adopt the shared catalog in a
later coordinated release after both installers implement the same admission
and lifetime rules.
