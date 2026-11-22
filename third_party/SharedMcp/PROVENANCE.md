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
| `Contracts.cs` | 2412 | `a2a07b3b620a29c42ae234412670a6af87178a8a9d3055f722321f73186404ec` |
| `McpTlsQualification.cs` | 10601 | `5c0aedc5a4128a3e372749c2e7670789cdfc7dfe0158f6993c85956782cea9f1` |
| `PrivateNetworkPolicy.cs` | 2759 | `ca2758abb19c0a0f5781365bb24ee3c71d7e7f71b14e29c4bbd3517c1b97171b` |
| `SharedMcp.csproj` | 678 | `48690adc7d78d05c664d5ad074b0e7460fd648d0ed6efcad031f2de9a6c12bc6` |
| `SharedMcpServer.cs` | 24415 | `de5a8d5b1dab8a20ed6f121110dd08d9f7fcf35908e5adaf0c4f676e644afccc` |

Update this snapshot only from a reviewed shared-owner source checkpoint, with
an explicit file inventory and refreshed byte/hash pins. Make shared behavior
changes in the owning shared library and bring the reviewed update here; do not
silently maintain a Cortex-only fork. When an admitted shared-repository package
exists, replace this source boundary deliberately and verify the complete Cortex
publish graph before removing the snapshot.
