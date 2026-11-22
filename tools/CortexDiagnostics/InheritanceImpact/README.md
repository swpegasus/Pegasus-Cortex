# HELD inheritance-impact diagnostic source

Status: HELD for Root/peer source review. Not compiled, tested, loaded, invoked or integrated.
This packet grants no execution, repair, restart, installation or security authority.

## Contents and integration

- AssessInheritanceImpact.cs: one-purpose aggregate-only library proposal.
- InheritanceImpact.csproj: Windows .NET 10 library; no entry point, package references,
  project references, production registration or solution integration.
- source-packet.json: source pins, fixed limits, review boundaries and integration list.

No existing product file, old preimage, installer, namespace, service, profile or store is changed.
The public Assess() method deliberately returns Incomplete/HeldForRootReview without native I/O.
The proposed traversal is private, unreachable from that surface. Do not invoke it by reflection.
A separately reviewed source/invoker decision is required before any compilation or invocation.

## Fixed scope and selected source parameters

The scope is only the previously identified Cortex execution-layout owner-state root:
`C:\Program Files\Pegasus\Cortex\state\users\<current Windows SID>`.
The prior verified SID is bound by SHA-256, not a persisted personal name or SID literal;
the caller must also be ordinary, non-Administrator, in the previously verified session 1.
No root override, ancestor enumeration, drive discovery, registry or DB/API lookup is present.
The only independently targeted directories are the root and execution/logs/tray children.

Limits: 8192 total encountered entries, 512 queued directories, depth 32, 15-second
COOPERATIVE budget. The root and dot entries consume the entry budget.
Enumeration uses a fixed 1024-byte FileIdBothDirectoryInfo batch. Before a native call,
the core reserves the conservative maximum of nine entries; it charges every returned
entry before filtering or retention. It can stop short of 8192 when a whole batch
cannot fit. On a malformed/failed batch, the reserved count remains an upper bound.

## Native admission and data handling

Root admission checks the fixed normalized volume-GUID path suffix and NTFS metadata.
An initial root identity is held for this observation; no prior physical root-ID baseline
was captured. This is not historical identity or custody proof.
Children open one component relative to an admitted retained directory handle via NtOpenFile,
with open-reparse-point and expected directory/non-directory options. Metadata-only access is
READ_CONTROL + FILE_READ_ATTRIBUTES + SYNCHRONIZE; directories additionally need LIST_DIRECTORY.
No file-data read access is requested for ordinary files.
Final-path, volume/file ID, creation time, type, owner and reparse checks precede DACL use/descent.
Multi-link ordinary files stop with IdentityAlias. Unsupported native/security data fails closed.
Protected directory DACLs stop propagation traversal, except the four explicit startup targets.

Paths, names, SIDs and native security descriptors are transient only. No file body, SDDL,
private filename, message, exception text or per-object identity leaves the aggregate DTO.
Security buffers are discarded/cleared; only transient digests support drift checks.
There are no setters, writers, snapshots, baseline capture, recursive filesystem operations,
process starts, privileges, external services or package dependencies.

## Honest limits requiring review

- A synchronous native operation can outlast the stopwatch. Fifteen seconds is an admission/
  continuation budget, NOT a hard wall-clock guarantee, permission to abandon ownership, or kill.
  It is one reason no callable native entry is enabled.
- CompleteObservedTraversal means the bounded observed namespace was exhausted under the
  admitted handles. It is NOT an atomic namespace seal, absence-of-concurrency proof, a
  rollback baseline, propagation authorization or ACL repair approval. Later creation/rename,
  changes after an object was inspected and other components' access needs remain custody issues.
- Native relative-open ABI/options, FileIdBothDirectoryInfo offsets/batch accounting,
  exception/cleanup behavior and fixed caller/root binding need independent review.
  No compile, deterministic fixture, native fixture or real-state runtime evidence is claimed.
- Future qualification needs its own scope; do not silently rebind this source to another root,
  relax admission or run against personal stores to obtain a pass.

The metadata/final-path/SafeFileHandle layout follows the narrow pattern in the existing
CortexLocationPathIdentity.cs (source pin d58000359d5585f786b861c7d0ef10614bbe9bafcd232a06e090d4b46c5b63e5).
The relative-open and handle-enumeration portions are NEW and unqualified.

## Primary references

- https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntopenfile
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_id_both_dir_information
- https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo
- https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces

Source packet only. The old exact backport and forward-source release selection remain separate.
