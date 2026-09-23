namespace IPop.Core.Entities;

/// A single day entry in a project's work calendar (interconnect ipop-21).
/// Kind: "workday" (explicit working day), "holiday" (non-working), "offday" (team off).
/// Used by Gantt scheduling to compute working-day durations and by the calendar view.
public class WorkCalendarEntry
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// The calendar date this entry applies to (unique per project).
    public DateTime Date { get; set; }

    /// "workday" | "holiday" | "offday"
    public required string Kind { get; set; }

    public string? Title { get; set; }
    public string? Note { get; set; }

    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Project Project { get; set; } = default!;
    public User CreatedBy { get; set; } = default!;
}
