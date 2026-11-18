using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using IPop.Application;
using IPop.Application.Projects;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Microsoft Project XML (MSPDI 2007+) export and import. The binary .mpp format is not read here:
/// there is no free .NET reader for it, so users save the plan as XML from Microsoft Project.
public sealed partial class GanttService
{
    public const int MaxImportBytes = 20 * 1024 * 1024;
    public const int MaxImportTasks = 2000;
    private const int MinutesPerDay = 480;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/project";

    // MSPDI link type codes: 0 FF, 1 FS, 2 SF, 3 SS.
    private static int ToMspType(string t) => t switch { "FF" => 0, "SS" => 3, _ => 1 };
    private static string Duration(int days) => $"PT{days * 8}H0M0S";
    private static string Iso(DateTime d, int hour) => d.Date.AddHours(hour).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    // ──────────────────────────────────────────────────────────────────────────
    // Export
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<GanttResult<MsProjectExportDto>> ExportMsProjectAsync(Guid actorId, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null || !await CanViewAsync(projectId, actorId, ct)) return Fail<MsProjectExportDto>("project_not_found");

        var groups = await db.ProjectGroups.AsNoTracking().Where(g => g.ProjectId == projectId).OrderBy(g => g.SortOrder).ToListAsync(ct);
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var links = await db.ProjectTaskDependencies.AsNoTracking().Where(d => d.Task.ProjectId == projectId)
            .Select(d => new { d.TaskId, d.DependsOnTaskId, d.LinkType, d.LagDays }).ToListAsync(ct);
        var holidays = await db.WorkCalendarEntries.AsNoTracking()
            .Where(e => e.ProjectId == projectId && (e.Kind == "holiday" || e.Kind == "offday")).OrderBy(e => e.Date).ToListAsync(ct);
        var milestones = await db.ProjectMilestones.AsNoTracking().Where(m => m.ProjectId == projectId)
            .Include(m => m.LinkedTasks).OrderBy(m => m.DueDate).ToListAsync(ct);
        var analysis = GanttScheduler.Analyze(tasks.Select(ProjectService.ToSched), links.Select(l => new SchedLink(l.DependsOnTaskId, l.TaskId, l.LinkType, l.LagDays)), holidays.Select(h => h.Date));

        var taskIds = tasks.Select(t => t.Id).ToHashSet();
        var byParent = tasks.Where(t => t.ParentTaskId.HasValue && taskIds.Contains(t.ParentTaskId.Value)).ToLookup(t => t.ParentTaskId!.Value);
        var uid = new Dictionary<Guid, int>();
        var next = 1;

        // Resources by name
        var resourceNames = tasks.SelectMany(t => t.Assignees).Select(a => a.User.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        var resourceUid = resourceNames.Select((n, i) => (n, uid: i + 1)).ToDictionary(x => x.n, x => x.uid, StringComparer.OrdinalIgnoreCase);

        var taskEls = new List<XElement>();
        var assignmentEls = new List<XElement>();
        var allDates = tasks.SelectMany(t => new[] { t.StartDate, t.DueDate }).Where(d => d.HasValue).Select(d => d!.Value).ToList();
        var minStart = allDates.Count > 0 ? allDates.Min() : (project.StartDate ?? DateTime.UtcNow.Date);
        var maxEnd = allDates.Count > 0 ? allDates.Max() : (project.Deadline ?? minStart);

        taskEls.Add(new XElement(Ns + "Task",
            new XElement(Ns + "UID", 0), new XElement(Ns + "ID", 0), new XElement(Ns + "Name", project.Name),
            new XElement(Ns + "Summary", 1), new XElement(Ns + "OutlineLevel", 0), new XElement(Ns + "OutlineNumber", "0"),
            new XElement(Ns + "Start", Iso(minStart, 8)), new XElement(Ns + "Finish", Iso(maxEnd, 17))));

        // Pre-assign UIDs in output order so predecessor references resolve.
        var order = new List<(object Item, int Level, string Outline)>();
        void WalkTask(ProjectTask t, int level, string outline)
        {
            uid[t.Id] = next++;
            order.Add((t, level, outline));
            var i = 1;
            foreach (var c in byParent[t.Id]) WalkTask(c, level + 1, $"{outline}.{i++}");
        }
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            order.Add((g, 1, (gi + 1).ToString()));
            var top = tasks.Where(t => t.GroupId == g.Id && (t.ParentTaskId is null || !taskIds.Contains(t.ParentTaskId.Value))).ToList();
            var ti = 1;
            foreach (var t in top) WalkTask(t, 2, $"{gi + 1}.{ti++}");
            if (gi == 0)
            {
                foreach (var m in milestones) order.Add((m, 2, $"1.{ti++}"));
            }
        }
        if (groups.Count == 0) foreach (var m in milestones) order.Add((m, 1, (order.Count + 1).ToString()));

