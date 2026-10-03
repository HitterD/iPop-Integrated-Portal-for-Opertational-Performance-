namespace IPop.Web.Models;

/// "Last seen …" for someone offline. Everyone sees it (no hide option, by office policy).
public static class LastSeen
{
    public static string? Label(string? status, DateTime? lastSeenUtc, DateTime nowUtc)
    {
        if (status is not (null or "off") || lastSeenUtc is not { } seen) return null;
        var ago = nowUtc - seen;
        if (ago < TimeSpan.FromMinutes(1)) return "Last seen just now";
        if (ago < TimeSpan.FromHours(1)) return $"Last seen {(int)ago.TotalMinutes}m ago";
        var local = seen.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        if (local.Date == today) return $"Last seen today {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Last seen yesterday {local:HH:mm}";
        if (ago < TimeSpan.FromDays(7)) return $"Last seen {local:ddd HH:mm}";
        return $"Last seen {local:d MMM yyyy}";
    }

    /// Offline label with the time when known, else plain "Offline".
    public static string OfflineLabel(DateTime? lastSeenUtc) =>
        Label("off", lastSeenUtc, DateTime.UtcNow) ?? "Offline";
}
