using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;
using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Contracts.V2;

internal static class CortexDashboardDisplayTests
{
    private const string Old = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Current = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Pruned = "cccccccccccccccccccccccccccccccc";
    private const string Mismatch = "dddddddddddddddddddddddddddddddd";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static int Run(string? imageOutputDirectory = null)
    {
        var previous = CortexTheme.Appearance;
        try
        {
            CortexTheme.SetAppearance(CortexAppearance.Light);
            CheckFormatting();
            var report = Report();
            var aggregate = UtilizationPage.Aggregate(report.Rows, "total").Single();
            Check(aggregate.Calls == 10 && aggregate.Failures == 1 && aggregate.ResponseBytes == 10000 && aggregate.ExactRecords == 7,
                "Operation totals included namespace/task projections or lost a source channel");
            Check(aggregate.AverageMilliseconds == 2600 && aggregate.MaximumMicroseconds == 4_000_000 && aggregate.SourceRows.Count == 2,
                "Weighted duration, maximum, or source provenance was lost");
            Check(UtilizationPage.Aggregate(report.Rows, "namespace").Single().Calls == 10,
                "Namespace operation rows were not combined independently across channels");
            using var usage = new UtilizationPage(autoLoad: false) { Dock = DockStyle.Fill };
            usage.Display(report);
            var usageGrids = Descendants(usage).OfType<DataGridView>().ToArray();
            Check(usageGrids.Length == 3 && usageGrids.All(grid => grid.DataSource is Array data &&
                (data.Length == 0 || data.GetValue(0)!.GetType().GetProperty("Channel") is null)), "A Channel column remains in a statistics projection");

            var reads = new List<string>();
            QueueWindow? selectedWindow = null;
            var changeSelectionBeforeReturn = false;
            var disposeBeforeReturn = false;
            Task<OperationReceipt> Dispatch(OperationRequest request, CancellationToken cancellation)
            {
                var operation = request.Arg.GetValueOrDefault("op");
                Check(operation is "ls" or "stat", "Fixture unexpectedly requested an action");
                if (operation == "ls")
                    return Task.FromResult(new OperationReceipt(true, "fixture-list", "fixture", "done", "job", Data: new Dictionary<string, object?>
                    { ["jobs"] = new[] { Row(Old, false), Row(Current, true), Row(Pruned, true), Row(Mismatch, false) } }));
                var jid = request.Arg["jid"];
                reads.Add(jid);
                if (changeSelectionBeforeReturn && jid == Old)
                {
                    changeSelectionBeforeReturn = false;
                    selectedWindow!.SelectJobAsync(Current).GetAwaiter().GetResult();
                }
                if (disposeBeforeReturn) { disposeBeforeReturn = false; selectedWindow!.Dispose(); }
                return Task.FromResult(new OperationReceipt(true, "fixture-stat", "fixture", "done", "job", Data: new Dictionary<string, object?>
                { ["rec"] = Result(jid), ["request"] = "PRIVATE_INPUT_MUST_NOT_BE_RECONSTRUCTED" }));
            }
            using var queue = new QueueWindow(Dispatch);
            selectedWindow = queue;
            queue.RefreshAsync().GetAwaiter().GetResult();
            var grid = Descendants(queue).OfType<DataGridView>().Single();
            var submission = Descendants(queue).OfType<TextBox>().Single(control => control.AccessibleName == "Retained job submission");
            var arguments = Descendants(queue).OfType<ListBox>().Single();
            var result = Descendants(queue).OfType<TextBox>().Single(control => control.AccessibleName == "Selected job result receipt");
            Check(grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).SequenceEqual(new[] { "Job ID", "State", "Operation", "Error", "Partial" }),
                "Queue does not expose exactly the five agreed columns");
            Check(grid.ReadOnly && grid.Columns[4] is DataGridViewCheckBoxColumn { ReadOnly: true } && grid.Rows[1].Cells[4].Value is true,
                "The read-only Partial indicator lost its programmatically supplied value");
            Check(submission.Text.Contains("Submission was not retained for this job", StringComparison.Ordinal) && result.Text.Contains(Old, StringComparison.Ordinal),
                "An older job lost its receipt or reconstructed a missing submission");
            queue.SelectJobAsync(Current).GetAwaiter().GetResult();
            Check(submission.Text.Contains("pao read_lines", StringComparison.Ordinal) && submission.Text.Contains("redacted", StringComparison.Ordinal) &&
                arguments.Items.Count == 3 && arguments.Items[0]!.ToString()!.Contains("src=", StringComparison.Ordinal) &&
                result.Text.Contains(Current, StringComparison.Ordinal) && !submission.Text.Contains("PRIVATE_INPUT", StringComparison.Ordinal),
                "The retained command, separate arguments, redaction state or actual selected receipt is wrong");
            queue.SelectJobAsync(Pruned).GetAwaiter().GetResult();
            Check(submission.Text.Contains("pruned", StringComparison.Ordinal) && arguments.Items.Count == 0, "Pruned submission input was reconstructed");
            queue.SelectJobAsync(Mismatch).GetAwaiter().GetResult();
            Check(submission.Text.Contains("identity mismatch", StringComparison.Ordinal) && result.Text.Length == 0, "A different job's result was displayed");
            changeSelectionBeforeReturn = true;
            queue.SelectJobAsync(Old).GetAwaiter().GetResult();
            Check(result.Text.Contains(Current, StringComparison.Ordinal) && submission.Text.Contains("pao read_lines", StringComparison.Ordinal),
                "A superseded selection overwrote the current job");

            using var notifications = new ServiceNotificationsPage(autoLoad: false);
            var time = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
            notifications.Display(new(time, true, "Running", 42, null, true, null,
                [new(time, "Application", "Warning", "PegasusCortex.ExecutionService", 1, "Synthetic warning", "Synthetic event message — unchanged")], false, null));
            var eventGrid = Descendants(notifications).OfType<DataGridView>().Single();
            Check(eventGrid.Columns[0].HeaderText == "Time (Windows local)" && eventGrid.Rows[0].Cells[0].Value?.ToString() == CortexDisplayFormat.Timestamp(time).Text &&
                eventGrid.Rows[0].Cells[0].ToolTipText.Contains(CortexDisplayFormat.Timestamp(time).Exact, StringComparison.Ordinal), "Service timestamps or exact stored UTC details are missing");
            if (imageOutputDirectory is not null)
            {
                RenderUnshown(queue, imageOutputDirectory, "queue-display.png");
                using var host = new Form { Text = "Cortex usage", ShowInTaskbar = false };
                CortexTheme.Apply(host);
                host.Controls.Add(usage);
                RenderUnshown(host, imageOutputDirectory, "utilization-display.png");
            }
            disposeBeforeReturn = true;
            queue.SelectJobAsync(Old).GetAwaiter().GetResult();
            Check(queue.IsDisposed, "Closing the queue during a read did not settle without a stale update");
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL dashboard display: " + error); return 1; }
        finally { CortexTheme.SetAppearance(previous); }
        Console.WriteLine("PASS dashboard display: compact/exact units, local DST/offset times, weighted operation totals without dimension double counting, five-column disabled Partial, retained/missing/pruned submission and JID/selection/disposal guards (injected fixtures)");
        return 0;
    }