        var groupUid = new Dictionary<Guid, int>();
        var milestoneUid = new Dictionary<Guid, int>();
        // First pass: UIDs for groups + milestones (tasks already have theirs; renumber everything sequentially in order).
        uid.Clear(); next = 1;
        foreach (var (item, _, _) in order)
        {
            switch (item)
            {
                case ProjectGroup g: groupUid[g.Id] = next++; break;
                case ProjectTask t: uid[t.Id] = next++; break;
                case ProjectMilestone m: milestoneUid[m.Id] = next++; break;
            }
        }

        var assignmentUid = 1;
        foreach (var (item, level, outline) in order)
        {
            switch (item)
            {
                case ProjectGroup g:
                {
                    var members = tasks.Where(t => t.GroupId == g.Id).ToList();
                    var ds = members.Where(t => t.StartDate.HasValue || t.DueDate.HasValue).ToList();
                    var el = Base(groupUid[g.Id], g.Name, level, outline, summary: true);
                    if (ds.Count > 0)
                    {
                        el.Add(new XElement(Ns + "Start", Iso(ds.Min(t => (t.StartDate ?? t.DueDate)!.Value), 8)),
                               new XElement(Ns + "Finish", Iso(ds.Max(t => (t.DueDate ?? t.StartDate)!.Value), 17)));
                    }
                    taskEls.Add(el);
                    break;
                }
                case ProjectTask t:
                {
                    var hasKids = byParent[t.Id].Any();
                    var el = Base(uid[t.Id], t.Title, level, outline, summary: hasKids);
                    var s = t.StartDate ?? t.DueDate; var d = t.DueDate ?? t.StartDate;
                    if (s.HasValue && d.HasValue)
                    {
                        var cal = WorkCalendar.Create([s.Value, d.Value], holidays.Select(h => h.Date));
                        var days = analysis.Floats.TryGetValue(t.Id, out var f) ? f.DurationDays : cal.Duration(s.Value, d.Value);
                        el.Add(new XElement(Ns + "Start", Iso(s.Value, 8)), new XElement(Ns + "Finish", Iso(d.Value, 17)),
                               new XElement(Ns + "Duration", Duration(days)), new XElement(Ns + "DurationFormat", 7));
                    }
                    var subs = byParent[t.Id].ToList();
                    var pct = t.Status == "done" ? 100 : subs.Count > 0 ? (int)Math.Round(subs.Count(x => x.Status == "done") * 100.0 / subs.Count) : 0;
                    el.Add(new XElement(Ns + "PercentComplete", pct));
                    if (t.ConstraintType is { } ct0 && t.ConstraintDate is { } cd)
                        el.Add(new XElement(Ns + "ConstraintType", ct0 switch { "mso" => 2, "snet" => 4, _ => 7 }),
                               new XElement(Ns + "ConstraintDate", Iso(cd, ct0 == "fnlt" ? 17 : 8)));
                    if (t.DeadlineDate is { } dl) el.Add(new XElement(Ns + "Deadline", Iso(dl, 17)));
                    foreach (var l in links.Where(l => l.TaskId == t.Id && uid.ContainsKey(l.DependsOnTaskId)))
                        el.Add(new XElement(Ns + "PredecessorLink",
                            new XElement(Ns + "PredecessorUID", uid[l.DependsOnTaskId]),
                            new XElement(Ns + "Type", ToMspType(GanttScheduler.NormalizeLinkType(l.LinkType))),
                            new XElement(Ns + "LinkLag", l.LagDays * MinutesPerDay * 10),
                            new XElement(Ns + "LagFormat", 7)));
                    taskEls.Add(el);
                    foreach (var a in t.Assignees)
                        assignmentEls.Add(new XElement(Ns + "Assignment",
                            new XElement(Ns + "UID", assignmentUid++), new XElement(Ns + "TaskUID", uid[t.Id]),
                            new XElement(Ns + "ResourceUID", resourceUid[a.User.Name]), new XElement(Ns + "Units", 1)));
                    break;
                }
                case ProjectMilestone m:
                {
                    var el = Base(milestoneUid[m.Id], m.Title, level, outline, summary: false);
                    el.Add(new XElement(Ns + "Start", Iso(m.DueDate, 8)), new XElement(Ns + "Finish", Iso(m.DueDate, 8)),
                           new XElement(Ns + "Duration", Duration(0)), new XElement(Ns + "DurationFormat", 7),
                           new XElement(Ns + "Milestone", 1), new XElement(Ns + "PercentComplete", m.AchievedAt.HasValue ? 100 : 0));
                    foreach (var tid in m.LinkedTasks.Select(x => x.TaskId).Where(uid.ContainsKey))
                        el.Add(new XElement(Ns + "PredecessorLink", new XElement(Ns + "PredecessorUID", uid[tid]),
                            new XElement(Ns + "Type", 1), new XElement(Ns + "LinkLag", 0), new XElement(Ns + "LagFormat", 7)));
                    taskEls.Add(el);
                    break;
                }
            }
        }

