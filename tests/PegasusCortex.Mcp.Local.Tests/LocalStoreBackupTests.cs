using System.Text.Json.Nodes;
using Pegasus.Cortex.Mcp.Local.Configuration;

internal static class LocalStoreBackupTests
{
    public static async Task ExactBackupAndCreateOnlyRestoreAsync()
    {
        await using var context = await LocalTestContext.CreateAsync();
        var stateRoot = Path.GetDirectoryName(context.DatabasePath)!;
        var started = await LocalProcessHarness.RunAsync(
            [
                "stdio", "--profile", context.ProfileId,
                "--state-root", stateRoot
            ],
            []).ConfigureAwait(false);
        LocalAssert.Equal(0, started.ExitCode, "The backup fixture did not initialize.");

        var backupBase = Path.Combine(context.Root, "store-backups");
        Directory.CreateDirectory(backupBase);
        CortexLocalRegistryAclPolicy.ProtectDirectory(backupBase);
        var backedUp = await LocalProcessHarness.RunAsync(
            [
                "backup-local",
                "--profile", context.ProfileId,
                "--state-root", stateRoot,
                "--destination-root", backupBase,
                "--run-id", "exact-local-backup",
                "--captured-at-utc", DateTimeOffset.UtcNow.ToString("O")
            ],
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            backedUp.ExitCode,
            $"The exact local backup failed: {backedUp.StandardError}");
        var result = JsonNode.Parse(backedUp.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("The local backup result is not JSON.");
        LocalAssert.True(result["accepted"]!.GetValue<bool>(), "The backup was not accepted.");
        var backupRoot = Path.Combine(
            backupBase,
            result["backupRelativePath"]!.GetValue<string>());

        var restoreParent = Path.Combine(context.Root, "store-restore");
        Directory.CreateDirectory(restoreParent);
        CortexLocalRegistryAclPolicy.ProtectDirectory(restoreParent);
        var destination = Path.Combine(restoreParent, context.ProfileId);
        var restoreArguments = new[]
        {
            "restore-local-backup",
            "--profile", context.ProfileId,
            "--destination-state-root", destination,
            "--backup-root", backupRoot
        };
        var restored = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            restored.ExitCode,
            $"The exact local restore failed: {restored.StandardError}");
        var restoredResult = JsonNode.Parse(restored.StandardOutput)?.AsObject() ??
            throw new InvalidDataException("The local restore result is not JSON.");
        LocalAssert.Equal(
            result["databaseSha256"]!.GetValue<string>(),
            restoredResult["databaseSha256"]!.GetValue<string>(),
            "The restore did not attest the exact database hash.");
        LocalAssert.Equal(
            result["registrySha256"]!.GetValue<string>(),
            restoredResult["registrySha256"]!.GetValue<string>(),
            "The restore did not attest the exact registry hash.");

        var restarted = await LocalProcessHarness.RunAsync(
            [
                "stdio", "--profile", context.ProfileId,
                "--state-root", destination
            ],
            []).ConfigureAwait(false);
        LocalAssert.Equal(
            0,
            restarted.ExitCode,
            $"The restored profile failed normal startup: {restarted.StandardError}");

        var replay = await LocalProcessHarness.RunAsync(
            restoreArguments,
            []).ConfigureAwait(false);
        LocalAssert.Equal(1, replay.ExitCode, "Restore replaced an existing profile.");

        var manifestPath = Path.Combine(backupRoot, "manifest.json");
        var manifest = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        manifest[^2] ^= 1;
        await File.WriteAllBytesAsync(manifestPath, manifest).ConfigureAwait(false);
        var secondParent = Path.Combine(context.Root, "tamper-restore");
        Directory.CreateDirectory(secondParent);
        CortexLocalRegistryAclPolicy.ProtectDirectory(secondParent);
        var tampered = await LocalProcessHarness.RunAsync(
            [
                "restore-local-backup",
                "--profile", context.ProfileId,
                "--destination-state-root", Path.Combine(secondParent, context.ProfileId),
                "--backup-root", backupRoot
            ],
            []).ConfigureAwait(false);
        LocalAssert.Equal(1, tampered.ExitCode, "Restore accepted a tampered manifest.");
    }
}
