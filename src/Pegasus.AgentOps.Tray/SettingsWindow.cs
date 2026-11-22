using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Tray;

internal sealed class SettingsWindow : Form
{
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Label _title = new() { AutoSize = true, Font = CortexTheme.TitleFont, Text = "Overview", UseMnemonic = false };
    private readonly Dictionary<string, Control> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RadioButton> _navigation = new(StringComparer.Ordinal);
    private readonly Func<CortexUserPresentationState>? _presentation;
    private readonly Action? _openQueue;
    private readonly Func<IDisposable>? _beginCertificatePasswordOperation;
    private readonly Func<IDisposable>? _beginMcpSetupOperation;
    private string _selected = string.Empty;

    public SettingsWindow(Func<CortexUserPresentationState>? presentation = null, Action? openQueue = null,
        Func<IDisposable>? beginCertificatePasswordOperation = null, Func<IDisposable>? beginMcpSetupOperation = null)
    {
        _presentation = presentation;
        _openQueue = openQueue;
        _beginCertificatePasswordOperation = beginCertificatePasswordOperation;
        _beginMcpSetupOperation = beginMcpSetupOperation;
        Text = "Pegasus Cortex";
        CortexTheme.Apply(this);
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(920, 640);
        Size = new Size(1160, 780);
        StartPosition = FormStartPosition.CenterScreen;
        CortexWindowIcon.Apply(this);
        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, Width = 224, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(16, 20, 12, 16)
        };
        navigation.Controls.Add(new Label
        {
            Text = "CORTEX", Font = CortexTheme.HeadingFont, AutoSize = true, Margin = new Padding(8, 0, 0, 24)
        });
        foreach (var name in new[] { "Overview", "Work", "Tools & recipes", "Connections & access", "Settings & support" })
        {
            var button = new RadioButton
            {
                Appearance = Appearance.Button, Text = name, AccessibleName = name, UseMnemonic = false,
                Width = 190, Height = 46, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 8), FlatStyle = FlatStyle.Flat
            };
            button.CheckedChanged += (_, _) => { if (button.Checked) Navigate(name); };
            _navigation.Add(name, button);
            navigation.Controls.Add(button);
        }
        var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 20, 16) };
        var heading = new Panel { Dock = DockStyle.Top, Height = 86, Padding = new Padding(16, 20, 0, 8) };
        heading.Controls.Add(_title);
        main.Controls.Add(_content);
        main.Controls.Add(heading);
        Controls.Add(main);
        Controls.Add(navigation);
        CortexTheme.Apply(this);
        _navigation["Overview"].Checked = true;
        Activated += (_, _) =>
        {
            if (_selected == "Overview" && _pages["Overview"] is CortexOverviewPage overview) overview.RefreshPresentation();
        };
    }

    internal string SelectedDestination => _selected;

    internal void Navigate(string destination)
    {
        if (!_navigation.ContainsKey(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        if (!_navigation[destination].Checked) { _navigation[destination].Checked = true; return; }
        if (!_pages.TryGetValue(destination, out var page))
        {
            // Opening Overview must not construct a page that opens a database, probes a pipe or starts a host.
            page = destination switch
            {
                "Overview" => new CortexOverviewPage(_presentation, () => OpenAdministratorService()),
                "Work" => CreateWorkPage(),
                "Tools & recipes" => CreateTabs(("Operation browser", () => new CortexOperationBrowserPage()),
                    ("Recipes", () => new RecipeRegistryPage()), ("Tools", () => new ToolRegistryPage())),
                "Connections & access" => CreateTabs(("Applications", () => new ApplicationAccessPage()),
                    ("Administrator codes & scopes", () => new RootCodeRegistryPage()),
                    ("Certificate passwords", () => new CortexCertificatePasswordsPage(_beginCertificatePasswordOperation)),
                    ("SMB passwords", () => new CortexCertificatePasswordsPage(_beginCertificatePasswordOperation, purpose: OwnerPasswordPurpose.SmbPassword)),
                    ("Connection profiles", () => new CortexConnectionProfilesPage())),
                "Settings & support" => new CortexSettingsAboutSection(CreateTabs(("Appearance", CreateAppearancePage),
                    ("Administrator service", () => new ServiceNotificationsPage()),
                    ("Processors", () => new CortexProcessorPolicyPage()),
                    ("MCP", () => new CortexMcpSettingsPage(beginSetupOperation:_beginMcpSetupOperation)),
                    ("Storage", () => new CortexPayloadLifecyclePage()),
                    ("About", () => new CortexAboutPage()))),
                _ => throw new ArgumentOutOfRangeException(nameof(destination))
            };
            page.Dock = DockStyle.Fill;
            _pages.Add(destination, page);
            _content.Controls.Add(page);
        }
        foreach (Control child in _content.Controls) child.Visible = ReferenceEquals(child, page);
        page.BringToFront();
        _selected = destination;
        _title.Text = destination;
        if (page is CortexOverviewPage overview) overview.RefreshPresentation();
        CortexTheme.Apply(this);
    }

    private Control CreateWorkPage()
    {
        var page = new Panel();
        var usage = new UtilizationPage { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        var queue = new Button { AutoSize = true, Text = "Open work queue", Enabled = _openQueue is not null };
        queue.Click += (_, _) => _openQueue?.Invoke();
        bar.Controls.Add(queue);
        bar.Controls.Add(new Label
        {
            AutoSize = true, Padding = new Padding(8, 8, 0, 0),
            Text = _openQueue is null ? "Queue is unavailable in this window context." : "Inspect jobs and their results in the queue."
        });
        page.Controls.Add(usage);
        page.Controls.Add(bar);
        return page;
    }

    private Control CreateAppearancePage()
    {
        var body = new FlowLayoutPanel
        {
            AutoScroll = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20)
        };
        body.Controls.Add(new Label { AutoSize = true, Font = CortexTheme.HeadingFont, Text = "Window appearance" });
        body.Controls.Add(new Label { AutoSize = true, Text = "Applies to Cortex windows for this session." });
        var appearance = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, AccessibleName = "Window appearance" };
        appearance.Items.AddRange(Enum.GetNames<CortexAppearance>());
        appearance.SelectedItem = CortexTheme.Appearance.ToString();
        appearance.SelectedIndexChanged += (_, _) =>
        {
            if (Enum.TryParse<CortexAppearance>(appearance.SelectedItem?.ToString(), out var value)) CortexTheme.SetAppearance(value);
        };
        body.Controls.Add(appearance);
        body.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 12, 0, 0),
            Text = "System follows the Windows app appearance. Windows high contrast takes priority over every theme. " +
                "This choice does not change startup, personal-profile or connection settings."
        });
        return body;
    }

    private void OpenAdministratorService()
    {
        Navigate("Settings & support");
        ((CortexSettingsAboutSection)_pages["Settings & support"]).Tabs.SelectedIndex = 1;
    }

    private static TabControl CreateTabs(params (string Title, Func<Control> Create)[] destinations)
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 8) };
        foreach (var destination in destinations)
            tabs.TabPages.Add(new TabPage(destination.Title) { Tag = destination.Create });
        void LoadSelection()
        {
            if (tabs.SelectedTab is not { } selected || selected.Controls.Count != 0) return;
            if (selected.Tag is not Func<Control> create) return;
            var child = create();
            child.Dock = DockStyle.Fill;
            selected.Controls.Add(child);
            CortexTheme.Apply(child);
        }
        tabs.SelectedIndexChanged += (_, _) => LoadSelection();
        if (tabs.TabCount > 0) tabs.SelectedIndex = 0;
        LoadSelection();
        return tabs;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

