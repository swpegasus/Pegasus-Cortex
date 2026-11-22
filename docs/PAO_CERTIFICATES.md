# Owner-local certificates

PAO's version 1 certificate family runs through the authenticated ordinary
signed-in owner host. It does not start a host, require the optional service,
or contact a timestamp server. The three explicit `store_admin_*` actions and
`issue_machine_server` request one same-owner UAC approval. Store changes and trust installation
are explicit operations, separate from creation and file exchange:

| Operation | Body fields after `version: 1` | Effect |
| --- | --- | --- |
| `certificate_create_ca` | `reference`, `subject`, optional `days`, `keyBits` | Create a new protected CA reference |
| `certificate_issue_code` | Same fields, plus `issuer: { reference, certificateId }` | Issue a new code-signing reference from the pinned CA |
| `certificate_issue_server` | Same fields, pinned `issuer`, and `dnsNames` / `ipAddresses` | Issue a server-authentication leaf with explicit subject alternative names |
| `certificate_issue_machine_server` | Same issuance fields as `issue_server` | Create a nonexportable machine key through UAC, sign its public CSR in the ordinary owner, and bind the public leaf to LocalMachine/My |
| `certificate_inspect` | `reference`, optional `certificateId` | Read validated public metadata |
| `certificate_export_public` | `reference`, `certificateId`, `format: "pem"` or `"der"` | Return public certificate content only |
| `certificate_sign` | `reference`, `certificateId` | Sign the exact admitted `src`/`sha` file |
| `certificate_verify` | `certificateId` | Observe the exact admitted file's signature and trust |
| `certificate_import_public` | `reference`, `certificateId`, `format`, `content` | Create a public-only reference from PEM or base64 DER |
| `certificate_import_private` | `reference`, `certificateId`, `passwordReference` | Import the exact `src`/`sha` protected PFX into a new reference |
| `certificate_export_private` | `reference`, `certificateId`, `passwordReference` | Create a protected PFX at the absent admitted `dst` |
| `certificate_store_list` | `scope`, `store`, optional `max`, `afterCertificateId` | Observe one public store page |
| `certificate_store_add` | `scope`, `store`, `reference`, `certificateId` | Insert public material into a nontrust store |
| `certificate_store_bind_server` | `reference`, `certificateId` | Explicitly bind the server leaf and its private key to CurrentUser/My; no trust installation |
| `certificate_store_trust` | Same fields | Explicitly install public material into a trust store |
| `certificate_store_remove` | `scope`, `store`, `certificateId` | Remove the exact public-only certificate |
| `certificate_store_admin_add` | `scope: "local_machine"`, `store`, `reference`, `certificateId` | Request UAC for one public nontrust-store insertion |
| `certificate_store_admin_trust` | Same fields | Request UAC for one explicit public trust installation |
| `certificate_store_admin_remove` | `scope: "local_machine"`, `store`, `certificateId` | Request UAC for one exact public-only removal |

CLI spelling is `pao certificate <action> --stdin`, with one JSON body on
standard input. Unified MCP uses the corresponding `certificate_<action>`
selector and supplies that JSON as the `text` string. Each operation's help
contains its exact fields and parser-validated examples.

Bodies have a 16 KiB UTF-8 limit and depth 8. Unknown or duplicate fields are
refused. References contain 1..64 lowercase ASCII letters, digits, dots,
underscores or hyphens, beginning with a letter or digit. Certificate IDs and
file hashes are exact lowercase SHA-256 identities. Subjects are literal common
names, at most 128 characters, without leading/trailing whitespace or controls.

Creation defaults to RSA 3072; explicit sizes are 2048, 3072 or 4096. CA validity
defaults to 365 days, with a 3650-day maximum. Code-signing validity defaults to
30 days, with a 366-day maximum and the issuer's validity envelope enforced.
TLS server leaves use the same validity and RSA bounds, a fixed server-authentication
purpose, and explicit DNS/IP subject alternative names. Their combined name count
is 1..16; DNS names are bounded ASCII labels without wildcards or URI syntax,
and IP addresses must be canonical and unscoped. These operations cannot issue
intermediate CAs or caller-selected extension policies.

## Storage and restart

The verified host supplies its fixed execution-state `certificates` directory
under the selected registered DataRoot. Requests cannot choose a vault, provider,
password, account or storage path. Each reference is create-only. An occupied
reference is refused without replacement or adoption.

An envelope binds the owner, reference, certificate ID, creation request identity
and public DER. Its PFX is password-encrypted; a new random password is wrapped
with CurrentUser DPAPI. Private material is reopened ephemerally when required.
The same Windows user/machine is required. This is not a portable key backup or
an implicit OS-store enrollment mechanism. Public-only imports instead store
validated public DER with no protected PFX/password fields; resolving such a
reference as a private key is refused. New files use ordinary owner, SYSTEM and
Administrators access; safe existing permissions are admitted without rewriting.

