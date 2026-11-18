namespace IPop.Infrastructure.Projects;

/// Working-day calendar used by the Gantt engine. A date maps to a "working-day ordinal": the n-th
/// working day counted from an anchor before every date in play. Weekends are non-working unless
/// <c>skipWeekends</c> is false (the v1 chart counts calendar days); holidays always are.
public sealed class WorkCalendar
{
    private readonly HashSet<DateTime> _holidays;
    private readonly bool _skipWeekends;
    private readonly DateTime _anchor;
    private readonly List<DateTime> _days = [];
    private DateTime _next;

    private WorkCalendar(DateTime anchor, IEnumerable<DateTime> holidays, bool skipWeekends)
    {
        _anchor = DateTime.SpecifyKind(anchor.Date, DateTimeKind.Utc);
        _next = _anchor;
        _holidays = holidays.Select(h => h.Date).ToHashSet();
        _skipWeekends = skipWeekends;
    }

    /// The anchor sits 60 days before the earliest date so "previous working day" never falls off the front.
    public static WorkCalendar Create(IEnumerable<DateTime> relevantDates, IEnumerable<DateTime>? holidays = null, bool skipWeekends = true)
    {
        var list = relevantDates.ToList();
        var min = list.Count == 0 ? DateTime.UtcNow.Date : list.Min().Date;
        return new WorkCalendar(min.AddDays(-60), holidays ?? [], skipWeekends);
    }

    public bool IsWorking(DateTime d) =>
        (!_skipWeekends || d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) && !_holidays.Contains(d.Date);

    private void ExtendThrough(DateTime d)
    {
        while (_next <= d)
        {
            if (IsWorking(_next)) _days.Add(_next);
            _next = _next.AddDays(1);
        }
    }

    private int LowerBound(DateTime d)
    {
        int lo = 0, hi = _days.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (_days[mid] < d) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// Ordinal of the first working day on or after <paramref name="d"/>.
    public int StartOrdinal(DateTime d)
    {
        d = d.Date;
        if (d < _anchor) return (int)(d - _anchor).TotalDays;
        ExtendThrough(d);
        while (true)
        {
            var i = LowerBound(d);
            if (i < _days.Count) return i;
            ExtendThrough(_next.AddDays(14));
        }
    }

    /// Ordinal of the last working day on or before <paramref name="d"/>.
    public int FinishOrdinal(DateTime d)
    {
        d = d.Date;
        if (d < _anchor) return (int)(d - _anchor).TotalDays;
        ExtendThrough(d);
        var i = LowerBound(d);
        return i < _days.Count && _days[i] == d ? i : i - 1;
    }

    public DateTime DateOf(int ordinal)
    {
        if (ordinal < 0) return _anchor.AddDays(ordinal);
        while (ordinal >= _days.Count) ExtendThrough(_next.AddDays(14));
        return _days[ordinal];
    }

    /// Working days a task spans, first to last day inclusive; at least 1.
    public int Duration(DateTime start, DateTime due) =>
        Math.Max(1, FinishOrdinal(due) - StartOrdinal(start) + 1);
}

public sealed record SchedTask(Guid Id, DateTime? Start, DateTime? Due, string? ConstraintType = null,
    DateTime? ConstraintDate = null, bool Done = false);
public sealed record SchedLink(Guid PredecessorId, Guid SuccessorId, string Type = "FS", int LagDays = 0);
public sealed record SchedChange(Guid TaskId, DateTime? OldStart, DateTime? OldDue, DateTime NewStart, DateTime NewDue);
public sealed record SchedConflict(Guid FromTaskId, Guid ToTaskId);
public sealed record SchedFloat(int SlackDays, bool IsCritical, int DurationDays);
public sealed record SchedPosition(Guid TaskId, DateTime Start, DateTime Due);
public sealed record SchedAnalysis(
    IReadOnlyDictionary<Guid, SchedFloat> Floats,
    IReadOnlyList<Guid> CriticalPath,
    IReadOnlyList<SchedConflict> Conflicts,
    IReadOnlyList<Guid> ConstraintViolations,
    DateTime? ProjectFinish);
public sealed record RescheduleOutcome(
    IReadOnlyList<SchedChange> Changes,
    IReadOnlyList<SchedConflict> Conflicts,
    IReadOnlyList<Guid> ConstraintViolations,
    DateTime? OldFinish, DateTime? NewFinish, string? Error);

/// Pure scheduling maths for the Gantt: link types with lag, earliest dates, reschedule preview,
/// slack / critical path and conflict detection. Spans are start..due inclusive in working days; a
/// task with one date lasts one day; a task with no dates is unscheduled (not part of the model).
public static class GanttScheduler
{
    private sealed class Node
    {
        public Guid Id;
        public int S, D;
        public int F => S + D - 1;
        public string? CT;
        public int CD;
        public bool Done;
        public bool IsMso => CT == "mso";
    }

