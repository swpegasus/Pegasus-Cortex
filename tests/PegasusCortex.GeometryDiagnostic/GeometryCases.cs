namespace Pegasus.Cortex.GeometryDiagnostic;

internal sealed record GeometryFixtureRecord(
    string Namespace, string RecordId, string Title, string Content,
    string[] Tags, bool Relevant, string Rationale);

internal sealed record GeometryCase(
    string Id, string Query, string Purpose, GeometryFixtureRecord[] Records,
    bool RequireZeroLexicalSeeds = false, string? RequireLexicallyAbsentRelevantId = null);

internal static class GeometryCases
{
    internal const string Version = "synthetic-geometry-cases/1";
    internal const string Shared = "knowledge.shared";
    internal const string Excluded = "knowledge.user";

    // Labels describe the query's information need, not a desired rank or score.
    // This manifest must be pinned before measurement; do not tune it to results.
    internal static GeometryCase[] Create() =>
    [
        new("c01", "sqlite busy timeout", "Strong exact answer; measure unnecessary disturbance.",
        [
            R("timeout", "SQLite busy timeout", "Use a bounded busy timeout and record write-lock ownership before retrying.", ["sqlite", "busy", "timeout"], true, "Directly answers how to bound SQLite lock waits."),
            R("backup", "Backup retention", "Keep the approved backup until recovery is verified.", ["backup"], false, "Retention does not explain lock waits."),
            R("layout", "Window layout", "Save the editor window position.", ["layout"], false, "Unrelated interface preference.")
        ]),
        new("c02", "sqlite locking", "Potential beneficial bridge to a lexical-absent relevant neighbor.",
        [
            R("seed", "SQLite locking evidence", "Measure transaction contention and journal ownership before changing policy.", ["transaction", "contention"], true, "Provides the direct diagnosis and a related diagnostic vocabulary."),
            R("neighbor", "Transaction contention ownership", "Inspect the active transaction retaining a write lock and record its owner.", ["transaction", "contention"], true, "Gives a relevant ownership step without either original query token."),
            R("billing", "Transaction receipt", "Send the customer a billing receipt after payment.", ["transaction"], false, "Financial use of transaction is unrelated to database locks.")
        ], RequireLexicallyAbsentRelevantId: "neighbor"),
        new("c03", "checksum archive", "No useful neighbor; geometry is allowed to add no value.",
        [
            R("checksum", "Checksum archive", "Verify the archive digest against the trusted expected digest.", ["checksum", "archive"], true, "Direct integrity verification guidance."),
            R("calendar", "Calendar colors", "Assign a color to a weekly meeting.", ["calendar"], false, "No integrity or archive relationship."),
            R("music", "Piano practice", "Practice scales before the recital.", ["music"], false, "Unrelated record.")
        ]),
        new("c04", "cancellation cleanup", "Exact answer under pressure from loosely related neighbors.",
        [
            R("exact", "Cancellation cleanup", "After cancellation, dispose the owned handle and retain uncertain published output.", ["ownership", "handles", "recovery"], true, "Specifies the requested cancellation cleanup contract."),
            R("recover", "Owned handle recovery", "Record ownership before releasing a resource after interruption.", ["ownership", "handles", "recovery"], true, "Relevant supporting resource-release guidance."),
            R("museum", "Museum ownership", "Catalog the provenance of a donated painting.", ["ownership", "recovery"], false, "Shared features describe museum administration."),
            R("sports", "Recovery schedule", "Rest after the race.", ["recovery"], false, "Physical recovery is unrelated."),
            R("catalog", "Handle catalog", "Display replacement cupboard handles in the showroom.", ["handles"], false, "The noun refers to furniture hardware.")
        ]),
        new("c05", "port connection", "Network endpoint troubleshooting; homonym distractor with strong lexical overlap.",
        [
            R("socket", "Port connection", "Check the socket endpoint and local listener before retrying a network connection.", ["socket", "network"], true, "Addresses a network port connection."),
            R("harbor", "Port connection timetable", "The harbor ferry connects two coastal ports.", ["port", "connection"], false, "A transport route does not diagnose a network endpoint."),
            R("listener", "Socket listener", "Confirm that the expected process owns the listening endpoint.", ["socket", "network"], true, "Relevant endpoint ownership evidence.")
        ]),
        new("c06", "index delay", "Database planning latency; a weak irrelevant direct seed may spread scheduling noise.",
        [
            R("cue", "Index delay", "The theatre cue index schedules a delayed lighting change.", ["queue", "schedule"], false, "The request concerns database query planning, not stage cues."),
            R("planner", "Database query planning latency", "Inspect the query plan and missing access path before changing database configuration.", ["database", "planner"], true, "Useful diagnosis of database query planning latency."),
            R("queue", "Stage queue schedule", "Queue the actors for the next scene.", ["queue", "schedule"], false, "A neighbor of the wrong seed remains irrelevant.")
        ]),
        new("c07", "certificate signing release", "Adversarial title and tag overlap without useful guidance.",
        [
            R("release", "Certificate signing release", "Verify the release certificate identity and sign only the admitted artifact.", ["certificate", "signing", "release"], true, "Provides the requested release-signing procedure."),
            R("poster", "Certificate signing release poster", "A decorative typography gallery with no operational instructions.", ["certificate", "signing", "release", "gallery"], false, "Keyword stuffing does not provide a signing procedure."),
            R("identity", "Artifact signer identity", "Match the approved signer and artifact digest before publication.", ["certificate", "artifact"], true, "Relevant supporting integrity check.")
        ]),
        new("c08", "sqlite deadlock", "Synonym-only relevant item with no direct seed.",
        [
            R("cycle", "Circular transaction waits", "Identify the owners in a wait cycle and remove the cyclic acquisition order.", ["wait", "cycle", "transaction"], true, "Describes resolving a database deadlock without the query tokens."),
            R("garden", "Garden watering", "Water the herbs in the morning.", ["garden"], false, "Unrelated distractor.")
        ], RequireZeroLexicalSeeds: true),
        new("c09", "the and of", "Intent has no useful geometric features; measure observed behavior.",
        [
            R("words", "The and of", "A worksheet discussing grammatical function words.", ["grammar"], false, "The query states no actionable information need."),
            R("recipe", "Soup recipe", "Simmer vegetables until tender.", ["cooking"], false, "No relevant answer exists for this intent.")
        ]),
        new("c10", "checkpoint", "Four direct seeds reach the current expansion threshold.",
        [
            R("state", "Checkpoint state", "Record verified state and the next action.", ["continuity", "handoff"], true, "A useful continuity checkpoint."),
            R("blocker", "Checkpoint blocker", "Record the exact unresolved blocker.", ["continuity", "handoff"], true, "Useful checkpoint content."),
            R("evidence", "Checkpoint evidence", "Keep bounded evidence coordinates with their limits.", ["continuity", "handoff"], true, "Useful checkpoint evidence."),
            R("road", "Checkpoint road", "Vehicles stop at the roadside inspection point.", ["road"], false, "A physical road checkpoint does not satisfy a continuity request."),
            R("handoff", "Continuity handoff", "Preserve verified state, blockers and the next action for continuation.", ["continuity", "handoff"], true, "Relevant supporting record without the query token.")
        ]),
        new("c11", "quasar telescope alignment", "No relevant answer; report noise without a quality pass gate.",
        [
            R("wordplay", "Quasar telescope alignment wallpaper", "An abstract wallpaper named after astronomy words.", ["quasar", "telescope", "alignment"], false, "Decoration offers no telescope alignment guidance."),
            R("stars", "Astronomy poster", "A chart used only as a decorative print.", ["telescope", "poster"], false, "No alignment procedure is present.")
        ]),
        new("c12", "pipe peer identity", "Explicitly excluded user namespace has a relevant tempting record.",
        [
            R("public", "Pipe peer identity", "A musical instrument catalog describing pipe organ makers.", ["pipe", "identity"], false, "Musical pipe provenance is not process authentication."),
            new(Excluded, "private", "Pipe peer identity", "Validate the peer process owner and generation before protocol admission.", ["pipe", "peer", "identity"], true, "Relevant process identity guidance, but outside the selected namespace.")
        ])
    ];

    private static GeometryFixtureRecord R(string id, string title, string content, string[] tags, bool relevant, string rationale) =>
        new(Shared, id, title, content, tags, relevant, rationale);
}
