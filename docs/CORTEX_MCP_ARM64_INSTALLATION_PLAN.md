# Cortex MCP ARM64 Fresh-Release Installation Plan

Last updated: 2026-08-07

## Scope

This is a release-neutral cutover procedure for the next common-source Linux
ARM64 Pegasus Cortex release. Allocate the release identifier only after the
source and focused tests are final, then substitute values exclusively from the
sealed package and native-probe evidence. Older release identifiers, hashes,
tool counts, migration `0006` instructions, transfer bundles, and database
contents are historical evidence and must not be rebound to new bytes.

The target is a fresh PostgreSQL database. No SQLite file, stale Cortex data,
project registry, credential, ACL, or machine identity is imported. Project-
local `.cortex` files remain authoritative and can seed new recovery heads after
activation through a separately verified workflow.

For this single-host deployment, Cortex and PostgreSQL may use trusted plaintext
loopback. That is the database transport only. Direct Codex-to-Cortex SSH stdio
as `codexsvc` or `cortexaudit` is an explicitly authorized, temporary,
operator-only exception for this cutover and canary; PostgreSQL is not exposed
to the LAN. Permanent external or remote MCP access remains exclusively
Nexus-relayed and Nexus-authenticated. Direct non-Nexus remote access must fail
closed and this temporary exception must not be generalized into a product
ingress contract.

## Release Inputs

Seal these values together after the fresh build:

- `<release>` and source-tree hash;
- `<delivery-root>` and outer delivery SHA-256;
- `<package-root>` and `package-manifest.json` SHA-256;
- installed tree, entry assembly, continuity guide, runtime, installer, launcher,
  smoke-tool, migration-set, and dependency hashes;
- exact main and auditor tool inventories (currently 16 and 4);
- native no-database probe command, stdout summary, exit code, architecture, OS,
  and runtime identity;
- focused local test commands and results, with skipped live work recorded as a
  skip rather than a pass.

Do not create release-bound prepare or activation packets before those bytes and
hashes exist.

## Isolated Native No-Database Probe

Extract the sealed validation application and runtime bundles into a new
root-owned stage, apply the manifest-declared executable modes, and independently
verify every file hash. With all `PEGASUS_CORTEX_PG_*` variables absent, run:

```sh
cd <validated-app-root>/apps/postgresql-fixture
env -i \
  PATH=/usr/bin:/bin \
  HOME=<empty-private-home> \
  DOTNET_ROOT=<validated-runtime-root>/runtime \
  DOTNET_MULTILEVEL_LOOKUP=0 \
  DOTNET_EnableDiagnostics=0 \
  <validated-runtime-root>/runtime/dotnet \
  ./PegasusCortex.Persistence.PostgreSql.IntegrationTests.dll
```

The expected final lines for the current fixture are:

```text
SKIP LIVE set PEGASUS_CORTEX_PG_INTEGRATION=1 and supply isolated loopback connections plus an explicit transport profile
RESULT offline=22 live=skipped
```

Required exit code: `0`. This proves the native ARM64 runtime can execute the
offline contract checks without a database, credentials, keys, network, or host
mutation. It is not PostgreSQL integration evidence. Any different count,
unexpected environment inheritance, native-load error, or nonzero exit blocks
provisioning.

## Safe SSH Delivery

Use the existing pinned `codexsvc` management route. The client command must pin
the known host key, use batch mode, disable password and keyboard-interactive
fallback, and bind the intended host alias. Do not use `StrictHostKeyChecking=no`
or accept a new key interactively.

1. Create a unique bounded upload directory under the management agent's
   protected staging root. Do not stage directly under the final version tree.
2. Transfer only the sealed delivery and its public evidence through the pinned
   route as `codexsvc`.
3. On the host, independently verify size and SHA-256 before privilege
   escalation. Reject links, special files, unexpected owners, unsafe modes,
   path escapes, or extra files.
4. The management agent moves the verified files into a new root-owned plan
   stage, records both pre- and post-move hashes, and makes the application
   payload read-only. `codexsvc` never owns the final release directory.
5. Record command identity, host-key fingerprint reference, remote physical
   paths, ownership/modes, hashes, timestamps, and exit statuses in immutable
   evidence. Do not record secrets or raw configuration contents.

