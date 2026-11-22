# Shared MCP neutral-core snapshot

This directory contains the product-neutral MCP transport core owned by the
Pegasus shared-library work. It is a pinned source snapshot from
`Pegasus Cerebral/tools/SharedMcp/SharedMcp`, copied on 2026-11-21 so a Cortex
checkout does not require a neighboring Cerebral checkout. No product dispatcher,
STDIO adapter, development host, test harness, configuration, generated output,
certificate, key or user data is included.

The source project declares `0.1.0-dev`, .NET 10 and C# 14, and references the
`Microsoft.AspNetCore.App` framework. No released shared package or upstream Git
commit is implied. The exact source files below were copied without modification.

Shayne Winter confirmed ownership and authorized public distribution of this
first-party snapshot under the repository's [Pegasus Community License 1.0](../../LICENSE)
on November 21, 2026. Framework and other third-party components retain their
own terms; the grant does not alter the pinned source bytes below.

| File | Bytes | SHA256 |
| --- | ---: | --- |
| `Contracts.cs` | 2802 | `1f4a897f56fad204c79f40418bfc32924bd77f7eb50572ce1983b8d16ee146a6` |
| `McpTlsQualification.cs` | 10601 | `5c0aedc5a4128a3e372749c2e7670789cdfc7dfe0158f6993c85956782cea9f1` |
| `PrivateNetworkPolicy.cs` | 2759 | `ca2758abb19c0a0f5781365bb24ee3c71d7e7f71b14e29c4bbd3517c1b97171b` |
| `SharedMcp.csproj` | 678 | `48690adc7d78d05c664d5ad074b0e7460fd648d0ed6efcad031f2de9a6c12bc6` |
| `SessionContracts.cs` | 4659 | `dcebafc3839c81b61552831a5163c79daf89980e9266311c46cf0464fbf45da9` |
| `SharedMcpServer.cs` | 32025 | `5eb0e27ac90b850a0ad9d165b993497f8c50715121ab5d543513505e3b512edc` |

The identity/lifecycle extension was imported on 2026-11-22 from the shared owner's
reviewed frozen source. It adds per-request credential verification, authenticated
session binding and drain-aware session cleanup. Credential custody, approval,
revocation and protected-operation policy remain Cortex-owned.

The exact-byte neutral fixture Tests/IdentityChecks.cs is carried in
tests/Pegasus.AgentOps.Tests/IdentityChecks.cs (SHA256
fa441e0221c652f9be16d103a75bc923a138f9e976c0d8c36e0f368922da3e3f).
Its dedicated selector is --shared-mcp-identity-only. That selector passed all 32
isolated real loopback HTTP checks in the Cortex test graph, using deterministic
verifier and dispatcher fixtures. It does not qualify installed clients or the
separate canonical Cerebral/STDIO suite. Product approval and credential custody
remain separate Cortex verification responsibilities.

Update this snapshot only from a reviewed shared-owner source checkpoint, with
an explicit file inventory and refreshed byte/hash pins. Make shared behavior
changes in the owning shared library and bring the reviewed update here; do not
silently maintain a Cortex-only fork. When an admitted shared-repository package
exists, replace this source boundary deliberately and verify the complete Cortex
publish graph before removing the snapshot.
