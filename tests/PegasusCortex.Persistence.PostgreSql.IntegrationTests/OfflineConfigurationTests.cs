using System.Net;
using System.Net.Sockets;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Service.Configuration;

namespace Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;

internal static class OfflineConfigurationTests
{
    private static TestPrivatePki? _currentPki;

    private static TestPrivatePki CurrentPki => _currentPki ??
        throw new FixtureAssertionException("The offline private-PKI fixture was not initialized.");

    private static string FixtureRootCertificatePath => CurrentPki.RootCertificatePath;

    public static int Run(Action<string> reportPass)
    {
        ArgumentNullException.ThrowIfNull(reportPass);
        using var privatePki = TestPrivatePki.Create();
        _currentPki = privatePki;
        try
        {
            var tests = new (string Name, Action Run)[]
            {
                ("integration requires the exact opt-in flag", ExactOptIn),
                ("preflight-only mode requires its exact opt-in flag", ExactPreflightOptIn),
                ("enabled integration requires both connections", RequiredConnections),
                ("connection hosts must be literal loopback addresses", LoopbackOnly),
                ("fixture database names require the isolated prefix", DatabasePrefix),
                ("migration and runtime roles must differ", SeparateRoles),
                ("both connections require one qualified verified TLS root", VerifiedTlsRoot),
                ("trusted loopback selects plaintext without certificate evidence", TrustedLoopback),
                ("fresh signed CRL evidence and its bounded responder are accepted", FreshRevocationEvidence),
                ("missing or unreachable revocation evidence fails closed", RevocationEvidenceAvailabilityFailures),
                ("stale CRL evidence fails closed", StaleRevocationEvidence),
                ("invalid CRL signature evidence fails closed", InvalidRevocationSignature),
                ("revoked server certificate evidence fails closed", RevokedServerCertificate),
                ("server certificate SAN mismatch remains denied", ServerCertificateSanMismatch),
                ("server certificate chain mismatch remains denied", ServerCertificateChainMismatch),
                ("backup/restore mode and proof location fail closed", BackupRestoreConfiguration),
                ("backup/restore proof is canonical, bounded, and non-secret", BackupRestoreProofContract),
                ("backup/restore proof publication is atomic and refuses overwrite", BackupRestoreProofFileContract),
                ("backup/restore proof root and file boundaries fail closed", BackupRestoreProofPathBoundary),
                ("restore verification requires a distinct fixture database", DistinctRestoreDatabase),
                ("fixture runtime privileges use one exact bounded allowlist", RuntimePrivilegeContract),
                ("valid configuration builds one Service options boundary without I/O", ValidConfiguration)
            };

            foreach (var test in tests)
            {
                test.Run();
                reportPass(test.Name);
            }
            return tests.Length;
        }
        finally
        {
            _currentPki = null;
        }
    }

    private static void ExactOptIn()
    {
        FixtureAssert.ConfigurationFailure(
            "integration_not_enabled",
            () => FixtureConfiguration.Load(new Dictionary<string, string?>()),
            "An absent integration flag was not denied before I/O.");
        FixtureAssert.ConfigurationFailure(
            "integration_not_enabled",
            () => FixtureConfiguration.Load(Values((FixtureConfiguration.IntegrationFlag, "true"))),
            "A non-canonical integration flag was not denied before I/O.");
        FixtureAssert.True(
            !FixtureConfiguration.IntegrationEnabled(new Dictionary<string, string?>()),
            "An absent integration flag enabled live tests.");
        FixtureAssert.True(
            !FixtureConfiguration.IntegrationEnabled(Values((FixtureConfiguration.IntegrationFlag, "true"))),
            "A non-canonical integration flag enabled live tests.");
        FixtureAssert.True(
            FixtureConfiguration.IntegrationEnabled(Values((FixtureConfiguration.IntegrationFlag, "1"))),
            "The exact integration flag did not enable live tests.");
    }

    private static void RequiredConnections()
    {
        FixtureAssert.ConfigurationFailure(
            "migration_connection_missing",
            () => FixtureConfiguration.Load(Values((FixtureConfiguration.IntegrationFlag, "1"))),
            "Missing migration configuration was not rejected before I/O.");
        FixtureAssert.ConfigurationFailure(
            "runtime_connection_missing",
            () => FixtureConfiguration.Load(Values(
                (FixtureConfiguration.IntegrationFlag, "1"),
                (FixtureConfiguration.MigrationConnection, Connection("migrator")))),
            "Missing runtime configuration was not rejected before I/O.");
    }

