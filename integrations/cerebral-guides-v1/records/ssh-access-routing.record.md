# Cerebral Guide: SSH Access and Route Pinning

Object: Knowledge.Cerebral.Guide.SshAccessRouting
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: inspect or use an existing SSH route, transfer artifacts, or run bounded remote commands
Required capabilities: authorized nonprivileged remote identity, verified host identity, and an SSH client
Rendered budget: 4096 UTF-8 bytes

## Establish the route

1. Confirm that the user authorized the target host and requested operation. Possessing a key or reachable socket does not broaden authority.
2. Resolve the current hostname or IP, nonprivileged username, intended interface/path, local alias, identity-file name, and known-host policy. Report paths and fingerprints without exposing private-key bytes, passwords, tokens, or agent contents.
3. Verify the host-key algorithm and fingerprint through an independent trusted channel before first use or after a change. Do not use `StrictHostKeyChecking=no`, discard known-host errors, or accept a changed key by convenience.
4. On multi-homed systems, pin the alias to the intended address so management or bulk transfer uses the approved direct route rather than whichever DNS/default route happens to win. Verify the effective peer and source route live.
5. Prefer `BatchMode=yes` for automation. Keep each remote operation bounded, noninteractive unless a human-only secret prompt is intentionally required, and constructed as separate arguments or a reviewed fixed script.

## Remote work

- Resolve exact remote paths and ownership before writes. Use a task-owned staging directory and refuse unintended collisions.
- Hash artifacts before and after transfer. An SSH success exit proves transport, not semantic admission of the received file.
- Preserve remote command exit status and bounded output. A disconnected client may leave remote work uncertain; reconcile the exact process, job, or receipt before retry.
- Use SFTP/SCP only for admitted files. Never copy credential stores, shell history, unrelated home content, or private state into an agent workspace.
- Tunnels, forwarding, agent forwarding, port publication, account creation, key installation, permission changes, and SSH service changes are separate security decisions, not normal connection steps.

## Failure boundary

Differentiate route failure, host-key mismatch, authentication rejection, authorization rejection, remote command failure, transfer-integrity failure, and uncertain disconnect. Do not “fix” access by broadening accounts, keys, firewall rules, routes, or permissions without exact authorization.

This guide is passive and contains no reusable hostnames, addresses, usernames, or credentials.
