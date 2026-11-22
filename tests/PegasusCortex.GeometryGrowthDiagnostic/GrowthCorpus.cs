using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pegasus.Cortex.GeometryGrowthDiagnostic;

internal sealed record RecordPayload(string Id, string Title, string Content, string[] Tags);
internal sealed record GrowthRecord(RecordPayload Payload, string[] TargetFor,
    Dictionary<string, string> UsefulFor, string FactKey, string Bank, Dictionary<string, string>? NoiseFor = null);
internal sealed record QuestionGold(string Id, string Family, string Query, string InformationNeed,
    string[] TargetIds, Dictionary<string, string> TargetRationales, Dictionary<string, string> UsefulSupport);
internal sealed record CorpusVariant(string Id, int Seed, string[] HalfBridgeOwnerQuestionIds);
internal sealed record CorpusCell(string Id, string Variant, string Kind, int BridgeOwnerSlots, int AddedNoise,
    string[] RecordIds, string Purpose, Dictionary<string, int> ExpectedDirectHits,
    string[]? AddedSupportBeneficiaryQuestionIds = null,
    Dictionary<string, int>? BeneficiariesByFamily = null);
internal sealed record GrowthRun(string Id, bool GeometryEnabled, string[] CellIds,
    string[] QuestionIds, int CooperativeSeconds);
internal sealed record GrowthBudget(int MainDatabases, int MainRows, int OffDatabases, int OffRows,
    int ProbeDatabases, int ProbeRows, int NearNoiseDatabases, int NearNoiseRows,
    int NearNoiseQueryMeasurements, int TotalDatabases, int TotalRows, int PeakRows,
    int MainQueryMeasurements, int DistinctQuestions, int Families,
    int MaximumDatabases, int MaximumRows, long MaximumFixtureBytes, int BatchSeconds);
internal sealed record NearNoiseProfile(int Rows, int UniqueIndexedPayloads, int MinimumMultiplicity,
    int MaximumMultiplicity, string Interpretation);
internal sealed record GrowthManifest(string Schema, string GeneratorVersion, string Scope,
    QuestionGold[] Questions, GrowthRecord[] Records, string[] BaseRecordIds,
    CorpusVariant[] Variants, CorpusCell[] Cells, GrowthRun[] Runs, GrowthBudget Budget,
    NearNoiseProfile NearNoise, string[] LabelRules, string[] EvidenceLimits);

internal static class GrowthCorpus
{
    internal const string Schema = "pegasus.cortex.geometry-growth-manifest/1";
    internal const string Version = "shared-48-growth/3";
    private const string IdentifierVersion = "shared-48-growth/1";
    internal const int K = 4;
    internal const int MaximumManifestBytes = 8 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    internal static GrowthManifest Create()
    {
        var specs = GrowthQuestions.Create();
        var records = new List<GrowthRecord>();
        var baseIds = new List<string>();
        GrowthRecord Add(string key, GrowthFact fact, string[] targets,
            IEnumerable<UsefulRelation> support, string bank, string factKey)
        {
            var useful = support.ToDictionary(value => value.QuestionId, value => value.Rationale, StringComparer.Ordinal);
            var value = new GrowthRecord(new(Opaque(key), fact.Title, fact.Content, fact.Tags),
                targets, useful, factKey, bank);
            records.Add(value);
            return value;
        }

        foreach (var question in specs)
            for (var index = 0; index < question.Targets.Length; index++)
                baseIds.Add(Add("base/" + question.Id + "/" + index, question.Targets[index],
                    [question.Id], question.TargetSupportByFacet[index], "fixed-base",
                    question.Id + "/canonical/" + index).Payload.Id);
        var filler = 0;
        while (baseIds.Count < 256)
            baseIds.Add(Add("base/background/" + filler, Background(filler++, 0), [], [],
                "fixed-base", "background/" + filler).Payload.Id);

        var variants = new[]
        {
            new CorpusVariant("a", 1729, specs.Where((_, index) => index % 4 is 0 or 2).Select(value => value.Id).ToArray()),
            new CorpusVariant("b", 2718, specs.Where((_, index) => index % 4 is 1 or 3).Select(value => value.Id).ToArray())
        };
        var direct = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var nonDirect = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var noise = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            direct[variant.Id] = new(StringComparer.Ordinal);
            nonDirect[variant.Id] = new(StringComparer.Ordinal);
            foreach (var question in specs)
            {
                var own = new UsefulRelation(question.Id,
                    "Useful partial evidence for the information need: " + question.Support.Content);
                var links = new[] { own }.Concat(question.SupportAlsoSupports).ToArray();
                var title = variant.Id == "a"
                    ? question.Query + ": " + question.Support.Title
                    : question.Support.Title + ": " + question.Query;
                var tags = variant.Id == "a" ? question.Support.Tags : question.Support.Tags.Reverse().ToArray();
                direct[variant.Id][question.Id] = Add(variant.Id + "/direct/" + question.Id,
                    new(title, question.Support.Content, tags), [], links, "query-direct-support",
                    question.Id + "/support-fact").Payload.Id;
                nonDirect[variant.Id][question.Id] = Add(variant.Id + "/non-direct/" + question.Id,
                    question.Support, [], links, "non-direct-alternative",
                    question.Id + "/support-fact").Payload.Id;
            }
            noise[variant.Id] = Enumerable.Range(0, 2048).Select(index =>
                Add(variant.Id + "/background/" + index, Background(index, variant.Seed), [], [],
                    "added-background", "background/" + variant.Seed + "/" + index).Payload.Id).ToArray();
        }