    private static void ExactPreflightOptIn()
    {
        FixtureAssert.True(
            !FixtureConfiguration.PreflightOnlyEnabled(ValidValues()),
            "An absent preflight-only flag enabled the read-only mode.");

        var nonCanonical = ValidValues();
        nonCanonical[FixtureConfiguration.PreflightOnlyFlag] = "true";
        FixtureAssert.True(
            !FixtureConfiguration.PreflightOnlyEnabled(nonCanonical) &&
            !FixtureConfiguration.Load(nonCanonical).PreflightOnly,
            "A non-canonical preflight-only flag enabled the read-only mode.");

        var enabled = ValidValues();
        enabled[FixtureConfiguration.PreflightOnlyFlag] = "1";
        FixtureAssert.True(
            FixtureConfiguration.PreflightOnlyEnabled(enabled) &&
            FixtureConfiguration.Load(enabled).PreflightOnly,
            "The exact preflight-only flag did not survive configuration capture.");
    }

    private static void LoopbackOnly()
    {
        FixtureAssert.ConfigurationFailure(
            "runtime_endpoint_invalid",
            () => FixtureConfiguration.Load(ValidValues(runtimeHost: "localhost")),
            "A remappable localhost hostname was accepted as a literal loopback endpoint.");
        FixtureAssert.ConfigurationFailure(
            "runtime_endpoint_invalid",
            () => FixtureConfiguration.Load(ValidValues(runtimeHost: "example.invalid")),
            "A non-loopback runtime endpoint was accepted.");
    }

    private static void DatabasePrefix()
    {
        FixtureAssert.ConfigurationFailure(
            "migration_endpoint_invalid",
            () => FixtureConfiguration.Load(ValidValues(database: "cortex")),
            "A database outside the fixture prefix was accepted.");
        FixtureAssert.ConfigurationFailure(
            "migration_endpoint_invalid",
            () => FixtureConfiguration.Load(ValidValues(database: "ctx_it_bad\nname")),
            "An unsafe database identifier was accepted for proof hashing.");
    }

    private static void SeparateRoles()
    {
        FixtureAssert.ConfigurationFailure(
            "fixture_roles_not_separate",
            () => FixtureConfiguration.Load(ValidValues(runtimeUsername: "MIGRATOR")),
            "Equivalent migration and runtime role names were accepted.");
    }