## Read-Only Pre-Mutation Checks

Immediately before mutation, capture one immutable preflight packet:

- `uname -m` is `aarch64`; OS release and filesystem capacity are supported;
- the physical target of `/opt/pegasus/apps/cortex/current`, its selector
  identity, release manifest hash, installed-tree hash, and smoke status;
- ownership, modes, and absence of links for Pegasus root, Cortex versions,
  release configurations, manifests, and staging roots;
- PostgreSQL version is at least 14 and is reachable only on a literal loopback
  address;
- the proposed fresh `ctx_it_` fixture database and fresh deployment database
  names do not already exist;
- migration and runtime roles are distinct; runtime is non-superuser,
  non-owner, non-`CREATEROLE`, non-`CREATEDB`, and non-`BYPASSRLS`;
- the old selected release, its runtime configuration, database identity, and
  prior receipts remain available for rollback;
- no pending prepare/activate/rollback intent uses any chosen plan ID.

Preflight is read-only. A mismatch stops the operation; it is not repaired
implicitly.

## Fresh PostgreSQL Acceptance

Use a separate native-fixture plan ID and a fresh disposable `ctx_it_` database.
The external operator creates the database and distinct migration/runtime roles;
the fixture never creates or drops them.

Set both connections to literal loopback IPs with `SSL Mode=Disable`, then run:

```sh
env -i \
  PATH=/usr/bin:/bin \
  HOME=<empty-private-home> \
  DOTNET_ROOT=<candidate>/runtime \
  DOTNET_MULTILEVEL_LOOKUP=0 \
  DOTNET_EnableDiagnostics=0 \
  PEGASUS_CORTEX_PG_INTEGRATION=1 \
  PEGASUS_CORTEX_TLS_POLICY=trusted-loopback \
  PEGASUS_CORTEX_PG_MIGRATION_CONNECTION='<protected migration connection>' \
  PEGASUS_CORTEX_PG_RUNTIME_CONNECTION='<protected runtime connection>' \
  <candidate>/runtime/dotnet \
  <candidate-native-fixture>/PegasusCortex.Persistence.PostgreSql.IntegrationTests.dll
```

Expected final result: `RESULT offline=22 live=passed`, exit `0`. The retained
evidence must show:

- migration ledger complete through `0007`;
- exact production runtime privilege reconciliation;
- forced RLS and exact migration-0007 policy inventory;
- runtime denial of direct protected-table access;
- proposal publication, owner-only change, immutable tombstone lineage;
- exact retry idempotency and concurrent CAS with one winner;
- tenant/namespace isolation, authentication, retention, and existing sync
  workflow checks.

Delete the disposable fixture database only under a separately recorded cleanup
step. The deployment database is a different fresh database.

## Prepare

Use a unique `<prepare-plan-id>` and the sealed manifest hash:

```sh
sudo python3 <staged>/install_cortex_mcp_arm64.py prepare \
  --package-root <absolute-protected-package-root> \
  --pegasus-root /opt/pegasus \
  --expected-manifest-sha256 <package-manifest-sha256> \
  --plan-id <prepare-plan-id>
```

Before this command, the management agent creates release-scoped protected
runtime and one-shot migration configurations for the fresh deployment database.
The sealed package/installer profile is `trusted-loopback-plaintext`; inside the
protected Service and migration environments that profile is represented by
`PEGASUS_CORTEX_TLS_POLICY=trusted-loopback`. The roles remain distinct, both
connection strings use literal loopback with SSL disabled, and runtime never
receives the migration credential. Do not substitute either value for the other.

Preparation must verify and install the immutable tree, run migration `0007`,
reconcile exact runtime grants, remove the one-shot migration configuration,
write a protected prepared receipt, and leave `current` unchanged. Preserve the
receipt, preparation intent/attempt/migration evidence, selector-before identity,
runtime-configuration hash, package-manifest hash, and installed-tree hash.

## Candidate-Direct Smoke

Set `CANDIDATE=/opt/pegasus/apps/cortex/versions/<release>` and do not use
`current` during candidate proof:

