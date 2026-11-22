using Pegasus.Cortex.DefaultLibrary.Contracts;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.DefaultLibrary.Seeds;

/// <summary>
/// Closed evidence allowlist for the built-in curated seed. Unknown authorities fail closed.
/// </summary>
public static class DefaultLibrarySourcePolicy
{
    private static readonly HashSet<string> AllowedAuthorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "pegasus-shared",
        "pegasus-nexus",
        "pegasus-server",
        "pegasus-coder",
        "pegasus-node",
        "pegasus-scatterbrain",
        "pegasus-cortex"
    };

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "source",
        "schema",
        "package"
    };

    private static readonly IReadOnlyDictionary<string, string> AllowedArtifacts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source://pegasus-shared/VERSION.json"] = "94f35960bc10e647a9d34d378a8c6361b0aca5d104bf5ec85025fe271dbcb7d9",
            ["package://pegasus-shared/1.20260711.153-a"] = "499773af4b8bd0f4524f59ebe76c58852c659cd0cdfe207f5e0acdf4de24ab07",
            ["schema://pegasus-shared/agent-outcome.v1"] = "b6bc3fea55c96b94eec206b052cf5eea544c8718fcf2eedc16cdd9d9b76052f9",
            ["schema://pegasus-shared/agent-coordination.v1"] = "6ff9191d5409040f7de956e2551bf6e6d86235d3dc526c7b6c44c74ea00f17d1",
            ["schema://pegasus-shared/release-version.v1"] = "f43272e6f25f70f53756fda60aea1b16efe40456397d0010d3d170c8311f0c62",
            ["schema://pegasus-shared/confidential-envelope.v1"] = "58838bd8f7edd255cc4452406d6d83683cd1efae3106706f9283fa579d346263",
            ["schema://pegasus-shared/certificate-bootstrap.v1"] = "b31d5fe2d85b57af5449daf732e01dae63bd1c322b03db04417a7c144caf03c2",
            ["source://pegasus-nexus/VERSION.json"] = "8a55fc120a5d6f6332e28b79b77e362761d841990caa0bbcb2e8f0cb02439725",
            ["source://pegasus-scatterbrain/VERSION.json"] = "95fda5c751af742b0d5206a73ea46581c8d872cbaaac359358ffdad5e3f997d4",
            ["source://pegasus-cortex/docs/OUTCOME_AND_TRANSPORT_CONTRACTS.md"] = "cb7ec4f36c808fdb4ff4bdd3d025df119668bf29e5b8bb79d36c487a61a21bf4",
            ["source://pegasus-cortex/src/PegasusCortex.Core/Outcomes/AgentOutcomePolicy.cs"] = "a49428d0499743b9e0c43320bcbdd01ba143ee47c0d75e24f7079d77fb65343c",
            ["source://pegasus-cortex/src/PegasusCortex.Core/Security/CortexTransportSecurity.cs"] = "0c43dfa5a6848bbbf15f22373a825e55bb4bd906ee743038ddbc8e54bb5a4b06",
            ["source://pegasus-cortex/docs/DEFAULT_LIBRARY_SOURCE_POLICY.md"] = "0a56e1848b37516f796125c925e5dc56aeb8ffa9028ef87fa71fe04daafa2cd8",
            ["source://pegasus-cortex/src/PegasusCortex.DefaultLibrary/Query/KnowledgeQueryService.cs"] = "8d95320a7e0686573292a641bd982b644bd247776d1a3857886c466090723add",
            ["package://pegasus-shared/1.20260711.421-a"] = "9696db2ccc40e74649233cfbfa34b94a201d5810b71264ce62d1ae33a12a35a6",
            ["source://pegasus-shared/docs/VERIFICATION_POLICY.md"] = "4d39327365870792d36490ee97c4544ef0deebad71029f82d4a5bf6e291e15d6",
            ["source://pegasus-shared/tools/Pegasus.ReleaseTools.psm1"] = "43559735b56d86c83cae408ccff4d7824da6a30650a5d39b274fe9890ce7d115",
            ["source://pegasus-nexus/releases/1.2026.07.11.13.13.A/release-manifest.json"] = "81c002f78489b14c30f5d8863ad030f1f82995eb1ad84b8058e7cfd77e344b6e",
            ["source://pegasus-server/releases/1.2026.07.11.13.07.A/manifest.json"] = "f4d9938f969abe21c39a64bbe9fd324618052b01c01c5467ee2279dd2c0d6d89",
            ["source://pegasus-server/src/PegasusServer.Infrastructure/FileSystem/FileSystemPathBoundary.cs"] = "f04e10b7f7cbeaf6013837284235d90b5654c1e6c15ab34844c929ea9e6c70b2",
            ["source://pegasus-coder/deployment/pegasus-coder.deployment-manifest.json"] = "52e0ff1afbd1b8588e2b7a2893dcdbea813a4a942553b811ad430f194ed0950c",
            ["source://pegasus-coder/deployment/pegasus-coder.thin.deployment-manifest.json"] = "d160eae1b74dc61c559cc54a64fcf17cd98942d68e9964b965131a5323a2db65",
            ["source://pegasus-cortex/docs/DEFAULT_LIBRARY_SOURCE_POLICY_1325.md"] = "d8b333cbe3f50098014c5ba7342655a9354d9749f4ae81a1c4576396424c1e34"
        };

    public static KnowledgeBundleValidationResult Validate(KnowledgeSeedBundleV1 bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var diagnostics = new List<PegasusDiagnostic>();
        foreach (var record in bundle.Records)
        {
            foreach (var evidence in record.Evidence)
            {
                if (!Uri.TryCreate(evidence.Reference, UriKind.Absolute, out var uri) ||
                    !AllowedSchemes.Contains(uri.Scheme) ||
                    !AllowedAuthorities.Contains(uri.Host) ||
                    !AllowedArtifacts.TryGetValue(evidence.Reference, out var expectedHash) ||
                    !string.Equals(evidence.Sha256, expectedHash, StringComparison.Ordinal))
                {
                    diagnostics.Add(new PegasusDiagnostic(
                        "default_library_seed_source_forbidden",
                        "The built-in seed references an authority or artifact class outside the curated source allowlist.",
                        DiagnosticSeverity.Error,
                        "cortex.default-library.seed-source.validate",
                        retryable: false,
                        new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["recordId"] = record.RecordId,
                            ["evidenceReference"] = evidence.Reference
                        }));
                }
            }
        }
        return new KnowledgeBundleValidationResult(diagnostics.Count == 0, diagnostics);
    }

    public static void ThrowIfInvalid(KnowledgeSeedBundleV1 bundle)
    {
        var result = Validate(bundle);
        if (!result.Accepted)
            throw new InvalidOperationException(result.Diagnostics[0].Message);
    }
}
