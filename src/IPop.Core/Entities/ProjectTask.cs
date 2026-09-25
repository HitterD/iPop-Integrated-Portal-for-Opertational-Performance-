namespace IPop.Core.Entities;

/// Task within a project. Supports self-referential subtasks via ParentTaskId.
/// Kanban status: "todo", "doing", "review", "done".
/// Priority: "low", "medium", "high", "urgent".
/// Time tracking: EstimatedHours vs ActualHours.
/// Dependency/Recurring/Template are stubbed per MVP (fields + no heavy logic).
public class ProjectTask
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// Self-reference for subtasks. Null => top-level task.
    public Guid? ParentTaskId { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }

    /// "todo" | "doing" | "review" | "done"
    public required string Status { get; set; }

    /// "low" | "medium" | "high" | "urgent"
    public string? Priority { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }

    /// Time tracking (hours). Estimate vs Actual.
    public decimal? EstimatedHours { get; set; }
    public decimal? ActualHours { get; set; }

    /// Ordering within a status column or list.
    public int SortOrder { get; set; }

    /// Dependency stub: free-text or JSON of prerequisite task ids. No graph calc in v1.
    public string? Dependencies { get; set; }

    /// Recurring stub: simple flag + rule text. No engine in v1.
    public bool IsRecurring { get; set; }
    public string? RecurrenceRule { get; set; }

    /// Template stub: mark as template for future cloning.
    public bool IsTemplate { get; set; }

    /// Tags for categorization/filtering (comma-separated for MVP, e.g. "backend,urgent").
    public string? Tags { get; set; }

    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Project Project { get; set; } = default!;
    public ProjectTask? ParentTask { get; set; }
    public User CreatedBy { get; set; } = default!;

    public ICollection<ProjectTask> Subtasks { get; set; } = [];
    public ICollection<ProjectTaskAssignee> Assignees { get; set; } = [];
}
