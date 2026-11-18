using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Built-in templates in New project → Template (mockup project-reports.html, artboard 2):
/// Software sprint, IT procurement, Event planning. Seeded at startup by fixed ids; an existing
/// row is left alone (its UsedCount and any admin edits survive restarts), a missing one is added.
public static partial class BuiltInProjectTemplates
{
    public static readonly Guid SoftwareSprintId = new("6f1d2a10-0000-4c00-9000-00000000a001");
    public static readonly Guid ItProcurementId = new("6f1d2a10-0000-4c00-9000-00000000a002");
    public static readonly Guid EventPlanningId = new("6f1d2a10-0000-4c00-9000-00000000a003");

    private sealed record G(string Name, string Color);
    private sealed record T(string Title, int Group, int? Due, string Priority = "medium", string? Description = null,
        int? Duration = null, string? Role = null, string? Recur = null);
    private sealed record M(string Title, int Due, int[] Tasks);
    private sealed record S(string Label, string Color, string Stage);
    private sealed record R(string Key, string Label, string? Hint);
    private sealed record Def(Guid Id, string Name, string Description, string Icon, string Color, G[] Groups, T[] Tasks, M[] Milestones,
        string? Category = null, int? Weeks = null, bool WorkingDays = false, S[]? Statuses = null, string[]? Tabs = null,
        R[]? Roles = null, string[]? Automations = null);

    private static Def V(Guid id, string name, string description, string icon, string color, string category, int weeks, bool workingDays,
        G[] groups, T[] tasks, M[] milestones, S[]? statuses, string[] tabs, R[] roles, string[] automations) =>
        new(id, name, description, icon, color, groups, tasks, milestones, category, weeks, workingDays, statuses, tabs, roles, automations);

    private static readonly System.Text.Json.JsonSerializerOptions Camel = new(System.Text.Json.JsonSerializerDefaults.Web);
    private static string? Json<TV>(TV? v) where TV : class => v is null ? null : System.Text.Json.JsonSerializer.Serialize(v, Camel);

    private static Def[]? _all;
    private static Def[] All => _all ??= [.. Legacy(), .. V2()];   // lazy: static fields in the partial files initialise in an unspecified order