        var calendar = new XElement(Ns + "Calendar",
            new XElement(Ns + "UID", 1), new XElement(Ns + "Name", "Standard"), new XElement(Ns + "IsBaseline", 0),
            new XElement(Ns + "WeekDays",
                Enumerable.Range(1, 7).Select(day => day is 1 or 7
                    ? new XElement(Ns + "WeekDay", new XElement(Ns + "DayType", day), new XElement(Ns + "DayWorking", 0))
                    : new XElement(Ns + "WeekDay", new XElement(Ns + "DayType", day), new XElement(Ns + "DayWorking", 1),
                        new XElement(Ns + "WorkingTimes",
                            new XElement(Ns + "WorkingTime", new XElement(Ns + "FromTime", "08:00:00"), new XElement(Ns + "ToTime", "12:00:00")),
                            new XElement(Ns + "WorkingTime", new XElement(Ns + "FromTime", "13:00:00"), new XElement(Ns + "ToTime", "17:00:00")))))));
        if (holidays.Count > 0)
            calendar.Add(new XElement(Ns + "Exceptions", holidays.Select((h, i) => new XElement(Ns + "Exception",
                new XElement(Ns + "EnteredByOccurrences", 0), new XElement(Ns + "Name", h.Title ?? "Holiday"), new XElement(Ns + "Type", 1),
                new XElement(Ns + "DayWorking", 0),
                new XElement(Ns + "TimePeriod", new XElement(Ns + "FromDate", Iso(h.Date, 0)), new XElement(Ns + "ToDate", Iso(h.Date, 23)))))));

        var root = new XElement(Ns + "Project",
            new XElement(Ns + "SaveVersion", 14), new XElement(Ns + "Name", project.Name), new XElement(Ns + "Title", project.Name),
            new XElement(Ns + "CreationDate", Iso(DateTime.UtcNow, DateTime.UtcNow.Hour)),
            new XElement(Ns + "ScheduleFromStart", 1),
            new XElement(Ns + "StartDate", Iso(project.StartDate ?? minStart, 8)),
            new XElement(Ns + "FinishDate", Iso(project.Deadline ?? maxEnd, 17)),
            new XElement(Ns + "CalendarUID", 1), new XElement(Ns + "MinutesPerDay", MinutesPerDay),
            new XElement(Ns + "MinutesPerWeek", MinutesPerDay * 5), new XElement(Ns + "DaysPerMonth", 20),
            new XElement(Ns + "DefaultStartTime", "08:00:00"), new XElement(Ns + "DefaultFinishTime", "17:00:00"),
            new XElement(Ns + "Calendars", calendar),
            new XElement(Ns + "Tasks", taskEls),
            new XElement(Ns + "Resources", resourceNames.Select(n => new XElement(Ns + "Resource",
                new XElement(Ns + "UID", resourceUid[n]), new XElement(Ns + "ID", resourceUid[n]),
                new XElement(Ns + "Name", n), new XElement(Ns + "Type", 1)))),
            new XElement(Ns + "Assignments", assignmentEls));
        var xml = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
        using var buffer = new MemoryStream();
        using (var w = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            xml.Save(w);
        var xmlText = Encoding.UTF8.GetString(buffer.ToArray());
        var safe = new string(project.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : ' ').ToArray()).Trim().Replace(' ', '-');
        return Ok(new MsProjectExportDto($"{(safe.Length == 0 ? "project" : safe)}.xml", xmlText));

