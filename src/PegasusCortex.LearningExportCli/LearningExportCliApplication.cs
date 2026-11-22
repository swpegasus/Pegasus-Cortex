using System.Text.Json;
using Pegasus.Cortex.LearningExports.Build;
using Pegasus.Cortex.LearningExports.Contracts;
using Pegasus.Cortex.LearningExports.Serialization;
using Pegasus.Shared.Diagnostics;

namespace Pegasus.Cortex.LearningExportCli;

public static class LearningExportCliApplication
{
    private const long MaximumBundleBytes = 128L * 1024 * 1024;
    private const long MaximumDefinitionBytes = 4L * 1024 * 1024;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        CliArguments parsed;
        try
        {
            parsed = CliArguments.Parse(args);
        }
        catch (CliUsageException exception)
        {
            await WriteAsync(output, CliResponse.Rejected(
                "validate",
                Diagnostic("learning_export_cli_usage_invalid", exception.Message)),
                cancellationToken).ConfigureAwait(false);
            return 2;
        }

        if (parsed.Help)
        {
            await WriteAsync(output, new
            {
                schemaVersion = 1,
                usage = "pegasus-cortex-learning-export --bundle <file> --definition <file> --subject <id> --policy-revision <n> --authority <id> --decision <id>=<sha256> --namespace <name> [repeat authority/decision/namespace]",
                operation = "plan",
                artifactsProduced = false,
                serverAuthorizationGranted = false,
                modelTrainingPerformed = false,
                networkAccess = false,
                description = "Validates inputs and creates an in-memory learning-export plan only. It cannot publish artifacts, train models, or access network services."
            }, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        const string operation = "plan";
        try
        {
            var bundle = await ReadAsync<LearningInputBundleV1>(
                parsed.BundlePath!, MaximumBundleBytes, cancellationToken).ConfigureAwait(false);
            var definition = await ReadAsync<LearningDatasetDefinitionV1>(
                parsed.DefinitionPath!, MaximumDefinitionBytes, cancellationToken).ConfigureAwait(false);
            var authority = new LearningExportAuthorityContext(
                parsed.SubjectId!,
                parsed.PolicyRevision,
                CanPlan: true,
                CanBuild: false,
                TrustedApprovalAuthorities: parsed.Authorities.ToHashSet(StringComparer.Ordinal),
                ApprovedDecisionHashes: parsed.Decisions,
                ExportableNamespaces: parsed.Namespaces.ToHashSet(StringComparer.Ordinal),
                ApprovedOutputRoot: null);
            var request = new LearningDatasetBuildRequest(
                PublishArtifacts: false,
                OutputRoot: null,
                Authority: authority);
            var result = await new LearningDatasetBuilder().ExecuteAsync(
                bundle,
                definition,
                request,
                cancellationToken).ConfigureAwait(false);

            var response = CliResponse.FromResult(operation, result);
            await WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            return result.Accepted ? 0 : 4;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteAsync(output, CliResponse.Rejected(
                operation,
                Diagnostic("learning_export_cli_canceled", "The offline planning operation was canceled.")),
                CancellationToken.None).ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            await WriteAsync(output, CliResponse.Rejected(
                operation,
                Diagnostic(
                    "learning_export_cli_input_invalid",
                    "An explicit input file could not be read or parsed within its configured limit.",
                    exception.GetType().Name)),
                cancellationToken).ConfigureAwait(false);
            return 3;
        }
        catch (Exception exception)
        {
            await WriteAsync(output, CliResponse.Rejected(
                operation,
                Diagnostic(
                    "learning_export_cli_failed",
                    "The offline planning operation failed before completion.",
                    exception.GetType().Name)),
                cancellationToken).ConfigureAwait(false);
            return 5;
        }
    }

    private static async ValueTask<T> ReadAsync<T>(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw new ArgumentException("An explicit input path was blank or contained a control character.", nameof(path));

        var fullPath = Path.GetFullPath(path);

        // Open first so all size checks apply to the handle actually read, not stale path metadata.
        await using var input = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        EnsureRegularNonReparsePath(fullPath);
        var initialLength = input.Length;
        EnsureLengthWithinLimit(initialLength, maximumBytes);

        using var boundedInput = new BoundedReadStream(input, maximumBytes, leaveOpen: true);
        var value = await JsonSerializer.DeserializeAsync<T>(
            boundedInput,
            LearningExportJson.CreateSerializerOptions(),
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"The {typeof(T).Name} document was empty.");

        var finalLength = input.Length;
        EnsureLengthWithinLimit(finalLength, maximumBytes);
        if (finalLength != initialLength || boundedInput.BytesRead != initialLength)
            throw new IOException("The explicit input file changed or was not read completely through the opened handle.");

        return value;
    }

    private static void EnsureRegularNonReparsePath(string fullPath)
    {
        var leafAttributes = File.GetAttributes(fullPath);
        if ((leafAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("The explicit input path must identify a regular, non-reparse file.");

        for (var parent = Directory.GetParent(fullPath); parent is not null; parent = parent.Parent)
        {
            parent.Refresh();
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The explicit input file cannot be reached through a reparse-point directory.");
        }
    }

    private static void EnsureLengthWithinLimit(long length, long maximumBytes)
    {
        if (length is <= 0 || length > maximumBytes)
            throw new IOException("The explicit input file is empty or exceeds its configured byte limit.");
    }

    private static async ValueTask WriteAsync<T>(
        TextWriter output,
        T value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, LearningExportJson.CreateSerializerOptions());
        await output.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static CliDiagnostic Diagnostic(string code, string message, string? cause = null) =>
        new(code, "error", "cortex.learning-export.cli", message, false,
            new Dictionary<string, string?>(StringComparer.Ordinal), cause);
}

public sealed record CliDiagnostic(
    string Code,
    string Severity,
    string Operation,
    string Message,
    bool Retryable,
    IReadOnlyDictionary<string, string?> Context,
    string? Cause);

public sealed record CliManifestSummary(
    string BuildId,
    string ManifestHash,
    int ItemCount,
    string ArtifactSha256,
    long ArtifactBytes,
    IReadOnlyDictionary<string, int> SplitCounts,
    IReadOnlyList<string> IntendedUses);

public sealed record CliResponse(
    int SchemaVersion,
    string Operation,
    bool Accepted,
    bool Published,
    string? ArtifactDirectory,
    CliManifestSummary? Manifest,
    IReadOnlyList<CliDiagnostic> Diagnostics,
    bool NetworkAccess,
    bool ServerAuthorizationGranted,
    bool ModelTrainingPerformed)
{
    public static CliResponse FromResult(string operation, LearningDatasetBuildResult result)
    {
        var manifest = result.Manifest is null
            ? null
            : new CliManifestSummary(
                result.Manifest.BuildId,
                result.Manifest.ManifestHash,
                result.Manifest.Items.Count,
                result.Manifest.Artifacts.Single().Sha256,
                result.Manifest.Artifacts.Single().Bytes,
                result.Manifest.Splits.ToDictionary(item => item.Name, item => item.ItemCount, StringComparer.Ordinal),
                result.Manifest.IntendedUses);
        return new CliResponse(
            1,
            operation,
            result.Accepted,
            Published: false,
            ArtifactDirectory: null,
            Manifest: manifest,
            Diagnostics: result.Diagnostics.Select(MapDiagnostic).ToArray(),
            NetworkAccess: false,
            ServerAuthorizationGranted: false,
            ModelTrainingPerformed: false);
    }

    public static CliResponse Rejected(string operation, CliDiagnostic diagnostic) =>
        new(1, operation, false, false, null, null, [diagnostic], false, false, false);

    private static CliDiagnostic MapDiagnostic(PegasusDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Severity.ToString().ToLowerInvariant(),
            diagnostic.Operation,
            diagnostic.Message,
            diagnostic.Retryable,
            diagnostic.Context,
            diagnostic.Cause);
}

internal sealed record CliArguments(
    bool Help,
    string? BundlePath,
    string? DefinitionPath,
    string? SubjectId,
    long PolicyRevision,
    IReadOnlyList<string> Authorities,
    IReadOnlyDictionary<string, string> Decisions,
    IReadOnlyList<string> Namespaces)
{
    private const int MaximumFieldCharacters = 256;

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] is "--help" or "-h")
            return new(true, null, null, null, 0, [], new Dictionary<string, string>(), []);

        string? bundle = null;
        string? definition = null;
        string? subject = null;
        long policyRevision = 0;
        var policyRevisionSeen = false;
        var authorities = new List<string>();
        var decisions = new Dictionary<string, string>(StringComparer.Ordinal);
        var namespaces = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--bundle":
                    bundle = ReadSingle(args, ref index, argument, bundle);
                    break;
                case "--definition":
                    definition = ReadSingle(args, ref index, argument, definition);
                    break;
                case "--subject":
                    subject = ReadBoundedSingle(args, ref index, argument, subject);
                    break;
                case "--policy-revision":
                    if (policyRevisionSeen)
                        throw new CliUsageException("--policy-revision may be supplied only once.");
                    policyRevisionSeen = true;
                    var revisionText = ReadSingle(args, ref index, argument, null);
                    if (!long.TryParse(revisionText, out policyRevision) || policyRevision <= 0)
                        throw new CliUsageException("--policy-revision requires a positive integer.");
                    break;
                case "--authority":
                    authorities.Add(ReadBoundedField(args, ref index, argument));
                    break;
                case "--decision":
                    AddDecision(decisions, ReadValue(args, ref index, argument));
                    break;
                case "--namespace":
                    namespaces.Add(ReadBoundedField(args, ref index, argument));
                    break;
                default:
                    throw new CliUsageException($"Unknown argument '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(definition) ||
            string.IsNullOrWhiteSpace(subject) || policyRevision <= 0 ||
            authorities.Count == 0 || decisions.Count == 0 || namespaces.Count == 0)
        {
            throw new CliUsageException(
                "Bundle, definition, subject, policy revision, authority, decision, and namespace arguments are required.");
        }

        return new(
            false,
            bundle,
            definition,
            subject,
            policyRevision,
            authorities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            new SortedDictionary<string, string>(decisions, StringComparer.Ordinal),
            namespaces.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static string ReadSingle(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        string? existing)
    {
        if (existing is not null)
            throw new CliUsageException($"{option} may be supplied only once.");
        return ReadValue(args, ref index, option);
    }

    private static string ReadBoundedField(IReadOnlyList<string> args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        if (value.Length > MaximumFieldCharacters)
            throw new CliUsageException($"{option} cannot exceed {MaximumFieldCharacters} characters.");
        return value;
    }

    private static string ReadBoundedSingle(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        string? existing)
    {
        if (existing is not null)
            throw new CliUsageException($"{option} may be supplied only once.");
        return ReadBoundedField(args, ref index, option);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]) ||
            args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"{option} requires a value.");
        }

        var value = args[index];
        if (value.Any(char.IsControl))
            throw new CliUsageException($"{option} cannot contain control characters.");
        return value;
    }

    private static void AddDecision(IDictionary<string, string> decisions, string binding)
    {
        var separator = binding.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0 || separator == binding.Length - 1)
            throw new CliUsageException("--decision requires <decision-id>=<lowercase-sha256>.");

        var id = binding[..separator];
        var hash = binding[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumFieldCharacters || id.Any(char.IsControl) ||
            hash.Length != 64 ||
            hash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new CliUsageException("--decision requires a decision ID of at most 256 characters and a lowercase SHA-256 hash.");
        }
        if (!decisions.TryAdd(id, hash))
            throw new CliUsageException($"Decision '{id}' was supplied more than once.");
    }
}

