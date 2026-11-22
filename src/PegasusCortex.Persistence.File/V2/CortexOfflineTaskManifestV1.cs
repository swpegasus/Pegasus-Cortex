using System.Text.Json;

namespace Pegasus.Cortex.Persistence.File.V2;

internal static class CortexOfflineTaskIdentity
{
    public static string Require(string? taskId, string parameterName)
    {
        var value = CortexFileV2Validation.RequireIdentifier(taskId, parameterName);
        if (!string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The stable offline task identity must use canonical lowercase ASCII.",
                parameterName);
        }
        return value;
    }

    public static string ScopeIdentity(string projectRoot, string taskId)
    {
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var canonicalRoot = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        return Pegasus.Cortex.Contracts.Serialization.CortexCanonicalJson.Sha256(
            canonicalRoot + "\n" + Require(taskId, nameof(taskId)));
    }
}

/// <summary>
/// Binds one stable Codex task/chat identity to one isolated offline file
/// scope. The project root is supplied by the caller and is never persisted.
/// </summary>
public sealed record CortexOfflineTaskManifestV1(
    string Schema,
    string TaskId,
    string ProjectId,
    string WriteNamespace,
    IReadOnlyList<string> ReadNamespaces,
    int HotMaximumUtf8Bytes,
    int ChunkMaximumRecords,
    int ChunkMaximumUtf8Bytes)
{
    public const string CurrentSchema = "pegasus.cortex.offline-task/1.0";

    public CortexOfflineTaskManifestV1 Validate(string expectedTaskId)
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException("The offline task manifest schema is unsupported.");
        var expected = CortexOfflineTaskIdentity.Require(expectedTaskId, nameof(expectedTaskId));
        var retained = CortexOfflineTaskIdentity.Require(TaskId, nameof(TaskId));
        if (!string.Equals(retained, expected, StringComparison.Ordinal))
            throw new InvalidDataException("The offline task manifest belongs to a different task identity.");
        var binding = ToProjectManifest().Validate();
        return this with { ReadNamespaces = binding.ReadNamespaces };
    }

    public CortexFileProjectManifestV2 ToProjectManifest() => new(
        CortexFileProjectManifestV2.CurrentSchema,
        ProjectId,
        WriteNamespace,
        ReadNamespaces,
        HotMaximumUtf8Bytes,
        ChunkMaximumRecords,
        ChunkMaximumUtf8Bytes);

    public static CortexOfflineTaskManifestV1 From(
        string taskId,
        CortexFileProjectManifestV2 projectManifest)
    {
        var validatedTaskId = CortexOfflineTaskIdentity.Require(taskId, nameof(taskId));
        var binding = (projectManifest ?? throw new ArgumentNullException(nameof(projectManifest))).Validate();
        return new(
            CurrentSchema,
            validatedTaskId,
            binding.ProjectId,
            binding.WriteNamespace,
            binding.ReadNamespaces,
            binding.HotMaximumUtf8Bytes,
            binding.ChunkMaximumRecords,
            binding.ChunkMaximumUtf8Bytes);
    }

    public static async ValueTask<CortexOfflineTaskManifestV1> ReadAsync(
        string projectRoot,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var path = CortexFileV2PathPolicy.OfflineScopeManifestPath(root, taskId);
        if (Directory.Exists(path))
            throw new InvalidDataException("A directory occupies the offline task manifest path.");
        byte[] bytes;
        try
        {
            bytes = await CortexFileV2AtomicIo.ReadExactAsync(
                path,
                CortexFileV2Limits.MaximumManifestUtf8Bytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                "No offline binding exists for the supplied task identity. Initialize that task scope explicitly.",
                exception);
        }

        try
        {
            return (JsonSerializer.Deserialize<CortexOfflineTaskManifestV1>(bytes, CortexFileV2Json.Options) ??
                throw new InvalidDataException("The offline task manifest is empty."))
                .Validate(taskId);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The offline task manifest is malformed.", exception);
        }
    }

    internal async ValueTask WriteImmutableAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var validated = Validate(TaskId);
        var root = CortexFileV2PathPolicy.ValidateExistingProjectRoot(projectRoot);
        var scopeRoot = CortexFileV2PathPolicy.OfflineScopeRoot(root, TaskId);
        var path = CortexFileV2PathPolicy.OfflineScopeManifestPath(root, TaskId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(validated, CortexFileV2Json.Options);
        await CortexFileV2AtomicIo.WriteCreateOnlyAtomicAsync(
            path,
            bytes,
            CortexFileV2Limits.MaximumManifestUtf8Bytes,
            scopeRoot,
            cancellationToken).ConfigureAwait(false);
    }
}
