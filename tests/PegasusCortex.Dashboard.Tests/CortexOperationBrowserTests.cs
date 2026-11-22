using System.Drawing.Imaging;
using System.Text.Json.Nodes;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;

internal static class CortexOperationBrowserTests
{
    internal static int Run(string? imageOutputDirectory = null)
    {
        try
        {
            var definition = JsonNode.Parse("""
                {"inputSchema":{"properties":{
                  "op":{"type":"string","enum":["ui_status","stat","terminal_screen","service_status","stat"]},
                  "src":{"type":"string"},"root":{"type":"string"},"detail":{"type":"boolean"}
                }}}
                """)!.AsObject();
            var stat = Help("stat", "Inspect a single fixture path", "Signed-in owner host; root may select the optional service.",
                ["op", "src", "detail"], ["src"]);
            var service = Help("service_status", "Inspect the fixture service", "Optional LocalSystem service with explicit root authorization.",
                ["op", "root"], ["root"]);
            var ui = Help("ui_status", "Read fixture UI metadata", "Signed-in owner session.", ["op"], []);
            var requests = new List<string>();
            var catalog = new CortexOperationCatalog(definition, operation =>
            {
                requests.Add(operation);
                return operation switch { "stat" => stat, "service_status" => service, "ui_status" => ui, _ => throw new InvalidOperationException("Unadvertised help request") };
            });
            Check(requests.Count == 0, "Catalog construction eagerly loaded help");
            Check(catalog.Operations.Select(item => item.Name).SequenceEqual(new[] { "service_status", "stat", "ui_status" }),
                "Advertised names were not sorted/deduplicated or unsupported terminal_screen was shown");
            Check(catalog.Operations.Single(item => item.Name == "service_status").Domain == "Windows services", "Domain grouping did not follow the advertised family");

            var statDetails = catalog.GetDetails("stat");
            Check(requests.SequenceEqual(new[] { "stat" }), "Selected help loaded other leaves");
            Check(statDetails.Required == "op, src" && statDetails.Allowed == "detail, op, src", "Selected required/allowed fields were lost");
            Check(statDetails.Examples.Contains("pao stat", StringComparison.Ordinal) &&
                statDetails.Constraints.Contains("not a second validator", StringComparison.Ordinal), "Selected examples or constraints were lost");
            Check(statDetails.Fields.Single(field => field.Name == "detail").Type == "boolean" &&
                statDetails.Fields.Single(field => field.Name == "src").Requirement == "Required" &&
                statDetails.Fields.Single(field => field.Name == "op").Schema.Contains("\"const\": \"stat\"", StringComparison.Ordinal),
                "Selected schema types or fixed selector were not shown");
            stat["description"] = "MUTATED AFTER SNAPSHOT";
            stat["inputSchema"]!["properties"]!["src"]!["type"] = "integer";
            definition["inputSchema"]!["properties"]!["op"]!["enum"]!.AsArray().Add("invented_after_snapshot");
            Check(catalog.GetDetails("stat").Description == "Inspect a single fixture path" &&
                catalog.GetDetails("stat").Fields.Single(field => field.Name == "src").Type == "string" &&
                catalog.Operations.Count == 3 && requests.Count == 1, "Cached projections retained mutable metadata or reloaded the same leaf");
            try { catalog.GetDetails("terminal_screen"); throw new Exception("Unsupported operation was selectable"); }
            catch (ArgumentOutOfRangeException) { }

            var themed = 0;
            var palette = CortexTheme.Resolve(CortexAppearance.Light, false, false);
            using var page = new CortexOperationBrowserPage(catalog, control =>
            {
                themed++;
                control.BackColor = palette.Window;
                control.ForeColor = palette.Ink;
            });
            var controls = Descendants(page).ToArray();
            var operations = controls.OfType<ListBox>().Single();
            var search = controls.OfType<TextBox>().Single(control => control.AccessibleName == "Search fixed operations");
            var reference = controls.OfType<RichTextBox>().Single();
            Check(themed == 1 && page.BackColor == palette.Window, "Injected theme was not used");
            Check(requests.SequenceEqual(new[] { "stat", "service_status" }), "Initial selection loaded more than its own help");
            Check(operations.SelectedItem is CortexOperationName { Name: "service_status" } &&
                reference.Text.Contains("Optional LocalSystem service", StringComparison.Ordinal) &&
                controls.OfType<Label>().Any(label => label.Text.Contains(CortexOperationBrowserPage.AvailabilityNotice, StringComparison.Ordinal)) &&
                !reference.Text.Contains("ready", StringComparison.OrdinalIgnoreCase),
                "The selected operation route and unobserved availability were collapsed");
            Check(controls.OfType<Button>().Count() == 0 && reference.ReadOnly && !reference.DetectUrls &&
                controls.OfType<DataGridView>().Single().ReadOnly, "Discovery surface exposes execution or editable contract content");

            search.Text = "FILES AND TEXT";
            Check(operations.Items.Count == 1 && operations.SelectedItem is CortexOperationName { Name: "stat" } &&
                reference.Text.Contains("Inspect a single fixture path", StringComparison.Ordinal) && requests.Count == 2,
                "Case-insensitive domain filtering lost its cached selected detail");
            search.Text = "ui_";
            Check(operations.SelectedItem is CortexOperationName { Name: "ui_status" } && requests.Count == 3,
                "Name filtering did not load exactly the newly selected leaf");
            search.Text = "status";
            Check(operations.Items.Count == 2 && operations.SelectedItem is CortexOperationName { Name: "ui_status" } && requests.Count == 3,
                "A still-visible selection was not preserved");
            search.Text = "no-such-operation";
            Check(operations.Items.Count == 0 && reference.Text.Contains("No operations match", StringComparison.Ordinal) &&
                controls.OfType<DataGridView>().Single().DataSource is null && requests.Count == 3,
                "Empty filtering retained stale field/detail content or fetched help");
            search.Clear();
            Check(operations.SelectedItem is CortexOperationName { Name: "service_status" } && requests.Count == 3,
                "Clearing an empty filter did not deterministically select the first cached leaf");

            using var unavailable = new CortexOperationBrowserPage(new CortexOperationCatalog(definition, _ => null), _ => { });
            Check(Descendants(unavailable).OfType<RichTextBox>().Single().Text.Contains("Not supplied", StringComparison.Ordinal),
                "Absent help was represented as an empty supported contract");
            using var failed = new CortexOperationBrowserPage(new CortexOperationCatalog(definition, _ => throw new InvalidOperationException("fixture")), _ => { });
            Check(Descendants(failed).OfType<RichTextBox>().Single().Text.Contains("details are unavailable", StringComparison.Ordinal),
                "Selected-help failure did not degrade to an actionable read-only state");
            if (imageOutputDirectory is not null) RenderCompiledBrowser(imageOutputDirectory);
            Console.WriteLine("PASS operation browser: advertised-only names, omitted unsupported leaf, lazy selected-help cache, immutable details, types/routes/examples, deterministic filtering and honest availability (injected unshown fixture; no runtime/pipe/database/probe)");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL operation browser: " + error); return 1; }
    }

    private static void RenderCompiledBrowser(string imageOutputDirectory)
    {
        if (!Path.IsPathFullyQualified(imageOutputDirectory)) throw new IOException("Use an explicit absolute fixture image directory.");
        var directory = Path.GetFullPath(imageOutputDirectory);
        PathNormalizer.RejectAbsoluteReparseComponents(directory);
        if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null)
            throw new IOException("Invalid fixture image directory.");
        var destination = Path.Combine(directory, "browser-light.png");
        if (destination.Length > 240) throw new IOException("Fixture image path exceeds the Windows path budget.");

        var previousAppearance = CortexTheme.Appearance;
        try
        {
            CortexTheme.SetAppearance(CortexAppearance.Light);
            using var form = new Form
            {
                Text = "PAO operation browser", ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), AutoScaleMode = AutoScaleMode.Font
            };
            CortexTheme.Apply(form);
            form.ClientSize = new Size(1280, 900);
            using var browser = new CortexOperationBrowserPage { Dock = DockStyle.Fill };
            form.Controls.Add(browser);
            // DrawToBitmap needs native control handles, but this fixture never makes the form visible or pumps live work.
            _ = form.Handle;
            foreach (var control in Descendants(form).ToArray()) _ = control.Handle;
            form.PerformLayout();
            browser.PerformLayout();
            var domains = Descendants(browser).OfType<TreeView>().Single();
            domains.SelectedNode = domains.Nodes.Cast<TreeNode>().Single(node => node.Tag as string == "Registry");
            var operations = Descendants(browser).OfType<ListBox>().Single();
            Check(operations.Items.Count > 0 && operations.Items.Cast<CortexOperationName>().All(item => item.Domain == "Registry"),
                "Compiled Registry members are missing from the render fixture.");
            operations.SelectedItem = operations.Items.Cast<CortexOperationName>().FirstOrDefault(item => item.Name == "registry_read")
                ?? operations.Items.Cast<CortexOperationName>().First();
            Check(browser.ClientSize.Width >= 1000 && browser.ClientSize.Height >= 650 && !form.Visible,
                "The unshown browser render has no useful layout area.");
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
            Check(image.Width >= 1280 && image.Height >= 900 && !form.Visible, "The browser render dimensions or visibility are invalid.");
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            image.Save(output, ImageFormat.Png);
        }
        finally { CortexTheme.SetAppearance(previousAppearance); }
    }

    private static JsonObject Help(string name, string description, string route, string[] allowed, string[] required)
    {
        var properties = new JsonObject();
        foreach (var field in allowed)
            properties[field] = field switch
            {
                "op" => new JsonObject { ["type"] = "string", ["const"] = name },
                "detail" => new JsonObject { ["type"] = "boolean", ["description"] = "Fixture detail switch" },
                _ => new JsonObject { ["type"] = "string", ["description"] = "Fixture exact identity or path" }
            };
        return new JsonObject
        {
            ["description"] = description, ["executionRoute"] = route,
            ["availability"] = "ready", // The browser must never promote supplied metadata into observed readiness.
            ["allowed"] = new JsonArray(allowed.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["constraints"] = "Fixture-only constraints; not a second validator.",
            ["examples"] = new JsonObject { ["mcp"] = new JsonObject { ["op"] = name }, ["cli"] = "pao " + name },
            ["inputSchema"] = new JsonObject
            {
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Prepend("op").Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            }
        };
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