Construction, inspection and resolution do not initialize a missing vault or
repair permissions. Inspection reads public DER without decrypting the key.
`ProtectedMaterialPresent` reports presence, not successful decryption or current
signing readiness. An omitted inspect pin permits public diagnosis after a lost
creation response; it does not authorize replacement or key rotation.

## Explicit private exchange and store operations

Private import/export uses standard password-protected PKCS#12 files, bounded
at 1 MiB. Import takes an exact admitted `src` and current `sha`. Export takes
an absent admitted `dst`, creates a private staging file, flushes it, and publishes
without replacement. Both require registered-workspace admission. Replies carry
only public identity, destination, byte count and output hash. PFX bytes never
belong in MCP request bodies, responses, history or process arguments.

`passwordReference` resolves an existing CurrentUser Generic Credential with
the fixed target `Pegasus.Cortex.Certificate.<reference>`. References follow the
same lowercase grammar. The blob must be a nonempty strict UTF-16LE password,
at most 1024 characters, without NUL. Resolution reads the credential set for
the current token's logon session; it never enumerates, writes, prompts or
substitutes another identity. Missing or unavailable references fail explicitly.
Provisioning is a separate owner action; do not put the password in a PAO request.
See the Windows [CredRead contract](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw).

The local **Connections & access → Certificate passwords** page supplies that
owner action. Enter a reference and masked password, then explicitly save or
replace its value. Save is an upsert, not an atomic create-only operation.
Checking returns presence only, never the password or a password-usability claim.
Removal requires confirmation for that exact reference. An unconfirmed change
does not retry; explicitly check before making another deliberate change.

This page operates only inside the ordinary owner Host's admission and active
work boundary. A standalone dashboard cannot read or write the credential set.
Passwords use mutable, cleared buffers and never enter PAO/MCP arguments, job
rows, receipts, configuration files or a Cortex database. The fixed Generic
Credential target is unchanged. Windows persistence is local to subsequent
logons of the same user on this computer; it does not make the credential
available to SYSTEM or another user. See the Windows
[CredWrite contract](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)
and [credential persistence definitions](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw).
Focused checks cover injected credential calls, the existing resolver and real
in-memory PKCS#12 consumers, owner lifetime with an owned SQLite fixture, and
an unshown native textbox with synthetic input. These do not establish actual
Windows credential-store provisioning, use of real saved passwords or installed
UI acceptance.

Store scopes are `current_user` and `local_machine`; kinds are `personal`,
`intermediate`, `root`, `trusted_publisher` and `trusted_people`. Inventory is
read-only for either scope, returns 32 entries by default (maximum 128), and
uses an optional certificate-ID continuation. Each call observes the store anew;
it does not promise a stable multi-call snapshot. The backend has a 4096-entry
inventory bound.

The ordinary owner host permits CurrentUser writes. LocalMachine writes are
refused before any store, key or credential access and require a separate
administrator operation. `store_add` accepts personal/intermediate targets;
root/publisher/people targets require `store_trust`. Those public-store operations
never import private keys, and removing a store entry with a private key is refused. A store
effect with an unknown result remains uncertain and cannot be automatically
replayed or acknowledged.

`store_bind_server` is a separate, fixed private-key operation. It resolves the
exact managed server reference and SHA-256 certificate ID, validates the server
leaf, and binds its key only to the signed-in owner's CurrentUser/My store.
It cannot select a machine store or trust store. The Windows key is persisted
without an exportable-key option; temporary protected PKCS#12 material stays in
memory and never enters arguments or receipts. Reopening verifies the selected
certificate and usable private key. A matching usable binding is unchanged;
an ambiguous or public-only conflict is refused. Uncertain persistence or store
outcomes remain explicit and do not trigger an automatic second attempt.

The live result includes a public locator:
`binding: { location: "CurrentUser", store: "My", certificateId: "<SHA-256 DER>" }`.
The shared MCP host can read that exact existing certificate without exporting
the private key. CA public export and root trust installation remain separate
PAO actions. A certificate valid under a selected custom CA does not itself
prove that another client already trusts that CA.

The certificate toolkit validates certificate material and chains without opening
network connections. The shared MCP plugin owns its HTTPS loopback connection
check and readiness result. Neither certificate issuance nor binding installs
the CA or starts an MCP listener.

