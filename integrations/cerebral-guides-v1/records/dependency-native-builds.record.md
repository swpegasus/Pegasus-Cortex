# Cerebral Guide: Dependency Closure and Native Builds

Object: Knowledge.Cerebral.Guide.DependencyNativeBuilds
Version: 1.0.0
Owner: Pegasus Cerebral
Triggers: resolve Python/native dependencies, build CUDA or C++ extensions, or reconcile a protected platform provider
Required capabilities: exact source and metadata inspection, official package/source retrieval, architecture-aware resolution, and license review
Rendered budget: 4096 UTF-8 bytes

## Close dependencies before compilation

1. Inventory the exact base platform: OS, architecture, Python ABI, compiler, CUDA/toolchain, framework/provider distribution names, and installed package metadata.
2. Read every selected project and subproject's declared build and runtime requirements. Do not stop at the application's direct lock; native package metadata can add late requirements.
3. Resolve one combined plan against the real base. Record additions, upgrades, base-satisfied requirements, protected packages, filenames, hashes, tags, source revisions, submodules, licenses, and expected build targets.
4. Verify official provenance, actual wheel metadata, `Requires-Python`, platform tags, archive integrity, embedded name/version, and license terms. “Importable” and “declared distribution requirement satisfied” are different claims.
5. Detect provider-name mismatches early. A vendor distribution may supply the same import namespace as an upstream package without satisfying metadata by name. Do not install, shadow, relabel, or fake a provider; require an upstream metadata correction, an admitted compatibility package, or an explicit reviewed architecture decision.
6. Determine whether missing dependencies are exercised, optional, or only used by excluded preview/training/file-format paths. Do not remove a declaration solely to make `pip check` pass; prove the supported import/operation closure and preserve truthful metadata.
7. Stage exact binary wheels offline where available. For native sources, pin repository commits and submodules, force local builds, bound concurrency and resources, and record compiled architecture targets.
8. Run a dependency-plan/preflight check before expensive native compilation, then run the real installer and final dependency check inside the completed candidate image.

## Failure behavior

Stop at the first integrity, license, platform, provider, compile, import, ABI, or dependency-check failure. Preserve the exact stage and inputs. Do not weaken validation, introduce a floating nightly, substitute a different backend, or adopt transient artifacts from an uncommitted failed container step.

Compilation success proves only compilation. Real imports, CPU/GPU calls, model construction, service behavior, and artifact quality remain separate qualification lanes.

This guide authorizes no dependency download, license acceptance, package install, metadata patch, or provider replacement.