/// <summary>
/// Limits bytes observed by the JSON parser even if an already-open input file grows during the read.
/// </summary>
internal sealed class BoundedReadStream(Stream input, long maximumBytes, bool leaveOpen) : Stream
{
    private long _bytesRead;

    public long BytesRead => _bytesRead;

    public override bool CanRead => input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("The buffer range is invalid.", nameof(count));

        var read = input.Read(buffer, offset, LimitReadLength(count));
        AccountForRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = input.Read(buffer[..LimitReadLength(buffer.Length)]);
        AccountForRead(read);
        return read;
    }

    public override int ReadByte()
    {
        var value = input.ReadByte();
        if (value >= 0)
            AccountForRead(1);
        return value;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("The buffer range is invalid.", nameof(count));

        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await input.ReadAsync(
            buffer[..LimitReadLength(buffer.Length)],
            cancellationToken).ConfigureAwait(false);
        AccountForRead(read);
        return read;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
            input.Dispose();
        base.Dispose(disposing);
    }

    private int LimitReadLength(int requested)
    {
        if (requested == 0)
            return 0;

        var bytesUntilOverflow = maximumBytes - _bytesRead + 1;
        if (bytesUntilOverflow <= 0)
            throw new IOException("The explicit input file exceeded its configured byte limit while being read.");
        return (int)Math.Min(requested, bytesUntilOverflow);
    }

    private void AccountForRead(int count)
    {
        _bytesRead += count;
        if (_bytesRead > maximumBytes)
            throw new IOException("The explicit input file exceeded its configured byte limit while being read.");
    }
}

internal sealed class CliUsageException(string message) : Exception(message);
