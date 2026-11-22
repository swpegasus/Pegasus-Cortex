using System.Drawing.Imaging;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Contracts.V2;

internal static class DashboardTestProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args is ["--about-only"]) return CortexAboutDialogTests.Run();
        if (args is ["--about-only", var aboutImages]) return CortexAboutDialogTests.Run(aboutImages);
        if (args is ["--presentation-only"]) return CortexPresentationTests.Run();
        if (args is ["--presentation-only", var presentationImages]) return CortexPresentationTests.Run(presentationImages);
        if (args is ["--operation-browser-only"]) return CortexOperationBrowserTests.Run();
        if (args is ["--operation-browser-only", var browserImages]) return CortexOperationBrowserTests.Run(browserImages);
        if (args is ["--dashboard-display-only"]) return CortexDashboardDisplayTests.Run();
        if (args is ["--dashboard-display-only", var displayImages]) return CortexDashboardDisplayTests.Run(displayImages);
        if (args is ["--lifecycle-only"])
        {
            CortexRuntimeExitTests.RunAsync().GetAwaiter().GetResult();
            TrayUiRestoreTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--service-control-only"])
        {
            ServiceControlPolicyTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--host-exit-control-only"])
        {
            CortexHostExitControlTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--payload-tiering-control-only"])
        {
            CortexPayloadTieringControlTests.RunAsync().GetAwaiter().GetResult();
            CortexPayloadTieringClientTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--payload-lifecycle-page-only"]) return CortexPayloadLifecyclePageTests.Run();
        if (args is ["--payload-lifecycle-control-only"])
        {
            CortexPayloadLifecycleControlTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--processor-policy-only"]) return CortexProcessorPolicyTests.Run();
        if (args is ["--mcp-settings-only"]) return CortexMcpSettingsPageTests.Run();
        if (args is ["--mcp-setup-only"])
        {
            CortexMcpSetupWorkflowTests.RunAsync().GetAwaiter().GetResult();
            CortexMcpSetupControlTests.RunAsync().GetAwaiter().GetResult();
            return CortexMcpSettingsPageTests.Run();
        }
        if (args is ["--certificate-passwords-page-only"]) return CortexCertificatePasswordsPageTests.Run();
        if (args is ["--connection-profiles-page-only"])
        {
            CortexConnectionProfilesPageTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--machine-server-elevation-only"])
        {
            CortexMachineServerCertificateElevationTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--certificate-elevation-only"])
        {
            CortexCertificateElevationTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--scheduled-task-elevation-only"])
        {
            CortexScheduledTaskElevationTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--windows-service-elevation-only"])
        {
            CortexWindowsServiceElevationTests.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        if (args is ["--certificate-native-preflight", var preflightOutput])
            return CortexCertificateElevationTests.RunNativePreflight(preflightOutput);
        if (args is ["--recipes-only"]) return RecipePresentationTests.Run();
        if (args is ["--recipes-only", var recipeImageDirectory]) return RecipePresentationTests.Run(recipeImageDirectory);
        if (args is ["--recipe-descriptors-only", var authorInput, var descriptorOutput]) return RecipeDisplayMetadataTests.Run(authorInput, descriptorOutput);
        if (args is ["--live-service-notifications"])
        {
            var snapshot = new WindowsCortexServiceNotificationSource()
                .ReadAsync(TimeSpan.FromDays(7), 100, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                snapshot.CheckedUtc,
                snapshot.ServiceInstalled,
                snapshot.ServiceState,
                snapshot.ServiceProcessId,
                snapshot.ScmError,
                snapshot.PipeReady,
                snapshot.PipeError,
                EventCount = snapshot.Events.Count,
                snapshot.EventsTruncated,
                snapshot.EventReadError,
                Events = snapshot.Events.Select(item => new
                {
                    item.TimeUtc,
                    item.Log,
                    item.Level,
                    item.Source,
                    item.EventId
                })
            }));
            return 0;
        }
        var fixture = Path.Combine(Path.GetTempPath(), "cortex-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            if (args is ["--personal-profile-only"])
            {
                PersonalProfileTests.RunAsync(fixture).GetAwaiter().GetResult();
                return 0;
            }
            var application = Path.Combine(fixture, "fixture.EXE");
            File.WriteAllBytes(application, [0x4d, 0x5a]);
            var appPolicy = RootCodeVerifier.NormalizePolicy(new("allow", [application, application]), true);
            var folderPolicy = RootCodeVerifier.NormalizePolicy(new("allow", [fixture]), false);
            var allApplications = RootCodeVerifier.NormalizePolicy(new("all", []), true);
            var allFolders = RootCodeVerifier.NormalizePolicy(new("all", []), false);
            Check(appPolicy.Values.Count == 1, "deduplication");
            var definition = new RootCodeDefinition("fixture", "TEST", appPolicy, folderPolicy);
            Check(RootCodeVerifier.AllowsApplication(definition, application), "listed app rejected");
            Check(!RootCodeVerifier.AllowsApplication(definition, Path.Combine(fixture, "other.exe")), "unlisted app permitted");
            Check(RootCodeVerifier.AllowsFolders(definition, new OperationRequest("stat", new Dictionary<string,string> { ["src"] = Path.Combine(fixture, "child.txt") })), "allowed descendant rejected");
            Check(!RootCodeVerifier.AllowsFolders(definition, new OperationRequest("stat", new Dictionary<string,string> { ["src"] = fixture + "-sibling" })), "prefix sibling escaped restriction");
            var deny = definition with { Folders = new("deny", [fixture]) };
            Check(!RootCodeVerifier.AllowsFolders(deny, new OperationRequest("stat", new Dictionary<string,string> { ["src"] = Path.GetDirectoryName(fixture)! })), "deny parent traversal permitted");
            var unrestricted = new RootCodeDefinition("all", "TEST", allApplications, allFolders);
            Check(allApplications.Mode == "off" && allFolders.Mode == "off" &&
                RootCodeVerifier.AllowsApplication(unrestricted, Path.Combine(fixture, "anything.exe")) &&
                RootCodeVerifier.AllowsFolders(unrestricted, new OperationRequest("stat", new Dictionary<string,string> { ["src"] = @"C:\anywhere" })),
                "explicit all policy did not canonicalize to unrestricted access");
            foreach (var policy in new[] { new RootAccessPolicy("allow", []), new RootAccessPolicy("allow", ["relative.exe"]), new RootAccessPolicy("bogus", []) })
            {
                try { RootCodeVerifier.NormalizePolicy(policy, true); throw new Exception("invalid policy accepted"); }
                catch (ArgumentException) { }
            }
            Console.WriteLine("PASS shared application/folder policy normalization and enforcement (fixture only)");

            var selected = new RegistryItem("fixture", new Dictionary<string,string>
            {
                ["Code"]="TEST", ["Application Mode"]="allow", ["Applications"]=application,
                ["Folder Mode"]="allow", ["Folders"]=fixture
            });
            using (var dialog = new RootCodeDialog(selected))
            {
                using var json = JsonDocument.Parse(dialog.SubmissionJson);
                Check(json.RootElement.GetProperty("applications").GetProperty("values").GetArrayLength() == 1, "app scope lost in editor");
                Check(json.RootElement.GetProperty("folders").GetProperty("mode").GetString() == "allow", "folder policy lost");
                Check(Children(dialog).OfType<Button>().Any(x => x.Text == "Add executable…"), "app picker missing");
                Check(Children(dialog).OfType<Button>().Any(x => x.Text == "Add folder…"), "folder picker missing");
                if (args is [var directory]) Render(dialog, directory, "root-scope-fixture.png");
                dialog.ClearCode();
                Check(dialog.Code.Length == 0, "editor did not clear code");
            }
            var allSelected = new RegistryItem("all-fixture", new Dictionary<string,string>
            {
                ["Code"]="TEST", ["Application Mode"]="all", ["Applications"]="",
                ["Folder Mode"]="all", ["Folders"]=""
            });
            using (var dialog = new RootCodeDialog(allSelected))
            {
                using var json = JsonDocument.Parse(dialog.SubmissionJson);
                Check(json.RootElement.GetProperty("applications").GetProperty("mode").GetString() == "off" &&
                    json.RootElement.GetProperty("folders").GetProperty("mode").GetString() == "off" &&
                    Children(dialog).OfType<ComboBox>().Count(box => box.Items.Contains("all")) == 2,
                    "root-code UI did not expose all as a clear unrestricted policy");
            }
            Console.WriteLine("PASS scope-editor round trip and picker controls (dummy code only)");

            using (var form = new Form { Size = new Size(1150, 740), Text = "Cortex utilization — synthetic fixture" })
            using (var page = new UtilizationPage(autoLoad: false) { Dock = DockStyle.Fill })
            {
                page.Display(new("cortex.utilization/1", "available", 7, 90, "2026-08-29T00:00:00Z", "2026-08-29T12:00:00Z", 4, 3, false,
                [new("total", "all", "mcp", "search", 100, 2, 200, 0, 32000, 300000, 15000, "2026-08-29T12:00:00Z"),
                 new("namespace", "knowledge.shared", "mcp", "search", 70, 0, 0, 0, 0, 0, 0, "2026-08-29T12:00:00Z")]));
                form.Controls.Add(page);
                Check(Children(page).OfType<DataGridView>().Count() == 3, "dashboard dimensions missing");
                Check(Children(page).OfType<Label>().Any(x => x.Text.Contains("UNKNOWN")), "offline uncertainty missing");
                if (args is [var directory]) Render(form, directory, "utilization-fixture.png");
            }
            Console.WriteLine("PASS dashboard fixture, dimensions and coverage labels");

            Check(CortexServiceEventPolicy.IsRelevant("PegasusCortex.ExecutionService", "bounded startup failure"),
                "product event source was not selected");
            Check(CortexServiceEventPolicy.IsRelevant("Service Control Manager", "The Pegasus Cortex Execution service stopped"),
                "service-control event was not selected");
            Check(!CortexServiceEventPolicy.IsRelevant("Service Control Manager", "An unrelated service stopped"),
                "unrelated service-control event was selected");
            Check(CortexServiceEventPolicy.NormalizeMessage("line 1\r\nroot=A1B2\tline 2", 128) ==
                  "line 1 root=[redacted] line 2", "event detail normalization or root-code redaction failed");
            using (var form = new Form { Size = new Size(1080, 700), Text = "Pegasus Cortex — Service notifications" })
            using (var page = new ServiceNotificationsPage(autoLoad: false) { Dock = DockStyle.Fill })
            {
                form.Controls.Add(page);
                page.Display(new(
                    new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc),
                    true,
                    "Running",
                    4242,
                    null,
                    true,
                    null,
                    [new(new DateTime(2026, 9, 10, 17, 0, 0, DateTimeKind.Utc), "Application", "Error",
                        "PegasusCortex.ExecutionService", 0, "service startup failed", "service startup failed: fixture")],
                    false,
                    null,
                    ServiceStartup: "automatic"));
                var labels = Children(page).OfType<Label>().Select(label => label.Text).ToArray();
                Check(labels.Any(text => text.Contains("Privileged service: RUNNING", StringComparison.Ordinal)),
                    "service state missing from notification page");
                Check(labels.Any(text => text.Contains("Service pipe: READY", StringComparison.Ordinal)),
                    "service pipe state missing from notification page");
                var eventGrid = Children(page).OfType<DataGridView>().Single();
                Check(eventGrid.Rows.Count == 1 && eventGrid.Rows[0].Cells[3].Value?.ToString() == "PegasusCortex.ExecutionService",
                    "bounded service event missing from notification page");
                Check(Children(page).OfType<TextBox>().Single().Text.Contains("service startup failed: fixture", StringComparison.Ordinal),
                    "selected event detail was not displayed");
                var controls = Children(page).OfType<Button>().ToDictionary(button => button.Text, StringComparer.Ordinal);
                Check(!controls["Start service"].Enabled && controls["Stop service"].Enabled && controls["Restart service"].Enabled,
                    "running service controls do not expose the exact valid actions");
                page.Display(new(
                    new DateTime(2026, 9, 10, 18, 1, 0, DateTimeKind.Utc), true, "Stopped", 0, null, false,
                    "svc_down", [], false, null, ServiceStartup: "manual"));
                Check(controls["Start service"].Enabled && !controls["Stop service"].Enabled && !controls["Restart service"].Enabled,
                    "stopped service controls do not expose only start");
                page.Display(new(
                    new DateTime(2026, 9, 10, 18, 2, 0, DateTimeKind.Utc), true, "Stopped", 0, null, false,
                    "svc_down", [], false, null, ServiceStartup: "disabled"));
                Check(!controls["Start service"].Enabled && !controls["Restart service"].Enabled &&
                    controls["Allow manual start"].Enabled && !controls["Disable service startup"].Enabled,
                    "disabled service was implicitly startable or hid the explicit enable action");
                if (args is [var directory]) Render(form, directory, "service-notifications-fixture.png");
            }
            Check(WindowsCortexServiceControl.TryParse("start", out var startAction) &&
                startAction == CortexServiceControlAction.Start &&
                WindowsCortexServiceControl.TryParse("stop", out var stopAction) &&
                stopAction == CortexServiceControlAction.Stop &&
                WindowsCortexServiceControl.TryParse("restart", out var restartAction) &&
                restartAction == CortexServiceControlAction.Restart &&
                !WindowsCortexServiceControl.TryParse("run", out _),
                "service control accepted a value outside the fixed action set");
            using (var settings = new SettingsWindow())
            {
                Check(settings.SelectedDestination == "Overview" &&
                    Children(settings).OfType<RadioButton>().Any(button => button.Text == "Settings & support"),
                    "task navigation missing from Cortex settings");
            }
            Console.WriteLine("PASS service health, fixed elevated controls, pipe status and bounded event notifications");

            TrayUiThread.RunAsync(() =>
            {
                Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "tray thread is not STA");
                var context = new ApplicationContext();
                var timer = new System.Windows.Forms.Timer { Interval = 25 };
                timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); context.ExitThread(); };
                timer.Start(); return context;
            }).GetAwaiter().GetResult();
            Console.WriteLine("PASS resident UI owns an STA message loop");

            using (var stop = new CancellationTokenSource())
            {
                var attempts = 0;
                var failures = 0;
                var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var supervisor = TrayUiSupervisor.RunAsync(
                    async () =>
                    {
                        if (Interlocked.Increment(ref attempts) == 1)
                            throw new InvalidOperationException("synthetic tray failure");
                        restarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token).ConfigureAwait(false);
                    },
                    stop.Token,
                    _ => Interlocked.Increment(ref failures),
                    TimeSpan.Zero);
                restarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                stop.Cancel();
                supervisor.GetAwaiter().GetResult();
                Check(attempts == 2, "tray UI was not restarted exactly once");
                Check(failures == 1, "tray UI failure was not reported exactly once");
            }
            Console.WriteLine("PASS tray UI failure restarts without canceling the host");
            using (var stop = new CancellationTokenSource())
            {
                var attempts = 0;
                TrayUiSupervisor.RunAsync(
                    () => { Interlocked.Increment(ref attempts); return Task.CompletedTask; },
                    stop.Token,
                    _ => throw new InvalidOperationException("A normal tray close was reported as a failure."),
                    TimeSpan.Zero).GetAwaiter().GetResult();
                Check(attempts == 1, "normal tray close restarted the optional UI");
            }
            Console.WriteLine("PASS normal tray close ends only the optional UI surface");
            // Earlier control fixtures install a WinForms context on this STA
            // thread. The pipe tests are non-UI and must not await that idle pump.
            Task.Run(() => PersonalProfileTests.RunAsync(fixture)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(fixture, recursive: true); }
    }
    private static IEnumerable<Control> Children(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Children(child)) yield return descendant; }
    }
    internal static void Render(Form form, string directory, string file)
    {
        var target = Path.GetFullPath(directory);
        PathNormalizer.RejectAbsoluteReparseComponents(target);
        if (!Directory.Exists(target) || new DirectoryInfo(target).LinkTarget is not null) throw new IOException("Invalid fixture rendering directory");
        var destination = Path.Combine(target, file);
        if (destination.Length > 240) throw new IOException("Fixture image path exceeds the Windows path budget");
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false;
        try
        {
            form.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            bitmap.Save(output, ImageFormat.Png);
        }
        finally { form.Hide(); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
