# Cerebral application authorization

Pegasus Cortex provides one private, local, versioned named-pipe authorization
route for the `PegasusCerebral` Windows service. It is separate from the public
PAO CLI/MCP contract and from persistent MCP connection approvals. The private
Cerebral application Full control grant is managed independently; an MCP grant
does not enroll or renew that application session.

## Security boundary

- The pipe is local-only, rejects network and anonymous identities, and admits
  only the LocalService identity used by Cerebral. The first request is not
  trusted on its own.
- Cortex resolves the live `PegasusCerebral` and Cortex execution-service
  processes through the Windows Service Control Manager. The pipe client PID,
  session 0 identity, canonical executable path, and running-service PID must
  all agree. Reparse paths are rejected.
- Both live service executables must carry valid Authenticode signatures with
  the Code Signing EKU. Their exact publisher leaf certificates must match.
  The check is dynamic: normal rebuilds may change executable bytes without a
  product hash update, and Cortex does not hardcode or disclose the publisher
  certificate selected for the installed pair.
- A caller-provided executable path, certificate claim, service name, root
  field, or credential field inside a PAO request is never accepted as proof.

## Credential lifecycle

The initial frame is `ApplicationAuthorizationRequest` version 1 with fixed
application ID `pegasus-cerebral` and one action:

- `issue`: creates a 256-bit credential when no active credential exists;
- `rotate`: atomically replaces the active credential;
- `status`: returns only label, public credential ID, generation, and state;
- `revoke`: removes the active verifier and leaves public revoked metadata;
- `connect`: authenticates a persistent application session.

In current source, a connected session retains its original application ID,
public credential ID and generation. Cortex rechecks that exact active tuple
before admitting each subsequent operation. Revocation or rotation therefore
denies the old connection's next operation as well as a new connection using
the old credential. Reissuing access does not silently renew an old session.
Malformed or unreadable registry state fails closed without returning credential
material. This does not undo an operation already admitted before revocation,
and it never automatically renews or replays work. Prepared releases retain the
behavior of their own compiled version.

Issue and rotate return credential material exactly once over the already
identity-validated pipe. Cerebral must immediately protect it beneath its
service boundary and must never expose it through an agent, UI, CLI, MCP,
receipt, task, or log. Cortex stores only a salted PBKDF2-SHA256 verifier. It
does not store plaintext credential material.

## Explicit protected service lifecycle

Current source exposes a separate protected service-lifecycle contract for the
exact `PegasusCerebral` service. Through MCP it requires boolean `system:true`,
that connection's active Full control approval, and the optional Cortex
execution service. It is not part of the private Cerebral application credential
session. The boolean is execution intent, not a credential or a grant; the
admitted Cortex owner Host supplies the nonsecret authority context internally.
`service_status` returns bounded state and, when running, the exact process ID
and canonical UTC process-start identity. `service_stop` and
`service_restart` require those observed values; `service_start` requires
`expect=stopped`; mutation timeouts are 1-120 seconds.

This is not a generic Service Control Manager interface. The adapter accepts
no other service name, executable path, command line, or machine, and cannot
target Cortex's own service. It executes only through the authenticated
LocalSystem broker, which checks the actual registered owner Host and the current
protected connection approval. Revocation or credential-generation changes deny
new dispatches. A name, chat label or task ID cannot substitute for that proof.
Legacy root-code arguments are rejected rather than interpreted as approval.

After `connect`, the same pipe carries sequential, existing
`OperationRequest` and `OperationReceipt` frames. Cortex removes no validation
from the typed PAO operations. It rejects `root`, `root_code`, `credential`,
caller-supplied frozen path roots, root administration, and queue administration
on this route. Valid operations use the existing service-owned FIFO,
idempotency, cancellation, bounded results, path validation, and error taxonomy.

The owner dashboard shows only nonsecret application state and supports
revocation. Enrollment and rotation remain service-to-service operations.

## Acceptance boundary

`CortexApplicationAuthorization` must be exercised through the running
`PegasusCerebral` Windows service. A console, test host, Integrator process, or
other signed-in-user process that instantiates the client directly is expected
to receive `UnauthorizedAccessException` while opening the pipe because it is
not the admitted LocalService/SCM process. That denial is not evidence that the
service route is unavailable. A live acceptance invokes `access.full.enable`
through Cerebral's ordinary owner command route, confirms the resulting
nonsecret `cortex.application.pipe` status, and exercises the required fixed
typed operation through the Worker. Tests that do not run as the installed
service use an injected transport and never relax the production pipe ACL or
peer verifier.

## Failure behavior

Unsigned, tampered, expired, wrong-EKU, wrong-signer, wrong-path, wrong-PID,
wrong-service, wrong-SID, non-session-zero, revoked, malformed, or stale
credentials fail closed. MCP connection approval and its optional Full control
remain separate from this application session. Cortex and Cerebral remain
functional at their ordinary nonprivileged boundaries when this optional session
is absent. These source contracts do not establish installed or live availability.