```sh
sudo -u codexsvc -- "$CANDIDATE/tools/smoke_cortex_mcp.py" \
  --command "$CANDIDATE/run-cortex-mcp" \
  --expected-guide-sha256 <guide-sha256> \
  --expected-entry-assembly-sha256 <entry-sha256> \
  --expected-surface main

sudo -u cortexaudit -- "$CANDIDATE/tools/smoke_cortex_mcp.py" \
  --command "$CANDIDATE/run-cortex-mcp" \
  --expected-guide-sha256 <guide-sha256> \
  --expected-entry-assembly-sha256 <entry-sha256> \
  --expected-surface auditor
```

Both exit `0`; the main surface is exactly 16 tools and the auditor surface is
exactly 4. Require PostgreSQL readiness, `storageKind=postgresql`, truthful route
capabilities, one valid zero-result broad search, reserved-content isolation,
and no route-specific readiness error. Re-read the selector and require it to
equal the pre-prepare identity.

## Activation

Use a new `<activation-plan-id>`; never reuse the prepare plan ID:

```sh
sudo python3 <staged>/install_cortex_mcp_arm64.py activate \
  --pegasus-root /opt/pegasus \
  --target-version <release> \
  --expected-manifest-sha256 <package-manifest-sha256> \
  --prepared-plan-id <prepare-plan-id> \
  --plan-id <activation-plan-id>
```

Activation must reverify the prepared receipt, manifest, immutable tree,
runtime-configuration hash, deleted migration credential, and unchanged selector
checkpoint before atomically selecting the candidate. Its packaged smoke and
activated receipt must succeed. Independently rerun both OS-principal smokes and
verify the selected physical target, receipt chain, migration `0007`, and runtime
privileges.

## Rollback

Activation restores the previous selector automatically if its smoke or receipt
publication fails. For an explicit rollback, allocate a third unique
`<rollback-plan-id>` and use only hashes from preserved preflight/activation
evidence:

```sh
sudo python3 <staged>/install_cortex_mcp_arm64.py rollback \
  --pegasus-root /opt/pegasus \
  --target-version <previous-release> \
  --expected-manifest-sha256 <previous-manifest-sha256> \
  --expected-current-version <release> \
  --expected-current-identity-sha256 <activated-selector-identity-sha256> \
  --expected-target-tree-sha256 <previous-tree-sha256> \
  --plan-id <rollback-plan-id>
```

For a schema-3 rollback target, also supply
`--expected-runtime-configuration-sha256 <trusted-previous-runtime-config-hash>`.
Do not supply it for the sealed schema-1 predecessor. Rollback changes only the
selector and smokes the preserved target; it does not delete either database,
release, configuration, or receipt.

## Codex SSH Stdio Registration

Register only after activation succeeds and only under the temporary
operator-only cutover exception stated above. These command shapes are not a
permanent remote-access contract:

```text
ssh -T -o BatchMode=yes -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=<pinned-known-hosts> -i <codexsvc-key> codexsvc@<pinned-host-alias> /opt/pegasus/apps/cortex/current/run-cortex-mcp
```

```text
ssh -T -o BatchMode=yes -o PasswordAuthentication=no -o KbdInteractiveAuthentication=no -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=<pinned-known-hosts> -i <auditor-key> cortexaudit@<pinned-host-alias> /opt/pegasus/apps/cortex/current/run-cortex-mcp
```

Use separate Codex aliases for main and auditor. Back up Codex configuration,
change only those aliases, and request one Codex restart. After restart, verify
initialize, `tools/list`, guide read, status, broad search, project brief, and
auditor isolation before treating the registration as ready. When the
Nexus-owned authenticated relay is available, replace these direct aliases with
the Nexus route and remove the exception. Any other direct remote connection is
unsupported and must fail closed.

## Current Gaps Before Deployment

- The fresh release, final hashes, native no-database packet, and release-bound
  prepare/activate packets do not exist until the current common source is
  finalized and packaged.
- The ARM validation-bundle manifest now declares the selected fixture transport
  explicitly. Private-PKI remains the default profile with revocation evidence;
  trusted-loopback plaintext forbids certificate and revocation inputs.
- This plan documents but does not prove a live PostgreSQL `0007` run. Native
  trusted-loopback acceptance remains mandatory on the ARM host before prepare.
- SSH, server state, deployment, registration, and Codex restart are external
  operations and require their own current authorization and immutable evidence.