    private static Def[] Legacy() =>
    [
        new(SoftwareSprintId, "Software sprint", "Backlog → In progress → Review → Done", "zap", "inf",
            [new("Backlog", "#8E8E93"), new("In progress", "#0A84FF"), new("Review", "#FF9F0A"), new("Done", "#2E9E5B")],
            [
                new("Sprint planning meeting", 0, 0, "high", "Agree the sprint goal and pull stories from the backlog."),
                new("Refine user stories and acceptance criteria", 0, 1),
                new("Estimate stories (story points)", 0, 1),
                new("Set up feature branch and CI pipeline", 1, 2),
                new("Build backend API endpoints", 1, 6, "high"),
                new("Build frontend screens", 1, 8, "high"),
                new("Write unit and integration tests", 1, 9),
                new("Code review and fixes", 2, 10),
                new("QA testing on staging", 2, 11, "high"),
                new("UAT with key users", 2, 12),
                new("Deploy to production", 3, 13, "urgent"),
                new("Sprint review and retrospective", 3, 13),
            ],
            [new("Mid-sprint check", 7, [3, 4, 5]), new("Sprint release", 14, [6, 7, 8, 9, 10])],
            "ICT", 2, false, null, ["overview", "table", "board", "gantt", "workload"], [new("lead", "Project lead", "can manage the project")], ["Move a task to Review when its pull request is opened"]),

        new(ItProcurementId, "IT procurement", "Request → Quotation → PO → Delivery → Install", "package", "war",
            [new("Request", "#8E8E93"), new("Quotation", "#0A84FF"), new("Purchase order", "#7657C4"), new("Delivery", "#FF9F0A"), new("Install", "#2E9E5B")],
            [
                new("Collect requirements and specifications from the user", 0, 1, "high"),
                new("Approve purchase request (department head)", 0, 3),
                new("Request quotations from at least 3 vendors", 1, 7),
                new("Compare quotations and recommend a vendor", 1, 10),
                new("Get budget approval from Finance", 2, 12, "high"),
                new("Issue purchase order to vendor", 2, 14),
                new("Receive goods and check against the PO", 3, 28),
                new("Register assets and update inventory", 4, 30),
                new("Install, configure and hand over to the user", 4, 32, "high"),
            ],
            [new("Installed", 32, [6, 7, 8])],
            "Finance & purchasing", 5, false, null, ["overview", "table", "board", "gantt", "files"], [new("lead", "Requester / lead", "can manage the project")], ["Remind the lead when a quotation is overdue"]),

        new(EventPlanningId, "Event planning", "Timeline, vendors, run-down checklist", "party-popper", "vio",
            [new("Preparation", "#0A84FF"), new("Vendors", "#FF9F0A"), new("Run-down", "#7657C4")],
            [
                new("Define event goal, audience and budget", 0, 0, "high"),
                new("Book the venue", 0, 3, "high"),
                new("Draft the event run-down", 0, 7),
                new("Send invitations and track RSVPs", 0, 10),
                new("Prepare name tags and attendance list", 0, 25),
                new("Choose catering vendor and menu", 1, 10),
                new("Book sound system and projector", 1, 12),
                new("Order banner, backdrop and printed materials", 1, 14),
                new("Arrange photographer / documentation", 1, 14),
                new("Arrange transport for guests", 1, 18),
                new("Technical rehearsal at the venue", 2, 27),
                new("Brief the MC and committee", 2, 27),
                new("Event day: registration and run-down", 2, 28, "urgent"),
                new("Post-event report and vendor payments", 2, 33),
            ],
            [new("Event day", 28, [10, 11, 12])],
            "Events", 5, false, null, ["overview", "table", "board", "calendar", "files"], [new("lead", "Event lead", "can manage the project")], ["Remind the lead 3 days before the event day"]),
    ];

