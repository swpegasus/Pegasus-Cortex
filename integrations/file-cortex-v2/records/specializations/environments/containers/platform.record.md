# Container Environment Tenets

These tenets specialize `BUILD-*`, `DEP-*`, `SEC-*`, `REL-*`, and `PORT-*` for container images and runtimes.

## CONTAINER-001 — Separate immutable image and durable state

**Rule:** Treat the image and writable container filesystem as replaceable; keep required persistence in explicit owned volumes or external stores.

**Practice:** Define migration, backup, permissions, cleanup, and recovery for each persistent mount.

**Boundary:** Deliberate ephemeral work may remain inside the container when loss is acceptable.

## CONTAINER-002 — Make the build context intentional

**Rule:** Include only required source and build inputs; exclude secrets, local caches, unrelated repositories, and developer artifacts.

**Practice:** Use reproducible stages and record the exact base-image identity.

**Boundary:** Development containers may include extra tools, but should remain distinct from release images.

## CONTAINER-003 — Never bake secrets into layers

**Rule:** Build arguments, copied files, package credentials, and deleted intermediate files may remain recoverable from image history.

**Practice:** Use runtime secret mechanisms or ephemeral build-secret mounts and verify published layers.

**Boundary:** Public non-sensitive build metadata is not a secret and may be embedded deliberately.

## CONTAINER-004 — Minimize runtime privilege

**Rule:** Prefer a non-root user, narrow capabilities, bounded mounts, read-only filesystems where practical, and no host control socket.

**Practice:** Grant only the devices, networks, files, and kernel capabilities required by the container's role.

**Boundary:** Required privilege must be explicit, justified, and isolated from unrelated workload.

## CONTAINER-005 — Design PID 1 and shutdown behavior

**Rule:** The container entry process must forward signals, reap children, stop intake, and complete bounded cleanup.

**Practice:** Distinguish process running, application ready, application healthy, and shutdown complete.

**Boundary:** A suitable init wrapper may own reaping and forwarding when the application cannot.

## CONTAINER-006 — Make network and volume contracts explicit

**Rule:** Ingress, egress, ports, names, service identity, volumes, ownership, and cleanup are deployment contracts.

**Practice:** Treat declaring a port separately from publishing it and constrain outbound access by need.

**Boundary:** Internal-only networks still require authentication and authorization where peers are not fully trusted.

## CONTAINER-007 — Identify architecture and image content exactly

**Rule:** Tags are mutable labels; releases should use digests or equivalent immutable identity and declare supported architectures.

**Practice:** Verify the selected platform image, native libraries, entrypoint, and package contents.

**Boundary:** Human-friendly tags remain useful when resolved identity is captured for deployment evidence.

## CONTAINER-008 — Bound runtime resources and logs

**Rule:** CPU, memory, process count, storage, temporary space, network, and logging require explicit limits and overload behavior.

**Practice:** Test the application's response to enforced limits and make eviction or restart semantics visible.

**Boundary:** Runtime limits complement rather than replace application-level backpressure and cleanup.
