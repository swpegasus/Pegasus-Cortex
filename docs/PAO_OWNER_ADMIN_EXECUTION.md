# PAO owner-admin execution, contract version 1

This additive PAO capability executes a reviewed headless program with the same
human owner's elevated Administrator token, session and loaded profile. It is
separate from ordinary User tool registration and from fixed LocalSystem file
operations. Ordinary `run`, `pwsh`, and interactive `app` remain on their existing
User routes. Protocol 1.0, Cortex MCP schema 2 and PAO schema 3 are retained.
Reviewed scope and invocation formats remain fixed. Current source retires
four-character root-code authorization in favor of approved connection authority
and boolean `system` intent.

SOURCE CHECKPOINT: the exclusive maintenance interval is implemented in source.
Focused qualification and independent source review remain separate from signed
packaging, installed acceptance and actual owner-admin token execution.
Source implementation and focused verification do not authorize deployment,
scope admission, grants, administrator execution or a dependent installer retry.

## Reusable authority and per-attempt integrity

An MCP connection must first have an explicit, current Full control approval.
`admin_admit` then uses `system:true` through the authenticated ordinary owner
Host and optional LocalSystem broker to admit a reviewed reusable scope. The
protected approval is established by the owner's fixed elevation control, not
by a name, chat/task label, request argument or boolean. Direct internal review
without the approved-connection callback still requires an actual elevated owner.
The strict scope body in `text`/stdin is an `OwnerAdminAuthorization`:

| Field | Contract |
| --- | --- |
| `version`, `id`, `taskId` | Version 1; unique 32 lowercase hex scope ID; exact task UUID. |
| `ownerSid`, `ownerSession`, `ownerProfile` | Exact human SID, positive session ID, canonical loaded profile path. |
| `clientApplicationPath`, `clientSha256` | Exact admitted owner-client executable path and SHA-256. Existing protected application policy and the scope's client pins remain independently enforced; a reported application or chat name is not executable identity. |
| `expiresAtUtc` | Explicit expiry, at most seven days after initial admission. |
| `programs`, `scripts` | Exact named paths and initial SHA-256 pins. Each entry has `allowRevisions`, false by default. True explicitly authorizes revised bytes at that exact path; it never authorizes arbitrary alternate paths. |
| `inputRoots`, `readRoots`, `writeRoots` | Canonical bounded roots for supplied inputs and declared effects. Every declared read/write root must independently pass the reviewed scope and applicable protected folder policy. |
| `declaredEffects` | Explicit set drawn from `filesystem`, `backup`, `installer`, `service_control`, `owner_process`. |
| `allowMaintenanceIntervals` | Explicitly true to permit begin/end within this exact scope and digest. |
| `allowOrdinaryQuiescenceAcknowledgements` | Optional separately reviewed authority for exact original unknown-effect evidence acknowledgements. Actual elevated owner review is still required for each unknown acknowledgement. |
| `allowUnconfinedAdministratorEffects` | Must explicitly be true. An administrator child is not confined by these declarations. They are reviewed authority, not a filesystem sandbox or kernel restriction. |

The service binds the scope to the approved connection's stable ID. It rechecks
the current Full control approval rather than accepting credential material in
the invocation. The broker supplies that check only after authenticating the
registered ordinary owner Host. Service possession of a privileged token alone
is not approval. The scope is immutable. A conflicting scope ID fails closed. Revocation is
explicit and permanent for that ID; a separately reviewed successor uses a new ID.

After initial setup, the ordinary owner client can run revised **in-scope**
attempts without another elevated manual handoff. A revised executable or script
must remain at its explicitly revision-enabled path and have fresh exact pins.
The current connection approval, applicable protected application/folder checks,
owner/session/client checks, reviewed task scope and expiry remain conjunctive
on each new launch. Revocation is rechecked after waiting for admission and
before process creation; it does not undo a process already created.

