# Cerebral application authorization

Pegasus Cortex provides one private, local, versioned named-pipe authorization
route for the `PegasusCerebral` Windows service. It is separate from the public
PAO CLI/MCP contract and from attended four-character root codes.

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

Issue and rotate return credential material exactly once over the already
identity-validated pipe. Cerebral must immediately protect it beneath its
service boundary and must never expose it through an agent, UI, CLI, MCP,
receipt, task, or log. Cortex stores only a salted PBKDF2-SHA256 verifier. It
does not store plaintext credential material.

## Attended service lifecycle

The public PAO CLI/MCP contract also exposes a separate attended,
root-authorized service-lifecycle route for the exact `PegasusCerebral`
service. It is not part of the private application credential session.
`service_status` returns bounded state and, when running, the exact process ID
and canonical UTC process-start identity. `service_stop` and
`service_restart` require those observed values; `service_start` requires
`expect=stopped`; mutation timeouts are 1-120 seconds.

This is not a generic Service Control Manager interface. The adapter accepts
no other service name, executable path, command line, or machine, and cannot
target Cortex's own service. It executes only through the authenticated
LocalSystem broker. The attended root value is validated before dispatch,
stripped before durable queueing, and never written to a receipt or Cortex
record.

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
credentials fail closed. Cortex continues to support attended root codes
independently. Cortex and Cerebral remain functional at their ordinary
nonprivileged boundaries when this optional application session is absent.
