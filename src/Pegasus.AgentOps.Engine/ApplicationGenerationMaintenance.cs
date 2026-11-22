using System.Globalization;
using Pegasus.AgentOps.Protocol;

namespace Pegasus.AgentOps.Engine;

public sealed partial class UserApplicationManager
{
    private const int MaximumInventoryPage = 8;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private long _catalogRevision;

    /// <summary>Bounded owner-local metadata; never arguments, environment or process bodies.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> InventoryAsync(
        int maximum = MaximumInventoryPage, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > MaximumInventoryPage) throw new AgentOpsException("invalid");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            string? after = null;
            if (cursor is not null)
            {
                if (cursor.Length > 128 || cursor.Any(char.IsControl)) throw new AgentOpsException("invalid");
                var parts = cursor.Split(':');
                if (parts.Length != 3 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                    throw new AgentOpsException("invalid");
                ValidateAid(parts[0]); ValidateAid(parts[2]);
                if (parts[0] != _epoch || revision != _catalogRevision) throw new AgentOpsException("app_inventory_changed");
                after = parts[2];
            }
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var ordered = _entries.Values.OrderBy(entry => entry.Aid, StringComparer.Ordinal).ToArray();
            var active = 0; var terminal = 0; var unknown = 0;
            foreach (var entry in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = "unknown"; int? exit = null;
                int? processes = null; string? issue = null; var complete = true;
                try
                {
                    var observed = entry.Host.ObserveOnly();
                    state = observed.State; exit = observed.Exit; processes = observed.Processes; issue = observed.Issue;
                    // Confirmed completed retirement is authoritative even if a historical issue remains.
                    complete = entry.Host.ReleaseReady ||
                        issue is not ("app_host_lost" or "app_observation_incomplete" or "app_owner_lost");
                }
                catch (Exception error) when (error is AgentOpsException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { issue = "app_observation_incomplete"; complete = false; }
                if (!complete) unknown++;
                else if (state is "exited" or "stopped") terminal++;
                else active++;
                if (after is not null && StringComparer.Ordinal.Compare(entry.Aid, after) <= 0 || rows.Count >= maximum) continue;
                var row = new Dictionary<string, object?>
                {
                    ["aid"] = entry.Aid, ["name"] = entry.Name, ["state"] = state,
                    ["pid"] = entry.Host.Pid, ["start"] = entry.Host.Start,
                    ["releasable"] = complete && entry.Host.ReleaseReady
                };
                if (processes is not null) row["processes"] = processes;
                if (exit is not null) row["exit"] = exit;
                if (issue is not null) row["issue"] = issue;
                rows.Add(row);
            }
            var last = rows.LastOrDefault()?.GetValueOrDefault("aid")?.ToString();
            var more = last is not null && ordered.Any(entry => StringComparer.Ordinal.Compare(entry.Aid, last) > 0);
            return new Dictionary<string, object?>
            {
                ["schema"] = "pegasus.pao.application-inventory/1", ["epoch"] = _epoch,
                ["revision"] = _catalogRevision, ["observedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["retained"] = _entries.Count, ["maximumRetained"] = MaxRetainedApplications,
                ["active"] = unknown == 0 ? active : null, ["knownActive"] = active,
                ["terminal"] = terminal, ["unknown"] = unknown, ["maximumActive"] = MaxActiveApplications,
                ["admission"] = _entries.Count >= MaxRetainedApplications ? "retained" :
                    unknown != 0 ? "unknown" : active >= MaxActiveApplications ? "active" : "available",
                ["items"] = rows, ["more"] = more,
                ["next"] = more ? _epoch + ":" + _catalogRevision.ToString(CultureInfo.InvariantCulture) + ":" + last : null
            };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reclaims one fully retired generation; never asks a live job to terminate.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> ReleaseAsync(
        string aid, string epoch, CancellationToken cancellationToken = default)
    {
        ValidateAid(aid); ValidateAid(epoch);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (epoch != _epoch) throw new AgentOpsException("precondition");
            cancellationToken.ThrowIfCancellationRequested();
            if (!_entries.TryGetValue(aid, out var entry)) throw new AgentOpsException("not_found");
            if (!entry.Host.ReleaseReady) throw new AgentOpsException("app_not_releasable");
            var nextRevision = checked(_catalogRevision + 1);
            var terminal = entry.Host.UnobservedSnapshot();
            // ReleaseReady proves DisposeAsync takes only its already-retired cleanup path.
            // Do not replace this check with cached state, PID disappearance or processes==0.
            await entry.Host.DisposeAsync();
            _entries.Remove(aid);
            if (_names.TryGetValue(entry.Name, out var current) && current.Aid == aid) _names.Remove(entry.Name);
            _catalogRevision = nextRevision;
            return new Dictionary<string, object?>
            {
                ["schema"] = "pegasus.pao.application-release/1", ["epoch"] = _epoch,
                ["aid"] = aid, ["name"] = entry.Name, ["state"] = "released",
                ["terminalState"] = terminal.State, ["exit"] = terminal.Exit, ["freedRetained"] = 1
            };
        }
        finally { _gate.Release(); }
    }
}