internal sealed record RegistryItem(string Name, IReadOnlyDictionary<string, string> Details,
    RecipePresentation? Recipe = null, string? Caption = null)
{
    public override string ToString() => Caption ?? Name;
}

internal abstract class RegistryPage : UserControl
{
    private readonly ListBox _items = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListView _details = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        GridLines = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        View = View.Details
    };
    private readonly Button _add = new() { AutoSize = true, Text = "Add new" };
    private readonly Button _edit = new() { AutoSize = true, Text = "Edit selected" };
    private readonly Button _remove = new() { AutoSize = true, Text = "Remove" };
    private readonly Button _refresh = new() { AutoSize = true, Text = "Refresh" };
    private readonly Label _status = new() { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly string _removeAction;
    private readonly Panel _detailHost = new() { Dock = DockStyle.Fill };
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Find a recipe or tool…" };
    private RegistryItem[] _registeredItems = [];
    private bool _loaded;

    protected RegistryPage(string guidance,bool allowAdd=true,bool allowEdit=true,string removeText="Remove",
        bool autoLoad=true,bool searchable=false)
    {
        _removeAction=removeText;_add.Visible=allowAdd;_edit.Visible=allowEdit;_remove.Text=removeText;
        _details.Columns.Add("Property", 190);
        _details.Columns.Add("Value", 480);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 240 };
        split.Panel1.Controls.Add(_items);
        _detailHost.Controls.Add(_details);
        split.Panel2.Controls.Add(_detailHost);
        if (searchable)
        {
            // A docked page starts at a small design-time width. Wait for its
            // first real layout so WinForms cannot clamp the tool captions away.
            var initialWidthApplied = false;
            split.SizeChanged += (_, _) =>
            {
                if (initialWidthApplied || split.Width < 600) return;
                split.SplitterDistance = 300;
                initialWidthApplied = true;
            };
            _items.HorizontalScrollbar = true;
            _search.AccessibleName = "Find a recipe or tool";
            split.Panel1.Controls.Add(_search);
            split.Panel1.Controls.Add(new Label { Dock = DockStyle.Top, Height = 20, Text = "Search recipes or tools" });
            _search.TextChanged += (_, _) => FilterItems(Selected?.Name);
        }

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6)
        };
        buttons.Controls.AddRange([_add, _edit, _remove, _refresh]);

        var guidanceLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 5, 8, 5),
            Text = guidance
        };
        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 2, 8, 2) };
        statusPanel.Controls.Add(_status);

        Controls.Add(split);
        Controls.Add(statusPanel);
        Controls.Add(guidanceLabel);
        Controls.Add(buttons);

        _items.SelectedIndexChanged += (_, _) => ShowSelected();
        _add.Click += async (_, _) => { if (await ShowAddOrReplaceAsync(null)) await RefreshItemsAsync(); };
        _edit.Click += async (_, _) => { if (Selected is not null) await AddOrReplaceAsync(); };
        _items.DoubleClick += async (_, _) => { if (Selected is not null) await AddOrReplaceAsync(); };
        _remove.Click += async (_, _) => await RemoveSelectedAsync();
        _refresh.Click += async (_, _) => await RefreshItemsAsync();
        HandleCreated += async (_, _) =>
        {
            if (_loaded || !autoLoad) return;
            _loaded = true;
            await RefreshItemsAsync();
        };
        CortexTheme.Apply(this);
    }

    protected RegistryItem? Selected => _items.SelectedItem as RegistryItem;
    protected abstract string Operation { get; }
    protected abstract IReadOnlyList<RegistryItem> ReadItems(OperationReceipt receipt);
    protected abstract Task<bool> ShowAddOrReplaceAsync(RegistryItem? selected);
    protected virtual Task RefreshAdditionalDataAsync() => Task.CompletedTask;
    protected Control RegistryDetailsView => _details;
    protected void SetDetailsView(Control view)
    {
        _detailHost.Controls.Clear();
        view.Dock = DockStyle.Fill;
        _detailHost.Controls.Add(view);
    }

    protected virtual IReadOnlyDictionary<string, string> RemoveArguments(RegistryItem selected) =>
        new Dictionary<string, string> { ["op"] = "rm", ["name"] = selected.Name };

    protected async Task<OperationReceipt> DispatchAsync(
        IReadOnlyDictionary<string, string> arguments,
        string? body = null) =>
        await TrayApplicationContext.DispatchAsync(Operation, arguments, body);

    protected async Task RefreshItemsAsync(string? selectName = null)
    {
        await RunBusyAsync(async () =>
        {
            var receipt = await DispatchAsync(new Dictionary<string, string> { ["op"] = "ls" });
            if (!receipt.Ok)
            {
                SetFailure(receipt);
                return;
            }

            await RefreshAdditionalDataAsync();
            DisplayItems(ReadItems(receipt), selectName ?? Selected?.Name);
        });
    }

    protected void DisplayItems(IReadOnlyList<RegistryItem> items, string? selectName = null)
    {
        _registeredItems = items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        FilterItems(selectName);
    }

    private void FilterItems(string? selectName)
    {
        var query = _search.Text.Trim();
        var items = _registeredItems.Where(item => query.Length == 0 ||
            item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Caption?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        _items.BeginUpdate();
        try
        {
            _items.Items.Clear();
            _items.Items.AddRange(items.Cast<object>().ToArray());
            if (items.Length > 0)
            {
                var index = Array.FindIndex(items, item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, selectName));
                _items.SelectedIndex = index >= 0 ? index : 0;
            }
        }
        finally { _items.EndUpdate(); }
        // An empty native list need not raise a selection event. Clear its
        // details explicitly so filtering cannot leave a stale recipe visible.
        ShowSelected();
        _status.Text = query.Length == 0 ? $"{items.Length} registered" : $"{items.Length} of {_registeredItems.Length} registered";
    }

    protected static JsonElement DataElement(OperationReceipt receipt, string key)
    {
        var raw = TrayApplicationContext.Value(receipt, key)?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return default;
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    protected static RegistryItem ObjectItem(JsonElement element)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            details[FriendlyName(property.Name)] = DisplayValue(property.Value);
        var name = element.TryGetProperty("name", out var value) ? value.GetString() ?? "(unnamed)" : "(unnamed)";
        return new RegistryItem(name, details);
    }

    protected static int DetailInt(RegistryItem? item, string key, int fallback) =>
        item is not null && item.Details.TryGetValue(key, out var raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    protected static string Detail(RegistryItem? item, string key) =>
        item is not null && item.Details.TryGetValue(key, out var value) ? value : string.Empty;

    private async Task AddOrReplaceAsync()
    {
        if (await ShowAddOrReplaceAsync(Selected)) await RefreshItemsAsync(Selected?.Name);
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = Selected;
        if (selected is null) return;
        if (MessageBox.Show(this, $"{_removeAction} '{selected.Name}'?", "Pegasus Cortex", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunBusyAsync(async () =>
        {
            var receipt = await DispatchAsync(RemoveArguments(selected));
            if (!receipt.Ok)
            {
                SetFailure(receipt);
                return;
            }
            _status.Text = $"{_removeAction}d {selected.Name}";
            await RefreshItemsAsync();
        });
    }

    protected virtual void ShowSelected()
        => ShowDetails(Selected?.Details);

    protected void ShowDetails(IReadOnlyDictionary<string, string>? details)
    {
        _details.BeginUpdate();
        try
        {
            _details.Items.Clear();
            if (details is null) return;
            foreach (var detail in details)
            {
                var row = new ListViewItem(detail.Key);
                row.SubItems.Add(detail.Value);
                _details.Items.Add(row);
            }
        }
        finally
        {
            _details.EndUpdate();
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _add.Enabled = _edit.Enabled = _remove.Enabled = _refresh.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _status.Text = $"Unavailable: {ex.Message}";
        }
        finally
        {
            UseWaitCursor = false;
            _add.Enabled = _edit.Enabled = _remove.Enabled = _refresh.Enabled = true;
        }
    }

    protected void SetFailure(OperationReceipt receipt)
    {
        var remedy = TrayApplicationContext.Value(receipt, "remedy")?.ToString();
        _status.Text = string.IsNullOrWhiteSpace(remedy) ? $"Failed: {receipt.Err}" : $"Failed: {receipt.Err} — {remedy}";
    }

    private static string FriendlyName(string name) => Regex.Replace(name, "(?<!^)([A-Z])", " $1");

    private static string DisplayValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "—",
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(DisplayValue)),
        _ => value.GetRawText()
    };
}

