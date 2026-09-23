namespace IPop.Core.Entities;

/// Project aggregates tasks and members. Grouping by year is derived from StartDate or an explicit Year field for fiscal bucketing.
public class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// Year for grouping (e.g., fiscal or calendar year). Can be derived from StartDate but stored explicitly for stable bucketing.
    public int Year { get; set; }

    /// Owner of the project (accountable party).
    public Guid OwnerId { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? Deadline { get; set; }

    /// Status: "planning", "active", "on_hold", "completed", "cancelled"
    public required string Status { get; set; }

    /// Computed progress (0-100) based on task completion. Stored for quick reads; recalculated on mutations.
    public int Progress { get; set; }

    /// Free-text risk/obstacle tracking per PRD.
    public string? Risk { get; set; }
    public string? Obstacle { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = default!;
    public ICollection<ProjectMember> Members { get; set; } = [];
    public ICollection<ProjectTask> Tasks { get; set; } = [];
}