        var cells = new List<CorpusCell>();
        string[] Ordered(IEnumerable<string> ids, int seed) => ids
            .OrderBy(id => Opaque("order/" + seed + "/" + id), StringComparer.Ordinal).ToArray();
        foreach (var variant in variants)
            foreach (var coverage in new[] { 0, 24, 48 })
                foreach (var addedNoise in new[] { 0, 256, 2048 })
                {
                    var covered = coverage == 0 ? [] : coverage == 24
                        ? variant.HalfBridgeOwnerQuestionIds : specs.Select(value => value.Id).ToArray();
                    cells.Add(new(variant.Id + "-b" + coverage.ToString("D2") + "-n" + addedNoise.ToString("D4"),
                        variant.Id, "main", coverage, addedNoise,
                        Ordered(baseIds.Concat(covered.Select(id => direct[variant.Id][id]))
                            .Concat(noise[variant.Id].Take(addedNoise)), variant.Seed),
                        "Shared immutable corpus; all 48 questions see the same eligible records.",
                        new(StringComparer.Ordinal)));
                }

        // Same-size near-noise control. Printed phrases in a costume-scene inventory
        // are wrong-task matches, not procedural facts. Each question gets an explicit
        // external rationale; Native review still adjudicates all overlaps before use.
        var nearIds = new List<string>();
        var questionOrder = Enumerable.Range(0, 4)
            .SelectMany(position => specs.GroupBy(value => value.Family)
                .Select(family => family.ElementAt(position))).ToArray();
        for (var index = 0; index < 256; index++)
        {
            var prompt = questionOrder[index % questionOrder.Length];
            string[] objects = ["amber scarf", "painted lantern", "linen ribbon", "wooden mask",
                "blue cushion", "woven cape", "silver bell", "paper flower"];
            var prop = objects[index % objects.Length];
            var fact = new GrowthFact(prompt.Query + " costume placard",
                "A miniature theater display has a cardboard placard printed with '" + prompt.Query +
                "'. A " + prop + " decorates the platform. This scene inventory describes costume objects.",
                ["theater", "costume", "placard"]);
            var row = Add("a/near-scene/" + index, fact, [], [], "near-query-overlap",
                prompt.Id + "/costume-scene/" + index);
            var externalReasons = specs.ToDictionary(question => question.Id,
                question => "Wrong task/sense for '" + question.InformationNeed +
                    "': this record describes a cardboard placard and costume scene, not operational procedure or evidence for that need. The printed phrase is not an asserted technical fact.",
                StringComparer.Ordinal);
            row = row with { NoiseFor = externalReasons };
            records[^1] = row;
            nearIds.Add(row.Payload.Id);
        }
        cells.Add(new("near-a-largest", "a", "near-noise", 48, 2048,
            Ordered(baseIds.Concat(direct["a"].Values).Concat(noise["a"].Take(1792)).Concat(nearIds), 1729),
            "Same-size clone of a-b48-n2048; replace256 far records with256 frozen wrong-task query-overlap costume records. Query-specific explicit noise rationales require review.",
            new(StringComparer.Ordinal)));

