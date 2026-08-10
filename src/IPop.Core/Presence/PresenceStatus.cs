namespace IPop.Core.Presence;

/// Pure presence computation — no I/O, no clock access (now is a parameter),
/// so every boundary is unit-testable. The effective status is derived from two
/// stored facts (lastSeenAt, manualStatus) and never persisted itself.
public static class PresenceStatus
{
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(15);

    /// Returns "on" | "away" | "busy" | "off".
    /// Manual "busy" wins over the clock; everything else is time-derived.
    public static string ComputeStatus(DateTime? lastSeenAt, string? manualStatus, DateTime now) =>
        manualStatus == "busy" ? "busy"
        : lastSeenAt is null ? "off"
        : now - lastSeenAt < AwayAfter ? "on"
        : now - lastSeenAt < OfflineAfter ? "away"
        : "off";
}
