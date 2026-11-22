using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pegasus.Cortex.Contracts.Serialization;
using Pegasus.Cortex.Contracts.WorkPackets;
using Pegasus.Cortex.Sync.Client.Contracts;

namespace Pegasus.Cortex.Sync.Client.ChangeSources;

public sealed record CortexMaterializeCurrentProcessOptions(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments,
    string ProfileId,
    string StateRoot,
    string ProjectLinkId,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Bounded adapter over the supported Mcp.Local materialize-current operator command.
/// ProcessStartInfo.ArgumentList is used directly: no shell, environment-selected endpoint,
/// or network transport is available through this boundary.
/// </summary>
public sealed class CortexMaterializeCurrentChangeSource : ICortexLocalSyncChangeSource
{
    private const string ResultContractId =
        "pegasus.cortex.local-current-materialization-result/1.0";
    private const int MaximumStandardOutputCharacters = 128 * 1024;
    private const int MaximumStandardErrorCharacters = 16 * 1024;
    private const int MaximumPrefixArguments = 16;
    private readonly CortexMaterializeCurrentProcessOptions _options;
    private readonly TimeProvider _timeProvider;

    public CortexMaterializeCurrentChangeSource(
        CortexMaterializeCurrentProcessOptions options,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(options);
    }

    public async ValueTask<CortexLocalSyncChange> ReadCurrentAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = _timeProvider.GetUtcNow();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(capturedAt),
            EnableRaisingEvents = true
        };
        if (!process.Start())
            throw new InvalidOperationException("The local materialize-current producer did not start.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EffectiveTimeout);
        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            MaximumStandardOutputCharacters,
            timeout.Token);
        var stderrTask = ReadBoundedAsync(
            process.StandardError,
            MaximumStandardErrorCharacters,
            timeout.Token);
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(timeout.Token),
                stdoutTask,
                stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("The local materialize-current producer exceeded its bounded timeout.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The local materialize-current producer exited with {process.ExitCode}: {Sanitize(stderr)}");
        }
        return ParseResult(Encoding.UTF8.GetBytes(stdout));
    }

    internal static CortexLocalSyncChange ParseResult(ReadOnlySpan<byte> stdoutUtf8)
    {
        if (stdoutUtf8.IsEmpty || stdoutUtf8.Length > MaximumStandardOutputCharacters * 4)
            throw new InvalidDataException("The materialize-current result is empty or unbounded.");
        CortexLocalCurrentMaterializationResult result;
        try
        {
            result = CortexCanonicalJson.Deserialize<CortexLocalCurrentMaterializationResult>(stdoutUtf8);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new InvalidDataException(
                "The producer emitted malformed JSON or more than one stdout document.",
                exception);
        }
        var canonicalResult = CortexCanonicalJson.Serialize(result);
        if (!stdoutUtf8.TrimAsciiWhitespace().SequenceEqual(canonicalResult) ||
            !string.Equals(result.ContractId, ResultContractId, StringComparison.Ordinal) ||
            !result.Accepted ||
            result.Disposition is not ("appended" or "idempotent") ||
            !IsHash(result.AuthoritySetHash) ||
            !IsHash(result.SnapshotHash) ||
            result.CurrentRevision < 1 || result.RetainedRevision < 1)
        {
            throw new InvalidDataException("The materialize-current result is noncanonical, rejected, or outside its contract.");
        }

        byte[] snapshotBytes;
        try
        {
            snapshotBytes = Convert.FromBase64String(result.SnapshotCanonicalBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The materialized snapshot is not canonical base64.", exception);
        }
        if (!string.Equals(
                Convert.ToBase64String(snapshotBytes),
                result.SnapshotCanonicalBase64,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The materialized snapshot base64 is not canonical.");
        }
        if (snapshotBytes.Length is 0 or > AgentTaskSnapshotContractLimits.MaximumSerializedSnapshotBytes)
            throw new InvalidDataException("The materialized task snapshot is empty or unbounded.");
        var snapshot = CortexCanonicalJson.Deserialize<AgentTaskSnapshotV1>(snapshotBytes);
        if (!AgentTaskSnapshotPolicy.HasValidHash(snapshot) ||
            !CortexCanonicalJson.Serialize(snapshot).AsSpan().SequenceEqual(snapshotBytes) ||
            !string.Equals(snapshot.ContractId, AgentTaskSnapshotContractIds.Snapshot, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ProjectId, result.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.ProjectLinkId, result.ProjectLinkId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotHash, result.SnapshotHash, StringComparison.Ordinal) ||
            snapshot.SnapshotRevision != result.RetainedRevision ||
            !string.Equals(snapshot.ContextSetId, result.ContextSetId, StringComparison.Ordinal) ||
            !string.Equals(
                ComputeAuthoritySetHash(snapshot.SourceRevisions),
                result.AuthoritySetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The materialized snapshot hash, contract, scope, revision, or authority set does not match its result envelope.");
        }

        return new CortexLocalSyncChange(
            result.ProjectId,
            result.ProjectLinkId,
            result.AuthoritySetHash,
            snapshot.IdempotencyKey,
            "current-recovery",
            AgentTaskSnapshotContractIds.Snapshot,
            snapshotBytes,
            snapshot.CapturedAt,
            snapshot.ExpiresAt,
            BaseRevisionId: null,
            BaseRevisionSha256: null);
    }

    private ProcessStartInfo CreateStartInfo(DateTimeOffset capturedAt)
    {
        var start = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in _options.PrefixArguments)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("materialize-current");
        start.ArgumentList.Add("--profile");
        start.ArgumentList.Add(_options.ProfileId);
        start.ArgumentList.Add("--state-root");
        start.ArgumentList.Add(_options.StateRoot);
        start.ArgumentList.Add("--project-link");
        start.ArgumentList.Add(_options.ProjectLinkId);
        start.ArgumentList.Add("--captured-at-utc");
        start.ArgumentList.Add(capturedAt.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture));
        return start;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[Math.Min(4096, maximumCharacters + 1)];
        var value = new StringBuilder(Math.Min(4096, maximumCharacters));
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return value.ToString();
            if (value.Length > maximumCharacters - read)
                throw new InvalidDataException("A producer output stream exceeded its fixed bound.");
            value.Append(buffer, 0, read);
        }
    }

    private static void ValidateOptions(CortexMaterializeCurrentProcessOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath) ||
            !Path.IsPathFullyQualified(options.ExecutablePath) ||
            !File.Exists(options.ExecutablePath) ||
            options.PrefixArguments is null || options.PrefixArguments.Count > MaximumPrefixArguments ||
            options.PrefixArguments.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl)) ||
            string.IsNullOrWhiteSpace(options.ProfileId) || options.ProfileId.Length > 128 ||
            string.IsNullOrWhiteSpace(options.ProjectLinkId) || options.ProjectLinkId.Length > 128 ||
            string.IsNullOrWhiteSpace(options.StateRoot) || !Path.IsPathFullyQualified(options.StateRoot) ||
            !Directory.Exists(options.StateRoot) ||
            options.EffectiveTimeout < TimeSpan.FromSeconds(5) ||
            options.EffectiveTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("The materialize-current process options are missing, unsafe, or outside fixed bounds.", nameof(options));
        }
        if (!string.Equals(
                options.ExecutablePath,
                Path.GetFullPath(options.ExecutablePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !string.Equals(
                options.StateRoot,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.StateRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Executable and state-root paths must already be canonical.", nameof(options));
        }
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string ComputeAuthoritySetHash(
        IReadOnlyList<AgentWorkSourceRevisionV1> sourceRevisions)
    {
        // This is the language-neutral pegasus.cortex.authority-set/1.0 projection.
        // Keeping it here avoids loading the full query runtime into the lightweight client.
        var captured = sourceRevisions
            .Take(AgentWorkPacketContractLimits.MaximumSourceRevisions + 1)
            .ToArray();
        if (captured.Length == 0 ||
            captured.Length > AgentWorkPacketContractLimits.MaximumSourceRevisions ||
            captured.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != captured.Length)
        {
            throw new InvalidDataException("The task snapshot authority sources are empty, duplicated, or unbounded.");
        }
        var projection = new
        {
            contractId = "pegasus.cortex.authority-set/1.0",
            sources = captured.OrderBy(item => item.SourceId, StringComparer.Ordinal)
                .Select(item => new { item.SourceId, item.RevisionId, item.Sha256 })
        };
        return CortexCanonicalJson.Sha256(CortexCanonicalJson.Serialize(projection));
    }

    private static string Sanitize(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between observation and bounded cleanup.
        }
    }

    private sealed record CortexLocalCurrentMaterializationResult(
        string ContractId,
        bool Accepted,
        string Disposition,
        string ProjectLinkId,
        string ProjectId,
        string ProjectNamespace,
        string AuthoritySetHash,
        long CurrentRevision,
        long RetainedRevision,
        string SnapshotHash,
        string ContextSetId,
        string? ProjectBriefDisposition,
        string? ProjectBriefContextSetId,
        string? ProjectionIssueCode,
        string SnapshotCanonicalBase64);
}

internal static class ReadOnlySpanByteExtensions
{
    public static ReadOnlySpan<byte> TrimAsciiWhitespace(this ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
        var end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            end--;
        return value[start..end];
    }
}