Each attempt supplies a strict `OwnerAdminDefinition` in stdin (CLI) or `text`
(MCP). It includes `version`, scope `id`, exact `taskId`, owner/client/profile
fields, fresh `expectedRid`, explicit expiry no later than the scope and at most
one day away, `action` (`exe` or `pwsh`), executable path/hash, working directory,
separate `arguments` array, exact `inputs`, read/write roots and effect classes.
`maintenanceEpoch` pins the currently active exact original interval. An old definition without it remains inspectable but cannot launch. `pwsh` additionally requires an exact `.ps1` path/hash and a pinned `pwsh.exe`.
The adapter constructs only `-NoLogo -NoProfile -NonInteractive -File SCRIPT`
followed by those literal script arguments. Generic command interpreters and
shell-string launch modes are rejected. An `exe` must be explicitly admitted.

`nonSecretInputs` must be true. Arguments and definitions are durable nonsecret
audit data; credentials, transient codes, tokens and secret environment values
must never be placed there. Secret file contents are not read into receipts or
records. Input records contain only path, hash and classification.

`pao admin validate --stdin` performs pure contract validation and returns the
normalized SHA-256, scope ID and execution RID without contacting a broker or
launching anything. Use that returned SHA for the corresponding exact run:

```
pao admin run system=true cfg=SCOPE_ID sha=DEFINITION_SHA rid=ORIGINAL_RID --stdin
pao admin stat system=true jid=ORIGINAL_JID
pao admin revoke system=true cfg=SCOPE_ID
```

Execution, status, scope revocation and admission require `system:true` and the
connection's current Full control approval. The syntax above describes the typed
CLI fields; a bare CLI boolean cannot supply the authenticated MCP authority
context or create approval. Use the approved MCP connection for protected work;
its adapter supplies public authority coordinates internally. Never put a bearer
credential in JSON, arguments or an invocation file. `validate` remains pure and
accepts neither `system` nor authorization material. Legacy root administration
and root-code arguments are rejected, with no automatic grant enrollment.

MCP equivalents use the single PAO tool with `admin_validate`, `admin_admit`,
`admin_run`, `admin_stat`, `admin_revoke`, `admin_reconcile`, `admin_begin`, `admin_end`, `admin_interval`, and `admin_ack`. Unknown and irrelevant fields are
rejected. A native library client credential cannot invoke privileged admin
operations; it retains its separate execution authorization boundary.

## Protected code and mutable data

The executable, script, executable directory, working directory, client executable
and code/config inputs must be protected against medium-token modification,
including unsafe ancestor replacement. Trusted owners/writers are LocalSystem,
Administrators and TrustedInstaller. The service never repairs these ACLs to make
a request pass. Stage revised code into an already approved protected task
directory using the existing fixed exact copy operation with `system:true` and current Full control approval, then
verify its resulting security and hashes. No protected staging occurs merely by
calling `validate` or `run`.

An input's `kind` defaults to `code`. `kind=data` permits a separately declared,
human-owned immutable data file without changing its owner or permissions.
**Both kinds are immutable runtime inputs:** the broker hashes their open handles
and retains read-only sharing leases for the observed root process lifetime.
Writing or replacing such an input during execution will fail with a sharing
violation. Code/config also requires protected ACLs so broker loss does not permit
medium-token code replacement.

Mutable effect targets must not be listed as immutable inputs. For example, the
original Cerebral `engine.db` that a new service must migrate/write is a declared
read/write effect target. Its owning domain wrapper must perform fresh identity,
hash and compare-and-swap checks appropriate to that operation. A prior observed
hash is not a transaction lease. Sealed backup/evidence files can be exact
immutable `data` inputs; preserve their human ownership, ACLs and sealed status.

## Token, profile and environment

The existing authenticated LocalSystem broker obtains the matching owner-session
token, selects its linked elevated token if necessary, verifies SID/session,
Administrator membership and High integrity, and duplicates a primary token.
Missing or mismatched tokens fail closed. There is no LocalSystem child fallback,
credential prompt, UAC automation or token-session reassignment.

The profile must already be loaded and match the admitted canonical profile path.
Launch uses `CreateProcessAsUserW`, an explicit application path, separately
serialized Windows argv, the token's session, no inherited handles, no inherited
standard streams and `CREATE_NO_WINDOW`. The child starts suspended; its original
PID and creation time are committed before its primary thread is resumed.

