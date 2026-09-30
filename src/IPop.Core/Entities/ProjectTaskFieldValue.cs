namespace IPop.Core.Entities;

/// <summary>A single task's non-empty value for one project custom field.</summary>
public class ProjectTaskFieldValue
{
    public Guid Id { get; set; }
    public Guid ProjectTaskId { get; set; }
    public Guid ProjectFieldId { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ProjectTask Task { get; set; } = default!;
    public ProjectField Field { get; set; } = default!;
}
