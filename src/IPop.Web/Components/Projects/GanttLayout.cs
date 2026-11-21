using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

public enum GanttLineKind { Group, Task, Milestone }

/// One line of the Gantt: a group summary, a task or a milestone. Y is the row index it is drawn on;
/// Hidden lines (inside a collapsed group) sit on their group's row and fade out.
public sealed class GanttLine
{
    public GanttLineKind Kind { get; init; }
    public Guid Id { get; init; }
    public Guid GroupId { get; init; }
    public string Wbs { get; init; } = "";
    public string Title { get; init; } = "";
    public string Color { get; init; } = "#8b9198";
    public GanttRowDto? Task { get; init; }
    public GanttMilestoneDto? Milestone { get; init; }
    /// First and last calendar day (tasks and milestones: their own dates; groups: the span of their tasks).
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public int Y { get; set; }
    public bool Hidden { get; set; }
    public bool Collapsed { get; set; }
    public int SpanDays => Start is { } s && End is { } e ? (int)(e.Date - s.Date).TotalDays + 1 : 0;
}

/// Rows of the Gantt in display order, with the date window of the grid. Pure: no px, no UI state.
public sealed class GanttLayout
{
    public DateTime Origin { get; private init; }
    public int TotalDays { get; private init; }
    public IReadOnlyList<GanttLine> Lines { get; private init; } = [];
    public int VisibleRows { get; private init; }
    public bool HasDates { get; private init; }
    public IReadOnlyDictionary<Guid, GanttLine> ByTask { get; private init; } = new Dictionary<Guid, GanttLine>();

    public static readonly Guid MilestonesGroupId = Guid.Parse("00000000-0000-0000-0000-00000000f00d");

    public static GanttLayout Build(GanttV2Dto dto, Func<GanttRowDto, bool>? keepTask = null, Func<GanttMilestoneDto, bool>? keepMilestone = null,
        ISet<Guid>? collapsed = null, bool filtering = false)
    {
        collapsed ??= new HashSet<Guid>();
        var lines = new List<GanttLine>();
        var byTask = new Dictionary<Guid, GanttLine>();
        var tasksByGroup = dto.Rows.Where(r => keepTask is null || keepTask(r)).ToLookup(r => r.GroupId);
        var y = 0;
        DateTime? min = null, max = null;
        void Span(DateTime? a, DateTime? b)
        {
            foreach (var d in new[] { a, b })
                if (d is { } v) { min = min is null || v.Date < min ? v.Date : min; max = max is null || v.Date > max ? v.Date : max; }
        }

        void Section(Guid groupId, string wbs, string name, string color, IEnumerable<GanttLine> children)
        {
            var kids = children.ToList();
            if (kids.Count == 0 && filtering) return;
            var isCollapsed = collapsed.Contains(groupId);
            var head = new GanttLine { Kind = GanttLineKind.Group, Id = groupId, GroupId = groupId, Wbs = wbs, Title = name, Color = color, Y = y, Collapsed = isCollapsed };
            var dated = kids.Where(k => k.Start.HasValue).ToList();
            if (dated.Count > 0) { head.Start = dated.Min(k => k.Start); head.End = dated.Max(k => k.End); }
            lines.Add(head);
            var groupY = y++;
            foreach (var k in kids)
            {
                k.Hidden = isCollapsed;
                k.Y = isCollapsed ? groupY : y++;
                lines.Add(k);
            }
        }

        for (var gi = 0; gi < dto.Groups.Count; gi++)
        {
            var g = dto.Groups[gi];
            var kids = tasksByGroup[g.Id].Select(r =>
            {
                var (s, e) = Dates(r);
                Span(s, e); Span(r.BaselineStart, r.BaselineDue); Span(r.DeadlineDate, null);
                var line = new GanttLine
                {
                    Kind = GanttLineKind.Task, Id = r.Id, GroupId = g.Id, Wbs = r.WbsPath, Title = r.Title, Color = g.Color, Task = r, Start = s, End = e,
                };
                byTask[r.Id] = line;
                return line;
            }).ToList();
            Section(g.Id, (gi + 1).ToString(), g.Name, g.Color, kids);
        }

        var ms = dto.Milestones.Where(m => keepMilestone is null || keepMilestone(m)).OrderBy(m => m.Date).ToList();
        if (ms.Count > 0)
        {
            var n = dto.Groups.Count + 1;
            Section(MilestonesGroupId, n.ToString(), "Milestones", "#7c4dd6", ms.Select((m, i) =>
            {
                Span(m.Date, m.Date);
                return new GanttLine { Kind = GanttLineKind.Milestone, Id = m.Id, GroupId = MilestonesGroupId, Wbs = $"{n}.{i + 1}", Title = m.Title, Color = "#7c4dd6", Milestone = m, Start = m.Date.Date, End = m.Date.Date };
            }));
        }

        Span(dto.Window.Start, dto.Window.End);
        Span(dto.Project.Start, dto.Project.Deadline);
        var hasDates = lines.Any(l => l.Kind != GanttLineKind.Group && l.Start.HasValue);
        var first = min ?? DateTime.Today;
        var last = max ?? first.AddDays(30);
        var origin = GanttMath.OriginFor(first);
        // Room for a slack tail and the owner label past the last bar.
        var total = GanttMath.TotalDays(origin, last.AddDays(14));
        return new GanttLayout
        {
            Origin = origin, TotalDays = total, Lines = lines, VisibleRows = y, HasDates = hasDates, ByTask = byTask,
        };
    }

    /// Calendar start and finish of a task bar: a task with only one date is a one-day bar.
    public static (DateTime? Start, DateTime? End) Dates(GanttRowDto r)
    {
        var s = r.StartDate ?? r.DueDate;
        var e = r.DueDate ?? r.StartDate;
        if (s is null || e is null) return (null, null);
        return e < s ? (e.Value.Date, s.Value.Date) : (s.Value.Date, e.Value.Date);
    }
}