The environment is deliberately minimal. It contains the verified owner's profile
and derived AppData directories, approved owner username/domain, OS/program
directories, AMD64 architecture, a protected task temporary directory, a PATH
limited to the executable directory/System32/Windows and protected system module
paths. It does not inherit the service environment, interactive owner's PATH,
user module paths or arbitrary owner variables. It is not an interactive admin
shell environment. A prerequisite such as Python must be freshly classified in
this actual launch context; an earlier interactive-shell discovery is not proof.

## Original identity, monitoring and recovery

The service persists the original RID/JID, full immutable invocation and definition
hash **before** entering a process API. Exactly repeated RID/definition returns
that original job. A reused RID with different content is rejected. A separate,
explicit intended attempt needs a fresh RID; no retry operation replays a launch.

The response contains root PID/start, known exit code, owner identity, definition
pin and `process_scope=root_process`. There is no stdout/stderr capture. The
reviewed program may write declared nonsecret result files and must itself wait
for any child effects that its completion contract requires. Root exit zero
proves root-process success, not deployment acceptance, child-tree quiescence or
database correctness; owning-product verification remains necessary.

Optional `tmo` (1..60 seconds) bounds observation only. Expiry returns the same
running identity with `observation_expired=true`. Disconnect/cancellation ends
only that observation. No kill-on-close job, lifetime timer, transport timeout or
scope revocation terminates the child. Native cleanup can terminate only a failed
pre-start process whose primary thread has never been resumed.

The in-memory monitor retains the original process handle and records its actual
exit. Broker disposal retires handles without killing. After broker loss, indexed
nonterminal rows become `recovery_required` with original PID/start where known;
their exit stays unknown. Terminal history remains on disk and is read only by
exact RID/JID. Reconcile unknown outcomes through fresh original-identity evidence;
never infer success, relaunch or adopt a reused PID. One administrator process lane is admitted. Starting, running and unknown
originals fence distinct administrator RIDs. The maintenance interval independently fences ordinary effects across the resident host and broker. Terminal attempt count does not exhaust admission.

## Exclusive maintenance interval

Reusable scope admission and interval lifetime are separate. Pre-stage protected
programs, scripts and immutable inputs using existing fixed operations while the
interval is OFF. Begin with the exact reviewed scope ID/hash and a fresh intended
32-hex RID. Preserve that RID when retrieving an ambiguous response.

```
pao admin begin system=true cfg=SCOPE_ID sha=SCOPE_SHA rid=BEGIN_RID
pao admin interval system=true cfg=SCOPE_ID
pao admin end system=true cfg=SCOPE_ID sha=SCOPE_SHA epoch=ORIGINAL_EPOCH rid=END_RID --stdin
```

All three require `system:true` through the current approved Full control
connection. Invocation templates contain no credential. CLI and the single MCP PAO tool expose the same closed fields;
END accepts proof JSON in stdin or MCP `text`. Interval controls do not accept
`wait`, arbitrary arguments or workspace selection.

BEGIN persists the original RID/JID/epoch, owner SID/session, exact scope ID/hash
and service instance, then publishes PREPARING before checking original indexed
user and broker effects. The private callback authenticates the existing service
and pins the actual user-host PID/start/executable/pipe. Routine same-host
registration renewal is allowed. Pages contain at most eight effects; a bounded
scan or timeout cannot become a zero-work result. Known busy originals refuse
activation and remain untouched. Ambiguous probes retain an UNCERTAIN fence.

PREPARING, ACTIVE, ENDING and UNCERTAIN refuse ordinary mutations at engine
admission, application transitions, frozen callback, startup/refill recovery,
final dispatch and mutating job/queue controls. Optional PowerShell/NSIS bootstrap
is counted as a startup effect and defers while closed. Held never-started work
keeps its original receipt, request, JID and RID and may resume only after OFF.
Fixed true read/status operations remain available; a history snapshot or caller
label is not read authority. No ordinary work is forwarded to a new service route.

The nonsecret barrier is a newly protected `execution-maintenance` directory
beside the private service directory under the configured data root. LocalSystem
and Administrators own/write it; the bound owner can read it. No existing private
service ACL is widened. Public mode/ack files reject untrusted ownership, writers,
reparse components, malformed content and duplicate/unknown properties. Missing
required state or failed reads fence effects. After private feature initialization, missing public directory/sentinel cannot be reconstructed as a fresh OFF record. The actual service supplies a required reader to both its engine and broker even when owner-admin construction fails; failed construction releases private handles. An OFF reader needs no live service.