    private sealed class Model
    {
        public required WorkCalendar Cal;
        public required Dictionary<Guid, Node> Nodes;
        public required List<SchedLink> Links;
        public required List<Guid> Topo;
        public required HashSet<Guid> Cyclic;
    }

    public static string NormalizeLinkType(string? type) => (type ?? "FS").Trim().ToUpperInvariant();
    public static bool IsValidLinkType(string? type) => NormalizeLinkType(type) is "FS" or "SS" or "FF";
    public static bool IsValidConstraintType(string? type) => type is "snet" or "fnlt" or "mso";

    private static Model Build(IEnumerable<SchedTask> tasks, IEnumerable<SchedLink> links, IEnumerable<DateTime>? holidays,
        bool skipWeekends, IEnumerable<DateTime>? extraDates = null)
    {
        var taskList = tasks.ToList();
        var dates = new List<DateTime>(extraDates ?? []);
        foreach (var t in taskList)
        {
            if (t.Start.HasValue) dates.Add(t.Start.Value);
            if (t.Due.HasValue) dates.Add(t.Due.Value);
            if (t.ConstraintDate.HasValue) dates.Add(t.ConstraintDate.Value);
        }
        var cal = WorkCalendar.Create(dates, holidays, skipWeekends);

        var nodes = new Dictionary<Guid, Node>();
        foreach (var t in taskList)
        {
            if (t.Start is null && t.Due is null) continue;
            var start = t.Start ?? t.Due!.Value;
            var due = t.Due ?? t.Start!.Value;
            if (due < start) due = start;
            var node = new Node { Id = t.Id, S = cal.StartOrdinal(start), D = cal.Duration(start, due), Done = t.Done };
            if (IsValidConstraintType(t.ConstraintType) && t.ConstraintDate.HasValue)
            {
                node.CT = t.ConstraintType;
                node.CD = t.ConstraintType == "fnlt" ? cal.FinishOrdinal(t.ConstraintDate.Value) : cal.StartOrdinal(t.ConstraintDate.Value);
            }
            nodes[t.Id] = node;
        }

        var good = links
            .Where(l => l.PredecessorId != l.SuccessorId && nodes.ContainsKey(l.PredecessorId) && nodes.ContainsKey(l.SuccessorId))
            .Select(l => l with { Type = NormalizeLinkType(l.Type) })
            .DistinctBy(l => (l.PredecessorId, l.SuccessorId)).ToList();

        var indeg = nodes.Keys.ToDictionary(k => k, _ => 0);
        foreach (var l in good) indeg[l.SuccessorId]++;
        var outs = good.GroupBy(l => l.PredecessorId).ToDictionary(g => g.Key, g => g.Select(l => l.SuccessorId).ToList());
        var queue = new Queue<Guid>(nodes.Keys.Where(k => indeg[k] == 0));
        var topo = new List<Guid>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            topo.Add(n);
            if (!outs.TryGetValue(n, out var next)) continue;
            foreach (var s in next) if (--indeg[s] == 0) queue.Enqueue(s);
        }
        var inTopo = topo.ToHashSet();
        var cyclic = nodes.Keys.Where(k => !inTopo.Contains(k)).ToHashSet();
        topo.AddRange(cyclic);
        return new Model { Cal = cal, Nodes = nodes, Links = good, Topo = topo, Cyclic = cyclic };
    }

