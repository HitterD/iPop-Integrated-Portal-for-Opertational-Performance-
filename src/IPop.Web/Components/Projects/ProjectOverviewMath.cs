using System.Globalization;
using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

/// Geometry of the Overview burn-up chart (mockup overviewView: viewBox 520x175, plot x 40..490, y 30..150).
public static class ProjectOverviewMath
{
    public const double Left = 40, Right = 490, Base = 150, Height = 120;

    public sealed record Burn(string PlanPoints, string DonePoints, string AreaPath,
        IReadOnlyList<(double X, string Label)> XLabels, IReadOnlyList<(double Y, int Value)> Ticks,
        double NowX, double? DoneX, double? DoneY, int PlanMax, int DoneLast, double PlanEndX, double PlanEndY);

    public static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);

    public static Burn Build(OverviewBurnUpDto b)
    {
        var n = Math.Max(1, b.Weeks.Count);
        var step = n > 1 ? (Right - Left) / (n - 1) : 0;
        double X(int i) => n > 1 ? Left + i * step : Left + (Right - Left) / 2;
        var max = Math.Max(1, Math.Max(b.Plan.Count > 0 ? b.Plan.Max() : 0, b.Done.Where(d => d.HasValue).Select(d => d!.Value).DefaultIfEmpty(0).Max()));
        double Y(int v) => Base - (double)v / max * Height;

        var plan = b.Plan.Select((v, i) => $"{F(X(i))},{F(Y(v))}");
        var doneIdx = new List<(int I, int V)>();
        for (var i = 0; i < b.Done.Count; i++) if (b.Done[i] is { } v) doneIdx.Add((i, v));
        var done = doneIdx.Select(p => $"{F(X(p.I))},{F(Y(p.V))}").ToList();
        var area = doneIdx.Count == 0 ? "" : $"M{F(X(doneIdx[0].I))},{F(Base)} L{string.Join(" L", done)} L{F(X(doneIdx[^1].I))},{F(Base)} Z";

        var every = Math.Max(1, (int)Math.Ceiling(n / 7.0));
        var labels = new List<(double, string)>();
        for (var i = 0; i < b.Weeks.Count; i += every) labels.Add((X(i), b.Weeks[i]));

        var ticks = new[] { 0, (int)Math.Round(max / 4.0), (int)Math.Round(max / 2.0), (int)Math.Round(max * 3 / 4.0), max }
            .Distinct().Select(v => (Y(v), v)).ToList();

        var last = doneIdx.Count > 0 ? doneIdx[^1] : (I: 0, V: 0);
        var planLast = b.Plan.Count > 0 ? b.Plan[^1] : 0;
        return new Burn(string.Join(" ", plan), string.Join(" ", done), area, labels, ticks, X(last.I),
            doneIdx.Count > 0 ? X(last.I) : null, doneIdx.Count > 0 ? Y(last.V) : null, max, last.V, X(Math.Max(0, b.Plan.Count - 1)), Y(planLast));
    }

    /// "1 h", "40 min", "Yesterday 16:20", "12 Nov 09:05" for the Latest updates feed.
    public static string Relative(DateTime utc, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var span = now - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h";
        var l = utc.ToLocalTime();
        var ln = now.ToLocalTime();
        if (l.Date == ln.Date.AddDays(-1)) return "Yesterday " + l.ToString("HH:mm", CultureInfo.InvariantCulture);
        return l.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
