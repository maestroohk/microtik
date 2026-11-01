namespace Kibo.Web.Components.Shared;

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };

    public static string Bits(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.##} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1e6:0.#} Mbps",
        >= 1_000 => $"{bitsPerSecond / 1e3:0} kbps",
        _ => $"{bitsPerSecond} bps"
    };

    public static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    public static string Ago(DateTime? utc, DateTime now)
    {
        if (utc == null) return "never";
        var seconds = (long)(now - utc.Value).TotalSeconds;
        if (seconds < 5) return "just now";
        if (seconds < 60) return $"{seconds}s ago";
        if (seconds < 3600) return $"{seconds / 60}m ago";
        if (seconds < 86400) return $"{seconds / 3600}h ago";
        return $"{seconds / 86400}d ago";
    }

    public static string Until(DateTime? utc, DateTime now)
    {
        if (utc == null) return "soon";
        var seconds = (long)(utc.Value - now).TotalSeconds;
        return seconds <= 0 ? "now" : $"in {Duration(seconds)}";
    }

    public static string Money(decimal amount, string currency) =>
        currency == "UGX" ? $"UGX {amount:#,0}" : $"{currency} {amount:#,0.00}";

    public static string Local(DateTime? utc, string timeZone = "Africa/Kampala")
    {
        if (utc == null) return "—";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), zone).ToString("d MMM, HH:mm");
    }
}