The three `store_admin_*` operations use a fixed one-shot mode in the installed
Host. The ordinary host inspects the pinned public certificate; the helper
receives only public DER or an exact removal identity over the existing owner
pipe. It cannot read a vault, private key or password. Add and trust remain
distinct, and scope must be `local_machine`. UAC must approve the same signed-in
owner in the same session; a different administrator account is refused. There
is no execution-service requirement, stored administrator token, generic command,
automatic elevation fallback or background store retry.

The ordinary host binds a random one-use capability to the original helper
process generation and protected installed Host path. It persists issued/unknown
metadata before releasing the public payload. Capabilities and payloads never
enter durable history. A declined or prepayload-canceled prompt has no store
effect. After payload release, client cancellation does not abandon the original
child or trigger another action; the host retains ownership until natural exit.
A missing report remains uncertain. A valid observed report stays authoritative
even if its acknowledgement is lost or the helper exits nonzero. Same-RID calls
return metadata, and restart recovery never recreates a prompt or store action.

Source/fixture qualification of this route is separate from an actual installed
UAC/store test. Such a test changes the selected machine store and needs its
explicit target and cleanup plan; advertising the action does not prove it is
installed or currently available.

## Explicit machine server keys

`issue_machine_server` is distinct from ordinary `issue_server` and
`store_bind_server`. It creates one new RSA key in the fixed Windows Software
Key Storage Provider, with machine scope, no overwrite, no export permission,
and a protected Administrators-owned key DACL granting SYSTEM and Administrators.
The operation GUID determines its internal name; callers cannot supply a key
container, provider, store, file path or alternate identity.

The same retained-helper coordinator authenticates three closed phases. It
releases the admitted specification and public CA only after recording issued
metadata. The helper sends a public PKCS#10 request. The ordinary owner signs
that request once using the pinned CA and publishes a public-only reference.
Only the signed public leaf returns to the helper. The CA private key remains
with the ordinary owner; the machine private key remains in Windows.

The helper validates the exact leaf, selected CA, SANs and held public key before
entering `LocalMachine/My`. A successful result includes
`binding: { location: "LocalMachine", store: "My", certificateId: "<SHA-256 DER>" }`.
This does not install CA trust, change MCP settings, start a service, or prove
that a running service can use the key. Those are separate deliberate steps.

Entry into the store write is recorded before the call. After possible entry,
the key is preserved even if the reply or readback fails. Before that boundary,
only the exact session's still-unbound key may be explicitly cleaned up.
Disposing a helper closes handles without deleting persisted material. Missing
helper reports remain unknown, with key/store flags omitted rather than false.
Reusing the RID cannot create another key or repeat UAC. Source and injected
fixtures do not qualify actual Windows key persistence or service-hosted TLS.

## File signing and evidence

Sign and verify additionally require `src`, `sha` and an existing registered
workspace, selected explicitly or through the host's registered context.
An absolute file's parent is never inferred as a signing root. Native admission
retains local path, ordinary-owner, no-reparse, file-identity, sharing and exact
preimage checks. Supported signing is one primary embedded RSA/SHA-256 PE or
registered PowerShell SIP signature; unavailable native capabilities or formats
remain explicit failures. Signing refuses an existing signature and creates no
timestamp. Files are never executed by these operations.

Verification is a completed observation: `ok: true` means the observation ran,
not that the signature is valid or trusted. Read the separate `signature`,
`trust`, `revocation`, signer ID and Windows status fields. Default Windows trust
evaluation is offline and revocation is not checked. A local uninstalled test CA
can produce a valid signature while trust remains `untrusted`.

## Request identity and interrupted work

Creation, imports, private export, store writes, issuance and signing require
an explicit RID. Its identity includes
the normalized action/body/defaults and canonical file/preimage when applicable.
The original request is validated before authority or internal metadata can be
projected away. File roots are frozen by the host, not accepted from callers.

Only the original live response contains public summaries, exports and native
results. Durable history and subsequent RID/job lookups retain safe references,
IDs and effect metadata; they omit subjects, public exports, request bodies,
source paths and all private material. Read operations create no durable job.

An in-flight or repeated mutation RID observes its original metadata rather
than issuing again. A changed request or cross-family RID collision is refused.
Detail pruning retains the identity barrier while its history row remains;
age/size expiration of completed history bounds that replay window. Restart with a lost queued/running body
produces `recovery_required`, never reconstructed creation or signing work.

An issued native or publication attempt can have an uncertain outcome. Preserve
the RID and reconcile the original reference or exact file; never automatically
retry it. A known observed postcondition remains distinct from an unknown
outcome, even if the operation returned an error. Neither is acknowledged,
rolled back or converted into a new attempt by this family.

System identities, LocalMachine writes, remote issuance, credential provisioning
and network timestamping remain separate capabilities. Availability of one
explicit operation never authorizes another change.
