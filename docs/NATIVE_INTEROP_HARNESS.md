# Isolated native source-provider interoperability

This is a test-executable mode, not an installed product mode. It runs the real
`CortexResidentServer`, typed operational application, SQLite store and passive
asset reader. It never resolves installed layout/credentials, starts a user
host/tray/service, reads the installed personal profile, seeds the live store,
registers a provider or invokes PAO execution. Only a unique native pipe opens;
there is no MCP process, MCP endpoint, initialize or JSON-RPC exchange.

## Build once

Use this source tree's `global.json` SDK and the existing test project:

```powershell
& 'C:\Program Files\Pegasus\Cortex\bin\pao.exe' dotnet build `
  'src=C:\CodexProjects\Pegasus Cortex\tests\PegasusCortex.Mcp.Local.Tests\PegasusCortex.Mcp.Local.Tests.csproj' `
  cfg=Release 'cwd=C:\CodexProjects\Pegasus Cortex' tmo=180 -- --no-restore -warnaserror
```

On another source checkout use its actual paths. The resulting source apphost is
`tests/PegasusCortex.Mcp.Local.Tests/bin/Release/net10.0-windows/PegasusCortex.Mcp.Local.Tests.exe`.
The harness is part of this existing non-packable test executable, with no new
NuGet dependency, production CLI switch or installed artifact.

## Prepare and launch

Run these in the ordinary signed-in Windows user session. An isolated shell
sandbox may hide its named-pipe namespace from an outside Cerebral/PAO process;
request an approved normal-session launch rather than weakening authentication.
This is not a request for administrator/UAC, a service or a trust change.

```powershell
$script = 'C:\CodexProjects\Pegasus Cortex\tools\Invoke-NativeInterop.ps1'
$fixture = & $script -Action Prepare -ClientExecutable 'C:\Program Files\dotnet\dotnet.exe' |
  ConvertFrom-Json
$fixture.root
# In a dedicated ordinary-user terminal, or an exec session with a short yield:
& $script -Action Start -RunRoot $fixture.root
```

The launcher starts the apphost hidden and holds its owning shell session until
exit. Keep that session open while the consumer runs. Do not run `Start` as a
long-held PAO `run`/`pwsh` job: its serialized execution worker would block the
consumer's PAO build/test operations. A detached `Start-Process` followed by
closing the owner shell is not a reliable Codex test-host lifetime.

Prepare creates exactly one random fixture under the source subtree
`.tmp/native-interop/<32-hex-GUID>`. Program Files, installed data locations,
other scratch trees, reparse components, foreign owner/session and unsafe ACLs
are rejected before use. Exactly one client, `cerebral`, is enrolled against
the provided OS-observed executable. Here the consumer is the test assembly
through `dotnet.exe`, not `Cerebral.Worker.exe`. Its protected key exists only
inside that private scratch fixture. No key is returned on stdout or supplied
as a command argument. No installed registry is consulted or changed.

`<root>/endpoint.json` is public discovery metadata containing:

- `contract`: `pegasus.cortex.native/1`;
- `nativePipe`: `pegasus-cortex-interop-<32-hex-GUID>`;
- `expectedHostExecutable`, `ownerSid`, `session`;
- `clientId`, `credentialFile`, `credentialDirectory`;
- `fixtureNamespace`: `project.cerebral-interop`;
- scratch `databasePath`, frame limits, `isolated=true`, `network=false`.

The descriptor is not readiness proof. Check actual pipe-server PID-derived
path/SID/session before reading the CurrentUser-DPAPI credential, then validate
the real hello. The server independently checks actual client identity and key.
`ready.json` supplies the passive fixture asset's `cortex://<pinned-id>` and PID;
it is also metadata, not proof that a later server is still alive.

The personal layer is deliberately `not_loaded` without querying the installed
profile. Shared/project operations remain available and namespace defaults are
not data permissions. PAO execution is disabled. The fixture owns one SQLite
database and an exclusive provider lease, has an explicit stop and a 900-second
maximum lifetime. A disposed consumer does not stop other consumers.

## Probes and shutdown

The owner probe uses the real frame/peer/key path and its one enrolled client.
Because that client is `dotnet.exe`, invoke the test **DLL** for this probe:

```powershell
$dll = 'C:\CodexProjects\Pegasus Cortex\tests\PegasusCortex.Mcp.Local.Tests\bin\Release\net10.0-windows\PegasusCortex.Mcp.Local.Tests.dll'
& 'C:\Program Files\dotnet\dotnet.exe' $dll native-interop probe --root $fixture.root
```

It verifies catalog/status/start, scratch task create, atomic finish, bounded
search, exact CRLF/tab/Unicode read and exact pinned asset bytes. These are
synthetic test records in the scratch database, not the live Codex task.

Cerebral's production-client test entry is:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' '<actual fresh Cerebral.Tools.Tests.dll>' `
  --native-cortex-interop (Join-Path $fixture.root 'endpoint.json')
```

The consumer's own fixture policies remain its responsibility. Do not redirect
an installed provider binding or add fallback on test failure.

After both probes, from a second ordinary-user shell:

```powershell
& $script -Action Stop -RunRoot $fixture.root
```

The source host cancels its listener/sessions, closes SQLite and exits. Its
stdout reports `state=stopped,activeConnections=0`; the owner shell also exits.
No process-name kill or service stop is used. Retain bounded fixture evidence
until the handoff completes; do not package the scratch database, keys or logs.
To run again after stop/expiry, prepare a new fixture rather than adopting an
old readiness file. Do not rebuild the same executable while the provider is
running; stop first or use a separate explicitly selected build output.

Source-provider interoperability is not installed-provider acceptance, deployed
Cerebral/model/LocalService evidence, signing, sustained load or installation.

## Current source acceptance

The October 7 source checkpoint passed the owner's separate-process native
probe (real scratch SQLite, exact Unicode, atomic finish and pinned asset bytes)
and five resident regression checks. Cerebral reported twelve checks through
its production dispatcher/native client and owner-session relay, including
idempotent writes and exact readback after reconnect and two local pipe hops.
The owner then stopped the fixture through its supported command, observed
zero active connections, verified process exit and observed owner-shell exit 0.
See `../artifacts/validation/native-interop-current.json` for bounded receipts,
reported-versus-owner evidence and excluded claims. The installed provider and
existing signed candidate were not changed by this source acceptance.
