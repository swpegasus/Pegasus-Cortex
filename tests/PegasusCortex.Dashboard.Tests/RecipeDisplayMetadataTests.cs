using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;

internal static class RecipeDisplayMetadataTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static int Run(string authorInputPath, string outputDirectory)
    {
        try
        {
            var output = Path.GetFullPath(outputDirectory);
            if (!Directory.Exists(output) || new DirectoryInfo(output).LinkTarget is not null) throw new IOException("Invalid fixture directory.");
            var bytes = File.ReadAllBytes(authorInputPath);
            if (bytes.Length > RecipeDisplayMetadata.MaximumCatalogBytes) throw new IOException("Oversized fixture input.");
            using var input = JsonDocument.Parse(bytes);
            Check(input.RootElement.GetProperty("schema").GetString() == "pegasus.agentops.recipe-display-fixture-input/1", "Unknown fixture input contract.");
            var recipes = new List<OneActionRecipeDefinition>(); var descriptors = new List<RecipeDisplayDescriptor>();
            foreach (var item in input.RootElement.GetProperty("records").EnumerateArray())
            {
                var key = item.GetProperty("key");
                var recipe = Definition(key.GetProperty("recipeName").GetString()!, key.GetProperty("definitionVersion").GetInt32(), key.GetProperty("scriptSha256").GetString()!);
                recipes.Add(recipe);
                var provenance = item.GetProperty("provenance").Deserialize<RecipeDisplayProvenance>(Json)!;
                Check(provenance.IsFixture && provenance.SourceArtifactSha256 == input.RootElement.GetProperty("sourceArtifactSha256").GetString(), "Fixture source pin lost.");
                descriptors.Add(new(new(recipe.Name, recipe.DefinitionVersion, recipe.ScriptSha256, RecipeDisplayMetadata.Generation(recipe)),
                    item.GetProperty("title").GetString()!, item.GetProperty("summary").GetString()!,
                    Lines(item, "steps"), Lines(item, "tools"), Lines(item, "inputs"), Lines(item, "outputs"), Lines(item, "conditions"),
                    item.GetProperty("authorState").GetString()!, provenance));
            }
            Check(recipes.Count > 1, "Fixture needs multiple exact identities.");
            var catalog = new RecipeDisplayCatalog(RecipeDisplayMetadata.Schema, descriptors.ToArray());
            var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(catalog, Json);
            var state = Path.Combine(output, "fixture-state"); Directory.CreateDirectory(state);
            var registry = Path.Combine(state, "recipes.v1.json");
            File.WriteAllBytes(Path.Combine(state, RecipeDisplayMetadata.FileName), catalogBytes);
            var snapshot = RecipeDisplayMetadata.LoadForRegistry(registry);
            var first = recipes[0]; var generation = RecipeDisplayMetadata.Generation(first);
            var detail = RecipeDisplayMetadata.ProjectDetail(first, generation, snapshot);
            Check(detail.Display?.Key.DefinitionVersion == first.DefinitionVersion && detail.Display.AuthorState == descriptors[0].AuthorState, "Wrong generation selected.");
            foreach (var definition in recipes)
            {
                var match = RecipeDisplayMetadata.ProjectDetail(definition, RecipeDisplayMetadata.Generation(definition), snapshot);
                Check(match.Display?.Key.DefinitionVersion == definition.DefinitionVersion && match.Display.Key.ScriptSha256 == definition.ScriptSha256,
                    "A future/past record overlaid another exact target.");
            }
            var changed = first with { TimeoutSeconds = 31 };
            Check(RecipeDisplayMetadata.Generation(changed) != generation && RecipeDisplayMetadata.ProjectDetail(changed,
                RecipeDisplayMetadata.Generation(changed), snapshot).DisplayStatus == "identity_mismatch", "Same-name/version/script recipe matched a different definition generation.");
            try { RecipeDisplayMetadata.ProjectDetail(changed, generation, snapshot); throw new Exception("Stale generation accepted."); }
            catch (AgentOpsException error) when (error.Code == "precondition") { }
            var missing = RecipeDisplayMetadata.LoadForRegistry(Path.Combine(output, "absent", "recipes.v1.json"));
            Check(RecipeDisplayMetadata.ProjectDetail(first, generation, missing).DisplayStatus == "missing", "Missing metadata did not degrade.");
            var malformed = RecipeDisplayMetadata.Parse("{\"schema\":\"bad\",\"schema\":\"bad\",\"records\":[]}"u8);
            Check(RecipeDisplayMetadata.ProjectDetail(first, generation, malformed).DisplayStatus == "invalid", "Malformed metadata did not degrade.");
            var tooLong = catalog with { Records = [descriptors[0] with { Summary = new string('x', 513) }] };
            Check(RecipeDisplayMetadata.ProjectDetail(first, generation, RecipeDisplayMetadata.Parse(JsonSerializer.SerializeToUtf8Bytes(tooLong, Json))).DisplayStatus == "invalid",
                "Display bounds were not enforced.");
            var duplicate = catalog with { Records = [descriptors[0], descriptors[0]] };
            Check(RecipeDisplayMetadata.ProjectDetail(first, generation, RecipeDisplayMetadata.Parse(JsonSerializer.SerializeToUtf8Bytes(duplicate, Json))).DisplayStatus == "invalid",
                "Ambiguous descriptor keys were accepted.");
            Console.WriteLine("PASS optional descriptor bounds, missing/bad/mismatch states, exact generation and current/future isolation (source fixture)");

            var golden = new OneActionRecipeDefinition("generation-vector", 1, RecipeActionKind.Tool, "fixture", ["literal", "Ω <>&\u2028"], [],
                Workspace: "fixture", TimeoutSeconds: 30, ExpectedExitCodes: [0], ExecutableSha256: new string('a', 64), MaxOutputBytes: 4096);
            Check(RecipeDisplayMetadata.Generation(golden) == "dc455c33013067082ac983d2d238d99e1297372bbf2120c3f36b4b34af9c178c", "Independent canonical generation vector failed.");
            Check(RecipeDisplayMetadata.Generation(golden with { FixedArguments = golden.FixedArguments!.Reverse().ToArray() }) != RecipeDisplayMetadata.Generation(golden),
                "Canonical generation discarded array order.");
            Console.WriteLine("PASS versioned canonical generation golden vector including Unicode and array order");

            var many = Enumerable.Range(0, 64).Select(index => Definition("rich-" + index, 1, new string('b', 64))).ToArray();
            var richLines = Enumerable.Range(0, 12).Select(_ => new string('z', 200)).ToArray();
            var rich = new RecipeDisplayCatalog(RecipeDisplayMetadata.Schema, many.Select(definition => new RecipeDisplayDescriptor(
                new(definition.Name, definition.DefinitionVersion, definition.ScriptSha256, RecipeDisplayMetadata.Generation(definition)), "Rich fixture", new string('q', 512),
                richLines, richLines, richLines, richLines, richLines, "declared", descriptors[0].Provenance)).ToArray());
            var richSnapshot = RecipeDisplayMetadata.Parse(JsonSerializer.SerializeToUtf8Bytes(rich, Json));
            var list = RecipeDisplayMetadata.ProjectList(many, richSnapshot);
            var listJson = JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(list.All(row => row.HasDisplay) && listJson.Length < many.Length * 512 && !listJson.Contains("steps", StringComparison.OrdinalIgnoreCase) &&
                !listJson.Contains("summary", StringComparison.OrdinalIgnoreCase) && !listJson.Contains(new string('z', 100), StringComparison.Ordinal), "Rich metadata inflated the default list.");
            Console.WriteLine("PASS 64 rich author catalogs keep default recipe list compact and body-free");

            var tool = JsonSerializer.SerializeToElement(new { name = "pwsh", executablePath = @"C:\Fixture\pwsh.exe" });
            var projected = JsonSerializer.SerializeToElement(detail, Json);
            var presentation = RecipePresentation.Create(projected, tool);
            Check(presentation.Summary == descriptors[0].Summary && presentation.AuthorTitle == descriptors[0].Title &&
                presentation.Sections.Any(section => section.Text.Contains(descriptors[0].Steps[0], StringComparison.Ordinal)), "Author meaning was not visible.");
            Check(presentation.Sections.Any(section => section.Title.Contains(descriptors[0].AuthorState.ToUpperInvariant(), StringComparison.Ordinal) &&
                section.Text.Contains("Fixture-bound", StringComparison.Ordinal)), "Author state was promoted to runtime acceptance.");
            using (var form = new FixtureForm { Size = new Size(1100, 900), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) })
            using (var page = new RecipeRegistryPage(autoLoad: false) { Dock = DockStyle.Fill })
            {
                form.Controls.Add(page); page.Display(JsonSerializer.SerializeToElement(new[] { detail }, Json), JsonSerializer.SerializeToElement(new[] { tool }));
                form.Show(); Application.DoEvents();
                var overview = Children(page).OfType<RichTextBox>().Single();
                Check(overview.Text.Contains(descriptors[0].Title, StringComparison.Ordinal) && overview.Text.Contains(descriptors[0].Steps[0], StringComparison.Ordinal), "Author UI sections absent.");
                using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(output, "author-descriptor-fixture.png"), ImageFormat.Png); form.Hide();
            }
            LateSelection(recipes.Take(2).ToArray(), snapshot);
            Console.WriteLine("PASS author overview, secondary technical view and discarded late-selection reply (isolated no-activation WinForms fixture)");
            File.WriteAllText(Path.Combine(output, "fixture-bindings.json"), JsonSerializer.Serialize(new
            {
                schema = "pegasus.agentops.recipe-display-fixture-bindings/1", evidence = "Synthetic definitions only; not real registered generations.",
                sourceArtifactSha256 = input.RootElement.GetProperty("sourceArtifactSha256").GetString(),
                catalogPath = Path.Combine(state, RecipeDisplayMetadata.FileName),
                records = descriptors.Select(descriptor => new { descriptor.Key, descriptor.AuthorState, descriptor.Provenance.SourceGroup })
            }, Json));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static OneActionRecipeDefinition Definition(string name, int version, string scriptPin) => new(name, version, RecipeActionKind.PowerShell, "pwsh",
        ["--fixture-only"], [], Workspace: "fixture", TimeoutSeconds: 30, ExpectedExitCodes: [0], ExecutableSha256: new string('a', 64),
        ScriptPath: @"C:\Fixture\GraphicsLauncher.ps1", ScriptSha256: scriptPin, MaxOutputBytes: 4096);
    private static string[] Lines(JsonElement item, string name) => item.GetProperty(name).EnumerateArray().Select(value => value.GetString()!).ToArray();
    private static void LateSelection(OneActionRecipeDefinition[] definitions, RecipeDisplaySnapshot metadata)
    {
        var first = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = new RecipeRegistryPage(false, (name, _, _) => name == definitions[0].Name ? first.Task : second.Task);
        page.Display(JsonSerializer.SerializeToElement(RecipeDisplayMetadata.ProjectList(definitions, metadata), Json));
        var list = Children(page).OfType<ListBox>().Single(); var overview = Children(page).OfType<RichTextBox>().Single();
        var a = definitions.Single(definition => definition.Name == ((RegistryItem)list.Items[0]).Name);
        var b = definitions.Single(definition => definition.Name == ((RegistryItem)list.Items[1]).Name);
        // Resolve by actual alphabetical selection rather than source ordering.
        list.SelectedIndex = 1;
        var bReply = JsonSerializer.SerializeToElement(RecipeDisplayMetadata.ProjectDetail(b, RecipeDisplayMetadata.Generation(b), metadata), Json);
        (b.Name == definitions[0].Name ? first : second).SetResult(bReply);
        var title = bReply.GetProperty("display").GetProperty("title").GetString()!;
        PumpUntil(() => overview.Text.Contains(title, StringComparison.Ordinal));
        var aReply = JsonSerializer.SerializeToElement(RecipeDisplayMetadata.ProjectDetail(a, RecipeDisplayMetadata.Generation(a), metadata), Json);
        (a.Name == definitions[0].Name ? first : second).SetResult(aReply);
        var until = Stopwatch.StartNew(); while (until.ElapsedMilliseconds < 100) { Application.DoEvents(); Thread.Sleep(1); }
        Check(overview.Text.Contains(title, StringComparison.Ordinal), "An old selection overwrote the current author description.");
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(3)) { Application.DoEvents(); Thread.Sleep(1); }
        Check(condition(), "Timed out waiting for isolated selection fixture.");
    }
    private sealed class FixtureForm : Form { protected override bool ShowWithoutActivation => true; }
    private static IEnumerable<Control> Children(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var item in Children(child)) yield return item; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
