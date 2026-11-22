# HELD inheritance-impact diagnostic source R2

Status: additive R2 source awaiting Root and peer review. Not compiled, tested,
loaded, invoked or integrated. Public Assess() still returns
Incomplete/HeldForRootReview without native I/O. No execution-enable flag or CLI
entry point exists. Do not invoke the private proposal by reflection.

## Purpose and preserved predecessor

R2 addresses exactly the two P2 root-admission findings in the consolidated peer
final msg_06065387e55a6fde016af55674617c87d0a7ea667be0038e02:

1. Enforce a directory-only native open and type/reparse/identity admission before
   owner or DACL queries. The old full-path CreateFileW root open is absent in R2.
   The LIST_DIRECTORY access bit can be requested only with FILE_DIRECTORY_FILE;
   an unexpected regular file therefore cannot be admitted with FILE_READ_DATA.
2. Bind the proposal to the actual current C: volume and the exact fixed ancestor
   chain, using retained directory handles and relative one-component opens.
   A matching suffix on an arbitrary volume is no longer an admission rule.

The four predecessor files in the parent folder are preserved byte-for-byte.
Their names, sizes and SHA-256 pins are recorded in source-packet.json.
This is one successor for source review, not a selected product or release input.

## Integration

Only these four new files under R2 are involved: AssessInheritanceImpact.cs,
InheritanceImpact.csproj, README.md and source-packet.json. The project is a
Windows .NET 10 library with a distinct R2 assembly/namespace, no package or
project references, and no solution, product or registration integration.
There are no existing-product, installer, installed-state or machine changes.

## Fixed scope and ancestor metadata admission

The only target remains the previously identified owner-state root beneath
C:\Program Files\Pegasus\Cortex\state\users and its current Windows SID component.
The prior verified SID is matched by SHA-256, never stored here as a SID literal.
The caller must be that ordinary non-Administrator Windows owner in session 1.
No override, drive search, registry lookup, DB/profile/API lookup or enumeration
outside that target is present.

The proposed acquisition obtains the current C:\ mount's volume GUID with
GetVolumeNameForVolumeMountPointW, then opens that volume root as a directory.
It admits only these exact components in order: Program Files, Pegasus, Cortex,
state, users, and the current owner SID. Each component opens relative to the
retained admitted parent with FILE_DIRECTORY_FILE and FILE_OPEN_REPARSE_POINT.
Reparse/device attributes, non-directory type, invalid file identity, another
volume or an unexpected final physical path stop Incomplete before security use.

The six outside-target ancestor directories (volume root plus five components)
request only FILE_READ_ATTRIBUTES, FILE_TRAVERSE and SYNCHRONIZE. They are not
enumerated and their owners/DACLs are not queried. Their handles remain retained;
current C: volume mapping, physical path, directory type, reparse state, volume,
file ID and creation time are revalidated before target security inspection,
before each queued-directory traversal and before proposed completion. Ancestor
last-write changes alone are not a historical identity or namespace-seal claim.

The owner-state root requests READ_CONTROL, FILE_READ_ATTRIBUTES,
LIST_DIRECTORY and SYNCHRONIZE through a directory-only open. Root and child
observations check expected type before owner/DACL use. Child files request no
FILE_READ_DATA; their metadata access and relative no-follow pattern are unchanged.

These are observations of the current mount/objects. No historical root-ID,
historical C: mapping, custody, or original-install identity baseline is claimed.
Native ABI, mount/final-path behavior and cleanup still require qualification.

## Bounds, output and failure behavior

Numeric limits remain 8192 total encountered entries, 512 queued directories,
depth 32, and a 15-second COOPERATIVE budget. The six admitted ancestors and the
target root now consume seven entries of that same 8192 budget. The separate
AncestorDirectoriesAdmitted counter excludes the owner-state root and reports
only successful ancestor admission; target security counters exclude ancestors.

Enumeration remains a fixed 1024-byte FileIdBothDirectoryInfo batch. Reserve up
to nine entries before every query; charge every returned entry before filtering
or retention, including dot/rejected entries. Failed or malformed batches retain
the conservative reserved charge. Stop rather than silently page or resume when
limits prevent continuation. All retained handles are disposed on return/failure.
The proposed native cleanup itself is unexecuted and unqualified.

The independent startup targets remain root, execution, logs and tray. Protected
directory boundaries stop descent except those four explicit targets. Unsupported
native/security records, access failures, multi-link files, owner mismatch and
observed identity/ACL drift stop Incomplete. No authorization or validation has
been relaxed to obtain completion.

Only aggregate counts and Complete/Incomplete/reason leave the DTO: no private
names, paths, SIDs, per-object identifiers, SDDL, content, native error text or
exception messages. Paths, identity and security data stay transient; descriptor
buffers are cleared after digesting. There are no file-body reads, snapshots,
rollback baselines, security setters, process/service starts or repair actions.

A synchronous native call or cleanup may outlast the stopwatch. The 15 seconds
are admission/continuation checks, not a hard wall-clock bound or permission to
abandon ownership or kill a process. CompleteObservedTraversal means exhaustion
of the bounded observed namespace under admitted handles; not atomic sealing,
absence of later change, custody proof, rollback proof or repair authorization.

## Evidence and next gate

Evidence in this packet is source-text readback and SHA-256 hashing only. R1's
consolidated peer review is incorporated; R2 independent review is pending.
No compiler, SDK, tests, native APIs, target traversal, registry, private store,
host/service action, security repair, installation or release selection ran.

Next: Root and peer review these exact R2 source pins, including the new native
root/ancestor acquisition, resource ownership, failure paths and counting. Stop
at source review. Any compilation, synthetic/native qualification, execution,
repair or product integration needs its separately scoped decision. Original
backport and forward-release selection remain held/separate.

## Primary references

- https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntopenfile
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
- https://learn.microsoft.com/en-us/windows/win32/fileio/file-access-rights-constants
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_id_both_dir_information
- https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getsecurityinfo
