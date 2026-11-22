using System.Text.Json;
using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Core.Knowledge.Geometry;
using Pegasus.Cortex.GeometryGrowthDiagnostic;
using Pegasus.Cortex.Persistence.Sqlite.V2;

namespace Pegasus.Cortex.GeometryChainDiagnostic;

internal static class ChainChecks
{
    internal static async Task<string[]> RunAsync(string planPath)
    {
        var passed = new List<string>();
        await ExpectAsync<GrowthInvariantException>(() =>
        {
            ChainProgram.Read(planPath, 1);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        passed.Add("bounded fixed-local input rejects oversized plan before reading its body");
        foreach (var leaf in new[] { "con", "trailing.", "trailing ", "name:ads", "../escape" })
            await ExpectAsync<GrowthInvariantException>(() =>
            {
                ChainProgram.AdmitRoot(@"C:\CodexProjects\Pegasus Cortex\artifacts\validation\gchain-rp-79fa\" + leaf,
                    "gchain-rp-79fa");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        passed.Add("declared output parent and ASCII leaf reject reserved/trailing/ADS/traversal paths");
        var calls = 0;
        CortexKnowledgeGeometryPoint P(string id, string title, bool direct = false, long revision = 1) =>
            new("caller-key-collision", "knowledge.shared", id, revision, title, [], title, 10, direct);
        var a = P("a", "aliasone", true);
        var b = P("b", "aliasone aliastwo");
        var c = P("c", "aliastwo aliasthree");
        var d = P("d", "aliasthree");
        ValueTask<IReadOnlyList<CortexKnowledgeGeometryPoint>> Graph(CortexKnowledgeTraversalRequest query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            IReadOnlyList<CortexKnowledgeGeometryPoint> result = query.Parent.RecordId switch
            {
                "a" => [b], "b" => [c, a], "c" => [d, b], _ => [a]
            };
            return ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>(result.Take(query.Limit).ToArray());
        }
        var off = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a], Graph).ConfigureAwait(false);
        ChainProgram.Need(!off.Enabled && calls == 0, "pure-default-off");
        var zero = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [], Graph,
            ChainProgram.Options(3)).ConfigureAwait(false);
        ChainProgram.Need(zero.StopReason == "zero-seeds" && calls == 0, "pure-zero-seeds");
        passed.Add("default off and zero seeds do not call provider");

        var two = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a], Graph,
            ChainProgram.Options(2)).ConfigureAwait(false);
        var three = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a], Graph,
            ChainProgram.Options(3)).ConfigureAwait(false);
        ChainProgram.Need(two.Admitted.Any(value => value.Point.RecordId == "c" && value.Depth == 2) &&
            !two.Admitted.Any(value => value.Point.RecordId == "d") &&
            three.Admitted.Any(value => value.Point.RecordId == "d" && value.Depth == 3) &&
            three.Admitted.Select(value => value.Coordinate).Distinct().Count() == three.Admitted.Count &&
            three.Admitted.Where(value => value.Depth > 0).All(value => !value.Point.Direct), "pure-genuine-alias-chain");
        foreach (var node in three.Admitted.Where(value => value.Depth > 0))
        {
            var query = three.Queries.Single(value => value.Request.Ordinal == node.QueryOrdinal);
            ChainProgram.Need(query.Request.Parent == node.Predecessor && query.Candidates.Contains(node.Coordinate),
                "pure-query-parent");
        }
        passed.Add("generic A(alias1)-B(alias1,alias2)-C(alias2,alias3)-D(alias3), actual 2/3 edges");
        passed.Add("cycles and caller-key collisions deduplicate by full coordinate; discovered nodes stay non-direct");

        var repeated = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a], Graph,
            ChainProgram.Options(3)).ConfigureAwait(false);
        ChainProgram.Need(ChainEvaluation.Same(three, repeated), "pure-determinism");
        var capped = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a], Graph,
            ChainProgram.Options(3) with { MaximumQueries = 1 }).ConfigureAwait(false);
        ChainProgram.Need(capped.Queries.Count == 1 && capped.StopReason == "query-limit", "pure-query-cap");
        var occurrenceCap = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a], Graph,
            ChainProgram.Options(3) with { MaximumCandidateOccurrences = 1 }).ConfigureAwait(false);
        ChainProgram.Need(occurrenceCap.CandidateOccurrences == 1 && occurrenceCap.StopReason == "candidate-limit",
            "pure-candidate-cap");
        passed.Add("deterministic replay, query cap and candidate occurrence cap");

        var dense = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a],
            (query, token) => ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>(
                Enumerable.Range(0, query.Limit).Select(index => P("dense-" + index, "aliasone")).ToArray()),
            ChainProgram.Options(3) with { MaximumVisited = 32 }).ConfigureAwait(false);
        ChainProgram.Need(dense.Admitted.Count == 32 && dense.StopReason == "visited-limit", "pure-visited-cap");
        passed.Add("visited cap remains exact and reports truncation");

        var revised = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a],
            (query, token) => ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>([a with { Revision = 2, Direct = false }]),
            ChainProgram.Options(2)).ConfigureAwait(false);
        ChainProgram.Need(revised.Admitted.Any(value => value.Coordinate.RecordId == "a" && value.Coordinate.Revision == 2),
            "pure-revision-identity");
        var otherSeed = a with { Namespace = "project.unit-other" };
        var multipleNamespaces = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared", "project.unit-other"],
            [a, otherSeed], (query, token) => ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>(
                [b with { Namespace = query.Parent.Namespace }]), ChainProgram.Options(2)).ConfigureAwait(false);
        ChainProgram.Need(multipleNamespaces.Admitted.Count(value => value.Point.RecordId == "a") == 2 &&
            multipleNamespaces.Admitted.Count(value => value.Point.RecordId == "b") == 2, "pure-namespace-identity");
        passed.Add("namespace and revision both participate in identity");

        await ExpectAsync<InvalidOperationException>(() => CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a],
            (query, token) => ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>(
                [b with { Namespace = "project.outside" }]), ChainProgram.Options(2)).AsTask()).ConfigureAwait(false);
        await ExpectAsync<InvalidOperationException>(() => CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a],
            (query, token) => ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>(
                Enumerable.Repeat(b, query.Limit + 1).ToArray()), ChainProgram.Options(2)).AsTask()).ConfigureAwait(false);
        await ExpectAsync<InvalidOperationException>(() => CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a],
            (query, token) => ValueTask.FromResult<IReadOnlyList<CortexKnowledgeGeometryPoint>>(
                [a with { Summary = "inconsistent immutable projection" }]), ChainProgram.Options(2)).AsTask()).ConfigureAwait(false);
        passed.Add("provider scope, oversized result and immutable-projection violations fail closed");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(() => CortexKnowledgeGeometry.TraverseAsync("origin",
                ["knowledge.shared"], [a], Graph, ChainProgram.Options(2), cancellation.Token).AsTask()).ConfigureAwait(false);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            await ExpectAsync<OperationCanceledException>(() => CortexKnowledgeGeometry.TraverseAsync("origin",
                ["knowledge.shared"], [a], async (query, token) =>
                {
                    cancellation.Cancel();
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    return new[] { b };
                }, ChainProgram.Options(2), cancellation.Token).AsTask()).ConfigureAwait(false);
        }
        var deadline = await CortexKnowledgeGeometry.TraverseAsync("origin", ["knowledge.shared"], [a],
            async (query, token) =>
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                return new[] { b };
            }, ChainProgram.Options(2) with { MaximumDurationMilliseconds = 100 }).ConfigureAwait(false);
        ChainProgram.Need(deadline.StopReason == "time-limit" && deadline.Truncated &&
            deadline.Queries.Count <= 1 && deadline.Queries.All(value => value.Status == "deadline"), "pure-deadline");
        passed.Add("caller cancellation before and during expansion; internal deadline is explicit");

        var coordinate = new CortexKnowledgeTraversalCoordinate("knowledge.shared", "knowledge",
            "r000000000000000000000000", long.MaxValue);
        var proof = new NodeProof(coordinate, coordinate, 3, 12, false, double.MaxValue,
            .9999999999999999, .9999999999999999, 64, .9999999999999999, .9999999999999999);
        ChainProgram.Need(JsonSerializer.SerializeToUtf8Bytes(proof, ChainProgram.Json).Length <= 640 &&
            JsonSerializer.SerializeToUtf8Bytes(coordinate, ChainProgram.Json).Length <= 128, "pure-proof-byte-cap");
        passed.Add("compact coordinate/node byte maxima fit frozen worst-case report bound");
        GrowthEvaluation.PureChecks();
        passed.Add("preserved C3 role arithmetic, fixed denominators, inclusive target-loss accounting");
        return passed.ToArray();
    }

    internal static async Task<object> AdapterAsync(string fixture, CancellationToken token)
    {
        var low = new SqliteCortexV2Store(new(Path.Combine(fixture, SqliteCortexV2StoreOptions.RequiredDatabaseFileName),
            BusyTimeoutMilliseconds: 1000, MaximumContentBytes: 4096, MaximumSearchChunksPerHead: 16, MaximumDatabasePages: 4096),
            TimeProvider.System);
        await using var operational = new SqliteCortexV2OperationalStore(low, "chain-adapter-contract");
        await low.InitializeAsync(token).ConfigureAwait(false);
        var specs = new[]
        {
            (Id:"contract-seed", Ns:"knowledge.shared", Kind:CortexKnowledgeKindV2.Observation, Title:"boundary origin silver willow"),
            (Id:"contract-bridge", Ns:"knowledge.shared", Kind:CortexKnowledgeKindV2.Observation, Title:"silver willow river stone"),
            (Id:"contract-archived", Ns:"knowledge.shared", Kind:CortexKnowledgeKindV2.Observation, Title:"boundary origin silver willow archived"),
            (Id:"contract-wrong-kind", Ns:"knowledge.shared", Kind:CortexKnowledgeKindV2.Decision, Title:"boundary origin silver willow decision"),
            (Id:"contract-outside", Ns:"project.chain-adapter-outside", Kind:CortexKnowledgeKindV2.Observation, Title:"boundary origin silver willow outside")
        };
        foreach (var spec in specs)
        {
            var command = new CortexKnowledgeCommandV2(spec.Ns, CortexKnowledgeActionV2.Create, spec.Id, null,
                "chain-contract-" + spec.Id, spec.Kind, CortexKnowledgeTierV2.Verified,
                spec.Title, spec.Title, ["silver", "willow"], new(CortexProvenanceSourceV2.Agent, "chain-contract", "Synthetic fixture."), null)
            {
                EffectiveProvenance = new(CortexProvenanceSourceV2.Agent, "chain_contract", "chain-contract",
                    "Synthetic isolated boundary fixture.", new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero))
            };
            var written = await operational.ExecuteKnowledgeAsync(command, token).ConfigureAwait(false);
            ChainProgram.Need(written.Record?.Revision == 1, "contract-seed-revision");
        }
        var archived = await operational.ExecuteKnowledgeAsync(new("knowledge.shared", CortexKnowledgeActionV2.Archive,
            "contract-archived", 1, "chain-contract-archive", null, null, null, null, null, null, null)
        {
            EffectiveProvenance = new(CortexProvenanceSourceV2.Agent, "chain_contract", "chain-contract",
                "Synthetic isolated archive boundary fixture.", new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero))
        }, token).ConfigureAwait(false);
        ChainProgram.Need(archived.Record?.Revision == 2, "contract-archive-revision");
        return await ValidateAdapterAsync(low, operational, token).ConfigureAwait(false);
    }

    private static async Task<object> ValidateAdapterAsync(SqliteCortexV2Store low,
        SqliteCortexV2OperationalStore operational, CancellationToken token)
    {
        var request = new CortexSearchRequestV2(["knowledge.shared"], "boundary origin", [CortexKnowledgeKindV2.Observation],
            null, 4, 4096, CortexResponseFormatV2.Compact);
        var off = await operational.TraverseKnowledgeAsync(request, cancellationToken: token).ConfigureAwait(false);
        ChainProgram.Need(!off.Traversal.Enabled && off.SeedQueries == 0 && off.NeighborQueries == 0, "contract-default-off");
        var rawDirect = await low.SearchHeadsAsync(new("knowledge.shared", "boundary origin", "knowledge", 32, 1), token).ConfigureAwait(false);
        ChainProgram.Need(rawDirect.Any(hit => hit.RecordId == "contract-wrong-kind") &&
            rawDirect.All(hit => hit.RecordId is not "contract-archived" and not "contract-outside"),
            "contract-raw-admission-preconditions");
        var on = await operational.TraverseKnowledgeAsync(request, ChainProgram.Options(3), token).ConfigureAwait(false);
        ChainEvaluation.Validate(on, 3);
        ChainProgram.Need(on.Traversal.Admitted.Any(node => node.Coordinate.RecordId == "contract-bridge"), "contract-bridge-discovery");
        ChainProgram.Need(on.Traversal.Admitted.All(node => node.Coordinate.RecordId is "contract-seed" or "contract-bridge"),
            "contract-before-traversal-exclusion");
        ChainProgram.Need(on.RawNeighborQueries.Any(query => query.Rejected >= 1), "contract-wrong-kind-raw-rejection");
        var archivedRead = await operational.ReadAsync(new("knowledge.shared", CortexRecordTypeV2.Knowledge,
            "contract-archived", 2), token).ConfigureAwait(false);
        ChainProgram.Need(archivedRead.Record is CortexKnowledgeV2 archivedKnowledge &&
            archivedKnowledge.State == CortexKnowledgeStateV2.Archived, "contract-archived-exact-state");
        var capped = await operational.TraverseKnowledgeAsync(request,
            ChainProgram.Options(3) with { MaximumQueries = 1 }, token).ConfigureAwait(false);
        ChainProgram.Need(capped.NeighborQueries == 1 && capped.Traversal.StopReason == "query-limit", "contract-actual-query-cap");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => operational.TraverseKnowledgeAsync(request,
                ChainProgram.Options(2), cancelled.Token).AsTask()).ConfigureAwait(false);
        }
        var count = await low.GetCatalogCountersAsync(token).ConfigureAwait(false);
        ChainProgram.Need(count.HeadRows == 5, "contract-preserved-rows");
        return new { schema = "pegasus.cortex.geometry-chain-adapter-contract/1", passed = true, databases = 1,
            seededHeads = 5, archivedRevision = 2, defaultOffProviderCalls = 0, activeAndKindAndNamespaceExclusion = true,
            normal = ChainEvaluation.Proof(on), capped = ChainEvaluation.Proof(capped),
            rawDirectRecordIds = rawDirect.Select(hit => hit.RecordId).ToArray(),
            rawWrongKindRejections = on.RawNeighborQueries.Select(value => value.Rejected).ToArray(),
            archivedExactState = "archived", archiveWasRemovedFromFtsBeforeAdapter = true,
            precancelledCallRejected = true, evidence = "Actual isolated SQLite. Cancellation while provider awaits is covered separately by fake-provider unit checks." };
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T) { return; }
        throw new GrowthInvariantException("expected-" + typeof(T).Name);
    }
}
