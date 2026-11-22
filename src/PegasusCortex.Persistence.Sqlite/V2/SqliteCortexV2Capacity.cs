namespace Pegasus.Cortex.Persistence.Sqlite.V2;

public sealed record SqliteCortexV2Capacity(
    long MainDatabaseBytes,
    long WalBytes,
    long PageSizeBytes,
    long PageCount,
    long FreelistPages,
    long MaxPageCount,
    long UsedPageBytes,
    double UtilizationPercent,
    string State,
    string Recommendation);

public static class SqliteCortexV2CapacityPolicy
{
    public const long WarningBytes = 512L * 1024 * 1024;
    public const long ElevatedBytes = 1024L * 1024 * 1024;
    public const long MaintenanceBytes = 2L * 1024 * 1024 * 1024;
    public const long DefaultCeilingBytes = 4L * 1024 * 1024 * 1024;

    public static string Classify(long mainDatabaseBytes, long maximumBytes)
    {
        if (mainDatabaseBytes < 0 || maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(mainDatabaseBytes));
        if (mainDatabaseBytes >= maximumBytes)
            return "ceiling";
        if (mainDatabaseBytes >= MaintenanceBytes)
            return "maintenance";
        if (mainDatabaseBytes >= ElevatedBytes)
            return "elevated";
        if (mainDatabaseBytes >= WarningBytes)
            return "warning";
        return "normal";
    }

    public static string Recommendation(string state) => state switch
    {
        "normal" => "No storage action is required.",
        "warning" => "Monitor growth and verify current backups.",
        "elevated" => "Plan retention maintenance and verify backup restore evidence.",
        "maintenance" => "Run reviewed maintenance before the active-store ceiling is approached.",
        "ceiling" => "Writes may fail at the configured active-store ceiling; maintenance or a deliberate capacity-profile change is required.",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
