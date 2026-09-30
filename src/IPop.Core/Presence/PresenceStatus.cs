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
    /// Manual "busy" wins over the clock; everything else is heartbeat-derived.
    public static string ComputeStatus(DateTime? lastSeenAt, string? manualStatus, DateTime now, bool idle = false) =>
        manualStatus == "busy" ? "busy"
        : lastSeenAt is null || now - lastSeenAt >= OfflineAfter ? "off"
        : idle ? "away"
        : "on";
}
