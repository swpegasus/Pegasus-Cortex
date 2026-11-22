# Programming Tenets

This is an open-ended library of durable, project-independent engineering guidance compiled from user direction, the coordinating agent, independent reviewers, and agents with different architecture, lifecycle, platform, tooling, and programming perspectives. Equivalent contributions are merged under one canonical owner instead of preserved as separate agent opinions.

The folder name `Programing Tenants` is retained exactly as requested. Within the library, the correct term **programming tenets** is used.

The current catalog is coverage, not scope. A missing subject can be added when it has broadly reusable, non-fringe guidance that passes [CONTRIBUTING.md](CONTRIBUTING.md). Existing examples never define the outer boundary of the library.

## How to use this library

1. Start with `core/` for universal invariants.
2. Load relevant files from `disciplines/` for broad technical behavior.
3. Use `workflows/` for an ordered kind of work.
4. Add only the relevant `specializations/` for a tool, protocol, interface class, platform, architecture, environment, ecosystem, or language.
5. Apply every tenet within current user and project authority; a tenet never grants permission by itself.
6. Reconcile guidance with current source, configuration, contracts, and observable evidence.
7. Reference canonical IDs instead of copying the same rule into several files.

## Structure

```text
core/                         Universal invariants
disciplines/                  Broad engineering subjects
workflows/                    Recurring sequences of work
specializations/
  tools/                      Tool-specific deltas
  protocols/                  Protocol-specific deltas
  interfaces/                 Interface-class deltas
  platforms/                  Operating and application platforms
  architectures/              Processor and runtime architectures
  environments/               Execution and development environments
  ecosystems/                 External product ecosystems
  languages/                  Language-specific behavior
```

## Core

- [Delivery, scope, and triage](core/delivery-and-scope.md)
- [Modularity and reuse](core/modularity-and-reuse.md)
- [Authority, security, and integrity](core/authority-security-and-integrity.md)
- [Failures, diagnostics, and evidence](core/failures-diagnostics-and-evidence.md)
- [Verification and efficiency](core/verification-and-efficiency.md)
- [Workspace, artifacts, and path safety](core/workspace-artifacts-and-path-safety.md)
- [Communication and handoff](core/communication-and-handoff.md)

## Disciplines

- [Requirements and decisions](disciplines/requirements-and-decisions.md)
- [Architecture and dependency direction](disciplines/architecture-and-dependency-direction.md)
- [Domain modeling, types, and identifiers](disciplines/domain-modeling-types-and-identifiers.md)
- [Interfaces, contracts, and serialization](disciplines/interfaces-contracts-and-serialization.md)
- [Data, persistence, and migrations](disciplines/data-persistence-and-migrations.md)
- [Concurrency and resource lifecycle](disciplines/concurrency-and-resource-lifecycle.md)
- [Networking, distribution, and messaging](disciplines/networking-distribution-and-messaging.md)
- [Configuration and feature controls](disciplines/configuration-and-feature-controls.md)
- [Dependencies and supply chain](disciplines/dependencies-and-supply-chain.md)
- [Observability and operability](disciplines/observability-and-operability.md)
- [Performance, capacity, and caching](disciplines/performance-capacity-and-caching.md)
- [Reliability, backup, and recovery](disciplines/reliability-backup-and-recovery.md)
- [Security engineering](disciplines/security-engineering.md)
- [Tenancy and isolation](disciplines/tenancy-and-isolation.md)
- [Privacy and data lifecycle](disciplines/privacy-and-data-lifecycle.md)
- [Portability, time, and encoding](disciplines/portability-time-and-encoding.md)
- [User-facing quality and accessibility](disciplines/user-facing-quality-and-accessibility.md)
- [Release, deployment, and rollback](disciplines/release-deployment-and-rollback.md)
- [Maintenance and documentation](disciplines/maintenance-and-documentation.md)
- [Version control and change history](disciplines/version-control-and-change-history.md)

## Workflows

- [Adding features](workflows/adding-features.md)
- [Building software](workflows/building.md)
- [Testing](workflows/testing.md)
- [Debugging](workflows/debugging.md)
- [Refactoring](workflows/refactoring.md)
- [Code review](workflows/code-review.md)
- [Continuous integration](workflows/continuous-integration.md)
- [Playing and interactive testing](workflows/playing-and-interactive-testing.md)

## Specializations

- Interfaces: [Command-line interfaces](specializations/interfaces/command-line.md)
- Tools: [PowerShell](specializations/tools/powershell.md), [CMD](specializations/tools/cmd.md), [Git](specializations/tools/git.md)
- Protocols: [Model Context Protocol](specializations/protocols/mcp.md)
- Platforms: [Windows](specializations/platforms/windows/platform.md), [Windows Registry](specializations/platforms/windows/registry.md), [Linux](specializations/platforms/linux/platform.md), [Web](specializations/platforms/web/platform.md)
- Architectures: [ARM64](specializations/architectures/arm64.md)
- Environments: [Codex and coding agents](specializations/environments/codex/agent-work.md), [Containers](specializations/environments/containers/platform.md)
- Ecosystems: [Steam](specializations/ecosystems/steam/integration.md)
- Languages: [Python](specializations/languages/python.md), [C# and .NET](specializations/languages/csharp-dotnet.md), [JavaScript and TypeScript](specializations/languages/javascript-typescript.md), [SQL](specializations/languages/sql.md)

## Canonical tenet format

```markdown
## ID — Short title

**Rule:** One durable decision rule.

**Practice:** Observable behavior that applies the rule.

**Boundary:** The important limit, exception, or escalation point.
```

See [CONTRIBUTING.md](CONTRIBUTING.md) before adding, moving, merging, or revising tenets.
