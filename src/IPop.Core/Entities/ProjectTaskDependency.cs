namespace IPop.Core.Entities;

/// <summary>TaskId waits for DependsOnTaskId to be done. Both tasks are in the same project.</summary>
public class ProjectTaskDependency
{
    public Guid TaskId { get; set; }
    public Guid DependsOnTaskId { get; set; }
    public ProjectTask Task { get; set; } = default!;
    public ProjectTask DependsOn { get; set; } = default!;
}
