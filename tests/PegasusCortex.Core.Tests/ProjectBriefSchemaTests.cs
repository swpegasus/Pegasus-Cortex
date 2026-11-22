using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Core.Query;

internal static class ProjectBriefSchemaTests
{
    private const string SchemaRelativePath =
        "schemas/cortex-project-brief.v1.schema.json";
    private const string CoreProjectRelativePath =
        "src/PegasusCortex.Core/PegasusCortex.Core.csproj";

    public static Task ContractParityAsync()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(ProjectPath(SchemaRelativePath)));
        var root = document.RootElement;
        var definitions = root.GetProperty("$defs");
        var bounds = root.GetProperty("x-pegasus-bounds");

        Equal(
            "https://json-schema.org/draft/2020-12/schema",
            root.GetProperty("$schema").GetString());
        Equal(3, root.GetProperty("oneOf").GetArrayLength());
        Equal(
            CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
            bounds.GetProperty("minimumResponseUtf8Bytes").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumUtf8Bytes,
            bounds.GetProperty("maximumUtf8Bytes").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumHotTasks,
            bounds.GetProperty("maximumHotTasks").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumReferences,
            bounds.GetProperty("maximumReferences").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumRecoverySteps,
            bounds.GetProperty("maximumRecoverySteps").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumTextCharacters,
            bounds.GetProperty("maximumTextCharacters").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumTitleCharacters,
            bounds.GetProperty("maximumTitleCharacters").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumGateSummaryCharacters,
            bounds.GetProperty("maximumGateSummaryCharacters").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumReferenceCharacters,
            bounds.GetProperty("maximumReferenceCharacters").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumSkippedSourceRevisions,
            bounds.GetProperty("maximumSkippedSourceRevisions").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumStateRevision,
            bounds.GetProperty("maximumStateRevision").GetInt64());

        var request = definitions.GetProperty("request").GetProperty("properties");
        var requestBudget = request.GetProperty("maximumUtf8Bytes");
        Equal(
            "#/$defs/hash",
            request.GetProperty("knownAuthoritySetHash").GetProperty("$ref").GetString());
        Equal(
            CortexProjectBriefContract.MinimumMaximumUtf8Bytes,
            requestBudget.GetProperty("minimum").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumUtf8Bytes,
            requestBudget.GetProperty("maximum").GetInt32());
        Equal(
            CortexProjectBriefContract.DefaultMaximumUtf8Bytes,
            requestBudget.GetProperty("default").GetInt32());

        var snapshot = definitions.GetProperty("snapshot").GetProperty("properties");
        Equal(
            CortexProjectBriefContract.SchemaVersion,
            snapshot.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Equal(
            CortexProjectBriefContract.ContractId,
            snapshot.GetProperty("contractId").GetProperty("const").GetString());
        Equal(
            CortexProjectBriefContract.MaximumStateRevision,
            snapshot.GetProperty("stateRevision").GetProperty("maximum").GetInt64());
        Equal(
            CortexProjectBriefContract.MaximumHotTasks,
            snapshot.GetProperty("hotTasks").GetProperty("maxItems").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumReferences,
            snapshot.GetProperty("checkpointReferences").GetProperty("maxItems").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumReferences,
            snapshot.GetProperty("evidenceReferences").GetProperty("maxItems").GetInt32());

        var response = definitions.GetProperty("response").GetProperty("properties");
        Equal(
            CortexProjectBriefContract.SchemaVersion,
            response.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Equal(
            CortexProjectBriefContract.ContractId,
            response.GetProperty("contractId").GetProperty("const").GetString());
        Equal(
            CortexProjectBriefContract.MaximumHotTasks,
            response.GetProperty("hotTasks").GetProperty("maxItems").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumHotTasks + 1,
            response.GetProperty("removedTaskIds").GetProperty("maxItems").GetInt32());

        Equal(
            CortexProjectBriefContract.MaximumTitleCharacters,
            definitions.GetProperty("text512").GetProperty("maxLength").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumTextCharacters,
            definitions.GetProperty("text1024").GetProperty("maxLength").GetInt32());
        Equal(
            CortexProjectBriefContract.MaximumReferenceCharacters,
            definitions.GetProperty("reference")
                .GetProperty("properties")
                .GetProperty("reference")
                .GetProperty("maxLength")
                .GetInt32());

        AssertObjectShape<CortexProjectBriefRequest>(definitions, "request");
        AssertObjectShape<CortexProjectBriefRetainedRecordLocator>(
            definitions,
            "retainedRecordLocator");
        AssertObjectShape<CortexProjectBriefReference>(definitions, "reference");
        AssertObjectShape<CortexProjectBriefGate>(definitions, "gate");
        AssertObjectShape<CortexProjectBriefTaskSource>(definitions, "taskSource");
        AssertObjectShape<CortexProjectBriefFreshness>(definitions, "freshness");
        AssertObjectShape<CortexProjectBriefTask>(definitions, "task");
        AssertObjectShape<CortexProjectBriefSnapshot>(definitions, "snapshot");
        AssertObjectShape<CortexProjectBriefResponse>(definitions, "response");

        SequenceEqual(
            Enum.GetValues<CortexProjectBriefDisposition>().Select(value => (int)value),
            response.GetProperty("disposition")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetInt32()));
        SequenceEqual(
            CortexProjectBriefContract.FallbackRecoveryOrder,
            definitions.GetProperty("recoveryOrder")
                .GetProperty("const")
                .EnumerateArray()
                .Select(value => value.GetString()!));
        Equal(
            CortexProjectBriefContract.FallbackRecoveryOrder.Count,
            definitions.GetProperty("recoveryOrder").GetProperty("minItems").GetInt32());
        Equal(
            CortexProjectBriefContract.FallbackRecoveryOrder.Count,
            definitions.GetProperty("recoveryOrder").GetProperty("maxItems").GetInt32());

        AssertRetainedReferenceBoundary();
        AssertCorePackageOwnsSchema();
        return Task.CompletedTask;
    }

    private static void AssertRetainedReferenceBoundary()
    {
        var linkId = $"plink_{new string('a', 64)}";
        var contentHash = new string('b', 64);
        var assertionHash = new string('c', 64);
        var valid = CortexProjectBriefReferencePolicy.CreateRetained(
            linkId,
            "project-1",
            "project.one",
            "task-snapshot",
            "task-set-1",
            revision: 1,
            contentHash,
            assertionHash);
        True(
            CortexProjectBriefReferencePolicy.IsValid(valid),
            "A complete retained-record locator was rejected.");
        using (var retainedDocument = JsonDocument.Parse(
                   CortexCanonicalJson.Serialize(valid)))
        {
            True(
                retainedDocument.RootElement
                    .GetProperty("retainedRecord").ValueKind ==
                JsonValueKind.Object,
                "Canonical retained references must emit their structured locator.");
        }
        var legacy = new CortexProjectBriefReference(
            $"hash://{contentHash}",
            contentHash,
            RecordId: null,
            RevisionId: null);
        using (var legacyDocument = JsonDocument.Parse(
                   CortexCanonicalJson.Serialize(legacy)))
        {
            True(
                legacyDocument.RootElement
                    .GetProperty("retainedRecord").ValueKind ==
                JsonValueKind.Null,
                "Canonical legacy references must emit retainedRecord as null.");
        }
        True(
            !CortexProjectBriefReferencePolicy.IsValid(
                new CortexProjectBriefReference(
                    "retained://fake",
                    contentHash,
                    RecordId: null,
                    RevisionId: null,
                    RetainedRecord: null)),
            "A bare retained URI passed without a structured locator.");
        True(
            !CortexProjectBriefReferencePolicy.IsValid(
                valid with { Reference = $"hash://{contentHash}" }),
            "A retained locator passed under a non-retained URI.");
        True(
            !CortexProjectBriefReferencePolicy.IsValid(
                valid with
                {
                    RetainedRecord = valid.RetainedRecord! with
                    {
                        ProjectNamespace = " project.one"
                    }
                }),
            "A retained locator accepted an invalid project namespace.");
        True(
            !CortexProjectBriefReferencePolicy.IsValidForProject(
                valid,
                $"plink_{new string('d', 64)}",
                "project-1",
                "project.one"),
            "A retained locator crossed project-link scope.");
        True(
            !CortexProjectBriefReferencePolicy.IsValidForProject(
                valid,
                linkId,
                "project-2",
                "project.one"),
            "A retained locator crossed project identity.");
        True(
            !CortexProjectBriefReferencePolicy.IsValidForProject(
                valid,
                linkId,
                "project-1",
                "project.two"),
            "A retained locator crossed project namespace.");
    }

    public static Task TextValidationParityAsync()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(ProjectPath(SchemaRelativePath)));
        var definitions = document.RootElement.GetProperty("$defs");
        var text512 = TextRule(definitions, "text512");
        var text1024 = TextRule(definitions, "text1024");

        Equal(text512.Pattern.ToString(), text1024.Pattern.ToString());
        AssertTextParity(text512, CortexProjectBriefContract.MaximumTitleCharacters);
        AssertTextParity(text1024, CortexProjectBriefContract.MaximumTextCharacters);
        return Task.CompletedTask;
    }

    private static void AssertTextParity(TextSchemaRule rule, int expectedMaximum)
    {
        Equal(1, rule.MinimumLength);
        Equal(expectedMaximum, rule.MaximumLength);

        var examples = new List<string>
        {
            string.Empty,
            " ",
            "brief",
            "two words",
            "line one\r\n\tline two",
            "\uFEFFbrief\uFEFF",
            new('x', expectedMaximum),
            new('x', expectedMaximum + 1)
        };
        for (var codePoint = (int)char.MinValue; codePoint <= char.MaxValue; codePoint++)
        {
            var value = (char)codePoint;
            if (char.IsWhiteSpace(value))
            {
                examples.Add($"{value}x");
                examples.Add($"x{value}");
                examples.Add($"x{value}y");
            }
            if (char.IsControl(value))
            {
                examples.Add($"{value}x");
                examples.Add($"x{value}");
                examples.Add($"x{value}y");
            }
        }

        foreach (var example in examples.Distinct(StringComparer.Ordinal))
        {
            var schemaAccepted =
                example.Length >= rule.MinimumLength &&
                example.Length <= rule.MaximumLength &&
                rule.Pattern.IsMatch(example);
            Equal(
                RuntimeAcceptsText(example, expectedMaximum),
                schemaAccepted,
                $"Text-rule mismatch for {Describe(example)}.");
            Equal(
                ProjectorAcceptsText(example, expectedMaximum),
                schemaAccepted,
                $"Projector text-rule mismatch for {Describe(example)}.");
        }
    }

    private static bool RuntimeAcceptsText(string value, int maximumCharacters)
    {
        var method = typeof(CortexProjectBriefService).GetMethod(
            "ValidText",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(CortexProjectBriefService).FullName,
                "ValidText");
        return (bool)(method.Invoke(null, [value, maximumCharacters])
            ?? throw new InvalidOperationException("ValidText returned no result."));
    }

    private static bool ProjectorAcceptsText(string value, int maximumCharacters)
    {
        var method = typeof(CortexProjectBriefProjector).GetMethod(
            "RequireProjectedText",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(
                typeof(CortexProjectBriefProjector).FullName,
                "RequireProjectedText");
        try
        {
            method.Invoke(null, [value, maximumCharacters, "value"]);
            return true;
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is ArgumentException)
        {
            return false;
        }
    }

    private static TextSchemaRule TextRule(JsonElement definitions, string name)
    {
        var definition = definitions.GetProperty(name);
        var pattern = definition.GetProperty("pattern").GetString()
            ?? throw new InvalidOperationException($"{name} has no pattern.");
        return new TextSchemaRule(
            definition.GetProperty("minLength").GetInt32(),
            definition.GetProperty("maxLength").GetInt32(),
            new Regex(
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)));
    }

    private static void AssertObjectShape<T>(JsonElement definitions, string name)
    {
        var definition = definitions.GetProperty(name);
        Equal(JsonValueKind.False, definition.GetProperty("additionalProperties").ValueKind);
        var expected = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var properties = definition.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var required = definition.GetProperty("required")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        SequenceEqual(expected, properties, $"{name} schema properties drifted.");
        SequenceEqual(expected, required, $"{name} required fields drifted.");
    }

    private static void AssertCorePackageOwnsSchema()
    {
        var project = XDocument.Load(ProjectPath(CoreProjectRelativePath));
        var schemaItem = project.Descendants("None").SingleOrDefault(element =>
            string.Equals(
                element.Attribute("Include")?.Value.Replace('\\', '/'),
                "../../schemas/cortex-project-brief.v1.schema.json",
                StringComparison.Ordinal));
        True(schemaItem is not null, "The Core package does not include the project-brief schema.");
        Equal("true", schemaItem!.Attribute("Pack")?.Value);
        Equal("schemas\\", schemaItem.Attribute("PackagePath")?.Value);
    }

    private static string ProjectPath(string relativePath) =>
        Path.Combine(
            FindProjectRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PegasusCortex.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
            throw new DirectoryNotFoundException(
                "Could not locate the Pegasus Cortex project root.");
    }

    private static string Describe(string value) =>
        string.Join(
            " ",
            value.Select(character => $"U+{(int)character:X4}"));

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message ?? $"Expected '{expected}', received '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string? message = null)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(message ?? "Sequences differ.");
    }

    private sealed record TextSchemaRule(
        int MinimumLength,
        int MaximumLength,
        Regex Pattern);
}
