using System.Drawing.Imaging;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;

internal static class RecipePresentationTests
{
    internal static int Run(string? imageDirectory = null)
    {
        try
        {
            const string hidden = "FIXTURE_VALUE_MUST_NOT_APPEAR";
            var recipe = JsonSerializer.SerializeToElement(new
            {
                name = "graphics-prepare", definitionVersion = 3, action = (int)RecipeActionKind.PowerShell,
                toolName = "pwsh", mode = 0, scriptFileName = "PrepareGraphics.ps1", argumentCount = 4,
                inputs = new[] { new { name = "project", type = 3, required = true, defaultValue = hidden, example = hidden } },
                workspace = "graphics", timeoutSeconds = 90, expectedExitCodes = new[] { 0 },
                outputBehavior = 0, maxOutputBytes = 4096, postconditionKind = "DirectoryExists",
                executableSha256 = new string('a', 64), scriptSha256 = new string('b', 64),
                fixedArguments = new[] { hidden }, environment = new { token = hidden },
                postcondition = new { pathTemplate = hidden }, description = hidden
            });
            var tool = JsonSerializer.SerializeToElement(new { name = "pwsh", executablePath = @"C:\Fixture\pwsh.exe" });
            var presentation = RecipePresentation.Create(recipe, tool);
            var text = presentation.Summary + string.Join("\n", presentation.Sections.Select(section => section.Text)) +
                string.Join("\n", presentation.TechnicalDetails.Values);
            Check(!text.Contains(hidden, StringComparison.Ordinal), "Sensitive or unknown metadata entered a display surface.");
            Check(text.Contains("PrepareGraphics.ps1", StringComparison.Ordinal) && text.Contains(@"C:\Fixture\pwsh.exe", StringComparison.Ordinal),
                "Admitted script/tool references are absent.");
            Check(text.Contains("project — relative path, required", StringComparison.Ordinal) && text.Contains("Stored argument count: 4", StringComparison.Ordinal),
                "Safe input facts are missing.");
            Check(text.Contains("90 seconds", StringComparison.Ordinal) && text.Contains("4,096 bytes", StringComparison.Ordinal) &&
                text.Contains("declared directory must exist", StringComparison.Ordinal), "Output or completion conditions are missing.");
            Check(text.Contains("UNKNOWN", StringComparison.Ordinal) && presentation.TechnicalDetails["Action"] == "PowerShell script",
                "Transitive uncertainty or friendly action label is absent.");
            Console.WriteLine("PASS recipe projection exposes safe direct-call facts and withholds values (synthetic unit fixture)");

            var legacy = JsonSerializer.SerializeToElement(new { name = "legacy-recipe", definitionVersion = 1, action = 4, toolName = "pwsh", mode = 0 });
            var legacyPresentation = RecipePresentation.Create(legacy);
            Check(legacyPresentation.Sections.Any(section => section.Text.Contains("not provided by the connected host", StringComparison.Ordinal)) &&
                legacyPresentation.TechnicalDetails["Action"] == "PowerShell script", "Legacy numeric summary did not degrade honestly.");
            var named = JsonSerializer.SerializeToElement(new { name = "named-recipe", action = "Tool", toolName = "fixture", mode = "User" });
            Check(RecipePresentation.Create(named).TechnicalDetails["Action"] == "Registered tool call", "Friendly enum compatibility failed.");
            Console.WriteLine("PASS legacy summary-only and named-enum projections remain readable (synthetic unit fixture)");

            using var form = new OffscreenFixtureForm
            {
                Size = new Size(1100, 820), Text = "Recipes — synthetic fixture", ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000)
            };
            using var page = new RecipeRegistryPage(autoLoad: false) { Dock = DockStyle.Fill };
            form.Controls.Add(page);
            var recipes = JsonSerializer.SerializeToElement(new[] { recipe, named });
            page.Display(recipes, JsonSerializer.SerializeToElement(new[] { tool }));
            form.CreateControl(); page.CreateControl(); form.PerformLayout();
            var list = Children(page).OfType<ListBox>().Single();
            var overview = Children(page).OfType<RichTextBox>().Single();
            Check(list.Items.Count == 2 && list.Items[0].ToString()!.Contains("pwsh", StringComparison.Ordinal), "Recipe list does not identify its tool.");
            Check(overview.Text.Contains("Execution outline", StringComparison.Ordinal) && overview.Text.Contains("Tools and calls", StringComparison.Ordinal) &&
                !overview.Text.Contains(hidden, StringComparison.Ordinal), "Readable overview is missing or exposed a value.");
            var tabs = Children(page).OfType<TabControl>().Single();
            Check(tabs.TabPages[0].Text == "Overview" && tabs.TabPages[1].Text == "Technical details", "Technical details were not separated from the overview.");
            Children(page).OfType<TextBox>().Single().Text = "pwsh";
            Check(list.Items.Count == 1 && list.Items[0].ToString()!.StartsWith("graphics-prepare", StringComparison.Ordinal), "Tool search did not filter recipes.");
            Children(page).OfType<TextBox>().Single().Text = "no matching recipe";
            Check(list.Items.Count == 0 && overview.TextLength == 0, "Empty search left stale recipe details visible.");
            Children(page).OfType<TextBox>().Single().Text = string.Empty;
            Check(list.Items.Count == 2, "Clearing search did not restore recipes.");
            if (imageDirectory is not null)
            {
                var directory = Path.GetFullPath(imageDirectory);
                if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) throw new IOException("Invalid fixture image directory.");
                var destination = Path.Combine(directory, "recipes-fixture.png");
                if (File.Exists(destination)) throw new IOException("Fixture image already exists.");
                // Native controls must be visible to paint their bitmap. This
                // task-owned fixture remains offscreen and never takes focus.
                form.Show(); Application.DoEvents();
                Check(Children(page).OfType<SplitContainer>().Single().Panel1.Width >= 280,
                    "The initialized recipe list clipped its registered tool captions.");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(destination, ImageFormat.Png);
                form.Hide();
            }
            Console.WriteLine("PASS recipe overview, tool search and technical tab (isolated WinForms control fixture; no host or recipe execution)");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static IEnumerable<Control> Children(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Children(child)) yield return descendant;
        }
    }
    private sealed class OffscreenFixtureForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