internal sealed class ToolRegistryPage : RegistryPage
{
    public ToolRegistryPage() : base("Registered executables are exact, versioned, and probed by the signed-in owner host. Select a tool to inspect its admitted path and policy.") { }
    protected override string Operation => "tool";

    protected override IReadOnlyList<RegistryItem> ReadItems(OperationReceipt receipt)
    {
        var data = DataElement(receipt, "tools");
        return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(ObjectItem).ToArray() : [];
    }

    protected override async Task<bool> ShowAddOrReplaceAsync(RegistryItem? selected)
    {
        using var dialog = new ToolDefinitionDialog(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var receipt = await DispatchAsync(
            new Dictionary<string, string> { ["op"] = "add" },
            JsonSerializer.Serialize(dialog.Definition, JsonOptions.Web));
        if (!receipt.Ok)
        {
            SetFailure(receipt);
            return false;
        }
        return true;
    }
}

internal sealed class RecipeRegistryPage : RegistryPage
{
    private readonly RichTextBox _overview = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window, DetectUrls = false, Font = new Font("Segoe UI", 10),
        AccessibleName = "Recipe overview"
    };
    private readonly Dictionary<string, JsonElement> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string, CancellationToken, Task<JsonElement?>>? _detailLoader;
    private CancellationTokenSource? _selectionStop;
    private (string Name, string Generation)? _pendingIdentity, _cachedIdentity;
    private RecipePresentation? _cachedDetails;
    private int _detailEpoch;