    /// Earliest start the successor may take because of this link.
    private static int RequiredStart(SchedLink l, int predStart, int predDuration, int succDuration) => l.Type switch
    {
        "SS" => predStart + l.LagDays,
        "FF" => predStart + predDuration - 1 + l.LagDays - succDuration + 1,
        _ => predStart + predDuration + l.LagDays
    };

    private static List<SchedConflict> FindConflicts(Model m) => m.Links
        .Where(l =>
        {
            var p = m.Nodes[l.PredecessorId]; var s = m.Nodes[l.SuccessorId];
            return !s.Done && s.S < RequiredStart(l, p.S, p.D, s.D);
        })
        .Select(l => new SchedConflict(l.PredecessorId, l.SuccessorId)).ToList();

    private static List<Guid> FindConstraintViolations(Model m) => m.Nodes.Values.Where(n => n.CT switch
    {
        "snet" => n.S < n.CD,
        "mso" => n.S != n.CD,
        "fnlt" => n.F > n.CD,
        _ => false
    }).Select(n => n.Id).ToList();

    /// Links a successor currently breaks in the planned dates.
    public static IReadOnlyList<SchedConflict> DetectConflicts(IEnumerable<SchedTask> tasks, IEnumerable<SchedLink> links,
        IEnumerable<DateTime>? holidays = null, bool skipWeekends = true) =>
        FindConflicts(Build(tasks, links, holidays, skipWeekends));

    /// Earliest dates every dated task can take honouring link types, lag, SNET and MSO. A task no link
    /// drives keeps its planned start (still raised by SNET / fixed by MSO).
    public static IReadOnlyList<SchedPosition> EarliestSchedule(IEnumerable<SchedTask> tasks, IEnumerable<SchedLink> links,
        IEnumerable<DateTime>? holidays = null, bool skipWeekends = true)
    {
        var m = Build(tasks, links, holidays, skipWeekends);
        var es = new Dictionary<Guid, int>();
        var incoming = m.Links.ToLookup(l => l.SuccessorId);
        foreach (var id in m.Topo)
        {
            var n = m.Nodes[id];
            var start = n.S;
            var driven = false;
            foreach (var l in incoming[id])
            {
                if (!es.TryGetValue(l.PredecessorId, out var ps)) continue;
                var req = RequiredStart(l, ps, m.Nodes[l.PredecessorId].D, n.D);
                if (!driven || req > start) start = req;
                driven = true;
            }
            if (n.CT == "snet") start = Math.Max(start, n.CD);
            if (n.IsMso) start = n.CD;
            es[id] = start;
        }
        return m.Topo.Select(id => new SchedPosition(id, m.Cal.DateOf(es[id]), m.Cal.DateOf(es[id] + m.Nodes[id].D - 1))).ToList();
    }

    /// Slack (total float, working days; negative = already breaking a link), critical flag and the
    /// critical path ordered by start, over the planned dates.
    public static SchedAnalysis Analyze(IEnumerable<SchedTask> tasks, IEnumerable<SchedLink> links,
        IEnumerable<DateTime>? holidays = null, bool skipWeekends = true)
    {
        var m = Build(tasks, links, holidays, skipWeekends);
        if (m.Nodes.Count == 0)
            return new SchedAnalysis(new Dictionary<Guid, SchedFloat>(), [], [], [], null);

        var finish = m.Nodes.Values.Max(n => n.F);
        var lf = new Dictionary<Guid, int>();
        var outgoing = m.Links.ToLookup(l => l.PredecessorId);
        foreach (var id in Enumerable.Reverse(m.Topo))
        {
            var n = m.Nodes[id];
            var limit = finish;
            if (n.CT == "fnlt") limit = Math.Min(limit, n.CD);
            foreach (var l in outgoing[id])
            {
                if (!lf.TryGetValue(l.SuccessorId, out var sLf)) continue;
                var s = m.Nodes[l.SuccessorId];
                var sLs = sLf - s.D + 1;
                limit = Math.Min(limit, l.Type switch
                {
                    "SS" => sLs - l.LagDays + n.D - 1,
                    "FF" => sLf - l.LagDays,
                    _ => sLs - 1 - l.LagDays
                });
            }
            if (n.IsMso) limit = Math.Min(limit, n.F);
            lf[id] = limit;
        }

        var floats = m.Nodes.Values.ToDictionary(n => n.Id, n =>
        {
            var slack = lf[n.Id] - n.F;
            return new SchedFloat(slack, slack <= 0, n.D);
        });
        var topoIndex = m.Topo.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var critical = m.Topo.Where(id => floats[id].IsCritical)
            .OrderBy(id => m.Nodes[id].S).ThenBy(id => topoIndex[id]).ToList();
        return new SchedAnalysis(floats, critical, FindConflicts(m), FindConstraintViolations(m), m.Cal.DateOf(finish));
    }

