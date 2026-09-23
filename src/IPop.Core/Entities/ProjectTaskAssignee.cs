namespace IPop.Core.Entities;

/// Multi-assignee PIC on a task. Multiple users can be assigned to one task.
public class ProjectTaskAssignee
{
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }

    /// Optional per-assignee role on the task (e.g., "lead", "reviewer").
    public string? Role { get; set; }

    public DateTime AssignedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public User User { get; set; } = default!;
}
