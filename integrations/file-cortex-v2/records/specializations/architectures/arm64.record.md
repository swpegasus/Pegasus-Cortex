# ARM64 Portability

These tenets specialize `CORE-MOD-001`, `CORE-MOD-007`, `CORE-VERIFY-004`, `CORE-FAIL-005`, `PORT-*`, `PERF-*`, and `RELEASE-*` for architecture portability and native boundaries.

## ARM64-001 — Distinguish OS, process, and target architecture

**Rule:** Detect the operating system architecture, current process architecture, and intended build or package architecture independently.

**Practice:** Use platform APIs and explicit build metadata rather than inferring from folder names or the development host.

**Boundary:** An x64 process can run on an ARM64 OS through emulation and must be reported as emulated, not native.

## ARM64-002 — Prove the ARM64 runtime dependency closure

**Rule:** Architecture-neutral source supports ARM64 only when its runtime, native transitive dependencies, host tools, and deployment model all support the target.

**Practice:** Apply `PORT-002`, inventory the complete runtime dependency closure, and isolate unsupported native or device behavior behind focused adapters.

**Boundary:** Architecture-neutral source still depends on the runtime and every native transitive dependency supporting the target.

## ARM64-003 — Select native dependencies explicitly

**Rule:** Package the exact native library or executable required for each supported architecture.

**Practice:** Validate loader resolution, dependency closure, signing, and architecture metadata; keep outputs from different targets distinct.

**Boundary:** Filename similarity or a “universal” package label is not proof that every contained binary supports ARM64.

## ARM64-004 — Validate ABI boundaries

**Rule:** Native interoperability must account for calling convention, pointer size, structure layout, alignment, endianness assumptions, and ownership.

**Practice:** Use fixed-width types where appropriate and test representative calls on native hardware or an accepted native environment.

**Boundary:** Successful compilation does not prove ABI correctness or safe memory behavior.

## ARM64-005 — Avoid x64-specific system assumptions

**Rule:** Do not hard-code x64 executable paths, registry views, package identifiers, tool locations, or process names.

**Practice:** Resolve capabilities and architecture-specific assets through explicit configuration or platform discovery.

**Boundary:** Compatibility layers can make an x64 path work temporarily without making it a valid native design.

## ARM64-006 — Separate cross-build, emulation, and native evidence

**Rule:** For ARM64 targets, native evidence must cover architecture-specific loaders, native dependencies, ABI boundaries, and runtime behavior that portable or emulated checks cannot establish.

**Practice:** Apply `PORT-009`, label each result, and require the appropriate native evidence before claiming native ARM64 support.

**Boundary:** Native smoke tests may still be insufficient for performance, device, driver, or long-running behavior.

## ARM64-007 — Keep data and protocols portable

**Rule:** Persisted and transmitted formats should define byte order, widths, alignment, versions, and encoding independently of in-memory native structures.

**Practice:** Serialize through explicit schemas and verify cross-architecture round trips.

**Boundary:** Dumping native structs or pointers is not a portable format.

## ARM64-008 — Detect instruction features at runtime

**Rule:** An ARM64 optimization that requires optional instruction features must select it from verified runtime capability rather than architecture name alone.

**Practice:** Apply `PERF-003`, keep the optimized path behind tested feature detection, and retain a correct portable fallback.

**Boundary:** Building for ARM64 does not prove that every ARM64 processor or emulation environment supports the same optional extensions.

## ARM64-009 — Validate packaging and installation separately

**Rule:** Before claiming ARM64 lifecycle support, verify every packaging, installation, launch, update, rollback, and removal stage that applies to the product.

**Practice:** Verify architecture selection, dependencies, permissions, service definitions, and rollback on the target platform.

**Boundary:** Libraries, SDKs, compile-only targets, and portable artifacts may have a narrower applicable lifecycle; do not claim stages that were not exercised.