    public static async Task EnsureSeededAsync(IPopDbContext db, CancellationToken ct = default)
    {
        var ids = All.Select(d => d.Id).ToList();
        var existing = await db.ProjectTemplates.IgnoreQueryFilters().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var baseTime = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var changed = false;
        for (var di = 0; di < All.Length; di++)
        {
            var d = All[di];
            if (existing.TryGetValue(d.Id, out var row))
            {
                // Backfill the Template Center metadata once; names, tasks and any admin edits stay as they are.
                if (row.Category is null && d.Category is not null)
                {
                    Apply(row, d);
                    changed = true;
                }
                continue;
            }
            changed = true;
            var tpl = new ProjectTemplate
            {
                Id = d.Id, Name = d.Name, Description = d.Description, Icon = d.Icon, Color = d.Color,
                IsBuiltIn = true, IsActive = true, Visibility = ProjectTemplateVisibility.Everyone,
                CreatedAt = baseTime.AddMinutes(di),   // keeps the built-ins in mockup order
            };
            Apply(tpl, d);
            db.ProjectTemplates.Add(tpl);
            var groupIds = d.Groups.Select((_, i) => Derive(d.Id, 100 + i)).ToArray();
            for (var gi = 0; gi < d.Groups.Length; gi++)
                db.ProjectTemplateGroups.Add(new ProjectTemplateGroup
                {
                    Id = groupIds[gi], TemplateId = d.Id, Name = d.Groups[gi].Name, Color = d.Groups[gi].Color, SortOrder = gi * 100,
                });
            var taskIds = d.Tasks.Select((_, i) => Derive(d.Id, 200 + i)).ToArray();
            for (var ti = 0; ti < d.Tasks.Length; ti++)
            {
                var t = d.Tasks[ti];
                db.ProjectTemplateTasks.Add(new ProjectTemplateTask
                {
                    Id = taskIds[ti], TemplateId = d.Id, Title = t.Title, Description = t.Description, Priority = t.Priority,
                    DueOffsetDays = t.Due, SortOrder = ti * 100, GroupId = groupIds[t.Group],
                    DurationDays = t.Duration, RoleKey = t.Role, RecurrenceRule = t.Recur,
                });
            }
            for (var mi = 0; mi < d.Milestones.Length; mi++)
            {
                var m = d.Milestones[mi];
                db.ProjectTemplateMilestones.Add(new ProjectTemplateMilestone
                {
                    Id = Derive(d.Id, 400 + mi), TemplateId = d.Id, Title = m.Title, DueOffsetDays = m.Due, SortOrder = mi * 100,
                    TaskIds = string.Join(",", m.Tasks.Select(i => taskIds[i])),
                });
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    private static void Apply(ProjectTemplate t, Def d)
    {
        t.Category = d.Category;
        t.DurationWeeks = d.Weeks;
        t.OffsetsInWorkingDays = d.WorkingDays;
        t.StatusesJson = Json(d.Statuses?.Select(s => new { label = s.Label, color = s.Color, stage = s.Stage }).ToArray());
        t.TabsJson = Json(d.Tabs);
        t.RolesJson = Json(d.Roles?.Select(r => new { key = r.Key, label = r.Label, hint = r.Hint }).ToArray());
        t.AutomationsJson = Json(d.Automations);
    }

    /// Data-integrity check over every built-in definition (used by tests); returns one message per problem.
    public static IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var d in All)
        {
            void E(string m) => errors.Add($"{d.Name}: {m}");
            var roleKeys = (d.Roles ?? []).Select(r => r.Key).ToHashSet();
            if (d.Category is null || d.Weeks is null or <= 0) E("missing category/weeks");
            if (d.Tasks.Length == 0 || d.Groups.Length == 0) E("empty");
            foreach (var t in d.Tasks)
            {
                if (t.Due is null or < 0) E($"task '{t.Title}' has no/negative offset");
                if (t.Group < 0 || t.Group >= d.Groups.Length) E($"task '{t.Title}' bad group");
                if (t.Duration is <= 0 || (t.Duration is { } du && t.Due is { } dd && du - 1 > dd)) E($"task '{t.Title}' bad duration");
                if (t.Role is not null && !roleKeys.Contains(t.Role)) E($"task '{t.Title}' unknown role {t.Role}");
                if (t.Title.Length > 300) E($"task '{t.Title}' title too long");
            }
            foreach (var m in d.Milestones)
                if (m.Due < 0 || m.Tasks.Length == 0 || m.Tasks.Any(i => i < 0 || i >= d.Tasks.Length)) E($"milestone '{m.Title}' bad refs");
            foreach (var tab in d.Tabs ?? []) if (!IPop.Application.Projects.ProjectStatusDefaults.TabKeys.Contains(tab)) E($"tab {tab}");
            if (d.Tabs is { Length: 0 }) E("no tabs");
            if (d.Statuses is { } ss)
            {
                if (ss.Any(s => !IPop.Application.Projects.ProjectStatusDefaults.Stages.Contains(s.Stage))) E("status stage");
                if (!ss.Any(s => s.Stage == "todo") || !ss.Any(s => s.Stage == "done")) E("statuses need todo and done");
                if (ss.Select(s => s.Label.ToLowerInvariant()).Distinct().Count() != ss.Length) E("duplicate status label");
            }
            if (d.Roles is { } rs && rs.Select(r => r.Key).Distinct().Count() != rs.Length) E("duplicate role key");
            if ((d.Automations?.Length ?? 0) is 0 or > 3) E("1-3 automations");
        }
        if (All.Select(d => d.Id).Distinct().Count() != All.Length) errors.Add("duplicate ids");
        return errors;
    }

    /// Stable child ids (template id with its last 3 hex digits replaced) so reseeding a
    /// wiped template recreates the same rows.
    private static Guid Derive(Guid parent, int n)
    {
        var s = parent.ToString("N");
        return Guid.ParseExact(s[..26] + n.ToString("x3") + s[29..], "N");
    }
}
