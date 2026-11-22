# Cerebral Guide: Docker Build and Image Admission

Object: Knowledge.Cerebral.Guide.ContainerBuildAdmission
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: build, rebuild, inspect, retain, tag, or admit a Docker or OCI image
Required capabilities: exact-path artifact reads, hashing, Docker inspection/build, and an authorized build host
Rendered budget: 4096 UTF-8 bytes

## Goal

Produce one reproducible immutable image while keeping build authority, dependencies, secrets, resources, and qualification claims explicit.

## Admission workflow

1. Resolve the exact source generation, manifest, base digest, target platform, build file, context root, dependency locks, and expected customization seal.
2. Inventory the actual host architecture, container engine, compiler/runtime prerequisites, free space, current GPU or service owners, and selected network route. Historical observations are not current admission.
3. Construct a minimal context. Exclude credentials, model weights, local state, unrelated repositories, caches, private keys, and control sockets.
4. Verify every staged file, lock, and wheel against its admitted length/hash and embedded identity before the expensive build begins. Parse native projects' declared requirements before compilation.
5. Use an exact base digest and explicit platform. Disable floating pulls and opportunistic dependency resolution. Give the candidate a unique task-owned tag, but capture the resulting immutable image ID separately.
6. Enforce wall-time, incremental-storage, free-space-floor, compiler-concurrency, and bounded rolling-log limits against the actual build operation. A client timeout alone is not proof the daemon stopped.
7. Build with only the network access genuinely required by pinned source retrieval. No credential, host socket, model-data mount, service activation, or unrelated host mutation belongs in the image build.
8. After success, inspect the image ID, architecture, ancestry, labels, entrypoint, non-root user, package identities, history, and size. Run required dependency checks inside the final image.

## Identity and qualification

Tags are convenient names, not deployment identity. Bind source, base digest, locks, build profile, customization seal, image ID, and receipt in one final record. A successful build remains unqualified until the exact image passes the required native-import, hardware, model, operation, cleanup, and client acceptance lanes.

## Failure handling

Preserve the first concrete error, build-history reference, bounded log, resource observations, and exact input identities. Do not bypass a failed guard, silently add packages, substitute a protected provider, or globally prune evidence. A failed Docker `RUN` does not produce a reusable layer; transient files from that step are not admitted artifacts. Retry only from a newly reviewed source/lock/profile identity when the cause is corrected.

This guide grants no Docker access and authorizes no build, pull, prune, deployment, or external mutation.
