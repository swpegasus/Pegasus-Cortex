using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pegasus.AgentOps.Tray;

internal sealed record CortexOperationName(string Name, string Domain)
{
    public override string ToString() => Name;
}

internal sealed record CortexOperationField(string Name, string Requirement, string Type, string Description, string Schema);

internal sealed record CortexOperationDetails(string Description, string ExecutionRoute, string Required, string Allowed,
    string Constraints, string Examples, IReadOnlyList<CortexOperationField> Fields);

/// <summary>A snapshot of advertised names; immutable selected-help projections are loaded on demand only.</summary>
internal sealed class CortexOperationCatalog
{
    private const string NotSupplied = "Not supplied in this operation's reference.";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly Func<string, JsonObject?> _readHelp;
    private readonly JsonObject _properties;
    private readonly Dictionary<string, CortexOperationDetails> _details = new(StringComparer.Ordinal);
    internal ReadOnlyCollection<CortexOperationName> Operations { get; }

    internal CortexOperationCatalog(JsonObject definition, Func<string, JsonObject?> readHelp)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(readHelp);
        _readHelp = readHelp;
        var properties = definition["inputSchema"]?["properties"] as JsonObject
            ?? throw new InvalidOperationException("The PAO field reference is unavailable.");
        _properties = (JsonObject)properties.DeepClone();
        var names = properties["op"]?["enum"] as JsonArray
            ?? throw new InvalidOperationException("The PAO operation list is unavailable.");
        Operations = Array.AsReadOnly(names.Select(node => node?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "terminal_screen")
            .Select(name => name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => new CortexOperationName(name, Domain(name))).ToArray());
    }

    internal CortexOperationDetails GetDetails(string operation)
    {
        if (!Operations.Any(item => item.Name == operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (_details.TryGetValue(operation, out var cached)) return cached;
        var help = _readHelp(operation);
        var schema = help?["inputSchema"] as JsonObject;
        var allowed = Strings(help?["allowed"]);
        var required = Strings(schema?["required"] ?? help?["required"]);
        var properties = schema?["properties"] as JsonObject ?? _properties;
        var fields = allowed.Union(required, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(name =>
        {
            var field = properties[name];
            var type = field?["type"];
            return new CortexOperationField(name,
                required.Contains(name, StringComparer.Ordinal) ? "Required" : help?["required"] is null ? "Not supplied" : "Optional",
                type is JsonArray types ? string.Join(" | ", Strings(types)) : Text(type),
                Text(field?["description"]), field?.ToJsonString(Indented) ?? NotSupplied);
        }).ToArray();
        var examples = help?["examples"] is { } example ? Text(example)
            : "MCP\r\n" + Text(help?["mcp_example"]) + "\r\n\r\nCLI\r\n" + Text(help?["cli_example"]);
        var details = new CortexOperationDetails(Text(help?["description"]), Text(help?["executionRoute"]),
            help?["required"] is null ? NotSupplied : Join(required),
            help?["allowed"] is null ? NotSupplied : Join(allowed),
            Text(help?["constraints"]), examples, Array.AsReadOnly(fields));
        _details.Add(operation, details);
        return details;
    }

    private static string[] Strings(JsonNode? node) => node is JsonArray array
        ? array.Select(item => item?.GetValue<string>()).Where(item => item is not null).Select(item => item!).ToArray() : [];

    private static string Text(JsonNode? node) => node switch
    {
        null => NotSupplied,
        JsonValue value when value.TryGetValue<string>(out var text) => text ?? NotSupplied,
        _ => node.ToJsonString(Indented)
    };

    private static string Join(string[] names) => names.Length == 0 ? "None declared." : string.Join(", ", names.Order(StringComparer.Ordinal));

    private static string Domain(string operation)
    {
        // These are presentation group names, not another table of operations, routes or schemas.
        if (operation.StartsWith("installed_program_", StringComparison.Ordinal)) return "Installed programs";
        if (operation.StartsWith("windows_device_", StringComparison.Ordinal)) return "Devices and drivers";
        if (operation.StartsWith("windows_network_", StringComparison.Ordinal)) return "Network inventory";
        if (operation.StartsWith("windows_account_", StringComparison.Ordinal)) return "Windows accounts & sessions";
        if (operation.StartsWith("connection_profile_", StringComparison.Ordinal)) return "Connection profiles";
        if (operation.StartsWith("windows_service_", StringComparison.Ordinal)) return "Windows services";
        if (operation.StartsWith("windows_task_", StringComparison.Ordinal)) return "Scheduled tasks";
        var family = operation.Split('_', 2)[0];
        return family switch
        {
            "ctx" => "Workspaces", "read" or "encoding" => "Files and text",
            "zip" or "7z" => "Archives", "git" => "Git", "dotnet" => ".NET",
            "proc" => "Processes", "service" => "Windows services", "net" => "Network inspection",
            "terminal" => "Terminals", "app" => "Applications", "job" => "Jobs",
            "feedback" => "Feedback", "tool" or "recipe" or "run" or "pwsh" or "do" => "Tools and recipes",
            "ui" => "Desktop automation", "registry" => "Registry", "nsis" => "Installers",
            "admin" => "Administrator execution", "help" or "diag" => "Discovery and diagnostics",
            _ => operation.Contains('_') ? family : "Files and text"
        };
    }
}

internal sealed class CortexOperationBrowserPage : UserControl
{
    internal const string AvailabilityNotice = "Availability has not been checked. Browsing does not run operations.";
    private readonly CortexOperationCatalog _catalog;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search operation names or domains…", AccessibleName = "Search fixed operations" };
    private readonly TreeView _domains = new() { Dock = DockStyle.Fill, HideSelection = false, AccessibleName = "Operation domains" };
    private readonly ListBox _operations = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = "Fixed operations" };
    private readonly RichTextBox _reference = new() { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None, AccessibleName = "Selected operation reference" };
    private readonly DataGridView _fields = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoGenerateColumns = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AccessibleName = "Selected operation fields"
    };
    private readonly TextBox _fieldSchema = new() { Dock = DockStyle.Bottom, Height = 130, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, AccessibleName = "Selected field schema" };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(8), AutoEllipsis = true, UseMnemonic = false };
    private string? _domain;
    private bool _updating;

    internal CortexOperationBrowserPage(CortexOperationCatalog? catalog = null, Action<Control>? applyTheme = null)
    {
        _catalog = catalog ?? new CortexOperationCatalog(PaoProgram.UnifiedMcpToolDefinition(), PaoProgram.UnifiedMcpOperationHelp);
        Size = new Size(900, 540);
        Padding = new Padding(10);
        var guidance = new Label
        {
            Dock = DockStyle.Top, Height = 56, Padding = new Padding(0, 4, 0, 8),
            Text = "Browse PAO operations and their contracts. Select a domain, then an operation.\r\n" + AvailabilityNotice
        };
        var catalogSplit = new SplitContainer { Size = new Size(880, 440), Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 210 };
        catalogSplit.Panel1.Controls.Add(_domains);
        var memberSplit = new SplitContainer { Size = new Size(660, 440), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel1, SplitterDistance = 150 };
        memberSplit.Panel1.Controls.Add(_operations);
        var details = new TabControl { Dock = DockStyle.Fill };
        var reference = new TabPage("Reference"); reference.Controls.Add(_reference);
        var fields = new TabPage("Fields and types"); fields.Controls.Add(_fields); fields.Controls.Add(_fieldSchema);
        details.TabPages.AddRange([reference, fields]);
        memberSplit.Panel2.Controls.Add(details);
        catalogSplit.Panel2.Controls.Add(memberSplit);
        foreach (var (name, title, width) in new[] { ("Name", "Field", 95), ("Requirement", "Use", 100), ("Type", "Type", 105), ("Description", "Description", 300) })
            _fields.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, DataPropertyName = name, Width = width });
        _fields.Columns[_fields.Columns.Count - 1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        Controls.Add(catalogSplit); Controls.Add(_status); Controls.Add(_search); Controls.Add(guidance);
        _search.TextChanged += (_, _) => FilterOperations();
        _domains.AfterSelect += (_, args) =>
        {
            if (_updating) return;
            _domain = args.Node?.Tag as string;
            DisplayOperations();
        };
        _operations.SelectedIndexChanged += (_, _) => ShowSelected();
        _fields.SelectionChanged += (_, _) => _fieldSchema.Text = _fields.CurrentRow?.DataBoundItem is CortexOperationField field ? field.Schema : string.Empty;
        (applyTheme ?? CortexTheme.Apply)(this);
        FilterOperations();
    }

    private IEnumerable<CortexOperationName> Matches() => _catalog.Operations.Where(operation =>
        operation.Name.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
        operation.Domain.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    private void FilterOperations()
    {
        var matches = Matches().ToArray();
        _updating = true;
        _domains.BeginUpdate();
        try
        {
            _domains.Nodes.Clear();
            var all = _domains.Nodes.Add($"All domains ({matches.Length})");
            foreach (var group in matches.GroupBy(item => item.Domain).OrderBy(group => group.Key, StringComparer.Ordinal))
                _domains.Nodes.Add(new TreeNode($"{group.Key} ({group.Count()})") { Tag = group.Key });
            if (_domain is not null && !matches.Any(item => item.Domain == _domain)) _domain = null;
            _domains.SelectedNode = _domains.Nodes.Cast<TreeNode>().FirstOrDefault(node => Equals(node.Tag, _domain)) ?? all;
        }
        finally { _domains.EndUpdate(); _updating = false; }
        DisplayOperations();
    }

    private void DisplayOperations()
    {
        var selected = (_operations.SelectedItem as CortexOperationName)?.Name;
        var matches = Matches().Where(item => _domain is null || item.Domain == _domain).ToArray();
        _updating = true;
        _operations.BeginUpdate();
        try
        {
            _operations.Items.Clear();
            _operations.Items.AddRange(matches.Cast<object>().ToArray());
            var index = Array.FindIndex(matches, item => item.Name == selected);
            _operations.SelectedIndex = matches.Length == 0 ? -1 : index >= 0 ? index : 0;
        }
        finally { _operations.EndUpdate(); _updating = false; }
        _status.Text = $"{matches.Length} of {_catalog.Operations.Count} operations";
        ShowSelected();
    }

    private void ShowSelected()
    {
        if (_updating) return;
        _fields.DataSource = null; _fieldSchema.Clear();
        if (_operations.SelectedItem is not CortexOperationName selected)
        {
            _reference.Text = "No operations match this filter. Clear or change the search to browse the catalog.";
            return;
        }
        try
        {
            var details = _catalog.GetDetails(selected.Name);
            var text = new StringBuilder().AppendLine(selected.Name).AppendLine().AppendLine(details.Description)
                .AppendLine().AppendLine("Execution route requirement").AppendLine(details.ExecutionRoute)
                .AppendLine().AppendLine("Required fields").AppendLine(details.Required)
                .AppendLine().AppendLine("Allowed fields").AppendLine(details.Allowed)
                .AppendLine().AppendLine("Operation-specific constraints").AppendLine(details.Constraints)
                .AppendLine().AppendLine("Examples (reference only)").AppendLine(details.Examples);
            _reference.Text = text.ToString();
            _fields.DataSource = details.Fields.ToArray();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException)
        {
            _reference.Text = selected.Name + "\r\n\r\nOperation details are unavailable (" + error.GetType().Name + ").";
        }
    }
}
