using System.Globalization;
using System.Text.Json;
using Pegasus.AgentOps.Engine;

namespace Pegasus.AgentOps.Tray;

internal sealed record RecipeSection(string Title, string Text);

/// <summary>
/// A display-only allowlist. Never render raw recipe arguments, variable values,
/// environment or postcondition paths, including in the technical view.
/// </summary>
internal sealed record RecipePresentation(string Name, string ToolName, string Summary,
    IReadOnlyList<RecipeSection> Sections, IReadOnlyDictionary<string, string> TechnicalDetails, string? AuthorTitle = null)
{
    internal static RecipePresentation Create(JsonElement recipe, JsonElement tool = default)
    {
        var name = Text(recipe, "name") ?? "Unnamed recipe";
        var toolName = Text(recipe, "toolName") ?? "Not supplied";
        var action = EnumValue<RecipeActionKind>(recipe, "action");
        var mode = EnumValue<ToolExecutionMode>(recipe, "mode");
        var scriptName = Text(recipe, "scriptFileName");
        if (scriptName is not null) scriptName = Path.GetFileName(scriptName);
        var argumentCount = Number(recipe, "argumentCount");
        var hasDetails = argumentCount is >= 0;
        var call = action == RecipeActionKind.PowerShell
            ? scriptName is not null ? $"Runs {scriptName} through the registered PowerShell tool '{toolName}'."
                : $"Runs a PowerShell script through the registered tool '{toolName}'."
            : action == RecipeActionKind.Dotnet ? $"Runs a .NET build through the registered tool '{toolName}'."
            : $"Calls the registered tool '{toolName}' once.";
        var executable = Text(tool, "executablePath");
        var tools = $"Direct tool: {toolName}" + (executable is null
            ? "\nExecutable reference: not provided by the connected host."
            : "\nExecutable reference: " + executable);
        if (action == RecipeActionKind.PowerShell)
            tools += "\nScript reference: " + (scriptName ?? "not provided by the connected host.");
        var directTools = tools;
        tools += "\nAdditional tools, recipes or script-internal calls: UNKNOWN — no author description is registered.";

        var inputs = new List<string>();
        if (Property(recipe, "inputs") is { ValueKind: JsonValueKind.Array } variables)
        {
            foreach (var variable in variables.EnumerateArray().Take(64))
            {
                var variableName = Text(variable, "name") ?? "Unnamed input";
                var type = EnumValue<RecipeVariableType>(variable, "type");
                var required = Property(variable, "required")?.ValueKind switch
                { JsonValueKind.True => "required", JsonValueKind.False => "optional", _ => "requirement not supplied" };
                inputs.Add($"• {variableName} — {VariableType(type)}, {required}");
            }
            if (variables.GetArrayLength() > 64) inputs.Add("Additional input names are not displayed.");
            if (inputs.Count == 0) inputs.Add("No input variables are declared.");
        }
        else inputs.Add("Input declarations are not provided by the connected host.");
        inputs.Add(hasDetails ? $"Stored argument count: {argumentCount.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}. Values are not displayed."
            : "Stored argument count is not provided by the connected host.");
        if (Text(recipe, "workspace") is { } workspace) inputs.Add("Workspace alias: " + workspace);
        inputs.Add("Defaults, examples and argument values are omitted from this view.");

        var output = EnumValue<ToolOutputBehavior>(recipe, "outputBehavior") switch
        {
            ToolOutputBehavior.Capture => "Captures the tool's standard output and standard error in the execution result.",
            ToolOutputBehavior.Discard => "Discards the tool's standard output and standard error.",
            ToolOutputBehavior.StatusOnly => "Returns execution status without tool output text.",
            _ => "Output behavior is not provided by the connected host."
        };
        output += Number(recipe, "maxOutputBytes") is { } outputBytes
            ? $"\nOutput limit: {outputBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes."
            : "\nOutput limit: uses the registered tool policy; its value is not supplied here.";
        var outputPolicy = output;
        output += "\nFiles or other products created inside the tool/script: UNKNOWN unless described by its author.";

        var conditions = new List<string> { mode switch
        {
            ToolExecutionMode.User => "Runs in the signed-in user context under the existing execution policy.",
            ToolExecutionMode.System => "Requires the separately authorized protected execution route.",
            _ => "Execution context is not supplied by the connected host."
        }};
        conditions.Add(Number(recipe, "timeoutSeconds") is { } timeout
            ? $"Timeout: {timeout.ToString(CultureInfo.InvariantCulture)} seconds."
            : "Timeout: uses the registered tool policy.");
        conditions.Add(Property(recipe, "expectedExitCodes") is { ValueKind: JsonValueKind.Array } exits
            ? "Success exit codes: " + string.Join(", ", exits.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Number)
                .Select(value => value.GetInt32().ToString(CultureInfo.InvariantCulture)))
            : "Success exit codes: uses the registered tool policy.");
        conditions.Add(EnumValue<RecipePostconditionKind>(recipe, "postconditionKind") switch
        {
            RecipePostconditionKind.FileExists => "After the call, the declared file must exist. Its path is omitted.",
            RecipePostconditionKind.DirectoryExists => "After the call, the declared directory must exist. Its path is omitted.",
            _ => !hasDetails ? "Postcondition is not provided by the connected host."
                : Property(recipe, "postconditionKind") is null or { ValueKind: JsonValueKind.Null }
                    ? "No additional registered postcondition is declared." : "Registered postcondition kind is unknown to this view."
        });
        conditions.Add("Executable and script admission checks remain enforced when the recipe runs.");

        var technical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name,
            ["Tool Name"] = toolName,
            ["Action"] = ActionName(action),
            ["Mode"] = mode switch { ToolExecutionMode.User => "User", ToolExecutionMode.System => "Protected execution", _ => "Not supplied" },
            ["Definition Version"] = Number(recipe, "definitionVersion")?.ToString(CultureInfo.InvariantCulture) ?? "Not supplied",
            ["Definition Generation"] = Pin(recipe, "generation"),
            ["Executable SHA-256"] = Pin(recipe, "executableSha256"),
            ["Script SHA-256"] = Pin(recipe, "scriptSha256")
        };
        var metadata = RecipeDisplayMetadata.ReadProjected(recipe);
        technical["Author Metadata"] = metadata.Status;
        if (metadata.Descriptor is { } author)
        {
            technical["Author State"] = author.AuthorState;
            technical["Description Source"] = author.Provenance.SourceId;
            technical["Description Source Group"] = author.Provenance.SourceGroup;
            technical["Description Source SHA-256"] = author.Provenance.SourceArtifactSha256;
            var provenance = (author.Provenance.IsFixture ? "Fixture-bound author declaration" : "Author declaration") +
                "; this description does not establish registration, execution or runtime readiness.\n" + author.Provenance.SourceScope;
            return new(name, toolName, author.Summary,
            [
                new("Author description — " + author.AuthorState.ToUpperInvariant(), provenance),
                new("Steps (author-declared)", string.Join("\n", author.Steps.Select((step, index) => $"{index + 1}. {step}"))),
                new("Tools and calls", Bullets(author.Tools) + "\n\nRegistered direct call\n" + directTools + "\nFurther undeclared calls remain UNKNOWN."),
                new("Inputs", Bullets(author.Inputs) + "\n\nRegistered input facts\n" + string.Join("\n", inputs)),
                new("Outputs", Bullets(author.Outputs) + "\n\nRegistered output policy\n" + outputPolicy),
                new("Completion conditions", Bullets(author.Conditions) + "\n\nRegistered conditions\n" + string.Join("\n", conditions))
            ], technical, author.Title);
        }
        return new(name, toolName, call,
        [
            new("Author description", metadata.Status switch
            {
                "identity_mismatch" => "No descriptor matches this exact recipe version, script pin and definition generation. Workflow details remain UNKNOWN.",
                "invalid" => "The optional author metadata is invalid and cannot be used. Workflow details remain UNKNOWN.",
                "missing" => "No author description is published for this recipe identity. Workflow details remain UNKNOWN.",
                _ => "Author descriptions are not provided by the connected host. Workflow details remain UNKNOWN."
            }),
            new("Execution outline", "1. Validate the registered tool and recipe admission.\n2. " + call +
                "\n3. Return the configured output and evaluate the registered completion conditions.\n\n" +
                "Workflow steps inside the tool/script: UNKNOWN. Cortex does not inspect script bodies to infer them."),
            new("Tools and calls", tools),
            new("Inputs", string.Join("\n", inputs)),
            new("Outputs", output),
            new("Completion conditions", string.Join("\n", conditions))
        ], technical);
    }

    private static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(line => "• " + line));

    private static string ActionName(RecipeActionKind? action) => action switch
    {
        RecipeActionKind.PowerShell => "PowerShell script", RecipeActionKind.Dotnet => ".NET build",
        RecipeActionKind.Git => "Git operation", RecipeActionKind.SevenZip => "7-Zip operation",
        RecipeActionKind.Tool => "Registered tool call", _ => "Not supplied"
    };
    private static string VariableType(RecipeVariableType? type) => type switch
    {
        RecipeVariableType.Text => "text", RecipeVariableType.Integer => "integer", RecipeVariableType.Boolean => "yes/no",
        RecipeVariableType.RelativePath => "relative path", RecipeVariableType.Choice => "choice", _ => "type not supplied"
    };
    private static T? EnumValue<T>(JsonElement value, string name) where T : struct, Enum
    {
        var property = Property(value, name);
        if (property is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(T), numeric)) return (T)Enum.ToObject(typeof(T), numeric);
        if (property is { ValueKind: JsonValueKind.String } text && Enum.TryParse<T>(text.GetString(), true, out var parsed) &&
            Enum.IsDefined(parsed)) return parsed;
        return null;
    }
    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) ? property : null;
    private static int? Number(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } property &&
        property.TryGetInt32(out var number) ? number : null;
    private static string? Text(JsonElement value, string name)
    {
        if (Property(value, name) is not { ValueKind: JsonValueKind.String } text || string.IsNullOrWhiteSpace(text.GetString())) return null;
        var raw = text.GetString()!;
        return string.Concat(raw.Take(2048).Select(character => char.IsControl(character) ? ' ' : character));
    }
    private static string Pin(JsonElement value, string name) => Text(value, name) is { Length: 64 } pin &&
        pin.All(Uri.IsHexDigit) ? pin : "Not supplied";
}
