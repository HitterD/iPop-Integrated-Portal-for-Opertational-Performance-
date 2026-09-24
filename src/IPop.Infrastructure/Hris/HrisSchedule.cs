namespace IPop.Infrastructure.Hris;

/// The hris.autoSync.cron setting. Only the minute and hour fields are honoured, each as
/// "*", "*/n" or a number — enough for the admin presets ("0 * * * *", "0 */6 * * *",
/// "0 2 * * *"); "off" or anything unparseable disables the schedule. Times are server-local.
public static class HrisSchedule
{
    public const string Off = "off";

    /// The latest scheduled slot at or before `now` (within the past 25 hours), or null.
    public static DateTime? LastSlot(string? cron, DateTime now)
    {
        if (!TryParse(cron, out var minute, out var hour)) return null;
        var t = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
        for (var i = 0; i < 25 * 60; i++, t = t.AddMinutes(-1))
            if (minute(t.Minute) && hour(t.Hour)) return t;
        return null;
    }

    /// Due when a slot has passed since the last run started — so a slot missed while the
    /// server was down runs once on startup instead of being skipped.
    public static bool IsDue(string? cron, DateTime now, DateTime? lastRun) =>
        LastSlot(cron, now) is { } slot && (lastRun is null || lastRun < slot);

    public static bool IsValid(string? cron) => cron == Off || TryParse(cron, out _, out _);

    private static bool TryParse(string? cron, out Func<int, bool> minute, out Func<int, bool> hour)
    {
        minute = hour = _ => false;
        var parts = (cron ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[2..].Any(p => p != "*")) return false;
        return TryField(parts[0], 59, out minute) & TryField(parts[1], 23, out hour);
    }

    private static bool TryField(string f, int max, out Func<int, bool> match)
    {
        match = _ => false;
        if (f == "*") { match = _ => true; return true; }
        if (f.StartsWith("*/") && int.TryParse(f[2..], out var step) && step > 0 && step <= max)
        {
            match = v => v % step == 0; return true;
        }
        if (int.TryParse(f, out var n) && n >= 0 && n <= max) { match = v => v == n; return true; }
        return false;
    }
}
