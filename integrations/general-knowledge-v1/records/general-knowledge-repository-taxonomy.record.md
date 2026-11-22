Pegasus Cortex uses one central per-user SQLite repository as the authoritative local knowledge and task store. Visible names are base-first, like an object path. Logical organization follows these durable roles:

1. knowledge.shared is the canonical cross-agent geometric space for generalized software and non-software knowledge. Subjects are expressed through record kind, title, content, and bounded topic tags rather than topic namespaces.
2. knowledge.user is the local overlay for durable preferences, corrections, interaction style, and user tendencies. It can be explicitly composed with knowledge.shared, but it must not ship in a starter corpus or become a universal rule.
3. project.<project-id> contains agent tasks, product-local decisions, current implementation facts, release evidence, incidents, and other material whose meaning depends on that project.
4. acceptance.* and validation.* contain isolated test or release evidence and are excluded from ordinary startup retrieval unless the current task is specifically investigating that evidence.

Routing rule: generalize a durable conclusion when it remains correct after removing project names, machine paths, task IDs, transient versions, and local state. Store it in knowledge.shared with topic tags. Keep project-specific evidence in its project namespace and personal behavior guidance in knowledge.user. Select only the spaces that can affect the current decision; excluded spaces must not influence ranking indirectly.

Deduplication rule: maintain one active canonical head for each durable idea. Revise the canonical head when meaning evolves; supersede or archive redundant active copies after exact readback of the replacement. Preserve provenance and immutable revision history. Never import raw chats, private reasoning, credentials, unbounded logs, or bulk legacy trees as general knowledge.

Compatibility rule: shared.general and shared.programming are one-hop inputs for knowledge.shared; user.preferences is a one-hop input for knowledge.user. New writes and visible receipts use the canonical base-first names. A bounded backup-first conversion may copy exact active heads, archive the legacy heads, and leave project namespaces unchanged.
