namespace IPop.Core.Entities;

/// <summary>
/// One block of time a person spent on a task. A running timer has <see cref="EndedAt"/> null
/// (at most one per user, enforced by a partial unique index). <see cref="Minutes"/> is the
/// billed length and is what rolls up into <see cref="ProjectTask.ActualHours"/>.
/// Source: "timer" | "manual" | "legacy" (backfilled from the old typed Actual hours).
/// </summary>
public class TaskTimeEntry
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int Minutes { get; set; }
    public string? Note { get; set; }
    public required string Source { get; set; }
    /// Set when the 10-hour cap stopped a forgotten timer, so the UI can ask for a correction.
    public bool AutoStopped { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public User User { get; set; } = default!;
}