END requires no active/unknown administrator original, fresh same-epoch user and
broker quiescence, and a strict `MaintenanceHandback` proof:

| Field | Meaning |
| --- | --- |
| `version` | Exactly 1. |
| `epoch`, `scopeId`, `beginJid`, `beginRid` | Exact original interval and reviewed scope. |
| `nativeReceiptsSha256` | Fresh `native_receipts_sha` returned by `admin_interval`; covers exact original native receipt bytes through the indexed interval/job map. |
| `evidenceSha256` | Nonsecret owning-domain evidence hash establishing descendants and effects are quiescent. The generic process observer cannot establish this by itself. |
| `effectsQuiescent` | Explicit true assertion by the authorized caller. It does not turn process exit into domain or deployment acceptance. |

The END RID fingerprints the proof as well as the exact original epoch/scope.
Changed proof on an already accepted END RID conflicts. Same-RID response loss
returns the original committed outcome. Root-process exit alone is insufficient.
Completed attempt count does not exhaust interval admission. The handback digest streams every original native receipt for the epoch in deterministic indexed JID order, retaining one receipt at a time. Its complete fresh digest remains mandatory; single active/unknown-effect fencing and bounded input/status fields are unchanged. After known finished and verified quiescent work, END may restore
OFF, ordinary fixed operations may stage a revised attempt, and a NEW BEGIN
RID/epoch admits its freshly pinned invocation without another scope admission.

Service/host loss never automatically clears an open interval. Restart marks it
UNCERTAIN with original IDs. `admin_ack` requires separately reviewed scope
permission plus an actual elevated owner client and a strict acknowledgement of
an exact original store/JID/RID/receipt hash with domain evidence SHA. The store
is `user`, `broker` or `interval`. Live effects and held queued work cannot be
acknowledged. The acknowledgement is separate protected metadata; it never edits
an original receipt, invents an exit/success, cancels, quarantines or replays work.
A changed receipt invalidates its acknowledgement. An exact uncertain interval
acknowledgement may authorize re-probing a replacement host for that END attempt.
Scope expiry/revocation blocks future administrator launches but never clears
the interval or prevents exact-original diagnostics and safe explicit handback.
## Deployment preparation and acceptance gate

1. Review the source and focused build/test evidence. Build a coherent owner
   candidate through the established product route; preserve the complete role,
   catalog, path, identity, signing and data-location contracts. Do not splice a
   stale-version partial debug DLL set into the installed product.
2. Resolve and review the existing owner update route, exact affected roles and
   service/host reload before protected changes. This change needs the engine,
   protocol, PAO CLI, execution service, actual user host and unified MCP runtime coherently updated.
   It creates no new Windows service and does not change trust, registration or
   guidance. No new grant is sought or recreated by this work.
3. Preserve the current Cortex/Cerebral database and sealed backup. The consumed
   Cerebral Source03 installer is closed evidence and is never an acceptance test.
4. After explicit reviewed deployment authorization, verify installed hashes and
   direct `pao.exe help admin_run`/`admin_validate` plus MCP advertised schema.
   Run a harmless owner/admin identity-and-argv fixture in a newly admitted scope,
   asserting actual owner SID, High/admin token, session and profile; observe a
   deliberate timeout/disconnect, same-RID retrieval and actual exit without
   replay. Inspect nonsecret fixture output, not just configuration.
5. Verify an ordinary User SDK read/build remains medium-token and interactive
   `app` remains on the normal user route. Verify native library credentials
   cannot reach admin execution. Revoke the fixture scope after acceptance.
6. Ordinary CLI access remains separate from MCP. Protected `system:true` syntax
   alone does not enroll or authenticate a connection; qualify it through the
   approved MCP route. Refresh/reconnect the adapter only if its advertised tools
   remain stale after the coherent update. Request a Codex
   restart only if concrete post-update evidence makes it necessary.

No installed administrator acceptance is established by source tests. The first
real administrator fixture is a separately reviewed deployment acceptance step.
