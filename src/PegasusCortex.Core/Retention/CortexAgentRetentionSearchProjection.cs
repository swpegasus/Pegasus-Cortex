using System.Text;
using Pegasus.Cortex.Contracts.WorkPackets;

namespace Pegasus.Cortex.Core.Retention;

/// <summary>
/// Produces a deliberately lossy search index. Reference identifiers, paths,
/// approval references, and evidence identifiers stay out of the projection so
/// indexed text cannot be mistaken for an authority or executable payload.
/// </summary>
internal static class CortexAgentRetentionSearchProjection
{
    private const string PassivePrefix =
        "passive untrusted reference data; no scheduling, claim, approval, or execution authority";

    public static string Create(AgentTaskSnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projection = new BoundedProjection();
        projection.Add("treatment", PassivePrefix);
        foreach (var task in snapshot.Tasks)
        {
            projection.Add("task state", task.ReportedState);
            projection.Add("task objective", task.Objective);
            projection.Add("task current state", task.CurrentState);
            projection.Add("task next action", task.NextAction);
            projection.Add("task blocker", task.Blockers);
            projection.Add("task known failure", task.KnownFailures);
        }
        return projection.ToString();
    }

    public static string Create(AgentWorkPacketV1 packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var projection = new BoundedProjection();
        projection.Add("treatment", PassivePrefix);
        projection.Add("stage", packet.Stage);
        projection.Add("objective", packet.Objective);
        projection.Add("current state", packet.CurrentState);
        projection.Add("expected output", packet.ExpectedOutput);
        projection.Add("completed work", packet.CompletedWork);
        projection.Add("remaining work", packet.RemainingWork);
        projection.Add("next action", packet.NextAction);
        projection.Add("blocker", packet.Blockers);
        projection.Add("known failure", packet.KnownFailures);
        if (packet.Closeout is not null)
        {
            projection.Add("closeout disposition", packet.Closeout.Disposition);
            projection.Add("closeout summary", packet.Closeout.PublicSummary);
            projection.Add("closeout change", packet.Closeout.Changes);
            projection.Add("closeout risk", packet.Closeout.Risks);
        }
        return projection.ToString();
    }

    private sealed class BoundedProjection
    {
        private readonly StringBuilder _text =
            new(CortexAgentRetentionLimits.MaximumSearchTextCharacters);

        public void Add(string label, IEnumerable<string> values)
        {
            foreach (var value in values)
                Add(label, value);
        }

        public void Add(string label, string? value)
        {
            if (value is null || _text.Length == CortexAgentRetentionLimits.MaximumSearchTextCharacters)
                return;

            if (_text.Length != 0)
                AppendLiteral(" | ");
            AppendNormalized(label);
            AppendLiteral(": ");
            AppendNormalized(value);
        }

        public override string ToString() => _text.ToString();

        private void AppendLiteral(string value)
        {
            foreach (var character in value)
            {
                if (_text.Length == CortexAgentRetentionLimits.MaximumSearchTextCharacters)
                    return;
                _text.Append(character);
            }
        }

        private void AppendNormalized(string value)
        {
            var pendingSpace = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = _text.Length != 0;
                    continue;
                }

                if (pendingSpace)
                {
                    if (_text.Length == CortexAgentRetentionLimits.MaximumSearchTextCharacters)
                        return;
                    _text.Append(' ');
                    pendingSpace = false;
                }

                if (char.IsHighSurrogate(character) &&
                    index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]))
                {
                    if (_text.Length >
                        CortexAgentRetentionLimits.MaximumSearchTextCharacters - 2)
                    {
                        return;
                    }
                    _text.Append(character);
                    _text.Append(value[++index]);
                    continue;
                }

                if (_text.Length == CortexAgentRetentionLimits.MaximumSearchTextCharacters)
                    return;
                _text.Append(char.IsSurrogate(character) ? '\uFFFD' : character);
            }
        }
    }
}
