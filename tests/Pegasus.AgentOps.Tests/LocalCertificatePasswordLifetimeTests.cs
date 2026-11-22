using Pegasus.AgentOps.Engine;

internal static class LocalCertificatePasswordLifetimeTests
{
    internal static Task RunAsync()=>RunCoreAsync(setup:false);
    internal static Task RunMcpSetupAsync()=>RunCoreAsync(setup:true);
    private static async Task RunCoreAsync(bool setup)
    {
        var root = Directory.CreateTempSubdirectory("cortex-password-lifetime-").FullName;
        try
        {
            var gate = new UserHostAdmission();
            var mode = new Mode();
            var peer = new LocalPeer("S-1-5-21-1-2-3-1001", 7, Elevated: false);
            await using (var engine = new AgentOpsEngine(root, null, false, null, false, null,
                maintenanceMode: mode, userHostAdmission: gate))
            {
                Func<IDisposable> begin = setup ? engine.BeginLocalMcpSetupOperation : engine.BeginLocalCertificatePasswordOperation;
                var denied = setup ? "certificate_setup_owner_denied" : "certificate_secret_owner_denied";
                Refused(begin, denied);
                engine.BindCertificateOwner(new(peer.Sid, () => peer,
                    () => throw new InvalidOperationException("repository_must_not_be_opened"),
                    (_, _) => throw new InvalidOperationException("signing_must_not_run")));
                var operation = begin();
                Check(gate.ActiveCount == 1 && await engine.InspectExitIssueAsync(CancellationToken.None) == "work_active",
                    "local credential operation was invisible to owner and maintenance inspection");
                using (var closing = gate.TryClose() ?? throw new Exception("close_failed"))
                {
                    Check(!closing.WhenIdleAsync(CancellationToken.None).IsCompleted,
                        "owner drained while local credential operation was still held");
                    Refused(begin, "host_stopping");
                    operation.Dispose();
                    operation.Dispose();
                    await closing.WhenIdleAsync(CancellationToken.None);
                    Check(gate.ActiveCount == 0 && await engine.UserExitBlockerAsync(CancellationToken.None) is null,
                        "released local credential operation retained ownership or blocked idle Exit");
                }
                mode.Phase = "held";
                Refused(begin, "maintenance_busy");
                Check(gate.ActiveCount == 0, "maintenance refusal leaked admission");
                mode.Phase = "off";
                peer = peer with { Elevated = true };
                Refused(begin, denied);
                peer = peer with { Elevated = false, Session = 8 };
                Refused(begin, denied);
                Check(gate.ActiveCount == 0, "identity refusal acquired owner admission");
            }
        }
        finally
        {
            var canonical = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(canonical).StartsWith("cortex-password-lifetime-", StringComparison.Ordinal) ||
                (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("owned_fixture_cleanup_boundary_invalid");
            Directory.Delete(canonical, recursive: true);
        }
        Console.WriteLine("PASS local "+(setup?"MCP setup":"certificate-password")+" lifetime: owned SQLite + injected identity/barrier; active owner/maintenance visibility, actual drain, closed admission and identity refusal; no credential access");
    }

    private static void Refused(Func<IDisposable> begin, string code)
    {
        try { using var unexpected = begin(); }
        catch (AgentOpsException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("local_password_admission_refusal_missing");
    }

    private sealed class Mode : IMaintenanceModeSource
    {
        internal string Phase = "off";
        public MaintenanceModeRecord? Read() => Phase == "off" ? null :
            new(1, new string('1', 32), Phase, new string('2', 32), new string('3', 32), "scope",
                Guid.Empty.ToString("D"), "fixture", 7, "service", null, 1);
        public bool IsAcknowledged(MaintenanceEffect effect) => false;
    }
    private static void Check(bool value, string code) { if (!value) throw new InvalidOperationException(code); }
}
