# Cerebral Guide: Secret-Safe Interactive Provisioning

Object: Knowledge.Cerebral.Guide.SecretProvisioning
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: a one-time provisioning operation requires a human credential to fetch exact private or gated data
Required capabilities: reviewed task-scoped client, private interactive TTY, exact destination ownership, and authorized data access
Rendered budget: 4096 UTF-8 bytes

## Credential boundary

Use a private interactive non-echo prompt owned by the reviewed provisioning program. The human enters the minimum scoped credential directly. Never place it in chat, tool arguments, environment variables, shell history, source, image layers, build arguments, screenshots, logs, manifests, or a persistent token store. Suppress SDK exception bodies and signed URLs that may contain authorization material.

Verify the authenticated account or service identity without printing the secret. Clear in-process references after use; process exit is part of cleanup. Do not pass provisioning credentials into the runtime service or model child.

## Data admission

1. Freeze repositories, revisions, selected filenames, expected sizes/hashes, license decisions, and total budget before prompting.
2. Use the reviewed exact SDK/client and endpoint. Fetch metadata first, then only admitted data files. Do not execute remote code, repository scripts, pickle payloads, or caller-selected URLs/classes.
3. Run CPU-only without GPU injection unless byte admission genuinely requires hardware. Bound time, memory, processes, cache, file count, and total bytes.
4. Mount only a separate empty model/data provisioning parent. Never expose the whole future service root containing state, keys, TLS material, jobs, or logs.
5. Download into a task-owned private cache, stream-copy while hashing into a separate verified stage, and validate configuration syntax and closed inventory.
6. Promote only through an absent-destination, same-filesystem, no-overwrite rename while the consuming service is stopped. Record model/data and license-lock hashes.
7. Remove only the task's private download cache after confirmed success. Preserve exact staging for inspection after failure. If visibility may have changed but durability/confirmation failed, report reconciliation required and do not replay or overwrite.

## Access and licensing

A working credential does not prove usage rights, acceptance of new terms, export eligibility, or permission for another repository. Keep access, license, data integrity, model compatibility, and runtime qualification as separate gates.

This guide contains no credential and authorizes no login, download, terms acceptance, promotion, deletion, or service mutation.
