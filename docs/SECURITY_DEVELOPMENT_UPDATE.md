# Security development update

This is the security change set for development release 2.2026.11.22.03.54.A,
following the reviewed repository findings. The matching signed update was
installed and its authenticated Codex shared connection verified. Existing
22.34 clients need the [new connection setup](MCP_CONNECTION_SETUP.md) after
upgrading.

## Remembered connections replace root codes

Every shared MCP request now presents a separately approved persistent credential. Network inclusion and TLS alone do not approve a connection. A random 256-bit client credential stays in the current Windows user's Credential Manager; protected machine metadata stores its digest, readable name, public ID, generation and approval revision. Neither a chat label nor a session ID is an authentication credential.

Ordinary access and Full control are distinct approvals. Both survive reconnects until changed. Revoke Full control retains ordinary access; revoke all access denies existing sessions and reconnects. Every chat using the same credential shares its approval. The tray provides full-ID copying, explicit revocation and a separate red Full control warning. The former code enrollment, verifier and code editor are removed; historical files are preserved but never loaded as active grants.

The client configuration contains an HTTPS URL and a public-ID header-helper command, never a bearer value. Setup preserves that helper across upgrades. Captured process and terminal environments mark their output channel, and the known credential-header helper refuses that channel. These controls prevent accidental credential capture; the Windows owner remains the local trust boundary.

## Protected execution retains explicit boundaries

Boolean SYSTEM intent does not grant authority. The service validates the actual registered owner Host and current connection approval before protected work, after queue waits and immediately before owner-admin process creation. Revocation prevents new protected effects; it does not undo an already started operation.

Cerebral's existing private application grant remains separate. Registered User tools execute through the ordinary owner Host; a LocalSystem process cannot qualify by impersonating a user thread. Fixed protected operations retain their existing narrow contracts. The private shared-MCP relay is rejected at public broker entry points and in ordinary queued jobs.

## Other reviewed findings

| Area | Change |
| --- | --- |
| NSIS dependency provenance | Retain exact pinned-archive and complete-tree admission before copying or publisher signing. Compiler-only cache admission is not sufficient. |
| Offline file context | Hold ancestor and file identities through relative reads, locks, writes and publication. Directory replacements cannot redirect an admitted operation outside its root. |
| Standalone archive tool | Use the same rooted I/O primitives for extraction, inventory, publication and identity-checked cleanup of owned entries. |
| Archive path budgets | Check strict UTF-8 segment lengths and final plus staging-path budgets before cleaning or creating destinations. |
| Task participant labels | Retain caller-declared labels separately from authenticated connection provenance. Public write schemas cannot manufacture verified source metadata. |
| Semantic content admission | Reject recognized prohibited secret, raw-stream and private-reasoning content before initialization, idempotency claims or batch writes; preserve legitimate code and placeholder examples. Existing data is not silently scanned or deleted. |
| Metadata allocation | Enforce size limits on the actual opened stream before allocation, read exactly and reject trailing data. Keep existing signature, ACL and path checks. |

On Windows, file replacement also denies competing writes, verifies the destination identity after metadata capture and preserves an admitted target's DACL and basic metadata. This is directory confinement and ordinary conflict detection, not atomic same-name compare-and-swap against arbitrary native races by the same Windows owner. Unix source paths remain fail-closed but were not executed in this Windows verification.

## Verification

Completed focused checks include 32 neutral identity/lifecycle checks over real loopback HTTP, 112 checks using the actual Cortex credential verifier over loopback HTTP, 189 injected primary-token and credential-output checks, protected approval/revocation and private-relay checks, and the replacement tray-page fixture. No real client credential or machine approval was created by those tests.

Broader passing suites include Core 36/36, MCP protocol 57/57, SQLite V2 23/23, file storage 21/21, standalone archive 27/27, ZIP 31/31 and 7-Zip 16/16. The NSIS provenance fixture passed 36 checks without signing or executing synthetic binaries. Scheduled-task checks used an injected Scheduler and owned SQLite; service-control checks used an injected SCM. The 7-Zip suite also exercised the existing installed executable on owned test data.

Windows junction and ordinary-directory swap cases ran. Symbolic-link capability probes explicitly skipped when link creation was unavailable. The 03.54 product and installer were prepared through the normal all-role signing pipeline, then passed deployment-plan and tamper/signing checks. The installed update preserved database file identities, endpoint settings and startup choices. Forty-seven real HTTPS assertions covered ordinary reconnects, Full control across reconnects, immediate Full-control revocation and all-access revocation. The actual Codex connector then verified the installed version, retrieved context, used ordinary PAO and saved an authenticated checkpoint. The independent security-cloud scan has not been rerun for this release.

The real ConPTY suite also passed 134 checks on owned local terminals. It uncovered and corrected inherited Ctrl+C suppression before shell creation: the prompt returned in 95 ms after interruption, with the follow-up command observed by 190 ms, instead of waiting for the full 15-second command. Reconnect kept the same process and prepared state, and all owned terminal processes were closed and released. No remote or installed terminal was used for that qualification.

See [shared MCP onboarding](CORTEX_SHARED_MCP.md) and [source build and signing](BUILD_FROM_SOURCE.md).
