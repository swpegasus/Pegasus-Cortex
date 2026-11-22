# Default Library source policy

The curated Default Library uses a closed source allowlist. Its purpose is to
preserve product and shared-contract knowledge without importing infrastructure
operations, transient coordination state, or host-specific details.

## Allowed source authorities

- `pegasus-shared` for versioned language-neutral schemas, immutable package
  metadata, version mapping, compatibility, diagnostics, and security contracts.
- `pegasus-nexus` for narrowly scoped identity/discovery contract metadata.
- `pegasus-server` for narrowly scoped lifecycle contract metadata.
- `pegasus-coder` for narrowly scoped agent/outcome contract metadata.
- `pegasus-node` for narrowly scoped typed outcome and capability metadata.
- `pegasus-scatterbrain` for narrowly scoped product version/contract metadata.
- `pegasus-cortex` for Cortex contracts, source policy, verified local behavior,
  and self-description.

Every evidence reference must identify an allowlisted authority, a bounded
artifact, an immutable SHA-256, verification state, observation time, and source
version when available. Broad documents are excluded when their unrelated
sections contain infrastructure or operational information.

## Excluded sources and content

- A coordination receipt is not eligible merely because it was acknowledged.
  Default Library v1 does not use coordination receipts as seed evidence.
- External approval-gated test or access channels may exist, but they are outside
  the curated baseline and confer no product authority.
- Infrastructure operations and access-channel records are outside the baseline.
- Secrets, credentials, private reasoning, raw conversations, raw console output,
  and executable instructions remain prohibited.

The source allowlist does not grant record visibility or execution authority.
Import and retrieval still require server-derived namespace and sensitivity
authorization.
