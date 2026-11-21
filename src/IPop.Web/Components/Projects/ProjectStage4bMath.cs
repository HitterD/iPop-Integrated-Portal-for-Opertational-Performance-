using System.Globalization;
using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

/// My work: which section a task sits in, and the short texts the rows show.
public static class MyWorkMath
{
    public enum Bucket { Late, Today, ThisWeek, Later }

    /// Sunday of the caller's Mon-Sun week (the server buckets the same way).
    public static DateTime WeekEnd(DateTime today) => today.Date.AddDays(((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7);

    public static Bucket BucketOf(MyWorkItemDto i, DateTime today)
    {
        if (i.IsLate) return Bucket.Late;
        if (i.DueDate is not { } due) return Bucket.Later;
        var d = due.Date;
        if (d < today.Date) return Bucket.Late;
        if (d == today.Date) return Bucket.Today;
        return d <= WeekEnd(today) ? Bucket.ThisWeek : Bucket.Later;
    }

    public static string BucketLabel(Bucket b) => b switch { Bucket.Late => "Late", Bucket.Today => "Today", Bucket.ThisWeek => "This week", _ => "Later" };

    public static string DueText(MyWorkItemDto i, DateTime today)
    {
        if (i.DueDate is not { } due) return "No date";
        var d = due.Date;
        if (d == today.Date) return "Today";
        var ci = CultureInfo.InvariantCulture;
        return BucketOf(i, today) == Bucket.ThisWeek ? d.ToString("ddd d MMM", ci) : d.ToString("d MMM", ci);
    }

    public static MyWorkCountsDto Remove(MyWorkCountsDto c, Bucket b) => b switch
    {
        Bucket.Late => c with { Late = Math.Max(0, c.Late - 1), DoneThisWeek = c.DoneThisWeek + 1 },
        Bucket.Today => c with { DueToday = Math.Max(0, c.DueToday - 1), DoneThisWeek = c.DoneThisWeek + 1 },
        Bucket.ThisWeek => c with { ThisWeek = Math.Max(0, c.ThisWeek - 1), DoneThisWeek = c.DoneThisWeek + 1 },
        _ => c with { Later = Math.Max(0, c.Later - 1), DoneThisWeek = c.DoneThisWeek + 1 },
    };

    public static int CountOf(MyWorkCountsDto c, Bucket b) => b switch { Bucket.Late => c.Late, Bucket.Today => c.DueToday, Bucket.ThisWeek => c.ThisWeek, _ => c.Later };

    public static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - (utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime());
        if (s.TotalMinutes < 1) return "just now";
        if (s.TotalMinutes < 60) return $"{(int)s.TotalMinutes} min";
        if (s.TotalHours < 24) return $"{(int)s.TotalHours} h";
        return s.TotalDays < 7 ? $"{(int)s.TotalDays} d" : utc.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// 42 days (6 Monday-first weeks) covering the month.
    public static IReadOnlyList<DateTime> MonthCells(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i)).ToList();
    }
}

/// All projects page: the shared timeline scale and the health chip texts.
public static class PortfolioMath
{
    public sealed record Bar(double Left, double Width);
    public sealed record Scale(DateTime Min, DateTime Max, double? TodayPct)
    {
        private double Span => Math.Max(1, (Max - Min).TotalDays);
        public double Pct(DateTime d) => Math.Clamp((d.Date - Min.Date).TotalDays / Span * 100, 0, 100);

        /// Null when the project has neither a start nor a deadline.
        public Bar? BarFor(PortfolioProjectDto p)
        {
            var a = p.StartDate ?? p.Deadline;
            var b = p.Deadline ?? p.StartDate;
            if (a is null || b is null) return null;
            var left = Pct(a.Value);
            var width = Math.Max(1.5, Pct(b.Value) - left);
            return new Bar(left, Math.Min(width, 100 - left));
        }
    }

    /// Scale = earliest start .. latest deadline across the rows; null when no row has a date.
    public static Scale? ScaleFor(IEnumerable<PortfolioProjectDto> rows, DateTime today)
    {
        DateTime? min = null, max = null;
        foreach (var p in rows)
        {
            var a = p.StartDate ?? p.Deadline;
            var b = p.Deadline ?? p.StartDate;
            if (a is { } x && (min is null || x.Date < min)) min = x.Date;
            if (b is { } y && (max is null || y.Date > max)) max = y.Date;
        }
        if (min is null || max is null) return null;
        if (max <= min) max = min.Value.AddDays(1);
        var scale = new Scale(min.Value, max.Value, null);
        var t = today.Date;
        return t >= min && t <= max ? scale with { TodayPct = scale.Pct(t) } : scale;
    }

    public static string TimelineHeading(Scale? s) =>
        s is null ? "Timeline" : $"Timeline · {s.Min.ToString("MMM", CultureInfo.InvariantCulture)} → {s.Max.ToString("MMM", CultureInfo.InvariantCulture)}";

    /// h0 on track, h1 at risk, h2 off track.
    public static (string Css, string Label) Health(string? health) => health switch
    {
        "off_track" => ("h2", "Off track"),
        "at_risk" => ("h1", "At risk"),
        _ => ("h0", "On track"),
    };
}
