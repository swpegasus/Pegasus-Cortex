# Knowledge.Windows.IPC.NamedPipes

Object: Knowledge.Windows.IPC.NamedPipes
Version: 1.0.0
Triggers: local process/service communication, privileged broker, user-host connection, application-session pipe
Rendered budget: 5120 UTF-8 bytes

## Admission

Create every pipe with an explicit security descriptor. Grant only the intended service, user, logon SID, or administrative principals the minimum rights required. Default named-pipe security can be broader than a local product contract expects. For a local-only pipe, deny network-origin access or use an explicitly local transport boundary.

Authenticate the peer after connection using the contract's required combination of token identity, logon/session SID, process ID, canonical executable path, process start identity, product/version protocol, and publisher identity. A pipe name is not authentication.

## Protocol

Use a versioned fixed operation schema, bounded frames, message/count/time limits, cancellation, and explicit error taxonomy. Never turn a privileged pipe into a generic command or arbitrary script transport. Validate authorization for every operation after authentication.

If impersonation is used, check that impersonation succeeded before any protected access and always revert. Failed impersonation must fail closed rather than continuing as the privileged server.

## Lifecycle

Handle connect/disconnect races, duplicate clients, half-written frames, server restarts, stale generations, and bounded backpressure. Bind retries to the same logical request only when idempotency is guaranteed. Do not retain secrets or unbounded pipe payloads in logs.

## Diagnostics

Expose protocol version, peer identity class, connection state, and bounded failure reason without exposing tokens, credentials, private data, or raw payloads.

References:
- https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights
- https://learn.microsoft.com/windows/win32/ipc/named-pipes
- https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-impersonatenamedpipeclient