    private static void CheckFormatting()
    {
        var culture = CultureInfo.InvariantCulture;
        Check(CortexDisplayFormat.Count(1_250_000, culture).Text == "1.25M" && CortexDisplayFormat.Count(999_999, culture).Text == "1M" &&
            CortexDisplayFormat.Count(1_250_000, culture).Exact == "1,250,000", "Compact counts or exact count detail is wrong");
        Check(CortexDisplayFormat.Bytes(1536, culture).Text == "1.54 KB" && CortexDisplayFormat.Bytes(1_500_000_000, culture).Text == "1.5 GB" &&
            CortexDisplayFormat.Bytes(1536, culture).Exact.Contains("1,536 bytes", StringComparison.Ordinal), "Byte display uses incorrect units or loses exact bytes");
        Check(CortexDisplayFormat.Duration(1250, culture).Text == "1.25 s" && CortexDisplayFormat.Duration(90000, culture).Text == "1.5 min" &&
            CortexDisplayFormat.Duration(7200000, culture).Text == "2 h" && CortexDisplayFormat.Duration(0.001m, culture).Text == "0.001 ms", "Adaptive duration units are wrong");
        var transitionStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var transitionEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture Pacific", TimeSpan.FromHours(-8), "Fixture Pacific", "Fixture Standard", "Fixture Daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), transitionStart, transitionEnd)]);
        var first = CortexDisplayFormat.Timestamp("2026-11-01T08:30:00Z", zone, culture);
        var second = CortexDisplayFormat.Timestamp("2026-11-01T09:30:00Z", zone, culture);
        Check(first.Text.Contains("01:30:00 Fixture Daylight (UTC-07:00)", StringComparison.Ordinal) &&
            second.Text.Contains("01:30:00 Fixture Standard (UTC-08:00)", StringComparison.Ordinal) && first.Exact != second.Exact,
            "Repeated local times lost their distinct DST/UTC offsets");
        Check(CortexDisplayFormat.Timestamp("invalid", zone, culture).Text == "Unknown time", "Invalid timestamp invented a local time");
    }

    private static CortexUsageReport Report() => new("cortex.utilization/1", "available", 7, 90, "2026-11-15T00:00:00Z", "2026-11-21T08:00:00Z", 4, 3, false,
        [new("total", "all", "mcp", "search", 2, 1, 4, 2, 2000, 2_000_000, 1_500_000, "2026-11-20T08:00:00Z"),
         new("total", "all", "offline", "search", 8, 0, 10, 5, 8000, 24_000_000, 4_000_000, "2026-11-21T08:00:00Z"),
         new("namespace", "project.fixture", "mcp", "search", 2, 1, 0, 0, 0, 0, 0, "2026-11-20T08:00:00Z"),
         new("namespace", "project.fixture", "offline", "search", 8, 0, 0, 0, 0, 0, 0, "2026-11-21T08:00:00Z"),
         new("task", "fixture-task", "mcp", "search", 100, 0, 0, 0, 0, 0, 0, "2026-11-21T08:00:00Z")]);

    private static object Row(string jid, bool partial) => new { jid, rid = "fixture-rid", state = "done", op = "read_lines", err = (string?)null, partial, seq = 1L };

    private static OperationReceipt Result(string jid)
    {
        var data = new Dictionary<string, object?> { ["lines"] = "Synthetic retained result; no file was read." };
        if (jid == Current) data["submission"] = JsonSerializer.SerializeToElement(new
        {
            schema = "pegasus.cortex.job-submission/1", state = "redacted", operation = "read_lines", command = "pao read_lines", toolReference = (string?)null,
            arguments = new[] { @"src=C:\Fixture\notes.txt", "line=1", "count=20" }, omitted = new[] { "root", "text", "environment" }, truncated = false
        }, Json);
        if (jid == Pruned) data["history_pruned"] = true;
        return new(true, jid == Mismatch ? "different-job" : jid, "fixture-rid", "done", "read_lines", Data: data);
    }

    private static void RenderUnshown(Form form, string directory, string file)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("Use an absolute task-owned image directory.");
        var target = Path.GetFullPath(directory);
        PathNormalizer.RejectAbsoluteReparseComponents(target);
        if (!Directory.Exists(target) || new DirectoryInfo(target).LinkTarget is not null) throw new IOException("Invalid fixture image directory.");
        var destination = Path.Combine(target, file);
        if (destination.Length > 240) throw new IOException("Fixture image path exceeds the Windows path budget.");
        form.ClientSize = new Size(1280, 960); form.ShowInTaskbar = false;
        ReportSubmissionBounds(form, "before-handles");
        _ = form.Handle;
        foreach (var control in Descendants(form).ToArray()) _ = control.Handle;
        form.PerformLayout();
        foreach (var panel in Descendants(form).OfType<TableLayoutPanel>()) panel.PerformLayout();
        ReportSubmissionBounds(form, "after-layout");
        Check(!form.Visible && form.ClientSize.Width >= 1280 && form.ClientSize.Height >= 900, "Render fixture became visible or has no useful dimensions");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        bitmap.Save(output, ImageFormat.Png);
    }

    private static void ReportSubmissionBounds(Form form, string phase)
    {
        var submission = Descendants(form).OfType<TextBox>().SingleOrDefault(control => control.AccessibleName == "Retained job submission");
        if (submission is null) return;
        var parent = submission.Parent ?? throw new InvalidOperationException("Submission view has no layout parent.");
        var intersection = Rectangle.Intersect(submission.Bounds, parent.ClientRectangle);
        Console.WriteLine("queue-submission-layout " + JsonSerializer.Serialize(new
        {
            phase, submission.TextLength, submission.IsHandleCreated, submission.Visible, submission.ReadOnly,
            submission.Multiline, submission.SelectionStart, submission.SelectionLength,
            bounds = new { submission.Left, submission.Top, submission.Width, submission.Height },
            parent = new { parent.ClientSize.Width, parent.ClientSize.Height },
            visibleArea = new { intersection.Width, intersection.Height },
            rows = parent is TableLayoutPanel table ? table.GetRowHeights() : Array.Empty<int>()
        }, Json));
        if (phase == "after-layout")
            Check(submission.TextLength > 0 && submission.Width >= 400 && submission.Height >= 60 &&
                intersection.Width == submission.Width && intersection.Height == submission.Height,
                "The submission text is absent or clipped out of its assigned layout area.");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
