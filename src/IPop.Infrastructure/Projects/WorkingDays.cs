namespace IPop.Infrastructure.Projects;

/// Monday-Friday date arithmetic for template offsets. Holidays are a TODO hook: pass an
/// <c>isHoliday</c> predicate once the company calendar is available server-side.
public static class WorkingDays
{
    public static bool IsWorkingDay(DateTime d, Func<DateTime, bool>? isHoliday = null) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (isHoliday is null || !isHoliday(d.Date));

    /// The date <paramref name="n"/> working days after <paramref name="start"/>. A weekend start counts as the
    /// next Monday, so n = 0 always lands on a working day. Negative n returns the same date rolled forward.
    public static DateTime AddWorkingDays(DateTime start, int n, Func<DateTime, bool>? isHoliday = null)
    {
        var d = start;
        while (!IsWorkingDay(d, isHoliday)) d = d.AddDays(1);
        for (var left = Math.Max(0, n); left > 0;)
        {
            d = d.AddDays(1);
            if (IsWorkingDay(d, isHoliday)) left--;
        }
        return d;
    }
}