        // Probe alternatives are separate corpora. Main targets/base records never change.
        cells.Add(new("probe-nondirect", "a", "probe", 48, 0,
            Ordered(baseIds.Concat(nonDirect["a"].Values), 1729),
            "Size-matched non-direct alternatives versus main a-b48-n0000; focused q04/q06.",
            new(StringComparer.Ordinal) { ["q04"] = 0 }));
        var padOne = Add("probe/pad-one",
            new("Hardlink owner observation", "A retained file object's owner observation supplements its link-count evidence.", ["file", "owner"]),
            [], [new("q06", "Owner evidence is useful partial context for exact file custody.")],
            "probe-support", "q06/additional-owner").Payload.Id;
        var padTwo = Add("probe/pad-two",
            new("Hardlink volume observation", "The retained object's volume identifier supports a bounded custody comparison.", ["volume", "custody"]),
            [], [new("q06", "Volume identity is useful partial evidence for exact custody.")],
            "probe-support", "q06/additional-volume").Payload.Id;
        cells.Add(new("probe-threshold-below", "a", "probe", 0, 0, Ordered(baseIds.Concat([padOne, padTwo]), 1729),
            "Frozen real FTS precondition: q06 has three direct hits.",
            new(StringComparer.Ordinal) { ["q06"] = 3 }));
        cells.Add(new("probe-threshold-at", "a", "probe", 1, 0,
            Ordered(baseIds.Concat([padOne, padTwo, direct["a"]["q06"]]), 1729),
            "Same base/pads plus the direct support record; q06 crosses three to four hits.",
            new(StringComparer.Ordinal) { ["q06"] = 4 }));
        var gap = Add("probe/gap",
            new("SQLite deadlock rendezvous", "Rendezvous ledger.", ["rendezvous"]), [],
            [new("q04", "A synthetic alias pointer becomes useful through the separately labelled rendezvous note.")],
            "probe-support", "q04/gap-pointer").Payload.Id;
        var intermediate = Add("probe/intermediate",
            new("Rendezvous circular resource waits", "Rendezvous connects the alias pointer to circular resource waits.", ["rendezvous", "circular", "resource", "waits"]),
            [], [new("q04", "This intermediate note connects the alias pointer to the canonical resource-cycle evidence.")],
            "probe-support", "q04/gap-intermediate").Payload.Id;
        var halfB = variants.Single(value => value.Id == "b");
        cells.Add(new("probe-long-gap", "b", "probe", 24, 0,
            Ordered(baseIds.Concat(halfB.HalfBridgeOwnerQuestionIds.Where(id => id != "q04").Select(id => direct["b"][id]))
                .Concat([gap, intermediate]), halfB.Seed),
            "Scoped SQLite deadlock query: b-half direct bridges with q04 replaced by the alias pointer and one intermediate. 281 rows versus main b-b24-n0000(280). Other SQLite seeds q02 and q06 remain visible.",
            new(StringComparer.Ordinal) { ["q04"] = 3 }));

