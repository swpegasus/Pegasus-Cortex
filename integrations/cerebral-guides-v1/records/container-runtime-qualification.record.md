# Cerebral Guide: Container Runtime and GPU Qualification

Object: Knowledge.Cerebral.Guide.ContainerRuntimeQualification
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: probe a built image, test native imports or GPU kernels, qualify a model operation, or inspect cleanup
Required capabilities: immutable image resolution, exact-container lifecycle control, bounded resource observation, and authorized hardware access
Rendered budget: 4096 UTF-8 bytes

## Separate the evidence lanes

Treat these as distinct outcomes: image exists; controller starts; dependency metadata is consistent; native modules import; a real CPU path runs; a real GPU kernel runs; model bytes load; one operation completes; artifacts validate; cancellation cleans up; private clients reach the service. Passing one lane does not imply another.

## Probe contract

1. Resolve the immutable image ID and recheck source, build, provider, and package identities before execution.
2. Recheck current GPU ownership, competing workloads, free memory/storage, driver visibility, and authorized operation scope. Do not stop unrelated work automatically.
3. Create one uniquely named container and record its exact ID before start. Inspect its image, user, command, mounts, labels, runtime, network, devices, capabilities, and limits.
4. Use non-root execution, read-only root, dropped capabilities, `no-new-privileges`, bounded CPUs, memory, PIDs, temporary space, cache, output, and timeout. Mount only the exact read-only or task-owned paths needed by the lane.
5. Use `network=none` for offline qualification. Select a GPU runtime and devices only for a lane that requires them; CPU/storage probes should explicitly use the ordinary runtime with no GPU injection.
6. Exercise concrete imports and operations, not only device visibility or a tiny allocation. Compare numerical results to an independent reference where feasible and capture peak resource use.
7. Stream bounded diagnostics, preserve the first failing stage, and stop on any integrity, import, ABI, kernel, timeout, or cleanup failure. Do not switch backends, pins, providers, or precision silently.
8. Admit the same exact container after exit, capture its bounded logs, and remove only that container when cleanup is confirmed. Verify no owned process, GPU context, mount, or listener remains.

## Promotion boundary

Record the exact image/model/input/profile hashes and the evidence level for each operation. Keep `hardwareQualified`, `modelQualified`, and supported-operation claims false until their real lanes pass. A model unload means the exact owned child or container exited and its resources were observed released; an allocator cache flush is not unload.

This guide grants no GPU, Docker, model, or service authority and does not authorize stopping another workload.
