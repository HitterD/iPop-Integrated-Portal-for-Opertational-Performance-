namespace IPop.Core.Entities;

/// <summary>An ordered section of tasks within one project.</summary>
public class ProjectGroup
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string Color { get; set; } = "#8E8E93";
    public int SortOrder { get; set; }
    public Project Project { get; set; } = default!;
    public ICollection<ProjectTask> Tasks { get; set; } = [];
}