        var recordsById = records.ToDictionary(value => value.Payload.Id, StringComparer.Ordinal);
        var baseSet = baseIds.ToHashSet(StringComparer.Ordinal);
        cells = cells.Select(cell =>
        {
            var beneficiaries = cell.RecordIds.Where(id => !baseSet.Contains(id))
                .SelectMany(id => recordsById[id].UsefulFor.Keys).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            return cell with
            {
                AddedSupportBeneficiaryQuestionIds = beneficiaries,
                BeneficiariesByFamily = specs.GroupBy(value => value.Family).ToDictionary(
                    family => family.Key, family => family.Count(value => beneficiaries.Contains(value.Id)),
                    StringComparer.Ordinal)
            };
        }).ToList();
        var nearGroups = records.Where(value => value.Bank == "near-query-overlap")
            .GroupBy(value => JsonSerializer.Serialize(new
            {
                value.Payload.Title, value.Payload.Content, value.Payload.Tags
            }, Json)).Select(group => group.Count()).ToArray();
        var nearProfile = new NearNoiseProfile(256, nearGroups.Length, nearGroups.Min(), nearGroups.Max(),
            "Artificial repeated-payload density stress: not256 diverse records or independent questions.");

        var questions = specs.Select(spec => new QuestionGold(spec.Id, spec.Family, spec.Query, spec.InformationNeed,
            records.Where(record => record.TargetFor.Contains(spec.Id, StringComparer.Ordinal)).Select(record => record.Payload.Id).ToArray(),
            records.Where(record => record.TargetFor.Contains(spec.Id, StringComparer.Ordinal))
                .ToDictionary(record => record.Payload.Id,
                    record => "Authored canonical answer facet for '" + spec.InformationNeed + "': " + record.Payload.Content,
                    StringComparer.Ordinal),
            records.Where(record => record.UsefulFor.ContainsKey(spec.Id))
                .ToDictionary(record => record.Payload.Id, record => record.UsefulFor[spec.Id], StringComparer.Ordinal))).ToArray();
        var allQuestions = specs.Select(value => value.Id).ToArray();
        var runs = new List<GrowthRun>
        {
            new("main-a", true, cells.Where(value => value.Kind == "main" && value.Variant == "a").Select(value => value.Id).ToArray(), allQuestions, 360),
            new("main-b", true, cells.Where(value => value.Kind == "main" && value.Variant == "b").Select(value => value.Id).ToArray(), allQuestions, 360),
            new("off-a-largest", false, ["a-b48-n2048"], allQuestions, 180),
            new("near-a-largest", true, ["near-a-largest"], allQuestions, 180),
            new("probe-nondirect", true, ["probe-nondirect"], ["q04", "q06"], 90),
            new("probe-threshold-below", true, ["probe-threshold-below"], ["q06"], 90),
            new("probe-threshold-at", true, ["probe-threshold-at"], ["q06"], 90),
            new("probe-long-gap", true, ["probe-long-gap"], ["q04"], 90)
        };
        var main = cells.Where(value => value.Kind == "main").ToArray();
        var probes = cells.Where(value => value.Kind == "probe").ToArray();
        var offRows = cells.Single(value => value.Id == "a-b48-n2048").RecordIds.Length;
        var budget = new GrowthBudget(main.Length, main.Sum(value => value.RecordIds.Length), 1, offRows,
            probes.Length, probes.Sum(value => value.RecordIds.Length), 1, 2352, 48,
            main.Length + 2 + probes.Length, main.Sum(value => value.RecordIds.Length) + offRows + 2352 + probes.Sum(value => value.RecordIds.Length),
            cells.Max(value => value.RecordIds.Length), main.Length * 48, 48, 12,
            25, 30_000, 512L * 1024 * 1024, 1800);
        var manifest = new GrowthManifest(Schema, Version, "Synthetic canonical knowledge.shared only; no old corpus or installed settings.",
            questions, records.ToArray(), baseIds.ToArray(), variants, cells.ToArray(), runs.ToArray(), budget, nearProfile,
            [
                "Relevance is a (question, record) relation; target and useful-support sets are disjoint.",
                "Every eligible non-target/non-support record is noise for that question under the reviewed closed-corpus relation table.",
                "Target counts and IDs are fixed across every cell/variant; useful-support denominators are cell-specific.",
                "Queries include submitted domain/task scope; InformationNeed is not hidden context for the provider.",
                "Target-facet reuse is individually adjudicated; no blanket inheritance across facets.",
                "BridgeOwnerSlots counts selected owner questions, not all useful beneficiaries; actual beneficiary sets are separate.",
                "Half bridge-owner sets complement and balance families; useful spillover can make beneficiary sets larger and unbalanced.",
                "Stable opaque identifier domain is shared-48-growth/1; candidate2 does not change canonical payload identities.",
                "Explicit cross-question reuse applies only where its written rationale is present.",
                "No role, rationale, question ID or bank label is submitted as indexed metadata.",
                "Main added background is far-unrelated synthetic nature/craft content.",
                "The near-noise cell has256 rows but48 unique indexed payloads repeated5/6 times: artificial density stress, not diverse near-noise reliability evidence."
            ],
            [
                "48 distinct questions and 12 families; variants and cells are correlated robustness/mechanism observations.",
                "Analytical uniform references are math, not executed retrieval providers.",
                "Current geometry performs one expansion pass and maximum similarity over direct seeds.",
                "Probe expectations are frozen fixture preconditions, not observed results or required geometry wins.",
                "The long-gap control has one extra record over the b-half comparator and does not isolate cardinality perfectly.",
                "Prepared manifest and arithmetic checks do not prove SQLite behavior or retrieval quality."
            ]);
        Validate(manifest);
        return manifest;
    }

    internal static void Validate(GrowthManifest manifest)
    {
        Require(manifest.Schema == Schema && manifest.GeneratorVersion == Version, "Unsupported growth manifest.");
        Require(manifest.Questions.Length == 48 && manifest.Questions.Select(value => value.Id).Distinct().Count() == 48,
            "Exactly48 unique questions required.");
        Require(manifest.Questions.GroupBy(value => value.Family).Count() == 12 &&
            manifest.Questions.GroupBy(value => value.Family).All(group => group.Count() == 4), "Twelve balanced families required.");
        var questions = manifest.Questions.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var records = manifest.Records.ToDictionary(value => value.Payload.Id, StringComparer.Ordinal);
        Require(records.Count == manifest.Records.Length && manifest.BaseRecordIds.Length == 256 &&
            manifest.BaseRecordIds.Distinct().Count() == 256, "Base/record identity collision.");
        foreach (var record in manifest.Records)
        {
            Require(record.Payload.Id.Length == 25 && record.Payload.Id[0] == 'r' &&
                record.Payload.Id[1..].All(Uri.IsHexDigit) && record.Payload.Title.Length <= 160 &&
                Encoding.UTF8.GetByteCount(record.Payload.Content) <= 512 && record.Payload.Tags.Length <= 8,
                "Record payload exceeds bounds.");
            Require(record.TargetFor.All(questions.ContainsKey) && record.UsefulFor.Keys.All(questions.ContainsKey) &&
                !record.TargetFor.Any(record.UsefulFor.ContainsKey) &&
                record.UsefulFor.Values.All(value => !string.IsNullOrWhiteSpace(value)), "Invalid query-specific role relation.");
            if (record.NoiseFor is not null)
                Require(record.NoiseFor.Keys.All(questions.ContainsKey) &&
                    record.NoiseFor.Keys.All(id => !record.TargetFor.Contains(id) && !record.UsefulFor.ContainsKey(id)) &&
                    record.NoiseFor.Values.All(value => !string.IsNullOrWhiteSpace(value)),
                    "Explicit noise overlaps a positive role.");
            if (record.Bank == "near-query-overlap")
                Require(record.NoiseFor?.Count == 48, "Near-noise requires individual question rationales.");
        }
        foreach (var question in manifest.Questions)
        {
            Require(question.TargetIds.Length > 0 &&
                question.TargetRationales.Count == question.TargetIds.Length &&
                question.TargetIds.All(id => question.TargetRationales.TryGetValue(id, out var reason) && !string.IsNullOrWhiteSpace(reason)) &&
                question.TargetIds.All(manifest.BaseRecordIds.Contains) &&
                !question.TargetIds.Any(question.UsefulSupport.ContainsKey) &&
                question.TargetIds.All(id => records[id].TargetFor.Contains(question.Id)) &&
                question.UsefulSupport.All(pair => records[pair.Key].UsefulFor.TryGetValue(question.Id, out var reason) && reason == pair.Value),
                "Gold target/support relation is inconsistent.");
        }
        Require(manifest.Variants.Length == 2 && manifest.Variants.All(value => value.HalfBridgeOwnerQuestionIds.Length == 24),
            "Two half-coverage variants required.");
        var a = manifest.Variants.Single(value => value.Id == "a").HalfBridgeOwnerQuestionIds.ToHashSet();
        var b = manifest.Variants.Single(value => value.Id == "b").HalfBridgeOwnerQuestionIds.ToHashSet();
        Require(!a.Overlaps(b) && a.Union(b).Count() == 48, "Half coverage must complement.");
        foreach (var variant in manifest.Variants)
            foreach (var family in manifest.Questions.GroupBy(value => value.Family))
                Require(family.Count(value => variant.HalfBridgeOwnerQuestionIds.Contains(value.Id)) == 2,
                    "Half coverage must balance each family.");
        Require(manifest.Cells.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() == manifest.Cells.Length,
            "Duplicate cell identity.");
        foreach (var cell in manifest.Cells)
        {
            Require(cell.Id.Length is > 0 and <= 48 && cell.Id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'),
                "Unsafe cell path component.");
            Require(cell.RecordIds.Distinct().Count() == cell.RecordIds.Length &&
                cell.RecordIds.All(records.ContainsKey) &&
                manifest.BaseRecordIds.All(cell.RecordIds.Contains) && cell.RecordIds.Length <= 2352,
                "Cell violates shared fixed-base bounds.");
            var actualBeneficiaries = cell.RecordIds.Where(id => !manifest.BaseRecordIds.Contains(id))
                .SelectMany(id => records[id].UsefulFor.Keys).ToHashSet(StringComparer.Ordinal);
            Require(cell.AddedSupportBeneficiaryQuestionIds is not null &&
                actualBeneficiaries.SetEquals(cell.AddedSupportBeneficiaryQuestionIds) &&
                cell.BeneficiariesByFamily is not null &&
                manifest.Questions.GroupBy(value => value.Family).All(family =>
                    cell.BeneficiariesByFamily!.TryGetValue(family.Key, out var count) &&
                    count == family.Count(value => actualBeneficiaries.Contains(value.Id))),
                "Useful beneficiary reporting mismatch.");
            if (cell.Kind == "main")
            {
                var variant = manifest.Variants.Single(value => value.Id == cell.Variant);
                var covered = cell.BridgeOwnerSlots == 0 ? [] : cell.BridgeOwnerSlots == 24 ? variant.HalfBridgeOwnerQuestionIds :
                    manifest.Questions.Select(value => value.Id).ToArray();
                Require(cell.RecordIds.Length == 256 + cell.BridgeOwnerSlots + cell.AddedNoise, "Main row count mismatch.");
                Require(cell.RecordIds.Count(id => records[id].Bank == "query-direct-support") == covered.Length &&
                    covered.All(q => cell.RecordIds.Any(id => records[id].Bank == "query-direct-support" &&
                        records[id].UsefulFor.ContainsKey(q) && records[id].FactKey == q + "/support-fact")),
                    "Coverage membership mismatch.");
            }
        }
        var budget = manifest.Budget;
        Require(budget.MainDatabases == 18 && budget.MainRows == 18_864 && budget.OffRows == 2352 &&
            budget.ProbeDatabases <= 4 && budget.TotalDatabases <= 25 && budget.TotalRows <= 30_000 &&
            budget.MainQueryMeasurements == 864 && budget.PeakRows == 2352 &&
            manifest.Cells.Count(value => value.Kind == "main") == budget.MainDatabases &&
            manifest.Cells.Where(value => value.Kind == "main").Sum(value => value.RecordIds.Length) == budget.MainRows &&
            manifest.Cells.Count(value => value.Kind == "probe") == budget.ProbeDatabases &&
            manifest.Cells.Where(value => value.Kind == "probe").Sum(value => value.RecordIds.Length) == budget.ProbeRows &&
            budget.NearNoiseDatabases == 1 && budget.NearNoiseRows == 2352 && budget.NearNoiseQueryMeasurements == 48 &&
            manifest.Cells.Count(value => value.Kind == "near-noise") == 1 &&
            manifest.Cells.Single(value => value.Kind == "near-noise").RecordIds.Length == 2352 &&
            budget.TotalRows == budget.MainRows + budget.OffRows + budget.ProbeRows + budget.NearNoiseRows &&
            budget.TotalDatabases == budget.MainDatabases + budget.OffDatabases + budget.ProbeDatabases + budget.NearNoiseDatabases &&
            budget.MaximumDatabases <= 25 && budget.MaximumRows <= 30_000 &&
            budget.MaximumFixtureBytes <= 512L * 1024 * 1024 && budget.BatchSeconds <= 1800,
            "Admitted budget mismatch.");
        var farCell = manifest.Cells.Single(value => value.Id == "a-b48-n2048");
        var nearCell = manifest.Cells.Single(value => value.Id == "near-a-largest");
        Require(manifest.NearNoise.Rows == 256 && manifest.NearNoise.UniqueIndexedPayloads == 48 &&
            manifest.NearNoise.MinimumMultiplicity == 5 && manifest.NearNoise.MaximumMultiplicity == 6,
            "Near-noise diversity disclosure mismatch.");
        Require(nearCell.RecordIds.Count(id => records[id].Bank == "near-query-overlap") == 256,
            "Near-noise replacement count mismatch.");
        foreach (var question in manifest.Questions)
        {
            var farSupport = question.UsefulSupport.Keys.Where(farCell.RecordIds.Contains).ToHashSet(StringComparer.Ordinal);
            var nearSupport = question.UsefulSupport.Keys.Where(nearCell.RecordIds.Contains).ToHashSet(StringComparer.Ordinal);
            Require(farSupport.SetEquals(nearSupport) && question.TargetIds.All(nearCell.RecordIds.Contains),
                "Near-noise changed positive question truth.");
        }
        Require(manifest.Runs.All(run => run.CellIds.All(id => manifest.Cells.Any(cell => cell.Id == id)) &&
            run.QuestionIds.All(questions.ContainsKey) && run.CooperativeSeconds is >= 30 and <= 420),
            "Invalid bounded run plan.");
    }

    internal static string Opaque(string key) =>
        "r" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(IdentifierVersion + "/" + key))).ToLowerInvariant()[..24];
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static GrowthFact Background(int ordinal, int seed)
    {
        string[] subjects = ["orchid", "sparrow", "marzipan", "kayak", "willow", "ceramic bowl", "violin",
            "cinnamon", "bicycle", "seashell", "sunflower", "origami crane", "mountain", "tulip", "watercolor", "chessboard"];
        string[] settings = ["morning meadow", "quiet courtyard", "summer garden", "shaded balcony",
            "coastal village", "sunlit terrace", "autumn orchard", "snowy valley"];
        string[] details = ["amber ribbon", "blue cloth", "round basket", "painted pebble",
            "green cushion", "linen napkin", "silver bell", "woven mat", "small lantern",
            "yellow vase", "wooden tray", "red scarf", "purple flower", "brown bench", "white shell", "pink cup"];
        var rotated = (ordinal + seed % 2048) % 2048;
        var subject = subjects[rotated % subjects.Length];
        var setting = settings[(rotated / subjects.Length) % settings.Length];
        var detail = details[(rotated / (subjects.Length * settings.Length)) % details.Length];
        return new(subject + " in the " + setting,
            "A fictional " + subject + " appears beside a " + detail + " in the " + setting + ". Scene " + (ordinal + 1) + ".",
            [subject.Replace(' ', '-'), "nature-craft"]);
    }
}