        static XElement Base(int uidValue, string name, int level, string outline, bool summary) => new(Ns + "Task",
            new XElement(Ns + "UID", uidValue), new XElement(Ns + "ID", uidValue), new XElement(Ns + "Name", name),
            new XElement(Ns + "Type", 0), new XElement(Ns + "IsNull", 0), new XElement(Ns + "Summary", summary ? 1 : 0),
            new XElement(Ns + "OutlineNumber", outline), new XElement(Ns + "OutlineLevel", level));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Import
    // ──────────────────────────────────────────────────────────────────────────

    private sealed class MspTask
    {
        public int Uid;
        public string Name = "";
        public int Level;
        public bool Summary, Milestone;
        public DateTime? Start, Finish;
        public double? DurationDays;
        public int Percent;
        public int? ConstraintType;
        public DateTime? ConstraintDate, Deadline;
        public List<(int Uid, int Type, int LagDays)> Preds = [];
        public bool Active = true;
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static string? Val(XElement e, string name) => Child(e, name)?.Value.Trim();
    private static int? IntVal(XElement e, string name) => int.TryParse(Val(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static DateTime? DateVal(XElement e, string name) =>
        DateTime.TryParse(Val(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// "PT16H0M0S" (also P1DT8H...) to working days at 8 h/day.
    private static double? ParseDays(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var ts = XmlConvert.ToTimeSpan(s);
            return ts.TotalHours / 8.0;
        }
        catch (FormatException) { return null; }
    }

    public async Task<GanttResult<MsProjectImportResultDto>> ImportMsProjectAsync(Guid actorId, Guid projectId, byte[] content, string? fileName, bool dryRun, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return Fail<MsProjectImportResultDto>("project_not_found");
        if (!await CanManageAsync(projectId, actorId, ct)) return Fail<MsProjectImportResultDto>("forbidden");
        if (content is null || content.Length == 0) return Fail<MsProjectImportResultDto>("invalid_file");
        if (content.Length > MaxImportBytes) return Fail<MsProjectImportResultDto>("file_too_large");
        if ((content.Length >= 4 && content[0] == 0xD0 && content[1] == 0xCF && content[2] == 0x11 && content[3] == 0xE0)
            || (fileName?.EndsWith(".mpp", StringComparison.OrdinalIgnoreCase) ?? false))
            return Fail<MsProjectImportResultDto>("mpp_not_supported");

        XDocument doc;
        try
        {
            using var ms = new MemoryStream(content);
            using var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 });
            doc = XDocument.Load(reader);
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException) { return Fail<MsProjectImportResultDto>("invalid_file"); }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "Project") return Fail<MsProjectImportResultDto>("invalid_file");

        var warnings = new List<string>();
        var msp = new List<MspTask>();
        foreach (var el in Child(root, "Tasks")?.Elements().Where(x => x.Name.LocalName == "Task") ?? [])
        {
            var u = IntVal(el, "UID");
            if (u is null) continue;
            var level = IntVal(el, "OutlineLevel") ?? 1;
            if (u == 0 || level == 0 && (IntVal(el, "Summary") == 1)) continue; // project summary row
            if (IntVal(el, "IsNull") == 1) continue;
            var t = new MspTask
            {
                Uid = u.Value, Name = Val(el, "Name") ?? "", Level = Math.Max(1, level),
                Summary = IntVal(el, "Summary") == 1, Milestone = IntVal(el, "Milestone") == 1,
                Start = DateVal(el, "Start"), Finish = DateVal(el, "Finish"), DurationDays = ParseDays(Val(el, "Duration")),
                Percent = Math.Clamp(IntVal(el, "PercentComplete") ?? 0, 0, 100),
                ConstraintType = IntVal(el, "ConstraintType"), ConstraintDate = DateVal(el, "ConstraintDate"), Deadline = DateVal(el, "Deadline"),
                Active = IntVal(el, "Active") != 0
            };
            foreach (var p in el.Elements().Where(x => x.Name.LocalName == "PredecessorLink"))
            {
                var pu = IntVal(p, "PredecessorUID");
                if (pu is null) continue;
                var lag = long.TryParse(Val(p, "LinkLag"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;
                var lagFormat = IntVal(p, "LagFormat") ?? 7;
                // LinkLag is tenths of a minute; elapsed formats (>=8) are treated like their working equivalent.
                var days = (int)Math.Clamp(Math.Round(lag / (MinutesPerDay * 10.0)), -365, 365);
                _ = lagFormat;
                t.Preds.Add((pu.Value, IntVal(p, "Type") ?? 1, days));
            }
            msp.Add(t);
        }
        if (msp.Count > MaxImportTasks) return Fail<MsProjectImportResultDto>("too_many_tasks");

        // Resources / assignments
        var resources = (Child(root, "Resources")?.Elements().Where(x => x.Name.LocalName == "Resource") ?? [])
            .Select(r => (Uid: IntVal(r, "UID"), Name: Val(r, "Name"))).Where(r => r.Uid.HasValue && !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Uid!.Value).ToDictionary(g => g.Key, g => g.First().Name!);
        var assignments = (Child(root, "Assignments")?.Elements().Where(x => x.Name.LocalName == "Assignment") ?? [])
            .Select(a => (Task: IntVal(a, "TaskUID"), Res: IntVal(a, "ResourceUID"))).Where(a => a.Task.HasValue && a.Res.HasValue)
            .ToLookup(a => a.Task!.Value, a => a.Res!.Value);
        var members = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && (m.Role == "owner" || m.Role == "manager" || m.Role == "member") && m.User.IsActive)
            .Select(m => new { m.UserId, m.User.Name }).ToListAsync(ct);
        var memberByName = members.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().UserId, StringComparer.OrdinalIgnoreCase);

        var existingGroups = await db.ProjectGroups.AsNoTracking().Where(g => g.ProjectId == projectId).OrderBy(g => g.SortOrder).ToListAsync(ct);
        var groupSort = existingGroups.Count == 0 ? 0 : existingGroups.Max(g => g.SortOrder);
        var taskSort = await db.ProjectTasks.Where(t => t.ProjectId == projectId).Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? 0;
        var optionByStage = (await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == projectId)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.CreatedAt).Select(o => new { o.Id, o.Stage }).ToListAsync(ct))
            .GroupBy(o => o.Stage).ToDictionary(g => g.Key, g => g.First().Id);