    public RecipeRegistryPage(bool autoLoad = true,
        Func<string, string, CancellationToken, Task<JsonElement?>>? detailLoader = null) : base(
        "Select a recipe to see what it calls, its inputs and outputs, and the conditions for completion. Script-internal steps need an author description.",
        autoLoad: autoLoad, searchable: true)
    {
        _detailLoader = detailLoader ?? (autoLoad ? ReadSelectedAsync : null);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var overview = new TabPage("Overview") { Padding = new Padding(10) };
        overview.Controls.Add(_overview);
        var technical = new TabPage("Technical details");
        technical.Controls.Add(RegistryDetailsView);
        tabs.TabPages.AddRange([overview, technical]);
        SetDetailsView(tabs);
    }
    protected override string Operation => "recipe";

    protected override async Task RefreshAdditionalDataAsync()
    {
        InvalidateDetails();
        _tools.Clear();
        try
        {
            var receipt = await TrayApplicationContext.DispatchAsync("tool", new Dictionary<string, string> { ["op"] = "ls" });
            if (receipt.Ok) ReadTools(DataElement(receipt, "tools"));
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
        { /* Optional tool-path lookup must not hide successfully loaded recipes. */ }
    }

    private void ReadTools(JsonElement tools)
    {
        if (tools.ValueKind != JsonValueKind.Array) return;
        foreach (var tool in tools.EnumerateArray())
            if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                _tools[name.GetString()!] = tool.Clone();
    }

    protected override IReadOnlyList<RegistryItem> ReadItems(OperationReceipt receipt)
    {
        return RecipeItems(DataElement(receipt, "recipes"));
    }

    private RegistryItem[] RecipeItems(JsonElement data) => data.ValueKind != JsonValueKind.Array ? [] :
        data.EnumerateArray().Select(recipe =>
        {
            var toolName = recipe.TryGetProperty("toolName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            _tools.TryGetValue(toolName ?? string.Empty, out var tool);
            var presentation = RecipePresentation.Create(recipe, tool);
            return new RegistryItem(presentation.Name, presentation.TechnicalDetails, presentation,
                presentation.Name + " — " + presentation.ToolName);
        }).ToArray();

    // The isolated renderer supplies synthetic projections and never contacts a host.
    internal void Display(JsonElement recipes, JsonElement tools = default)
    {
        InvalidateDetails();
        _tools.Clear(); ReadTools(tools);
        DisplayItems(RecipeItems(recipes));
    }

    protected override void ShowSelected()
    {
        base.ShowSelected();
        if (Selected?.Recipe is not { } recipe) { InvalidateDetails(); _overview.Clear(); return; }
        var generation = Detail(Selected, "Definition Generation");
        var identity = (Selected.Name, generation);
        if (_cachedIdentity == identity && _cachedDetails is { } cached) { Render(cached); return; }
        Render(recipe);
        if (_detailLoader is null || generation.Length != 64 || _pendingIdentity == identity) return;
        _selectionStop?.Cancel(); _selectionStop?.Dispose();
        _selectionStop = new CancellationTokenSource(); _pendingIdentity = identity;
        _ = LoadSelectedAsync(Selected, generation, _detailEpoch, _selectionStop.Token);
    }

    private void Render(RecipePresentation recipe)
    {
        ShowDetails(recipe.TechnicalDetails);
        _overview.Clear();
        Append(recipe.AuthorTitle ?? recipe.Name, bold: true);
        if (recipe.AuthorTitle is not null) Append(recipe.Name);
        Append(recipe.Summary);
        foreach (var section in recipe.Sections)
        {
            Append(section.Title, bold: true);
            Append(section.Text);
        }
        _overview.SelectionStart = 0;
        _overview.ScrollToCaret();
    }

    private async Task LoadSelectedAsync(RegistryItem selected, string generation, int epoch, CancellationToken token)
    {
        try
        {
            var detail = await _detailLoader!(selected.Name, generation, token);
            if (token.IsCancellationRequested || IsDisposed || epoch != _detailEpoch ||
                Selected?.Name != selected.Name || Detail(Selected, "Definition Generation") != generation) return;
            if (detail is not { } value || value.GetProperty("name").GetString() != selected.Name || value.GetProperty("generation").GetString() != generation)
                throw new InvalidDataException("Selected recipe identity changed.");
            _tools.TryGetValue(selected.Recipe!.ToolName, out var tool);
            _cachedDetails = RecipePresentation.Create(value, tool); _cachedIdentity = (selected.Name, generation);
            Render(_cachedDetails);
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            if (!token.IsCancellationRequested && !IsDisposed && epoch == _detailEpoch && Selected?.Name == selected.Name)
                Append("Selected details are unavailable or the recipe changed. Refresh to retrieve its current description.");
        }
        finally { if (epoch == _detailEpoch && _pendingIdentity == (selected.Name, generation)) _pendingIdentity = null; }
    }

    private static async Task<JsonElement?> ReadSelectedAsync(string name, string generation, CancellationToken token)
    {
        await using var dispatcher = new RoutingDispatcher();
        var receipt = await dispatcher.DispatchAsync(new OperationRequest("recipe",
            new Dictionary<string, string> { ["op"] = "get", ["name"] = name, ["generation"] = generation }), token);
        return receipt.Ok ? DataElement(receipt, "recipe") : null;
    }

    private void InvalidateDetails()
    {
        _detailEpoch++; _selectionStop?.Cancel(); _selectionStop?.Dispose(); _selectionStop = null;
        _pendingIdentity = null; _cachedIdentity = null; _cachedDetails = null;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) InvalidateDetails();
        base.Dispose(disposing);
    }

    private void Append(string text, bool bold = false)
    {
        _overview.SelectionStart = _overview.TextLength;
        using var font = new Font(_overview.Font, bold ? FontStyle.Bold : FontStyle.Regular);
        _overview.SelectionFont = font;
        _overview.AppendText(text + Environment.NewLine + Environment.NewLine);
    }

    protected override async Task<bool> ShowAddOrReplaceAsync(RegistryItem? selected)
    {
        using var dialog = new RecipeDefinitionDialog(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var receipt = await DispatchAsync(
            new Dictionary<string, string> { ["op"] = "add" },
            JsonSerializer.Serialize(dialog.Definition, JsonOptions.Web));
        if (!receipt.Ok)
        {
            SetFailure(receipt);
            return false;
        }
        return true;
    }
}

internal sealed class RootCodeRegistryPage : RegistryPage
{
    public RootCodeRegistryPage() : base("Select a code, then Edit selected to set its domains. All = unrestricted; allow = only listed paths; deny = exclude listed paths. MCP and CLI identify as the product-local pao.exe. Changes require Save.") { }
    protected override string Operation => "root";

    protected override IReadOnlyList<RegistryItem> ReadItems(OperationReceipt receipt)
    {
        var data = DataElement(receipt, "codes");
        if (data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(x =>
        {
            var label = x.GetProperty("label").GetString() ?? "(unnamed)";
            var legacy=x.TryGetProperty("legacyHidden",out var legacyValue)&&legacyValue.GetBoolean();
            var applications=x.GetProperty("applications");var folders=x.GetProperty("folders");
            static string Values(JsonElement policy)=>policy.GetProperty("values").EnumerateArray().Select(value=>value.GetString()).Where(value=>value is not null).Aggregate(string.Empty,(current,value)=>current.Length==0?value!:current+Environment.NewLine+value);
            return new RegistryItem(label, new Dictionary<string, string>
            {
                ["Label"] = label,
                ["Code"] = legacy ? "Legacy hidden code — replace to make visible" : x.GetProperty("code").GetString() ?? string.Empty,
                ["Application Mode"] = DisplayRootMode(applications.GetProperty("mode").GetString()),
                ["Applications"] = Values(applications),
                ["Folder Mode"] = DisplayRootMode(folders.GetProperty("mode").GetString()),
                ["Folders"] = Values(folders)
            });
        }).ToArray();
    }

    private static string DisplayRootMode(string? mode) =>
        string.Equals(mode, "off", StringComparison.Ordinal) ? "all" : mode ?? "all";

    protected override async Task<bool> ShowAddOrReplaceAsync(RegistryItem? selected)
    {
        using var dialog = new RootCodeDialog(selected);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var receipt = await DispatchAsync(
            new Dictionary<string, string> { ["op"] = "upsert", ["label"] = dialog.CodeLabel },
            dialog.SubmissionJson);
        dialog.ClearCode();
        if (!receipt.Ok)
        {
            SetFailure(receipt);
            return false;
        }
        return true;
    }

    protected override IReadOnlyDictionary<string, string> RemoveArguments(RegistryItem selected) =>
        new Dictionary<string, string> { ["op"] = "rm", ["label"] = selected.Name };
}

internal sealed class ApplicationAccessPage : RegistryPage
{
    public ApplicationAccessPage() : base(
        "Application credentials are issued and rotated directly between signed Windows services. This page shows only public identity and state; Revoke never exposes credential material.",
        allowAdd:false,allowEdit:false,removeText:"Revoke") { }

    protected override string Operation => "application_access";

    protected override IReadOnlyList<RegistryItem> ReadItems(OperationReceipt receipt)
    {
        var data=DataElement(receipt,"applications");if(data.ValueKind!=JsonValueKind.Array)return [];
        return data.EnumerateArray().Select(value=>
        {
            var id=value.GetProperty("applicationId").GetString()??"(unknown)";
            return new RegistryItem(id,new Dictionary<string,string>
            {
                ["Application"] = value.TryGetProperty("label",out var label)?label.GetString()??id:id,
                ["Identifier"] = id,
                ["State"] = value.GetProperty("state").GetString()??"unknown",
                ["Credential ID"] = value.TryGetProperty("credentialId",out var credential)&&credential.ValueKind==JsonValueKind.String?credential.GetString()??string.Empty:string.Empty,
                ["Generation"] = value.TryGetProperty("generation",out var generation)&&generation.ValueKind==JsonValueKind.Number?generation.GetInt64().ToString(CultureInfo.InvariantCulture):string.Empty
            });
        }).ToArray();
    }

    protected override Task<bool> ShowAddOrReplaceAsync(RegistryItem? selected)=>Task.FromResult(false);
    protected override IReadOnlyDictionary<string,string> RemoveArguments(RegistryItem selected)=>
        new Dictionary<string,string>{{"op","revoke"},{"application_id",selected.Name}};
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

internal abstract class DefinitionDialog : Form
{
    private readonly TableLayoutPanel _fields = new()
    {
        AutoScroll = true,
        ColumnCount = 2,
        Dock = DockStyle.Fill,
        Padding = new Padding(10)
    };

    protected DefinitionDialog(string title)
    {
        CortexWindowIcon.Apply(this);
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(640, 540);
        Size = new Size(720, 650);
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        _fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };
        var save = new Button { AutoSize = true, Text = "Save" };
        save.Click += (_, _) => Save();
        buttons.Controls.AddRange([cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(_fields);
        Controls.Add(buttons);
        CortexTheme.Apply(this);
    }

    protected TextBox AddText(string label, string value = "", bool multiline = false)
    {
        var control = new TextBox { Dock = DockStyle.Fill, Text = value, Multiline = multiline };
        if (multiline) control.Height = 72;
        AddField(label, control, multiline ? 80 : 32);
        return control;
    }

    protected NumericUpDown AddNumber(string label, int value, int minimum, int maximum)
    {
        var control = new NumericUpDown { Dock = DockStyle.Left, Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value, minimum, maximum), Width = 150 };
        AddField(label, control, 32);
        return control;
    }

    protected ComboBox AddChoice<T>(string label, T selected) where T : struct, Enum
    {
        var control = new ComboBox { Dock = DockStyle.Left, DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        control.Items.AddRange(Enum.GetNames<T>());
        control.SelectedItem = selected.ToString();
        AddField(label, control, 32);
        return control;
    }

    protected TextBox AddPath(string label, string value, string filter)
    {
        var field = new TextBox { Dock = DockStyle.Fill, Text = value };
        var panel = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browse = new Button { AutoSize = true, Text = "Browse…" };
        browse.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { CheckFileExists = true, Filter = filter };
            if (picker.ShowDialog(this) == DialogResult.OK) field.Text = picker.FileName;
        };
        panel.Controls.Add(field, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        AddField(label, panel, 34);
        return field;
    }

    protected static int[] ParseExitCodes(string text)
    {
        var values = text.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.Parse(x, NumberStyles.Integer, CultureInfo.InvariantCulture)).Distinct().ToArray();
        return values.Length == 0 ? [0] : values;
    }

    protected static string[] ParseArguments(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected static int DetailInt(RegistryItem? item, string key, int fallback) =>
        item is not null && item.Details.TryGetValue(key, out var raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    protected static string Detail(RegistryItem? item, string key) =>
        item is not null && item.Details.TryGetValue(key, out var value) ? value : string.Empty;

    protected abstract void BuildDefinition();

    private void AddField(string label, Control control, int height)
    {
        var row = _fields.RowCount++;
        _fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _fields.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0), Text = label }, 0, row);
        _fields.Controls.Add(control, 1, row);
    }

    private void Save()
    {
        try
        {
            BuildDefinition();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            MessageBox.Show(this, ex.Message, "Invalid definition", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

internal sealed class ToolDefinitionDialog : DefinitionDialog
{
    private readonly TextBox _name;
    private readonly NumericUpDown _version;
    private readonly TextBox _executable;
    private readonly ComboBox _workingDirectoryRule;
    private readonly TextBox _fixedWorkingDirectory;
    private readonly NumericUpDown _timeout;
    private readonly TextBox _expectedExits;
    private readonly NumericUpDown _maxOutput;
    private readonly TextBox _probeArguments;
    private readonly TextBox _requiredText;

    public ToolDefinitionDialog(RegistryItem? selected) : base(selected is null ? "Add registered tool" : "Replace registered tool")
    {
        _name = AddText("Name", selected?.Name ?? string.Empty);
        _version = AddNumber("Definition version", DetailInt(selected, "Definition Version", 0) + 1, 1, 1_000_000);
        _executable = AddPath("Executable", Detail(selected, "Executable Path"), "Executable files (*.exe)|*.exe|All files (*.*)|*.*");
        _workingDirectoryRule = AddChoice("Working directory", ToolWorkingDirectoryRule.Caller);
        _fixedWorkingDirectory = AddText("Fixed directory (optional)");
        _timeout = AddNumber("Timeout (seconds)", 30, 1, 86_400);
        _expectedExits = AddText("Expected exit codes", "0");
        _maxOutput = AddNumber("Max output bytes", 65_536, 1, 65_536);
        _probeArguments = AddText("Probe arguments (one/line)", multiline: true);
        _requiredText = AddText("Probe required text");
    }

    public RegisteredToolDefinition Definition { get; private set; } = null!;

    protected override void BuildDefinition()
    {
        var name = _name.Text.Trim();
        var executable = Path.GetFullPath(_executable.Text.Trim());
        if (string.IsNullOrWhiteSpace(name) || !File.Exists(executable)) throw new ArgumentException("Choose an existing executable and provide a name.");
        var rule = Enum.Parse<ToolWorkingDirectoryRule>(_workingDirectoryRule.Text);
        var fixedDirectory = string.IsNullOrWhiteSpace(_fixedWorkingDirectory.Text) ? null : Path.GetFullPath(_fixedWorkingDirectory.Text.Trim());
        if (rule == ToolWorkingDirectoryRule.Fixed && fixedDirectory is null) throw new ArgumentException("A fixed working directory is required for the Fixed rule.");
        var probeArgs = ParseArguments(_probeArguments.Text);
        ToolProbeDefinition? probe = probeArgs.Length == 0 ? null : new ToolProbeDefinition(probeArgs, [0], string.IsNullOrWhiteSpace(_requiredText.Text) ? null : _requiredText.Text);
        Definition = new RegisteredToolDefinition(
            name,
            (int)_version.Value,
            executable,
            WorkingDirectoryRule: rule,
            FixedWorkingDirectory: fixedDirectory,
            TimeoutSeconds: (int)_timeout.Value,
            ExpectedExitCodes: ParseExitCodes(_expectedExits.Text),
            MaxOutputBytes: (int)_maxOutput.Value,
            VersionProbe: probe,
            EnvironmentMode: ToolEnvironmentMode.Replace);
    }
}

internal sealed class RecipeDefinitionDialog : DefinitionDialog
{
    private readonly TextBox _name;
    private readonly NumericUpDown _version;
    private readonly ComboBox _action;
    private readonly TextBox _tool;
    private readonly TextBox _arguments;
    private readonly TextBox _workspace;
    private readonly TextBox _workingDirectory;
    private readonly NumericUpDown _timeout;
    private readonly TextBox _expectedExits;
    private readonly ComboBox _output;
    private readonly TextBox _script;

    public RecipeDefinitionDialog(RegistryItem? selected) : base(selected is null ? "Add one-action recipe" : "Replace one-action recipe")
    {
        _name = AddText("Name", selected?.Name ?? string.Empty);
        _version = AddNumber("Definition version", DetailInt(selected, "Definition Version", 0) + 1, 1, 1_000_000);
        _action = AddChoice("Action", RecipeActionKind.Tool);
        _tool = AddText("Registered tool", Detail(selected, "Tool Name"));
        _arguments = AddText("Fixed arguments (one/line)", multiline: true);
        _workspace = AddText("Workspace alias (optional)");
        _workingDirectory = AddText("Working directory (optional)");
        _timeout = AddNumber("Timeout (seconds)", 30, 1, 86_400);
        _expectedExits = AddText("Expected exit codes", "0");
        _output = AddChoice("Output behavior", ToolOutputBehavior.Capture);
        _script = AddPath("PowerShell script (optional)", string.Empty, "PowerShell scripts (*.ps1)|*.ps1|All files (*.*)|*.*");
    }

    public OneActionRecipeDefinition Definition { get; private set; } = null!;

    protected override void BuildDefinition()
    {
        var name = _name.Text.Trim();
        var tool = _tool.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(tool)) throw new ArgumentException("Recipe name and registered tool are required.");
        var action = Enum.Parse<RecipeActionKind>(_action.Text);
        var script = string.IsNullOrWhiteSpace(_script.Text) ? null : Path.GetFullPath(_script.Text.Trim());
        if (action == RecipeActionKind.PowerShell && (script is null || !File.Exists(script))) throw new ArgumentException("PowerShell recipes require an existing .ps1 script.");
        Definition = new OneActionRecipeDefinition(
            name,
            (int)_version.Value,
            action,
            tool,
            FixedArguments: ParseArguments(_arguments.Text),
            Workspace: string.IsNullOrWhiteSpace(_workspace.Text) ? null : _workspace.Text.Trim(),
            WorkingDirectory: string.IsNullOrWhiteSpace(_workingDirectory.Text) ? null : _workingDirectory.Text.Trim(),
            TimeoutSeconds: (int)_timeout.Value,
            ExpectedExitCodes: ParseExitCodes(_expectedExits.Text),
            ScriptPath: script,
            OutputBehavior: Enum.Parse<ToolOutputBehavior>(_output.Text));
    }
}

internal sealed class RootCodeDialog : Form
{
    private readonly TextBox _label = new() { Dock = DockStyle.Fill };
    private readonly TextBox _code = new()
    {
        CharacterCasing = CharacterCasing.Upper,
        Dock = DockStyle.Left,
        MaxLength = 4,
        Width = 120
    };
    private readonly ComboBox _applicationMode=new(){Dock=DockStyle.Left,DropDownStyle=ComboBoxStyle.DropDownList,Width=160};
    private readonly TextBox _applications=new(){Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical};
    private readonly ComboBox _folderMode=new(){Dock=DockStyle.Left,DropDownStyle=ComboBoxStyle.DropDownList,Width=160};
    private readonly TextBox _folders=new(){Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical};

    public RootCodeDialog(RegistryItem? selected)
    {
        CortexWindowIcon.Apply(this);
        CortexTheme.Apply(this);
        AutoScaleMode = AutoScaleMode.Font;
        Text = selected is null ? "Add root code and scope" : "Edit root code and scope";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(820, 580);
        MinimumSize = new Size(760, 550);
        MinimizeBox = MaximizeBox = false;
        _label.Text = selected?.Name ?? string.Empty;
        _label.ReadOnly = selected is not null;
        _code.Text=selected is not null&&selected.Details.TryGetValue("Code",out var selectedCode)&&Regex.IsMatch(selectedCode,"^[A-Z0-9]{4}$",RegexOptions.CultureInvariant)?selectedCode:string.Empty;
        _applicationMode.Items.AddRange(["all","allow","deny"]);_applicationMode.SelectedItem=DisplayMode(selected?.Details.GetValueOrDefault("Application Mode"));
        _folderMode.Items.AddRange(["all","allow","deny"]);_folderMode.SelectedItem=DisplayMode(selected?.Details.GetValueOrDefault("Folder Mode"));
        _applications.Text=selected?.Details.GetValueOrDefault("Applications")??string.Empty;_folders.Text=selected?.Details.GetValueOrDefault("Folders")??string.Empty;

        var fields = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Padding = new Padding(12),AutoScroll=true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute,34));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,34));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,34));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,115));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,34));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,115));
        fields.Controls.Add(new Label { AutoSize = true, Text = "Label", Padding = new Padding(0, 7, 0, 0) }, 0, 0);
        fields.Controls.Add(_label, 1, 0);
        fields.Controls.Add(new Label { AutoSize = true, Text = "4-character code", Padding = new Padding(0, 7, 0, 0) }, 0, 1);
        fields.Controls.Add(_code, 1, 1);
        fields.Controls.Add(new Label { AutoSize = true, Text = "Application policy", Padding = new Padding(0, 7, 0, 0) }, 0, 2);fields.Controls.Add(_applicationMode,1,2);
        fields.Controls.Add(new Label { AutoSize = true, Text = "Executable paths", Padding = new Padding(0, 7, 0, 0) }, 0, 3);fields.Controls.Add(PathListPanel(_applications,true),1,3);
        fields.Controls.Add(new Label { AutoSize = true, Text = "Folder policy", Padding = new Padding(0, 7, 0, 0) }, 0, 4);fields.Controls.Add(_folderMode,1,4);
        fields.Controls.Add(new Label { AutoSize = true, Text = "Folder roots", Padding = new Padding(0, 7, 0, 0) }, 0, 5);fields.Controls.Add(PathListPanel(_folders,false),1,5);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };
        var save = new Button { AutoSize = true, Text = "Save" };
        save.Click += (_, _) =>
        {
            if (!Regex.IsMatch(_label.Text, "^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(_code.Text, "^[A-Z0-9]{4}$", RegexOptions.CultureInvariant))
            {
                MessageBox.Show(this, "Use a 1–32 character label and exactly four letters or digits.", "Invalid root code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try{_submission=BuildSubmission();}catch(Exception ex)when(ex is ArgumentException or IOException){MessageBox.Show(this,ex.Message,"Invalid root-code policy",MessageBoxButtons.OK,MessageBoxIcon.Warning);return;}
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.AddRange([cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(fields);
        Controls.Add(buttons);
        var explanation = new Label { Dock = DockStyle.Top, Height = 86, Padding = new Padding(12, 6, 12, 0), Text = "Application domain: exact calling .exe (normally pao.exe), not the program it launches. Folder domain: listed roots and descendants. All permits every path; Allow restricts to the list; Deny excludes it. Both filters must permit the operation." };
        Controls.Add(explanation);
    }

    public string CodeLabel => _label.Text.Trim();
    public string Code => _code.Text;
    private object? _submission;
    public string SubmissionJson=>JsonSerializer.Serialize(_submission??BuildSubmission(),JsonOptions.Web);
    public void ClearCode() { _code.Clear(); _submission = null; }

    private object BuildSubmission()
    {
        static string[] Lines(string value)=>value.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var applications = RootCodeVerifier.NormalizePolicy(new RootAccessPolicy(_applicationMode.Text, Lines(_applications.Text)), application: true);
        var folders = RootCodeVerifier.NormalizePolicy(new RootAccessPolicy(_folderMode.Text, Lines(_folders.Text)), application: false);
        return new { code = _code.Text, applications, folders };
    }

    private static string DisplayMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)
            ? "all"
            : mode.ToLowerInvariant();

    private Control PathListPanel(TextBox list,bool executable)
    {
        var panel=new TableLayoutPanel{ColumnCount=2,Dock=DockStyle.Fill};panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var add=new Button{AutoSize=true,Text=executable?"Add executable…":"Add folder…"};add.Click+=(_,_)=>
        {
            string? selected;if(executable){using var picker=new OpenFileDialog{CheckFileExists=true,Filter="Executable files (*.exe)|*.exe"};selected=picker.ShowDialog(this)==DialogResult.OK?picker.FileName:null;}
            else{using var picker=new FolderBrowserDialog{ShowNewFolderButton=false};selected=picker.ShowDialog(this)==DialogResult.OK?picker.SelectedPath:null;}
            if(selected is null)return;list.Text=list.TextLength==0?selected:list.Text+Environment.NewLine+selected;
        };panel.Controls.Add(list,0,0);panel.Controls.Add(add,1,0);return panel;
    }
}
