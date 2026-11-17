namespace IPop.Application.Projects;

public record ProjectStatusDefault(string Label, string Color, string Stage);

/// The status set a project starts with (materialized into rows the first time statuses are read
/// or written), plus the stage, health and tab vocabularies.
public static class ProjectStatusDefaults
{
    public static readonly IReadOnlyList<string> Stages = ["todo", "doing", "review", "done"];

    public static readonly IReadOnlyList<ProjectStatusDefault> Options =
    [
        new("Not started", "#c4c4c4", "todo"),
        new("Working on it", "#fdab3d", "doing"),
        new("Stuck", "#e2445c", "doing"),
        new("Ready to check", "#579bfc", "review"),
        new("Done", "#00c875", "done"),
    ];

    public static readonly IReadOnlyList<string> Healths = ["on_track", "at_risk", "off_track"];

    public static readonly IReadOnlyList<string> TabKeys =
    [
        "overview", "table", "board", "gantt", "list", "timeline", "calendar", "workload", "reports",
        "doc", "whiteboard", "form", "files", "activity", "team", "gallery",
    ];

    public const int MaxOptions = 20;
    public const int MaxLabelLength = 40;
    public const int MaxTabs = 16;
}
