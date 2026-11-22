# Context and retrieval

A model's working context is the material it can use for the current answer.
Your project can be much larger: files, decisions, unfinished work and lessons
from many conversations. Cortex keeps selected durable records and provides a
bounded way to retrieve them. The connected model remains responsible for
reasoning over the material it actually receives.

## Three kinds of material

**Tasks** describe continuing work: the objective, current state, dependencies,
blockers and next action. Revision checks help detect another writer changing
the record between reading and updating it.

**Knowledge** records retain reusable material such as a correction, decision,
preference, failure, workaround or observation. Provenance and evidence tier
help a reader distinguish an explicit instruction from a verified observation
or an inference. They do not make the content automatically correct forever.

**Portable guidance** carries generalized behavior and lessons. Starter packs
do not contain the operator's private conversations, personal profile or task
history. A generated task cache is a replaceable fallback, not the database.

Project namespaces organize project-owned material. `knowledge.shared` contains
reusable knowledge; the separately controlled `knowledge.user` layer contains
local personal preferences when that profile is available. Namespace selection
is explicit on requests, rather than a hidden current-project switch. These
organizational names are not separate tenants or per-client permissions.

## A retrieval loop that is easy to inspect

1. **Start small.** Ask for the current task brief and relevant context in the
   selected spaces. The normal start budget is 4 KiB, with an 8 KiB ceiling.
   A byte budget is not a token count; different models tokenize text differently.
2. **Search when needed.** Use a specific question or term and inspect bounded
   previews. Narrowing the spaces can improve focus, while broadening them may
   be appropriate when the first query misses useful material.
3. **Read the selected record.** Use its namespace, record type, identifier and
   revision. A preview helps choose a source; it is not the whole source.
4. **Verify live facts.** A retained build result or filename can be stale.
   Check the current file, application or provider when the decision depends on it.
5. **Retain a useful outcome.** Save a compact checkpoint or durable correction
   after meaningful progress, rather than recording every tool call.

Exact reads can batch up to eight selected coordinates. Clients should reuse a
body while they still have it at the same revision, and retrieve it again after
losing it or observing a newer revision. A remembered identifier alone is not
proof that the body is still loaded in the model's working context.

## Example search and exact read

The following requests use the public semantic MCP shape. The record is
synthetic and the excerpt is illustrative, not an installed query result.

First call `cortex_search`:

```json
{
  "namespaces": ["knowledge.shared"],
  "text": "restart recovery",
  "limit": 3,
  "maxUtf8Bytes": 4096,
  "format": "detailed"
}
```

A relevant preview could identify these selected fields:

```json
{
  "namespace": "knowledge.shared",
  "recordType": "knowledge",
  "recordId": "example.restart-recovery",
  "revision": 3,
  "title": "Check the original operation before retrying"
}
```

Then call `cortex_read` with the actual coordinate returned by your search:

```json
{
  "namespace": "knowledge.shared",
  "recordType": "knowledge",
  "recordId": "example.restart-recovery",
  "revision": 3
}
```

The exact read supplies that record's content. If the selected revision is
unavailable or the response is an error, the client must not fill in a plausible
body. A newer revision is a reason to inspect the newer record, not to silently
pretend the original content was loaded.

## What geometric retrieval means

Here, geometry is a way to compare small sets of weighted text features. It is
not a second AI model that reasons about the project.

For the shared and personal knowledge spaces, Cortex derives a deterministic
sparse vector from a record's title, tags and indexed summary. Records with
overlapping features point in similar directions. A direct search hit can seed
bounded expansion terms that recover a related record using different wording.
Direct keyword matches remain ahead of those related neighbors.

```text
Query: restart recovery
        |
        +--> Direct match: check the original operation
                    |
                    +--> Related terms: retry / receipt / uncertain result
                                      |
                                      +--> Candidate: do not replay unknown work
```

This diagram explains the mechanism; it is not a measured query trace.
Only selected knowledge spaces contribute ranking signals. The geometric
spaces are `knowledge.shared` and `knowledge.user`; do not assume every project
record is a learned semantic embedding.

The current implementation weights title, tag and indexed-summary terms,
normalizes a vector of at most 256 terms, and compares vectors by dot product.
That makes the calculation reproducible without downloading an embedding model.
It also means terminology and indexed text matter: a relationship that is not
represented by those features may not be found. The exact record, rather than
the ranking feature vector, remains the content to read.

The separate **Geometry.Shadow** project is default-off research that observes
the existing provider and returns its original result unchanged. It is not the
production retrieval promise and does not establish superior ranking quality.

## What retention does not solve

A record can contain a mistake. A search can miss the right record. An old
decision can be superseded. A model can misread a correct source. Useful
continuity therefore depends on clear provenance, selective updates and current
verification, as well as storage.

Retained text is passive reference material. It cannot grant a tool permission,
change the operator's objective, or authorize effects outside the current task.
Do not use the store for credentials, private keys, raw transcripts, raw terminal
streams or private reasoning. Do not assume that automatic content screening
will reliably remove all sensitive material.

For the detailed contracts, see [local operational V2](../LOCAL_OPERATIONAL_V2.md),
[knowledge geometry source](../../src/PegasusCortex.Core/Knowledge/Geometry/CortexKnowledgeGeometry.cs),
and the [MCP contract](../../src/PegasusCortex.Mcp/V2/CortexV2McpContract.cs).
