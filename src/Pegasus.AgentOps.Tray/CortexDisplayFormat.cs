using System.Globalization;

namespace Pegasus.AgentOps.Tray;

internal readonly record struct CortexDisplayValue(string Text, string Exact);

/// <summary>Presentation only. Stored UTC values and numeric measurements remain unchanged.</summary>
internal static class CortexDisplayFormat
{
    internal static CortexDisplayValue Count(long value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var divisor = Divisor(value);
        var suffix = divisor == 1_000_000_000m ? "B" : divisor == 1_000_000m ? "M" : divisor == 1_000m ? "K" : string.Empty;
        return new(Short(value / divisor, culture) + suffix, value.ToString("N0", culture));
    }

    internal static CortexDisplayValue Bytes(long value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var divisor = Divisor(value);
        var unit = divisor == 1_000_000_000m ? "GB" : divisor == 1_000_000m ? "MB" : divisor == 1_000m ? "KB" : "B";
        return new(Short(value / divisor, culture) + " " + unit, value.ToString("N0", culture) + " bytes (decimal units: 1 KB = 1,000 bytes)");
    }

    internal static CortexDisplayValue Duration(decimal milliseconds, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var magnitude = Math.Abs(milliseconds);
        var divisor = magnitude >= 3_597_000 ? 3_600_000m : magnitude >= 59_950 ? 60_000m : magnitude >= 999.5m ? 1_000m : 1m;
        var unit = divisor == 3_600_000m ? "h" : divisor == 60_000m ? "min" : divisor == 1_000m ? "s" : "ms";
        var display = magnitude is > 0 and < 0.001m ? (milliseconds < 0 ? "−" : "") + "<0.001 ms" : Short(milliseconds / divisor, culture) + " " + unit;
        return new(display,
            milliseconds.ToString("0.############################", culture) + " ms");
    }

    internal static CortexDisplayValue Timestamp(DateTime value, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        if (value == DateTime.MinValue) return new("Unknown time", "No timestamp was recorded.");
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return Timestamp(new DateTimeOffset(utc), zone, culture);
    }

    internal static CortexDisplayValue Timestamp(string? value, TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? Timestamp(instant, zone, culture) : new("Unknown time", string.IsNullOrEmpty(value) ? "No timestamp was recorded." : value);

    private static CortexDisplayValue Timestamp(DateTimeOffset instant, TimeZoneInfo? zone, CultureInfo? culture)
    {
        zone ??= TimeZoneInfo.Local;
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var name = zone.IsDaylightSavingTime(instant) ? zone.DaylightName : zone.StandardName;
        if (string.IsNullOrEmpty(name)) name = zone.Id;
        return new(local.ToString("yyyy-MM-dd HH:mm:ss", culture) + " " + name + " (UTC" + local.ToString("zzz", CultureInfo.InvariantCulture) + ")",
            instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static decimal Divisor(long value)
    {
        var divisor = 1m;
        while (divisor < 1_000_000_000m && Math.Abs(value / divisor) >= 999.5m) divisor *= 1000m;
        return divisor;
    }

    private static string Short(decimal value, CultureInfo culture) => value.ToString(Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.#" : Math.Abs(value) < 1 ? "0.###" : "0.##", culture);
}
