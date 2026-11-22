using System.Data.Common;
using Npgsql;
using Pegasus.Cortex.Persistence.PostgreSql.Diagnostics;
using Pegasus.Cortex.Persistence.PostgreSql.IntegrationTests;
using Pegasus.Cortex.Service.Configuration;

int offlineCount;
try
{
    offlineCount = OfflineConfigurationTests.Run(
        name => Console.WriteLine($"PASS OFFLINE {name}"));
}
catch (FixtureAssertionException exception)
{
    Console.Error.WriteLine($"FAIL OFFLINE {exception.Message}");
    return 1;
}
catch (FixtureConfigurationException exception)
{
    Console.Error.WriteLine($"FAIL OFFLINE code={exception.Code}");
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL OFFLINE unexpected_type={SafeType(exception)}");
    return 1;
}

IReadOnlyDictionary<string, string?> flagOnly = new Dictionary<string, string?>(StringComparer.Ordinal)
{
    [FixtureConfiguration.IntegrationFlag] =
        Environment.GetEnvironmentVariable(FixtureConfiguration.IntegrationFlag)
};
if (!FixtureConfiguration.IntegrationEnabled(flagOnly))
{
    Console.WriteLine(
        $"SKIP LIVE set {FixtureConfiguration.IntegrationFlag}=1 and supply isolated loopback connections plus an explicit transport profile");
    Console.WriteLine($"RESULT offline={offlineCount} live=skipped");
    return 0;
}

var values = FixtureConfiguration.CaptureEnvironment();
FixtureConfiguration configuration;
try
{
    configuration = FixtureConfiguration.Load(values);
    // Release the fixture's captured string references before any network operation.
    values = new Dictionary<string, string?>();
}
catch (FixtureConfigurationException exception)
{
    Console.Error.WriteLine($"FAIL CONFIG code={exception.Code}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL CONFIG unexpected_type={SafeType(exception)}");
    return 2;
}

FixtureBackupRestoreProofV1? restoreProof = null;
try
{
    if (configuration.BackupRestoreMode == FixtureBackupRestoreMode.Capture)
    {
        FixtureBackupRestoreProofFile.ValidateCaptureTarget(configuration.BackupRestoreProofLocation!);
    }
    else if (configuration.BackupRestoreMode == FixtureBackupRestoreMode.VerifyRestoredState)
    {
        restoreProof = FixtureBackupRestoreProofFile.Read(configuration.BackupRestoreProofLocation!);
        FixtureBackupRestoreProofContract.EnsureValidForRestore(
            restoreProof,
            configuration.DatabaseName);
    }
}
catch (FixtureConfigurationException exception)
{
    Console.Error.WriteLine($"FAIL CONFIG code={exception.Code}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL CONFIG unexpected_type={SafeType(exception)}");
    return 2;
}

using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
{
    try
    {
        await using var revocationResponder = configuration.RevocationEvidence is null
            ? null
            : await FixtureRevocationResponder
                .StartAsync(configuration.RevocationEvidence, timeout.Token)
                .ConfigureAwait(false);
        Console.WriteLine(configuration.RevocationEvidence is null
            ? "PASS LIVE trusted-loopback plaintext transport selected"
            : "PASS LIVE private-PKI signed CRL responder ready");

        if (configuration.PreflightOnly)
        {
            await LivePostgreSqlFixture.RunPreflightAsync(
                configuration,
                name => Console.WriteLine($"PASS PREFLIGHT {name}"),
                timeout.Token).ConfigureAwait(false);
            Console.WriteLine($"RESULT offline={offlineCount} live=preflight_passed");
            return 0;
        }

        if (configuration.BackupRestoreMode == FixtureBackupRestoreMode.VerifyRestoredState)
        {
            await FixtureRestoredStateEquivalenceWorkflow.RunAsync(
                    configuration,
                    restoreProof!,
                    name => Console.WriteLine($"PASS EQUIVALENCE {name}"),
                    timeout.Token)
                .ConfigureAwait(false);
            Console.WriteLine($"RESULT offline={offlineCount} live=restored_state_equivalent");
            return 0;
        }

        await LivePostgreSqlFixture.RunAsync(
                configuration,
                name => Console.WriteLine($"PASS LIVE {name}"),
                timeout.Token)
            .ConfigureAwait(false);
        Console.WriteLine($"RESULT offline={offlineCount} live=passed");
        return 0;
    }
    catch (FixtureAssertionException exception)
    {
        Console.Error.WriteLine($"FAIL LIVE assertion={exception.Message}");
    }
    catch (FixtureConfigurationException exception)
    {
            Console.Error.WriteLine(
                $"FAIL LIVE code={exception.Code} operation=postgresql-transport");
    }
    catch (PostgreSqlPersistenceException exception)
    {
        Console.Error.WriteLine(
            $"FAIL LIVE code={exception.Code} operation={exception.Operation} sql_state={exception.SqlState ?? "none"}");
    }
    catch (CortexPostgreSqlConfigurationException exception)
    {
        Console.Error.WriteLine(
            $"FAIL LIVE code={exception.Code} operation={exception.Operation}");
    }
    catch (NpgsqlException exception) when (exception.SqlState is null)
    {
        Console.Error.WriteLine(
            "FAIL LIVE code=cortex_postgresql_tls_pre_authentication_failed operation=postgresql-fixture tls=verify-full revocation=required");
    }
    catch (DbException exception)
    {
        Console.Error.WriteLine(
            $"FAIL LIVE database_type={SafeType(exception)} sql_state={exception.SqlState ?? "none"}");
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("FAIL LIVE operation=fixture code=cancelled_or_timed_out");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL LIVE unexpected_type={SafeType(exception)}");
    }
}

return 3;

static string SafeType(Exception exception) =>
    exception.GetType().FullName ?? exception.GetType().Name;
