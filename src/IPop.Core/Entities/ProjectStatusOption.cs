namespace IPop.Core.Entities;

/// A project's own status label (monday-style "Working on it", "Stuck", ...). Each one maps to one of
/// the four task stages, and a task showing it always has <see cref="ProjectTask.Status"/> == <see cref="Stage"/>,
/// so reports, workload and progress keep reading the stage.
public class ProjectStatusOption
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Label { get; set; }
    /// "#rrggbb"
    public required string Color { get; set; }
    /// "todo" | "doing" | "review" | "done"
    public required string Stage { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }

    public Project Project { get; set; } = default!;
}
