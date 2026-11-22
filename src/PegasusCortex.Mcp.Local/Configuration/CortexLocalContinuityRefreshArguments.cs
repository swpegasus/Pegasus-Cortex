namespace Pegasus.Cortex.Mcp.Local.Configuration;

/// <summary>
/// Strict arguments for the explicit local continuity refresh command. Watch
/// remains a foreground operator mode with a fixed safety-poll contract.
/// </summary>
internal enum CortexLocalContinuityRefreshMode
{
    Once,
    Watch
}

internal sealed record CortexLocalContinuityRefreshArguments(
    CortexLocalCurrentSnapshotArguments Current,
    CortexLocalContinuityRefreshMode Mode)
{
    public const string Command = "continuity-refresh";

    public CortexLocalContinuityRefreshArguments(
        CortexLocalCurrentSnapshotArguments current)
        : this(current, CortexLocalContinuityRefreshMode.Once)
    {
    }

    public static CortexLocalContinuityRefreshArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 10 ||
            !string.Equals(args[0], Command, StringComparison.Ordinal) ||
            args[1] is not ("--once" or "--watch"))
        {
            throw new ArgumentException(
                "Usage: continuity-refresh --once|--watch --profile <id> --state-root <dedicated absolute profile directory> --project-link <id> --captured-at-utc <round-trip UTC timestamp>.");
        }

        // Reuse the established materializer's exact scope, path, timestamp,
        // and near-current validation instead of creating a second parser.
        var materializeArguments = new string[9];
        materializeArguments[0] = "materialize-current";
        Array.Copy(args, 2, materializeArguments, 1, args.Length - 2);
        return new CortexLocalContinuityRefreshArguments(
            CortexLocalCurrentSnapshotArguments.Parse(materializeArguments),
            string.Equals(args[1], "--watch", StringComparison.Ordinal)
                ? CortexLocalContinuityRefreshMode.Watch
                : CortexLocalContinuityRefreshMode.Once);
    }
}
