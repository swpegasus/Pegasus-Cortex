# Knowledge.Windows

Object: Knowledge.Windows
Version: 1.0.0
Purpose: route agents to the smallest Windows-specific object that materially affects the current operation
Rendered budget: 4096 UTF-8 bytes

## Existing foundations

- tenet.specializations-platforms-windows-platform — scopes, elevation, paths, ACLs, locks, precise process identity, SCM service semantics, restarts, and executable resolution.
- tenet.specializations-platforms-windows-registry — hive/view/type/ownership/policy/rollback behavior.
- tenet.core-workspace-artifacts-and-path-safety — containment, reparse boundaries, atomic replacement, and reversible workspace migration.
- tenet.disciplines-release-deployment-and-rollback — artifact identity, signing order, public trust, explicit trust installation, and release verification.
- Knowledge.Windows.FileTransfer.Robocopy — resilient substantial directory-tree transfer.

## Operational objects

- Knowledge.Windows.Identity.SIDAndProfileScope
- Knowledge.Windows.IPC.NamedPipes
- Knowledge.Windows.Process.JobObjects
- Knowledge.Windows.Service.UserSessionBoundary
- Knowledge.Windows.Installer.Maintenance
- Knowledge.Windows.Diagnostics.EventLog

## Retrieval rule

Search previews select an object. Exact-read the selected active revision before relying on its full operational contract. Prefer current Windows APIs and live capability/identity checks over remembered version assumptions.

## Boundary

These records explain Windows behavior. They do not grant administrative rights, authorize service/registry/trust changes, or replace current product contracts and live machine evidence.
