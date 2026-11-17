namespace IPop.Web.Components.Projects;

/// Projects v2: the views a project can show as tabs (docs/mockups/ipop-apple/projects-v2.html, VIEWS + PICK).
/// A key is what the API stores in Project.Tabs; <see cref="TabIndex"/> maps it to the page's legacy `tab` number.
public static class ProjectViewCatalog
{
    public sealed record View(string Key, string Label, string Icon, string Color, string Description, bool IsNew = false, bool Later = false);

    /// Used when the project has no saved tabs (Project.Tabs == null).
    public static readonly IReadOnlyList<string> DefaultTabs = ["overview", "table", "board", "gantt", "workload", "calendar", "reports"];

    public static readonly IReadOnlyDictionary<string, View> Views = new Dictionary<string, View>
    {
        ["overview"] = new("overview", "Overview", "layout-dashboard", "#e05a9b", "Goal, progress, milestones, risks"),
        ["table"] = new("table", "Table", "table", "#579bfc", "Rows & columns, like Excel"),
        ["board"] = new("board", "Board", "kanban", "#2e9d6b", "Cards by status, drag to move"),
        ["gantt"] = new("gantt", "Gantt", "chart-gantt", "#e2445c", "Dependencies & critical path", IsNew: true),
        ["list"] = new("list", "List", "list", "#8b9198", "Simple checklist"),
        ["timeline"] = new("timeline", "Timeline", "calendar-range", "#fdab3d", "Bars by person or phase"),
        ["calendar"] = new("calendar", "Calendar", "calendar-days", "#0e9aa7", "Tasks on a month view"),
        ["workload"] = new("workload", "Workload", "users", "#7c4dd6", "Who has too much this week", IsNew: true),
        ["activity"] = new("activity", "Activity", "activity", "#2f6fed", "Everything that changed", Later: true),
        ["team"] = new("team", "Team", "user", "#b97f05", "One lane per person", Later: true),
        ["gallery"] = new("gallery", "Gallery", "image", "#d8452e", "Cards with evidence photos", Later: true),
        ["reports"] = new("reports", "Reports", "chart-no-axes-combined", "#5b636b", "Burndown, milestones, on-time"),
        ["doc"] = new("doc", "Brief", "file-text", "#2f6fed", "A Notes page inside the project"),
        ["whiteboard"] = new("whiteboard", "Whiteboard", "pen-tool", "#fdab3d", "Brainstorm, flowcharts"),
        ["form"] = new("form", "Request form", "clipboard-list", "#7c4dd6", "Requests become tasks", IsNew: true),
        ["files"] = new("files", "Files", "folder", "#2e9d6b", "All evidence & attachments"),
    };

    public static readonly IReadOnlyList<(string Heading, string[] Keys)> PickGroups =
    [
        ("Plan & track tasks", ["table", "board", "gantt", "list", "timeline", "calendar", "workload", "overview", "reports", "activity", "team", "gallery"]),
        ("Docs & files", ["doc", "whiteboard", "form", "files"]),
    ];

    /// Page `tab` number (0 Board, 1 Table, 2 Timeline, 3 Workload, 4 Overview, 5 Reports, 6 Calendar); -1 = no view yet (placeholder).
    public static int TabIndex(string key) => key switch
    {
        "board" => 0, "table" => 1, "gantt" or "timeline" => 2, "workload" => 3, "overview" => 4, "reports" => 5, "calendar" => 6,
        _ => -1,
    };

    /// The tabs to show: the project's own list (unknown keys dropped), or the defaults.
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string>? tabs)
    {
        if (tabs is null) return DefaultTabs;
        var list = tabs.Where(Views.ContainsKey).Distinct().ToList();
        return list.Count == 0 ? DefaultTabs : list;
    }

    public static View Get(string key) => Views.TryGetValue(key, out var v) ? v : new View(key, key, "layout-dashboard", "#8b9198", "");

    /// One line under the big icon of a view that has no screen yet.
    public static string PlaceholderText(string key) => key switch
    {
        "activity" or "team" or "gallery" => "Planned for phase 2. The tab stays here and fills in when it ships.",
        "doc" => "A Notes page inside the project.",
        "whiteboard" => "Opens the iPop Whiteboard inside this project: same canvas, same templates and flowcharts.",
        "form" => "Requests become tasks.",
        "files" => "All evidence and attachments.",
        _ => "Same tasks as the Table, shown differently.",
    };
}