    /// What moving <paramref name="taskId"/> to <paramref name="newStart"/> (optionally with a new due
    /// date, i.e. a resize) would change. "single" moves only that task and reports the links that
    /// break; "cascade" also pushes successors later just enough to satisfy their links, never earlier.
    public static RescheduleOutcome PreviewReschedule(IEnumerable<SchedTask> tasks, IEnumerable<SchedLink> links,
        IEnumerable<DateTime>? holidays, Guid taskId, DateTime newStart, string mode, DateTime? newDue = null,
        bool skipWeekends = true)
    {
        var taskList = tasks.ToList();
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("single" or "cascade")) return Fail("invalid_mode");
        var target = taskList.FirstOrDefault(t => t.Id == taskId);
        if (target is null) return Fail("task_not_found");
        if (newDue is { } nd0 && nd0.Date < newStart.Date) return Fail("invalid_range");

        var wasUnscheduled = target.Start is null && target.Due is null;
        var seeded = wasUnscheduled
            ? taskList.Select(t => t.Id == taskId ? t with { Start = newStart, Due = newDue ?? newStart } : t).ToList()
            : taskList;
        var extra = new List<DateTime> { newStart };
        if (newDue.HasValue) extra.Add(newDue.Value);
        var m = Build(seeded, links, holidays, skipWeekends, extra);

        DateTime? Finish() => m.Nodes.Count == 0 ? null : m.Cal.DateOf(m.Nodes.Values.Max(n => n.F));
        var node = m.Nodes[taskId];
        var oldFinish = wasUnscheduled ? Finish() : Finish();
        var oldS = node.S; var oldD = node.D;

        var ns = m.Cal.StartOrdinal(newStart);
        var nd = newDue.HasValue ? Math.Max(1, m.Cal.FinishOrdinal(newDue.Value) - ns + 1) : node.D;
        if (node.IsMso && (ns != node.CD)) return Fail("constraint_violation");
        node.S = ns; node.D = nd;

        var changed = new HashSet<Guid>();
        if (ns != oldS || nd != oldD || wasUnscheduled) changed.Add(taskId);

        if (mode == "cascade" && changed.Count > 0)
        {
            var incoming = m.Links.ToLookup(l => l.SuccessorId);
            foreach (var id in m.Topo)
            {
                if (id == taskId || m.Cyclic.Contains(id)) continue;
                var n = m.Nodes[id];
                var req = int.MinValue;
                foreach (var l in incoming[id])
                    if (changed.Contains(l.PredecessorId))
                    {
                        var p = m.Nodes[l.PredecessorId];
                        req = Math.Max(req, RequiredStart(l, p.S, p.D, n.D));
                    }
                if (req == int.MinValue || req <= n.S || n.Done || n.IsMso) continue;
                n.S = req;
                changed.Add(id);
            }
        }

        var byId = taskList.ToDictionary(t => t.Id);
        var changes = m.Topo.Where(changed.Contains).Select(id =>
        {
            var n = m.Nodes[id];
            return new SchedChange(id, byId[id].Start, byId[id].Due, m.Cal.DateOf(n.S), m.Cal.DateOf(n.F));
        }).ToList();

        return new RescheduleOutcome(changes, FindConflicts(m), FindConstraintViolations(m), oldFinish, Finish(), null);

        static RescheduleOutcome Fail(string e) => new([], [], [], null, null, e);
    }
}