    private static void VerifiedTlsRoot()
    {
        var migrationMode = ValidValues();
        migrationMode[FixtureConfiguration.MigrationConnection] = Connection(
            "migrator",
            sslMode: "Require");
        FixtureAssert.ConfigurationFailure(
            "migration_tls_policy_invalid",
            () => FixtureConfiguration.Load(migrationMode),
            "A migration connection without full certificate verification was accepted.");

        var runtimeRelativeRoot = ValidValues();
        runtimeRelativeRoot[FixtureConfiguration.RuntimeConnection] = Connection(
            "runtime",
            host: "::1",
            rootCertificate: "fixture-ca.pem");
        FixtureAssert.ConfigurationFailure(
            "runtime_root_certificate_path_invalid",
            () => FixtureConfiguration.Load(runtimeRelativeRoot),
            "A relative runtime root-certificate path was accepted.");

        var runtimeBypass = ValidValues();
        runtimeBypass[FixtureConfiguration.RuntimeConnection] = Connection(
            "runtime",
            host: "::1",
            trustServerCertificate: true);
        FixtureAssert.ConfigurationFailure(
            "runtime_tls_policy_invalid",
            () => FixtureConfiguration.Load(runtimeBypass),
            "A runtime connection with certificate trust bypass enabled was accepted.");

        var mismatchedRoots = ValidValues();
        mismatchedRoots[FixtureConfiguration.RuntimeConnection] = Connection(
            "runtime",
            host: "::1",
            rootCertificate: Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "other-ca.pem")));
        FixtureAssert.ConfigurationFailure(
            "fixture_root_certificate_mismatch",
            () => FixtureConfiguration.Load(mismatchedRoots),
            "Different migration and runtime trust roots were accepted.");
    }

    private static void ValidConfiguration()
    {
        var configuration = FixtureConfiguration.Load(ValidValues());
        FixtureAssert.Equal("ctx_it_guard", configuration.DatabaseName, "The database name changed during capture.");
        FixtureAssert.Equal("migrator", configuration.MigrationUsername, "The migration role changed during capture.");
        FixtureAssert.Equal("runtime", configuration.RuntimeUsername, "The runtime role changed during capture.");
        FixtureAssert.True(!configuration.PreflightOnly, "A normal fixture configuration changed to preflight-only mode.");
        FixtureAssert.Equal(
            FixtureRootCertificatePath,
            configuration.RootCertificatePath,
            "The canonical root-certificate path changed during capture.");
        var summary = configuration.PostgreSqlOptions.GetSanitizedSummary();
        FixtureAssert.True(
            summary.TlsPolicy == CortexPostgreSqlTlsPolicy.PrivateCaVerifyFull &&
            summary.RootCertificateConfigured,
            "The Service options boundary did not preserve the verified private-CA policy.");
        var revocationSummary = configuration.RevocationEvidence!.GetSanitizedSummary();
        FixtureAssert.True(
            revocationSummary.Profile == FixtureRevocationEvidence.ProfileV1 &&
            revocationSummary.Transport == "loopback-http-signed-crl" &&
            revocationSummary.Port == CurrentPki.DistributionPointUri.Port,
            "The private-PKI revocation contract did not survive configuration capture.");
    }

    private static void TrustedLoopback()
    {
        var values = Values(
            (FixtureConfiguration.IntegrationFlag, "1"),
            (FixtureConfiguration.TlsPolicyVariable, "trusted-loopback"),
            (FixtureConfiguration.MigrationConnection, PlaintextConnection("migrator")),
            (FixtureConfiguration.RuntimeConnection, PlaintextConnection("runtime")));
        var configuration = FixtureConfiguration.Load(values);
        var summary = configuration.PostgreSqlOptions.GetSanitizedSummary();
        FixtureAssert.True(
            configuration.TlsPolicy == CortexPostgreSqlTlsPolicy.TrustedLoopback &&
            !configuration.UsesTls &&
            string.IsNullOrEmpty(configuration.RootCertificatePath) &&
            configuration.RevocationEvidence is null &&
            !summary.RootCertificateConfigured,
            "Trusted loopback did not produce the exact certificate-free Service boundary.");

        values[FixtureRevocationEvidence.EvidenceRootVariable] = FixtureRootCertificatePath;
        FixtureAssert.ConfigurationFailure(
            "trusted_loopback_revocation_evidence_unexpected",
            () => FixtureConfiguration.Load(values),
            "Trusted loopback accepted contradictory revocation evidence.");

        var tlsEnabled = Values(
            (FixtureConfiguration.IntegrationFlag, "1"),
            (FixtureConfiguration.TlsPolicyVariable, "trusted-loopback"),
            (FixtureConfiguration.MigrationConnection, Connection("migrator")),
            (FixtureConfiguration.RuntimeConnection, Connection("runtime")));
        FixtureAssert.ConfigurationFailure(
            "migration_tls_policy_invalid",
            () => FixtureConfiguration.Load(tlsEnabled),
            "Trusted loopback accepted an enabled TLS connection.");

        var invalidPolicy = Values(
            (FixtureConfiguration.IntegrationFlag, "1"),
            (FixtureConfiguration.TlsPolicyVariable, "prefer"),
            (FixtureConfiguration.MigrationConnection, PlaintextConnection("migrator")),
            (FixtureConfiguration.RuntimeConnection, PlaintextConnection("runtime")));
        FixtureAssert.ConfigurationFailure(
            "fixture_tls_policy_invalid",
            () => FixtureConfiguration.Load(invalidPolicy),
            "An unsupported fixture transport policy was accepted.");
    }

    private static void FreshRevocationEvidence()
    {
        var configuration = FixtureConfiguration.Load(ValidValues());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var responder = FixtureRevocationResponder
            .StartAsync(configuration.RevocationEvidence!, timeout.Token)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        try
        {
            FixtureAssert.True(
                responder.ServedRequests == 1,
                "The bounded responder did not complete its exact self-probe.");
        }
        finally
        {
            responder.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var released = new TcpListener(
            IPAddress.Parse(CurrentPki.DistributionPointUri.Host),
            CurrentPki.DistributionPointUri.Port)
        {
            ExclusiveAddressUse = true
        };
        released.Start(backlog: 1);
        released.Stop();
    }

    private static void RevocationEvidenceAvailabilityFailures()
    {
        using (var missing = TestPrivatePki.Create())
        {
            File.Delete(missing.CrlPath);
            FixtureAssert.ConfigurationFailure(
                "fixture_revocation_crl_missing",
                () => FixtureConfiguration.Load(ValidValuesFor(missing)),
                "A missing signed CRL was accepted.");
        }

        var configuration = FixtureConfiguration.Load(ValidValues());
        var blocked = new TcpListener(
            IPAddress.Parse(CurrentPki.DistributionPointUri.Host),
            CurrentPki.DistributionPointUri.Port)
        {
            ExclusiveAddressUse = true
        };
        blocked.Start(backlog: 1);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            FixtureAssert.ConfigurationFailure(
                "fixture_revocation_responder_unavailable",
                () => FixtureRevocationResponder
                    .StartAsync(configuration.RevocationEvidence!, timeout.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult(),
                "An unavailable CRL responder endpoint was accepted.");
        }
        finally
        {
            blocked.Stop();
        }
    }

    private static void StaleRevocationEvidence() =>
        AssertPrivatePkiRejected(
            TestPrivatePki.Create(TestCrlState.Stale),
            "fixture_revocation_crl_stale",
            "A stale CRL was accepted.");

    private static void InvalidRevocationSignature() =>
        AssertPrivatePkiRejected(
            TestPrivatePki.Create(TestCrlState.InvalidSignature),
            "fixture_revocation_crl_signature_invalid",
            "A CRL with an invalid signature was accepted.");

    private static void RevokedServerCertificate() =>
        AssertPrivatePkiRejected(
            TestPrivatePki.Create(TestCrlState.Revoked),
            "fixture_revocation_server_certificate_revoked",
            "A revoked PostgreSQL certificate was accepted.");

    private static void ServerCertificateSanMismatch() =>
        AssertPrivatePkiRejected(
            TestPrivatePki.Create(sanMismatch: true),
            "fixture_revocation_server_certificate_san_mismatch",
            "A PostgreSQL certificate without the configured literal IP SANs was accepted.");

    private static void ServerCertificateChainMismatch() =>
        AssertPrivatePkiRejected(
            TestPrivatePki.Create(chainMismatch: true),
            "fixture_revocation_server_certificate_chain_invalid",
            "A PostgreSQL certificate issued by a different private CA was accepted.");

    private static void AssertPrivatePkiRejected(
        TestPrivatePki privatePki,
        string expectedCode,
        string message)
    {
        using (privatePki)
        {
            FixtureAssert.ConfigurationFailure(
                expectedCode,
                () => FixtureConfiguration.Load(ValidValuesFor(privatePki)),
                message);
        }
    }

    private static void BackupRestoreConfiguration()
    {
        var invalidMode = ValidValues();
        invalidMode[FixtureConfiguration.BackupRestoreModeVariable] = "verify";
        FixtureAssert.ConfigurationFailure(
            "backup_restore_mode_invalid",
            () => FixtureConfiguration.Load(invalidMode),
            "A non-canonical backup/restore mode was accepted.");

        var missingRoot = ValidValues();
        missingRoot[FixtureConfiguration.BackupRestoreModeVariable] = "capture";
        missingRoot[FixtureConfiguration.BackupRestoreProofFile] = "capture.json";
        FixtureAssert.ConfigurationFailure(
            "backup_restore_proof_root_missing",
            () => FixtureConfiguration.Load(missingRoot),
            "Capture mode was accepted without a trusted proof root.");

        var missingFile = ValidValues();
        missingFile[FixtureConfiguration.BackupRestoreModeVariable] = "capture";
        missingFile[FixtureConfiguration.BackupRestoreProofRoot] = ProofRoot();
        FixtureAssert.ConfigurationFailure(
            "backup_restore_proof_file_missing",
            () => FixtureConfiguration.Load(missingFile),
            "Capture mode was accepted without a proof leaf.");

        var unexpectedProof = ValidValues();
        unexpectedProof[FixtureConfiguration.BackupRestoreProofRoot] = ProofRoot();
        unexpectedProof[FixtureConfiguration.BackupRestoreProofFile] = "unexpected.json";
        FixtureAssert.ConfigurationFailure(
            "backup_restore_proof_location_unexpected",
            () => FixtureConfiguration.Load(unexpectedProof),
            "A proof location without an explicit backup/restore mode was accepted.");

        var legacyProof = ValidValues();
        legacyProof[FixtureConfiguration.LegacyBackupRestoreProof] =
            Path.Combine(ProofRoot(), "legacy.json");
        FixtureAssert.ConfigurationFailure(
            "backup_restore_legacy_proof_variable_unsupported",
            () => FixtureConfiguration.Load(legacyProof),
            "The removed arbitrary proof-path variable was silently accepted.");

        AssertInvalidProofLocation("relative-root", "proof.json", "A relative root was accepted.");
        AssertInvalidProofLocation(
            ProofRoot() + Path.DirectorySeparatorChar,
            "proof.json",
            "A non-canonical root with a trailing separator was accepted.");
        AssertInvalidProofLocation(ProofRoot(), "subdir/proof.json", "A multi-segment leaf was accepted.");
        AssertInvalidProofLocation(ProofRoot(), "Proof.json", "An uppercase leaf was accepted.");
        AssertInvalidProofLocation(ProofRoot(), "con.json", "A reserved Windows leaf was accepted.");
        AssertInvalidProofLocation(ProofRoot(), "proof.json:ads", "An ADS leaf was accepted.");
        AssertInvalidProofLocation(ProofRoot(), "proof .json", "A normalization-unsafe leaf was accepted.");
        AssertInvalidProofLocation(ProofRoot(), "proof.txt", "A non-JSON proof leaf was accepted.");
        AssertInvalidProofLocation(
            ProofRoot(),
            new string('a', FixtureBackupRestoreProofFile.MaximumLeafCharacters - 4) + ".json",
            "An overlong proof leaf was accepted.");
        AssertInvalidProofLocation(
            Path.Combine(ProofRoot(), new string('a', 220)),
            "proof.json",
            "A proof path above the 240-character boundary was accepted.");

        var conflictingPreflight = ValidValues();
        conflictingPreflight[FixtureConfiguration.PreflightOnlyFlag] = "1";
        SetProofLocation(conflictingPreflight, "verify-restored-state", "conflict.json");
        FixtureAssert.ConfigurationFailure(
            "backup_restore_preflight_conflict",
            () => FixtureConfiguration.Load(conflictingPreflight),
            "Restore verification was ambiguously combined with pristine preflight mode.");

        var capture = ValidValues();
        SetProofLocation(capture, "capture", "capture.json");
        var captureConfiguration = FixtureConfiguration.Load(capture);
        FixtureAssert.True(
            captureConfiguration.BackupRestoreMode == FixtureBackupRestoreMode.Capture &&
            string.Equals(
                captureConfiguration.BackupRestoreProofLocation!.RootPath,
                ProofRoot(),
                StringComparison.Ordinal) &&
            string.Equals(
                captureConfiguration.BackupRestoreProofLocation.FileName,
                "capture.json",
                StringComparison.Ordinal),
            "The exact capture mode and proof location did not survive configuration capture.");

        var verify = ValidValues(database: "ctx_it_restored");
        SetProofLocation(verify, "verify-restored-state", "verify.json");
        FixtureAssert.True(
            FixtureConfiguration.Load(verify).BackupRestoreMode ==
                FixtureBackupRestoreMode.VerifyRestoredState,
            "The exact restored-state equivalence mode did not survive configuration capture.");
    }

    private static void BackupRestoreProofContract()
    {
        var proof = SampleProof();
        FixtureBackupRestoreProofContract.EnsureValid(proof);
        var first = CortexCanonicalJson.Serialize(proof);
        var second = CortexCanonicalJson.Serialize(proof);
        FixtureAssert.SequenceEqual(first, second, "Proof serialization was not deterministic.");
        FixtureAssert.True(
            first.Length <= FixtureBackupRestoreProofFile.MaximumProofBytes,
            "The canonical proof exceeded its file boundary.");

        var text = Encoding.UTF8.GetString(first);
        FixtureAssert.True(
            !text.Contains("password", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("connection", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("rootCertificate", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("127.0.0.1", StringComparison.Ordinal) &&
            !text.Contains(FixtureRootCertificatePath, StringComparison.Ordinal),
            "The backup/restore proof leaked connection or trust-path material.");

        var changedIdentity = proof with
        {
            SourceDatabaseIdentitySha256 = CortexCanonicalJson.Sha256("different-database")
        };
        FixtureAssert.ConfigurationFailure(
            "backup_restore_proof_hash_mismatch",
            () => FixtureBackupRestoreProofContract.EnsureValid(changedIdentity),
            "A proof change without resealing was accepted.");
    }

    private static void BackupRestoreProofFileContract()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ctxpf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SetPrivateDirectory(directory);
        var location = FixtureBackupRestoreProofLocation.Parse(directory, "proof.json");
        var nonCanonicalLocation = FixtureBackupRestoreProofLocation.Parse(
            directory,
            "noncanonical.json");
        try
        {
            var proof = SampleProof();
            FixtureBackupRestoreProofFile.ValidateCaptureTarget(location);
            FixtureBackupRestoreProofFile.WriteNew(location, proof);
            if (!OperatingSystem.IsWindows())
            {
                FixtureAssert.True(
                    File.GetUnixFileMode(location.FullPath) ==
                        (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    "The published proof file was not private to the current Unix user.");
            }
            FixtureAssert.Equal(
                proof.ProofSha256,
                FixtureBackupRestoreProofFile.Read(location).ProofSha256,
                "The published proof did not round-trip exactly.");
            FixtureAssert.ConfigurationFailure(
                "backup_restore_proof_exists",
                () => FixtureBackupRestoreProofFile.WriteNew(location, proof),
                "Proof publication overwrote an existing handoff.");
            FixtureAssert.Equal(
                0,
                Directory.GetFiles(directory, ".ctxpf_*.tmp").Length,
                "Proof publication left a fixture-owned temporary file behind.");

            var canonical = CortexCanonicalJson.Serialize(proof);
            using (var stream = new FileStream(
                nonCanonicalLocation.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(canonical);
                stream.WriteByte((byte)'\n');
            }
            SetPrivateFile(nonCanonicalLocation.FullPath);
            FixtureAssert.ConfigurationFailure(
                "backup_restore_proof_not_canonical",
                () => FixtureBackupRestoreProofFile.Read(nonCanonicalLocation),
                "A non-canonical proof file was accepted.");
        }
        finally
        {
            if (File.Exists(location.FullPath))
                File.Delete(location.FullPath);
            if (File.Exists(nonCanonicalLocation.FullPath))
                File.Delete(nonCanonicalLocation.FullPath);
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void BackupRestoreProofPathBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "ctxpb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SetPrivateDirectory(root);
        try
        {
            var missing = FixtureBackupRestoreProofLocation.Parse(root, "missing.json");
            FixtureAssert.ConfigurationFailure(
                "backup_restore_proof_unavailable",
                () => FixtureBackupRestoreProofFile.Read(missing),
                "A missing proof file did not fail closed.");

            var directoryLeaf = FixtureBackupRestoreProofLocation.Parse(root, "directory.json");
            Directory.CreateDirectory(directoryLeaf.FullPath);
            FixtureAssert.ConfigurationFailure(
                "backup_restore_proof_file_not_regular",
                () => FixtureBackupRestoreProofFile.Read(directoryLeaf),
                "A directory was accepted as a regular proof file.");
            Directory.Delete(directoryLeaf.FullPath);

            if (!OperatingSystem.IsWindows())
            {
                var looseRoot = Path.Combine(root, "loose-root");
                Directory.CreateDirectory(looseRoot);
                File.SetUnixFileMode(
                    looseRoot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                var looseLocation = FixtureBackupRestoreProofLocation.Parse(looseRoot, "proof.json");
                FixtureAssert.ConfigurationFailure(
                    "backup_restore_proof_root_permissions_invalid",
                    () => FixtureBackupRestoreProofFile.ValidateCaptureTarget(looseLocation),
                    "A group-accessible proof root was accepted.");
                Directory.Delete(looseRoot);

                var looseFile = FixtureBackupRestoreProofLocation.Parse(root, "loose-file.json");
                FixtureBackupRestoreProofFile.WriteNew(looseFile, SampleProof());
                File.SetUnixFileMode(
                    looseFile.FullPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                FixtureAssert.ConfigurationFailure(
                    "backup_restore_proof_file_permissions_invalid",
                    () => FixtureBackupRestoreProofFile.Read(looseFile),
                    "A group-readable proof file was accepted.");
                File.Delete(looseFile.FullPath);
            }

            VerifySymlinkDenialsWhenSupported(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DistinctRestoreDatabase()
    {
        var proof = SampleProof();
        FixtureAssert.ConfigurationFailure(
            "backup_restore_database_not_distinct",
            () => FixtureBackupRestoreProofContract.EnsureValidForRestore(proof, "ctx_it_source"),
            "Restored-state equivalence accepted the source fixture database as its target.");
        FixtureBackupRestoreProofContract.EnsureValidForRestore(proof, "ctx_it_restored");
    }

    private static void RuntimePrivilegeContract()
    {
        string[] expectedSchemas =
        [
            "cortex_meta",
            "cortex_data",
            "cortex_index",
            "cortex_private",
            "cortex_sync"
        ];
        string[] expectedReadTables =
        [
            "cortex_meta.schema_migrations",
            "cortex_meta.rls_policy_contract",
            "cortex_meta.privileged_function_contract",
            "cortex_data.collection_snapshot",
            "cortex_data.collection_head",
            "cortex_data.knowledge_revision",
            "cortex_data.knowledge_head",
            "cortex_data.knowledge_link",
            "cortex_data.learning_proposal",
            "cortex_data.learning_decision",
            "cortex_data.audit_event",
            "cortex_data.project_link",
            "cortex_data.retained_chain",
            "cortex_data.retained_document",
            "cortex_data.retention_audit",
            "cortex_index.generation_state",
            "cortex_sync.journal_batch",
            "cortex_sync.journal_entry",
            "cortex_sync.journal_commit",
            "cortex_sync.journal_outcome",
            "cortex_sync.journal_source_head",
            "cortex_sync.journal_ack",
            "cortex_sync.rls_policy_contract",
            "cortex_sync.schema_object_contract"
        ];
        string[] expectedAppendTables =
        [
            "cortex_data.learning_proposal",
            "cortex_data.learning_decision",
            "cortex_data.project_link",
            "cortex_data.knowledge_revision",
            "cortex_data.knowledge_head",
            "cortex_data.audit_event",
            "cortex_data.retained_chain",
            "cortex_data.retained_document",
            "cortex_data.retention_audit",
            "cortex_sync.journal_batch",
            "cortex_sync.journal_entry",
            "cortex_sync.journal_commit",
            "cortex_sync.journal_outcome",
            "cortex_sync.journal_source_head",
            "cortex_sync.journal_ack"
        ];
        string[] expectedProtectedTables =
        [
            "cortex_data.knowledge_publication_origin",
            "cortex_data.knowledge_publication_change"
        ];
        string[] expectedFunctions =
        [
            "cortex_private.sensitivity_rank(text)",
            "cortex_private.register_knowledge_publication_origin(text,text,text,text,text,text,text,text,bigint,timestamp with time zone)",
            "cortex_private.apply_knowledge_publication_change(text,text,text,text,text,bigint,text,text,text,text,text,timestamp with time zone,text,text,text,text,text,text,text,text,text,text,jsonb,bytea,text,integer,text,text,bytea,bytea,text,integer,text,text,bytea)"
        ];
        FixtureAssert.SequenceEqual(
            expectedSchemas,
            FixtureRuntimePrivilegeBootstrap.RequiredSchemas,
            "The fixture schema allowlist changed without an explicit contract update.");
        FixtureAssert.SequenceEqual(
            expectedReadTables,
            FixtureRuntimePrivilegeBootstrap.RequiredReadTables,
            "The fixture read allowlist changed without an explicit contract update.");
        FixtureAssert.SequenceEqual(
            expectedAppendTables,
            FixtureRuntimePrivilegeBootstrap.RequiredAppendTables,
            "The fixture append allowlist changed without an explicit contract update.");
        FixtureAssert.SequenceEqual(
            expectedProtectedTables,
            FixtureRuntimePrivilegeBootstrap.RequiredProtectedTables,
            "The fixture protected-table allowlist changed without an explicit contract update.");
        FixtureAssert.SequenceEqual(
            expectedFunctions,
            FixtureRuntimePrivilegeBootstrap.RequiredRuntimeFunctions,
            "The fixture function allowlist changed without an explicit contract update.");
        FixtureAssert.True(
            FixtureRuntimePrivilegeBootstrap.RequiredSchemas.Distinct(StringComparer.Ordinal).Count() ==
                FixtureRuntimePrivilegeBootstrap.RequiredSchemas.Count &&
            FixtureRuntimePrivilegeBootstrap.RequiredReadTables.Distinct(StringComparer.Ordinal).Count() ==
                FixtureRuntimePrivilegeBootstrap.RequiredReadTables.Count &&
            FixtureRuntimePrivilegeBootstrap.RequiredAppendTables.Distinct(StringComparer.Ordinal).Count() ==
                FixtureRuntimePrivilegeBootstrap.RequiredAppendTables.Count &&
            FixtureRuntimePrivilegeBootstrap.RequiredProtectedTables.Distinct(StringComparer.Ordinal).Count() ==
                FixtureRuntimePrivilegeBootstrap.RequiredProtectedTables.Count &&
            FixtureRuntimePrivilegeBootstrap.RequiredRuntimeFunctions.Distinct(StringComparer.Ordinal).Count() ==
                FixtureRuntimePrivilegeBootstrap.RequiredRuntimeFunctions.Count &&
            FixtureRuntimePrivilegeBootstrap.RequiredAppendTables.All(table =>
                FixtureRuntimePrivilegeBootstrap.RequiredReadTables.Contains(table, StringComparer.Ordinal)) &&
            !FixtureRuntimePrivilegeBootstrap.RequiredProtectedTables.Any(table =>
                FixtureRuntimePrivilegeBootstrap.RequiredReadTables.Contains(table, StringComparer.Ordinal) ||
                FixtureRuntimePrivilegeBootstrap.RequiredAppendTables.Contains(table, StringComparer.Ordinal)),
            "The fixture privilege allowlist contains duplicates or write-only tables.");
    }

    private static FixtureBackupRestoreProofV1 SampleProof()
    {
        var attempts = new[]
        {
            Attempt("bundle-origin", "committed", "origin"),
            Attempt("bundle-origin", "conflict", "identity-conflict"),
            Attempt("bundle-stale-origin", "conflict", "head-conflict"),
            Attempt("bundle-continuation", "committed", "continuation"),
            Attempt("bundle-branch-a", "committed", "branch-a"),
            Attempt("bundle-branch-b", "conflict", "branch-b")
        };
        var origin = attempts[0];
        var continuation = attempts[3];
        var winner = attempts[4];
        return FixtureBackupRestoreProofContract.Create(
            "ctx_it_source",
            new FixtureSyncWorkflowEvidence(
                attempts,
                new FixtureReceiptProofV1(
                    origin.BundleId,
                    origin.BundleHash,
                    origin.CommitId,
                    origin.StorageRevision,
                    "receipt-" + Hash("receipt-id"),
                    Hash("receipt"),
                    Hash("canonical-receipt")),
                new FixtureSourceHeadProofV1(
                    continuation.StorageRevision,
                    winner.BundleId,
                    winner.BundleHash,
                    winner.CommitId,
                    winner.StorageRevision,
                    SourceSequence: 3,
                    EventId: "event-branch-a",
                    EventHash: Hash("event-branch-a"))));
    }

    private static FixtureDurableAttemptProofV1 Attempt(
        string bundleId,
        string disposition,
        string identity) =>
        new(
            bundleId,
            Hash("bundle-" + identity),
            "cm_" + Hash("commit-" + identity),
            "sr_" + Hash("storage-" + identity),
            disposition,
            SourceEventCount: 1,
            DurableStateHash: Hash("durable-" + identity));

    private static string Hash(string value) => CortexCanonicalJson.Sha256(value);

    private static void AssertInvalidProofLocation(string root, string fileName, string message)
    {
        try
        {
            _ = FixtureBackupRestoreProofLocation.Parse(root, fileName);
        }
        catch (FixtureConfigurationException)
        {
            return;
        }
        throw new FixtureAssertionException(message);
    }

    private static void SetProofLocation(
        IDictionary<string, string?> values,
        string mode,
        string fileName)
    {
        values[FixtureConfiguration.BackupRestoreModeVariable] = mode;
        values[FixtureConfiguration.BackupRestoreProofRoot] = ProofRoot();
        values[FixtureConfiguration.BackupRestoreProofFile] = fileName;
    }

    private static string ProofRoot() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void VerifySymlinkDenialsWhenSupported(string root)
    {
        var targetRoot = Path.Combine(root, "target-root");
        var rootLink = Path.Combine(root, "root-link");
        var targetParent = Path.Combine(root, "target-parent");
        var targetStage = Path.Combine(targetParent, "stage");
        var ancestorLink = Path.Combine(root, "ancestor-link");
        var fileTarget = Path.Combine(root, "target.json");
        var fileLink = Path.Combine(root, "file-link.json");
        Directory.CreateDirectory(targetRoot);
        SetPrivateDirectory(targetRoot);
        Directory.CreateDirectory(targetParent);
        SetPrivateDirectory(targetParent);
        Directory.CreateDirectory(targetStage);
        SetPrivateDirectory(targetStage);
        File.WriteAllBytes(fileTarget, [0x7B, 0x7D]);
        SetPrivateFile(fileTarget);

        var fileLinkCreated = false;
        var rootLinkCreated = false;
        var ancestorLinkCreated = false;
        try
        {
            try
            {
                File.CreateSymbolicLink(fileLink, fileTarget);
                fileLinkCreated = true;
                var fileLocation = FixtureBackupRestoreProofLocation.Parse(root, "file-link.json");
                FixtureAssert.ConfigurationFailure(
                    "backup_restore_proof_file_unsafe",
                    () => FixtureBackupRestoreProofFile.Read(fileLocation),
                    "A symbolic-link proof file was accepted.");

                Directory.CreateSymbolicLink(rootLink, targetRoot);
                rootLinkCreated = true;
                var rootLocation = FixtureBackupRestoreProofLocation.Parse(rootLink, "proof.json");
                FixtureAssert.ConfigurationFailure(
                    "backup_restore_proof_root_unsafe",
                    () => FixtureBackupRestoreProofFile.ValidateCaptureTarget(rootLocation),
                    "A symbolic-link proof root was accepted.");

                Directory.CreateSymbolicLink(ancestorLink, targetParent);
                ancestorLinkCreated = true;
                var ancestorLocation = FixtureBackupRestoreProofLocation.Parse(
                    Path.Combine(ancestorLink, "stage"),
                    "proof.json");
                FixtureAssert.ConfigurationFailure(
                    "backup_restore_proof_ancestor_unsafe",
                    () => FixtureBackupRestoreProofFile.ValidateCaptureTarget(ancestorLocation),
                    "A proof root below a symbolic-link ancestor was accepted.");
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Some Windows hosts do not grant symlink creation to the test identity.
            }
        }
        finally
        {
            if (fileLinkCreated && File.Exists(fileLink))
                File.Delete(fileLink);
            if (rootLinkCreated && Directory.Exists(rootLink))
                Directory.Delete(rootLink);
            if (ancestorLinkCreated && Directory.Exists(ancestorLink))
                Directory.Delete(ancestorLink);
            if (File.Exists(fileTarget))
                File.Delete(fileTarget);
            if (Directory.Exists(targetRoot))
                Directory.Delete(targetRoot);
            if (Directory.Exists(targetStage))
                Directory.Delete(targetStage);
            if (Directory.Exists(targetParent))
                Directory.Delete(targetParent);
        }
    }

    private static Dictionary<string, string?> ValidValues(
        string database = "ctx_it_guard",
        string migrationHost = "127.0.0.1",
        string runtimeHost = "::1",
        string runtimeUsername = "runtime") =>
        ValidValuesFor(CurrentPki, database, migrationHost, runtimeHost, runtimeUsername);

    private static Dictionary<string, string?> ValidValuesFor(
        TestPrivatePki privatePki,
        string database = "ctx_it_guard",
        string migrationHost = "127.0.0.1",
        string runtimeHost = "::1",
        string runtimeUsername = "runtime")
    {
        var values = Values(
            (FixtureConfiguration.IntegrationFlag, "1"),
            (FixtureConfiguration.MigrationConnection, Connection(
                "migrator",
                migrationHost,
                database,
                rootCertificate: privatePki.RootCertificatePath)),
            (FixtureConfiguration.RuntimeConnection, Connection(
                runtimeUsername,
                runtimeHost,
                database,
                rootCertificate: privatePki.RootCertificatePath)));
        privatePki.AddEnvironment(values);
        return values;
    }

    private static string Connection(
        string username,
        string host = "127.0.0.1",
        string database = "ctx_it_guard",
        string sslMode = "VerifyFull",
        string? rootCertificate = null,
        bool trustServerCertificate = false) =>
        $"Host={host};Port=5432;Database={database};Username={username};Password=offline-placeholder;" +
        $"Ssl Mode={sslMode};Root Certificate={rootCertificate ?? FixtureRootCertificatePath};" +
        $"Trust Server Certificate={trustServerCertificate}";

    private static string PlaintextConnection(string username) =>
        $"Host=127.0.0.1;Port=5432;Database=ctx_it_guard;Username={username};" +
        "Password=offline-placeholder;Ssl Mode=Disable";

    private static Dictionary<string, string?> Values(params (string Name, string? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
}
