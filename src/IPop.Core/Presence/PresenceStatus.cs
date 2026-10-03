namespace IPop.Core.Presence;

/// Pure presence computation — no I/O, no clock access (now is a parameter),
/// so every boundary is unit-testable. The effective status is derived from stored
/// facts (lastSeenAt, manualStatus, idle) and never persisted itself.
///
/// on   = an iPop tab is open and beating (even minimized/background) and the user is active.
/// away = a tab is still beating but the computer has been idle (no input / screen locked).
/// off  = no beat for OfflineAfter: every tab closed, computer asleep/off, or network gone.
public static class PresenceStatus
{
    /// Client beats every 30s; hidden tabs are throttled to ~1/min, so two minutes
    /// tolerates one late beat without flapping between on and off.
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    /// Returns "on" | "away" | "busy" | "off".
    /// Busy is shown only while heartbeat-derived presence is online.
    public static string ComputeStatus(DateTime? lastSeenAt, string? manualStatus, DateTime now, bool idle = false) =>
        lastSeenAt is null || now - lastSeenAt >= OfflineAfter ? "off"
        : manualStatus == "busy" ? "busy"
        : idle ? "away"
        : "on";

    /// Busy durations offered in the status menu. Null/empty = until turned off.
    public static readonly string[] BusyDurations = ["1h", "2h", "today"];

    /// When a Busy chosen now ends: null = until turned off. "today" ends at the next local
    /// midnight in the user's time zone. False for an unknown duration.
    public static bool TryBusyUntil(string? duration, DateTime nowUtc, TimeZoneInfo tz, out DateTime? until)
    {
        until = null;
        switch (duration)
        {
            case null or "": return true;
            case "1h": until = nowUtc.AddHours(1); return true;
            case "2h": until = nowUtc.AddHours(2); return true;
            case "today":
                var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
                until = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified), tz);
                return true;
            default: return false;
        }
    }

    /// The manual status still in force: a timed Busy past its end is automatic again.
    public static string? EffectiveManual(string? manualStatus, DateTime? busyUntilUtc, DateTime now) =>
        manualStatus == "busy" && busyUntilUtc is { } end && end <= now ? null : manualStatus;

    /// Busy mutes banners, sounds and ringing — but only while the user is actually online.
    public static bool IsBusy(DateTime? lastSeenAt, string? manualStatus, DateTime? busyUntilUtc, DateTime now) =>
        ComputeStatus(lastSeenAt, EffectiveManual(manualStatus, busyUntilUtc, now), now) == "busy";
}
