# Git Tenets

These tenets specialize `VCS-*`, `CORE-WORK-006`, `CORE-AUTH-008`, and `REVIEW-003` for Git's index, worktree, branch, and remote mechanics.

## GIT-001 — Inspect Git's three states deliberately

**Rule:** Distinguish committed history, staged index, and working tree before changing, staging, committing, or discarding anything.

**Practice:** Review status, unstaged diff, staged diff, untracked files, and branch identity relevant to the task.

**Boundary:** A clean status does not prove generated or ignored external state is current.

## GIT-002 — Distinguish Git restore sources and targets

**Rule:** A Git restore or discard operation must identify whether content comes from the index, committed history, or another revision and whether it changes the index, working tree, or both.

**Practice:** Inspect the affected state and select exact paths or hunks rather than applying a broad command to obtain a convenient baseline.

**Boundary:** `CORE-WORK-006` still governs ownership; knowing Git mechanics does not authorize discarding another workstream.

## GIT-003 — Stage only intended content

**Rule:** The index should contain exactly the coherent change intended for the next commit or handoff.

**Practice:** Stage paths or hunks deliberately and review the staged diff before committing.

**Boundary:** Bulk staging is acceptable after confirming the entire working tree is one owned change.

## GIT-004 — Keep commits coherent and reversible

**Rule:** A Git commit should apply `VCS-001` while remaining a valid parent for the branch operations and review workflow that follow.

**Practice:** Review the staged tree, parent revision, commit metadata, and any required build or migration evidence before recording it.

**Boundary:** Commit creation itself requires workflow or user authority.

## GIT-005 — Preserve shared history

**Rule:** Apply `VCS-002` before amend, rebase, branch deletion, or force-update of Git history that may be published or consumed.

**Practice:** Prefer a revert when shared audit history must remain intact.

**Boundary:** Private unpublished branches may be rewritten when their owner and workflow allow it.

## GIT-006 — Resolve conflicts semantically

**Rule:** During a Git merge, rebase, or cherry-pick, apply `VCS-003` to both textual conflicts and cleanly merged files affected by the combined changes.

**Practice:** Re-run focused verification and inspect contracts, generated files, and migrations after resolution.

**Boundary:** Generated artifacts should usually be regenerated from resolved source rather than hand-merged.

## GIT-007 — Validate remote mutations

**Rule:** Confirm repository, remote, outgoing commits, source and destination refs, remote-tracking state, permissions, policy, and the proposed ref change before push, tag, merge, or release operations.

**Practice:** Treat remote mutation and publication as separate authority from local editing.

**Boundary:** Read-only fetch and inspection do not grant permission to update remote state.

## GIT-008 — Treat a committed secret as compromised

**Rule:** Deleting a secret or rewriting version-control history does not revoke credentials that entered reachable or shared history.

**Practice:** Revoke or rotate the secret first, then remove it from current content and follow the governed shared-history process in `GIT-005` when historical removal is required.

**Boundary:** `CORE-AUTH-004` governs secret handling and `CORE-WORK-002` governs generated artifacts; Git cleanup is containment, not credential recovery.
