using System.Threading.Channels;
using Pegasus.Cortex.Mcp.Local.Configuration;

namespace Pegasus.Cortex.Mcp.Local.Recovery;

internal enum CortexLocalContinuitySignal
{
    FileHint,
    SafetyPoll
}

/// <summary>
/// Advisory wake-up source for the foreground continuity watcher. Decisions
/// always use a fresh deterministic input fingerprint after waking.
/// </summary>
internal interface ICortexLocalContinuityChangeSignalSource : IAsyncDisposable
{
    ValueTask<CortexLocalContinuitySignal> WaitAsync(
        TimeSpan safetyPollInterval,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coalesces exact authority-file filesystem events into hints. FileSystemWatcher
/// loss or overflow cannot hide a change because every wait also has a fixed
/// safety-poll deadline.
/// </summary>
internal sealed class FileSystemCortexLocalContinuityChangeSignalSource :
    ICortexLocalContinuityChangeSignalSource
{
    private static readonly IReadOnlySet<string> RootNames =
        new HashSet<string>(PathComparer)
        {
            "START_HERE.md",
            "AGENTS.md"
        };

    private static readonly IReadOnlySet<string> CortexNames =
        new HashSet<string>(PathComparer)
        {
            "STATE.md",
            "TASKS.md",
            "RECOVERY.md",
            "meta.json"
        };

    private readonly Channel<byte> _hints = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly FileSystemWatcher _rootWatcher;
    private readonly FileSystemWatcher _cortexWatcher;
    private bool _disposed;

    public FileSystemCortexLocalContinuityChangeSignalSource(string projectRoot)
    {
        var root = CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot);
        var cortexRoot = Path.Combine(root, ".cortex");
        if (!Directory.Exists(cortexRoot) ||
            (File.GetAttributes(cortexRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The continuity watcher requires a regular .cortex directory.");
        }

        _rootWatcher = CreateWatcher(root, RootNames);
        try
        {
            _cortexWatcher = CreateWatcher(cortexRoot, CortexNames);
        }
        catch
        {
            _rootWatcher.Dispose();
            throw;
        }
        _rootWatcher.EnableRaisingEvents = true;
        _cortexWatcher.EnableRaisingEvents = true;
    }

    public async ValueTask<CortexLocalContinuitySignal> WaitAsync(
        TimeSpan safetyPollInterval,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (safetyPollInterval != CortexLocalContinuityWatch.SafetyPollInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(safetyPollInterval),
                "The continuity watcher safety poll is fixed at 60 seconds.");
        }

        using var pending = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var hint = _hints.Reader.ReadAsync(pending.Token).AsTask();
        var poll = Task.Delay(safetyPollInterval, pending.Token);
        var completed = await Task.WhenAny(hint, poll).ConfigureAwait(false);
        if (ReferenceEquals(completed, hint))
        {
            await hint.ConfigureAwait(false);
            pending.Cancel();
            while (_hints.Reader.TryRead(out _))
            {
            }
            return CortexLocalContinuitySignal.FileHint;
        }

        await poll.ConfigureAwait(false);
        pending.Cancel();
        return CortexLocalContinuitySignal.SafetyPoll;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        _rootWatcher.EnableRaisingEvents = false;
        _cortexWatcher.EnableRaisingEvents = false;
        _rootWatcher.Dispose();
        _cortexWatcher.Dispose();
        _hints.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private FileSystemWatcher CreateWatcher(
        string directory,
        IReadOnlySet<string> names)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.CreationTime
        };
        watcher.Changed += (_, args) => SignalIfRelevant(args.Name, names);
        watcher.Created += (_, args) => SignalIfRelevant(args.Name, names);
        watcher.Deleted += (_, args) => SignalIfRelevant(args.Name, names);
        watcher.Renamed += (_, args) =>
        {
            SignalIfRelevant(args.OldName, names);
            SignalIfRelevant(args.Name, names);
        };
        // Buffer overflow and other watcher faults are themselves hints to
        // resample the complete deterministic authority set.
        watcher.Error += (_, _) => Signal();
        return watcher;
    }

    private void SignalIfRelevant(string? name, IReadOnlySet<string> names)
    {
        if (name is not null && names.Contains(name))
            Signal();
    }

    private void Signal()
    {
        if (!_disposed)
            _hints.Writer.TryWrite(0);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
