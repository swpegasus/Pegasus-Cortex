using Pegasus.Cortex.Contracts.V2;
using Pegasus.Cortex.Persistence.Sqlite.V2;
using Pegasus.Cortex.Product;

namespace Pegasus.AgentOps.Tray;

internal sealed class UtilizationPage : UserControl
{
    private readonly ComboBox _days = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 132, Padding = new Padding(12), Text = "Choose a time window, then refresh to inspect measured usage." };
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly DataGridView _operations = Grid("total");
    private readonly DataGridView _namespaces = Grid("namespace");
    private readonly DataGridView _tasks = Grid("task");
    private readonly ToolTip _exact = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _busy, _resourcesDisposed;

    public UtilizationPage(bool autoLoad = true)
    {
        _days.Items.AddRange([7, 30, 90]); _days.SelectedIndex = 0;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        bar.Controls.AddRange([new Label { Text = "Days (UTC)", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, _days, _refresh]);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        AddGrid(tabs, "Calls", _operations);
        AddGrid(tabs, "Namespaces", _namespaces);
        AddGrid(tabs, "Task declarations", _tasks);
        var caveat = new Label
        {
            Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12),
            Text = "Coverage: instrumented Cortex processes for this Windows user; older binaries are not measured. " +
                "Pinned tasks are not an activity denominator. Task IDs are observed declarations, not verified agent identities.\r\n" +
                "Offline refresh/capture/search/read are separate from MCP calls. Direct HOT.json reads are UNKNOWN (no watcher). " +
                "Payload bytes are not tokens used. Results returned are not proof of usefulness.\r\n" +
                "Daily aggregates retain 90 days. Counts are recorded calls, not deduplicated logical requests. " +
                "Multi-namespace calls count once per namespace; do not sum namespace or task views as total calls. " +
                "No query text, record bodies, root codes, or transcripts are collected. Refresh is read-only."
        };
        var details = new Panel { Dock = DockStyle.Bottom, Height = 152, AutoScroll = true, Visible = false };
        details.Controls.Add(caveat);
        details.ClientSizeChanged += (_, _) => caveat.MaximumSize = new Size(Math.Max(1, details.ClientSize.Width - 20), 0);
        var measurement = new CheckBox
        {
            Dock = DockStyle.Bottom, AutoSize = true, Text = "Show measurement details and limits",
            AccessibleName = "Show measurement details and limits", Padding = new Padding(12, 8, 12, 8)
        };
        measurement.CheckedChanged += (_, _) => details.Visible = measurement.Checked;
        Controls.Add(tabs); Controls.Add(details); Controls.Add(measurement); Controls.Add(_summary); Controls.Add(bar);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _days.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        if (autoLoad) HandleCreated += async (_, _) => await RefreshAsync();
        CortexTheme.Apply(this);
    }

    private static DataGridView Grid(string dimension)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoGenerateColumns = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            RowHeadersVisible = false, Tag = dimension
        };
        grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.Visible = column.Name == "Target" ? dimension != "total" : dimension == "total" ||
                    column.Name is "Operation" or "Calls" or "Failures" or "LastSeen";
                column.HeaderText = column.Name switch
                {
                    "Target" => dimension == "namespace" ? "Namespace" : "Declared task ID",
                    "Calls" => dimension == "namespace" ? "Accesses" : "Calls",
                    "ExactRecords" => "Full records", "ResponseBytes" => "Response bytes",
                    "Average" => "Average duration", "Maximum" => "Maximum duration",
                    "LastSeen" => "Last seen (Windows local)", _ => column.Name
                };
            }
        };
        grid.CellFormatting += (_, args) =>
        {
            if (args.RowIndex < 0 || args.ColumnIndex < 0 || grid.Rows[args.RowIndex].DataBoundItem is not UsageDisplayRow row) return;
            if (row.Format(grid.Columns[args.ColumnIndex].Name) is not { } value) return;
            args.Value = value.Text;
            args.FormattingApplied = true;
        };
        grid.CellToolTipTextNeeded += (_, args) =>
        {
            if (args.RowIndex < 0 || args.ColumnIndex < 0 || grid.Rows[args.RowIndex].DataBoundItem is not UsageDisplayRow row) return;
            var column = grid.Columns[args.ColumnIndex].Name;
            var value = row.Format(column);
            args.ToolTipText = (column == "LastSeen" && value is { } time ? time.Text + "\r\nStored UTC: " + time.Exact : value?.Exact ?? row.Operation) +
                "\r\n" + row.Provenance;
        };
        return grid;
    }

    private static void AddGrid(TabControl tabs, string name, DataGridView grid)
    {
        var page = new TabPage(name); page.Controls.Add(grid); tabs.TabPages.Add(page);
    }

    internal void Display(CortexUsageReport report)
    {
        var totals = Aggregate(report.Rows, "total");
        _summary.Text = report.State == "available"
            ? $"{report.Days} UTC days: {CortexDisplayFormat.Count(totals.Sum(x => x.Calls)).Text} recorded calls • " +
              $"{CortexDisplayFormat.Count(totals.Sum(x => x.Failures)).Text} failures • " +
              $"{CortexDisplayFormat.Count(totals.Sum(x => x.ExactRecords)).Text} full records • {CortexDisplayFormat.Bytes(totals.Sum(x => x.ResponseBytes)).Text} response\r\n" +
              $"Observed processes: {CortexDisplayFormat.Count(report.ObservedProcesses).Text} • task IDs: {CortexDisplayFormat.Count(report.ObservedTasks).Text}\r\n" +
              $"Collection began: {CortexDisplayFormat.Timestamp(report.CollectionStartedUtc).Text}\r\n" +
              $"Last sample: {(report.LastSeenUtc is null ? "none in selected window" : CortexDisplayFormat.Timestamp(report.LastSeenUtc).Text)}" +
              (report.RowsTruncated ? " • Detail rows capped at 512" : "")
            : "Utilization history: " + report.State + ". No measured history is not zero usage. The installed MCP runtime must include this collector.";
        _exact.SetToolTip(_summary, $"Calls: {totals.Sum(x => x.Calls):N0}; failures: {totals.Sum(x => x.Failures):N0}; " +
            $"full records: {totals.Sum(x => x.ExactRecords):N0}; response bytes: {totals.Sum(x => x.ResponseBytes):N0}.\r\n" +
            $"Observed processes: {report.ObservedProcesses:N0}; declared task IDs: {report.ObservedTasks:N0}.\r\n" +
            "Stored collection UTC: " + report.CollectionStartedUtc + "\r\nStored last sample UTC: " + report.LastSeenUtc);
        _operations.DataSource = totals.Select(row => new UsageDisplayRow(row)).ToArray();
        _namespaces.DataSource = Aggregate(report.Rows, "namespace").Select(row => new UsageDisplayRow(row)).ToArray();
        _tasks.DataSource = Aggregate(report.Rows, "task").Select(row => new UsageDisplayRow(row)).ToArray();
    }

    internal static IReadOnlyList<UsageAggregate> Aggregate(IReadOnlyList<CortexUsageRow> rows, string dimension) =>
        rows.Where(row => row.Dimension == dimension)
            .GroupBy(row => (Key: dimension == "total" ? "all" : row.Key, row.Operation))
            .OrderBy(group => group.Key.Key, StringComparer.Ordinal).ThenBy(group => group.Key.Operation, StringComparer.Ordinal)
            .Select(group => new UsageAggregate(group.Key.Key, group.Key.Operation, group.Sum(row => row.Calls),
                group.Sum(row => row.Failures), group.Sum(row => row.Results), group.Sum(row => row.ExactRecords),
                group.Sum(row => row.ResponseBytes), group.Sum(row => row.TotalMicroseconds), group.Max(row => row.MaximumMicroseconds),
                group.Select(row => row.LastSeenUtc).OrderDescending(StringComparer.Ordinal).First(), Array.AsReadOnly(group.ToArray())))
            .ToArray();

    internal sealed record UsageAggregate(string Key, string Operation, long Calls, long Failures, long Results,
        long ExactRecords, long ResponseBytes, long TotalMicroseconds, long MaximumMicroseconds, string LastSeenUtc,
        IReadOnlyList<CortexUsageRow> SourceRows)
    {
        internal decimal AverageMilliseconds => Calls == 0 ? 0 : TotalMicroseconds / (Calls * 1000m);
    }

    private sealed class UsageDisplayRow(UsageAggregate source)
    {
        public string Target => source.Key;
        public string Operation => source.Operation;
        public long Calls => source.Calls;
        public long Failures => source.Failures;
        public long Results => source.Results;
        public long ExactRecords => source.ExactRecords;
        public long ResponseBytes => source.ResponseBytes;
        public decimal Average => source.AverageMilliseconds;
        public decimal Maximum => source.MaximumMicroseconds / 1000m;
        public string LastSeen => source.LastSeenUtc;
        internal string Provenance => "Recorded sources: " + string.Join("; ", source.SourceRows.Select(row =>
            $"{row.Channel}: {row.Calls:N0} calls, {row.TotalMicroseconds:N0} µs total, {row.MaximumMicroseconds:N0} µs maximum"));

        internal CortexDisplayValue? Format(string column) => column switch
        {
            "Calls" => CortexDisplayFormat.Count(Calls), "Failures" => CortexDisplayFormat.Count(Failures),
            "Results" => CortexDisplayFormat.Count(Results), "ExactRecords" => CortexDisplayFormat.Count(ExactRecords),
            "ResponseBytes" => CortexDisplayFormat.Bytes(ResponseBytes), "Average" => CortexDisplayFormat.Duration(Average),
            "Maximum" => CortexDisplayFormat.Duration(Maximum), "LastSeen" => CortexDisplayFormat.Timestamp(LastSeen), _ => null
        };
    }

    private async Task RefreshAsync()
    {
        if (_busy || IsDisposed) return;
        _busy = true; _refresh.Enabled = false;
        var days = (int)(_days.SelectedItem ?? 7);
        try
        {
            var report = await Task.Run(async () =>
            {
                var layout = CortexProductLayout.ResolveCurrent();
                await using var store = new SqliteCortexV2Store(new(layout.CortexDatabasePath));
                return await store.ReadUsageAsync(days, _stop.Token);
            }, _stop.Token);
            if (!IsDisposed) Display(report);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { if (!IsDisposed) _summary.Text = "Utilization unavailable. Database is absent, locked, incompatible, or inaccessible. No state was changed."; }
        finally { _busy = false; if (!IsDisposed) _refresh.Enabled = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _stop.Cancel(); _stop.Dispose(); _exact.Dispose();
        }
        base.Dispose(disposing);
    }
}
