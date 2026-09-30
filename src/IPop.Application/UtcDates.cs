namespace IPop.Application;

/// Npgsql maps DateTime to `timestamp with time zone` and throws on write when Kind is
/// Unspecified (or Local), which is exactly what a date picker posts ("2026-10-01T00:00:00").
/// Date-only fields (project deadline/start, task due/start, milestones, holidays) are stored
/// as the picked calendar day at UTC midnight — the same shape the UI's "Today"/"+7 days"
/// shortcuts already send (DateTime.UtcNow.Date) — so the day never shifts with the server's
/// or the viewer's zone. Mirrors the rule in CalendarEndpoints (all-day events) and
/// MeetingEndpoints (range queries). Deliberately not a global EF converter.
public static class UtcDates
{
    /// A calendar-date value: Utc values pass through untouched; Unspecified/Local values are
    /// relabelled as UTC with the same wall-clock (no zone conversion, so a picked midnight
    /// stays that day's midnight instead of sliding to the previous day east of Greenwich).
    public static DateTime AsUtcDate(DateTime d) =>
        d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public static DateTime? AsUtcDate(DateTime? d) => d is { } v ? AsUtcDate(v) : null;
}
