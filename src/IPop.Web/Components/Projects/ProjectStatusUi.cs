using System.Text.RegularExpressions;
using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

/// Projects v2: a project's custom statuses (label + colour + stage) as the views show them.
/// When the project has none loaded (offline / older API) the four stages stand in, with empty
/// ids so a pick falls back to a stage-only move.
public static class ProjectStatusUi
{
    public static readonly IReadOnlyList<ProjectStatusOptionDto> StageDefaults =
    [
        new(Guid.Empty, "To Do", "#c4c4c4", "todo", 0, 0),
        new(Guid.Empty, "In Progress", "#fdab3d", "doing", 1, 0),
        new(Guid.Empty, "Review", "#579bfc", "review", 2, 0),
        new(Guid.Empty, "Done", "#00c875", "done", 3, 0),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> Stages =
    [
        ("todo", "Not started"), ("doing", "Working"), ("review", "Review"), ("done", "Done"),
    ];

    public static readonly string[] Palette =
        ["#b9bcc0", "#fdab3d", "#e2445c", "#a25ddc", "#579bfc", "#00c875", "#0e9aa7", "#e05a9b", "#b97f05", "#5b636b"];

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);

    /// Colours come from the server; only a plain hex value may reach a style attribute.
    public static string SafeColor(string? c) =>
        c is not null && Hex.IsMatch(c) ? (IsTodoGrey(c) ? "var(--s-todo)" : c) : "#8b9198";

    /// The default "Not started" greys follow the theme (--s-todo: light #b9bcc0, dark #6d7379) so white
    /// status text stays readable in both; every other colour is the project's own choice.
    public static bool IsTodoGrey(string c) =>
        c.Equals("#b9bcc0", StringComparison.OrdinalIgnoreCase) || c.Equals("#c4c4c4", StringComparison.OrdinalIgnoreCase);

    public static string StageLabel(string? stage) => stage switch
    {
        "todo" => "To Do", "doing" => "In Progress", "review" => "Review", "done" => "Done", _ => stage ?? "",
    };

    public static ProjectStatusOptionDto? FirstOfStage(IReadOnlyList<ProjectStatusOptionDto> options, string stage) =>
        options.FirstOrDefault(o => o.Stage == stage);

    /// The option a task shows: its own StatusOptionId, else the first option of its stage.
    public static ProjectStatusOptionDto Resolve(ProjectTaskDto t, IReadOnlyList<ProjectStatusOptionDto> options)
    {
        if (options.Count == 0) options = StageDefaults;
        if (t.StatusOptionId is { } id && options.FirstOrDefault(o => o.Id == id) is { } own) return own;
        return options.FirstOrDefault(o => o.Stage == t.Status) ?? options[0];
    }

    /// Null for the stage stand-ins: the server then moves by stage alone.
    public static Guid? RealId(ProjectStatusOptionDto o) => o.Id == Guid.Empty ? null : o.Id;
}
