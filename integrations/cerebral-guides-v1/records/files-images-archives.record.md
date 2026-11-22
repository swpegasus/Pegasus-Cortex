# Cerebral Guide: Files, Images, and Archives

Object: Knowledge.Cerebral.Guide.FilesImagesArchives
Version: 1.2.0
Owner: Pegasus Cerebral
Triggers: inspecting, editing, converting, extracting, generating, or validating a file or media artifact
Required capabilities: exact-path file operations; format-aware reader/editor where applicable
Rendered budget: 4096 UTF-8 bytes

## Goal

Preserve source fidelity, path safety, format semantics, and evidence while keeping model context bounded.

## Workflow

1. Resolve the exact source, destination, type, size, and ownership.
2. Detect reparse points, traversal, archive escape, collision, encoding, and unsupported-format risks before mutation.
3. Use a format-aware tool for documents, spreadsheets, presentations, PDFs, images, audio, and structured archives.
4. Read metadata or bounded excerpts first. Load full content only when the decision requires it.
5. Preserve the original unless the user requested in-place modification.
6. Write atomically or to a clearly owned output.
7. Reopen or render the produced artifact and validate the user-visible result.
8. Report output location, evidence level, and any fidelity limitation.

Attachments are typed inputs independent from prompt text. Preserve their IDs, media types, sizes, durable or temporary ownership, and removal controls without inserting transport instructions into the editable composer. A prompt may contain only an attachment. Preview or open an attachment through its owning handler, and keep failed imports retryable without duplicating the admitted object.

## Images and visual inputs

Confirm the model/profile actually accepts images before sending them. A projector, OCR layer, filename, or textual description is not equivalent to native image understanding. Use visual inspection for layout, composition, clipping, color, and rendering claims. Keep private or irrelevant screen regions out of captures.

Normalize image orientation and convert only when the selected model transport requires a supported pixel format. Preserve the original attachment and record the derived representation; resizing for a bounded model input must retain aspect ratio and enough detail for the requested inspection.

## Archives

List and test before extraction when practical. Reject absolute paths, parent traversal, reparse escapes, unsafe names, excessive counts, expansion ratios, or paths beyond the product budget. Extraction success does not prove the extracted application or document is valid.

## Failure and recovery

Unknown encoding or unsupported media must produce an explicit limitation. A partial copy or extraction requires inspection before retry. Never normalize, overwrite, delete, or “repair” ambiguous content silently.

## Context discipline

Prefer dimensions, hashes only when integrity matters, concise metadata, bounded excerpts, and links/coordinates over embedding entire artifacts or raw binary-derived output.

## Windows directory transfer

For substantial Windows directory trees, exact-read and apply `Knowledge.Windows.FileTransfer.Robocopy`. Robocopy is preferred when resumability, bounded retries, incremental comparison, speed, live progress, or a durable transfer log matters. Keep single-file atomic writes, live-database backup, archive admission, and installer semantics with their owning interfaces. Mirror, purge, move, and link traversal remain explicit rather than defaults.
