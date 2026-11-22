# The evolution of Cortex

Cortex began as a file-based way to carry useful context beyond one conversation.
The later product combines that visible, portable approach with structured
records, selected retrieval and practical tools. These are stages in the design,
not a claim that every stage was a separate named release.

## Project files made continuity visible

The first useful step was to put continuing guidance and project state somewhere
other than the current chat. Readable project files could be inspected, edited
and carried with the work. That visibility remains a strength: a person can
understand the retained material without treating a model as the only source.

As the material grows, however, sending all of it into every conversation becomes
less useful. It consumes working context and can bury the decision that matters.
The problem shifts from "How do we keep this?" to "How do we recover the right
part now?" The exact original filenames and folder layout are not prerequisites
for understanding that change; current generated cache files should not be
mistaken for the original implementation.

## Records made shared work more coordinated

Tasks and reusable knowledge acquired distinct identities, revisions and lifecycle
states. Transactional storage made it possible to update project state coherently
when several conversations were involved, instead of treating one prose file as
an informal shared database.

Files did not disappear. Portable behavior packs and the file reference provider
retain a role alongside the local operational database. The useful distinction
is portable guidance versus active task and knowledge state. The tradeoff is
that active storage needs supported tools and maintenance; arbitrary edits to
database files are not an alternative interface.

## Retrieval made retained context usable

A small task brief, scoped search, bounded previews and exact selected reads
became a repeatable workflow. Direct matches can be complemented by deterministic
related-term retrieval. The canonical record remains the material to read.

This gives the assistant a way to retrieve rather than repeatedly recite. It
also introduces a responsibility: a bounded result is a selection, and a useful
record may be missing from that selection. The model still needs to recognize
when to refine a query, widen its budget or verify a current fact elsewhere.

## Project shared and personal material gained different roles

Project namespaces organize the facts, vocabulary, decisions and tasks belonging
to that work. Shared knowledge carries reusable guidance. The separately
controlled personal layer carries local user preferences when it is available.

This separation helps a conversation retrieve the relevant combination without
shipping private user history as starter material. Namespace names organize
records; they are not independent tenants or per-client security boundaries.
Retaining preferences also does not imply automatic personality inference or
universal automatic capture of every conversation.

## Continuity grew a practical tool counter

PAO connects project work to typed filesystem, developer, process and supported
Windows operations. Useful results can stay compact, while failure details and
explicit full receipts remain available when needed. Local terminal sessions
support continuing interactive work across a client disconnect.

The benefit is a clearer connection between a requested operation and its result.
The limit is equally important: a tool does not authorize itself, a queued job
is not completed work, and reconnecting must not replay a command. Terminal
continuity ends when its owner Host stops; it is not a reboot-proof transcript.

## Separate pieces became a maintainable Windows product

A resident ordinary-user Host lets adapters share an owned runtime instead of
creating a new semantic store for every client. Internally, contracts, Core,
storage and providers keep distinct responsibilities.

Unified deployment separates replaceable program files from mutable databases,
configuration and connection material. Ordinary continuity and local execution
remain distinct from the optional privileged service. Closing a window, stopping
the runtime and installing an update are therefore different actions.

That makes maintenance more understandable, but it also makes identity and
lifetime explicit: a source build, an installed component and an already-running
process can be different generations. Components that communicate through
publisher checks must form a compatible set signed with the required same
publisher identity.

## Shared MCP broadened the client front door

The public connection moved from per-chat direct STDIO startup to an optional
shared HTTPS MCP endpoint. Compatible clients can use the same resident service.
The listener belongs to the selected Application or existing Service owner;
service gateway mode preserves the ordinary-owner boundary for semantic work.

The common endpoint reduces duplicated adapter processes. It does not remove
the need to configure its owner, TLS and allowed network boundary. Changing
ownership takes a deliberate restart, not a live takeover of another process.
A saved URL is not proof that a client actually adopted the connection.

Remembered connection approval and the retirement of older development access
controls are separate ongoing work. A direction under development is not a
capability to assume in a prepared release.

## What the evolution is aiming for

The direction is recoverable local work: smaller useful working contexts,
organized project state, explicit tool use and maintenance that preserves the
operator's data. Long-running projects, project switching and several
conversations working on the same subject are natural targets. Local models
are an intended beneficiary, without a promise that every workload will fit a
particular GPU or perform like a larger model.

The current host is a Windows product for local operation and deliberately
configured trusted LAN/VPN access. It is not a public anonymous cloud service,
a multi-tenant isolation system, or a claim of a cross-platform Host. Portable
guidance and compatible clients on another platform are different questions.

Read [context and retrieval](context-and-retrieval.md), the
[Together case study](together.md), and the
[local operational reference](../LOCAL_OPERATIONAL_V2.md) for the current design.