        var newGroups = new List<ProjectGroup>();
        var newTasks = new List<ProjectTask>();
        var newLinks = new List<ProjectTaskDependency>();
        var newMilestones = new List<(ProjectMilestone M, List<Guid> Linked)>();
        var newAssignees = new List<ProjectTaskAssignee>();
        var uidToTask = new Dictionary<int, ProjectTask>();
        var uidToMilestone = new Dictionary<int, (ProjectMilestone M, List<Guid> Linked)>();
        var groupUids = new HashSet<int>();
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        // Pass 1: structure. The outline stack tracks the current ancestor chain.
        var stack = new List<(int Level, ProjectTask? Task, ProjectGroup? Group)>();
        ProjectGroup? firstGroup = null;
        foreach (var t in msp)
        {
            if (!t.Active) { warnings.Add($"Skipped inactive task \"{t.Name}\"."); continue; }
            while (stack.Count > 0 && stack[^1].Level >= t.Level) stack.RemoveAt(stack.Count - 1);
            var name = t.Name.Trim();
            if (name.Length == 0) name = "(untitled)";

            if (t.Summary && t.Level == 1)
            {
                var g = new ProjectGroup { Id = Guid.NewGuid(), ProjectId = projectId, Name = name.Length > 100 ? name[..100] : name, Color = "#8E8E93", SortOrder = groupSort += 100 };
                newGroups.Add(g); groupUids.Add(t.Uid); firstGroup ??= g;
                stack.Add((t.Level, null, g));
                continue;
            }

            var isMilestone = !t.Summary && (t.Milestone || (t.DurationDays is 0 && (t.Start ?? t.Finish).HasValue));
            if (isMilestone)
            {
                var date = (t.Start ?? t.Finish)?.Date;
                if (date is null) { warnings.Add($"Skipped milestone \"{name}\": no date."); continue; }
                var m = new ProjectMilestone
                {
                    Id = Guid.NewGuid(), ProjectId = projectId, Title = name.Length > 300 ? name[..300] : name,
                    DueDate = DateTime.SpecifyKind(date.Value, DateTimeKind.Utc), CreatedAt = now,
                    AchievedAt = t.Percent == 100 ? now : null
                };
                var entry = (m, new List<Guid>());
                newMilestones.Add(entry); uidToMilestone[t.Uid] = entry;
                continue;
            }

            // Task: parent is the nearest enclosing task; group is the enclosing group, else the first group.
            var parentTask = stack.LastOrDefault(s => s.Task is not null).Task;
            var group = stack.LastOrDefault(s => s.Group is not null).Group;
            Guid groupId;
            if (parentTask is not null) groupId = parentTask.GroupId;
            else if (group is not null) groupId = group.Id;
            else if (firstGroup is not null) groupId = firstGroup.Id;
            else if (existingGroups.Count > 0) groupId = existingGroups[0].Id;
            else
            {
                var g = new ProjectGroup { Id = Guid.NewGuid(), ProjectId = projectId, Name = "Tasks", Color = "#8E8E93", SortOrder = groupSort += 100 };
                newGroups.Add(g); firstGroup = g; groupId = g.Id;
            }
            if (name.Length > 300) { name = name[..300]; warnings.Add($"Task title truncated to 300 characters: \"{name[..40]}…\"."); }

            var start = t.Start?.Date; var due = t.Finish?.Date;
            if (start.HasValue && !due.HasValue)
            {
                var days = Math.Max(1, (int)Math.Round(t.DurationDays ?? 1));
                due = WorkingDays.AddWorkingDays(start.Value, days - 1);
            }
            if (due.HasValue && due < start) due = start;
            var status = t.Percent >= 100 ? "done" : t.Percent > 0 ? "doing" : "todo";
            var task = new ProjectTask
            {
                Id = Guid.NewGuid(), ProjectId = projectId, GroupId = groupId, ParentTaskId = parentTask?.Id,
                Title = name, Status = status, StatusOptionId = optionByStage.TryGetValue(status, out var oid) ? oid : null,
                StartDate = start.HasValue ? DateTime.SpecifyKind(start.Value, DateTimeKind.Utc) : null,
                DueDate = due.HasValue ? DateTime.SpecifyKind(due.Value, DateTimeKind.Utc) : null,
                SortOrder = taskSort += 100, CreatedById = actorId, CreatedAt = now, UpdatedAt = now,
                CompletedAt = status == "done" ? now : null,
                DeadlineDate = t.Deadline.HasValue ? DateTime.SpecifyKind(t.Deadline.Value.Date, DateTimeKind.Utc) : null
            };
            switch (t.ConstraintType)
            {
                case 2 or 4 or 7 when t.ConstraintDate.HasValue:
                    task.ConstraintType = t.ConstraintType switch { 2 => "mso", 4 => "snet", _ => "fnlt" };
                    task.ConstraintDate = DateTime.SpecifyKind(t.ConstraintDate.Value.Date, DateTimeKind.Utc);
                    break;
                case null or 0 or 1: break;
                default: warnings.Add($"Constraint of \"{name}\" is not supported (only Must Start On, Start No Earlier Than, Finish No Later Than); ignored."); break;
            }
            newTasks.Add(task); uidToTask[t.Uid] = task;
            stack.Add((t.Level, task, null));

            foreach (var rid in assignments[t.Uid].Distinct())
            {
                if (!resources.TryGetValue(rid, out var rname)) continue;
                if (memberByName.TryGetValue(rname.Trim(), out var userId))
                    newAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = userId, AssignedAt = now });
                else unmatched.Add(rname.Trim());
            }
        }
        foreach (var n in unmatched.OrderBy(n => n))
            warnings.Add($"Resource \"{n}\" is not a member of this project; not assigned.");

        // Pass 2: links. Milestone predecessors become the milestone's linked tasks.
        var succAdj = new Dictionary<Guid, List<Guid>>();
        bool Reaches(Guid from, Guid to)
        {
            var seen = new HashSet<Guid>(); var q = new Stack<Guid>(); q.Push(from);
            while (q.Count > 0)
            {
                var n = q.Pop();
                if (n == to) return true;
                if (!seen.Add(n) || !succAdj.TryGetValue(n, out var nx)) continue;
                foreach (var x in nx) q.Push(x);
            }
            return false;
        }
        foreach (var t in msp)
        {
            foreach (var (pu, type, lag) in t.Preds)
            {
                if (uidToMilestone.TryGetValue(t.Uid, out var ms))
                {
                    if (uidToTask.TryGetValue(pu, out var pt)) ms.Linked.Add(pt.Id);
                    else warnings.Add($"Milestone \"{ms.M.Title}\": predecessor {pu} is not a task; ignored.");
                    continue;
                }
                if (!uidToTask.TryGetValue(t.Uid, out var succ)) continue;
                if (!uidToTask.TryGetValue(pu, out var pred))
                {
                    warnings.Add(groupUids.Contains(pu) || uidToMilestone.ContainsKey(pu)
                        ? $"Link into \"{succ.Title}\" from a summary or milestone row was skipped."
                        : $"Link into \"{succ.Title}\" refers to an unknown task ({pu}); skipped.");
                    continue;
                }
                var linkType = type switch { 0 => "FF", 3 => "SS", _ => "FS" };
                if (type == 2) warnings.Add($"Start-to-Finish link {pred.Title} → {succ.Title} is not supported; imported as Finish-to-Start.");
                else if (type is < 0 or > 3) warnings.Add($"Unknown link type {type} on \"{succ.Title}\"; imported as Finish-to-Start.");
                if (pred.ParentTaskId == succ.Id || succ.ParentTaskId == pred.Id)
                { warnings.Add($"Link between \"{pred.Title}\" and its own parent/subtask was skipped."); continue; }
                if (Reaches(succ.Id, pred.Id))
                { warnings.Add($"Link {pred.Title} → {succ.Title} would form a cycle; skipped."); continue; }
                if (newLinks.Any(l => l.TaskId == succ.Id && l.DependsOnTaskId == pred.Id)) continue;
                newLinks.Add(new ProjectTaskDependency { TaskId = succ.Id, DependsOnTaskId = pred.Id, LinkType = linkType, LagDays = lag });
                if (!succAdj.TryGetValue(pred.Id, out var lst)) succAdj[pred.Id] = lst = [];
                lst.Add(succ.Id);
            }
        }

        var result = new MsProjectImportResultDto(newGroups.Count, newTasks.Count, newLinks.Count, newMilestones.Count, warnings, dryRun);
        if (dryRun) return Ok(result);

        db.ProjectGroups.AddRange(newGroups);
        db.ProjectTasks.AddRange(newTasks);
        db.ProjectTaskAssignees.AddRange(newAssignees);
        db.ProjectTaskDependencies.AddRange(newLinks);
        foreach (var (m, linked) in newMilestones)
        {
            var ids = linked.Distinct().ToList();
            m.TaskId = ids.Count > 0 ? ids[0] : null;
            db.ProjectMilestones.Add(m);
            db.ProjectMilestoneTasks.AddRange(ids.Select(id => new ProjectMilestoneTask { MilestoneId = m.Id, TaskId = id }));
        }
        await db.SaveChangesAsync(ct);
        return Ok(result);
    }
}
