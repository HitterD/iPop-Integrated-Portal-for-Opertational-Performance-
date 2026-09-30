namespace IPop.Core.Entities;

/// <summary>A named set of Projects filters (tab, search, group…). Personal unless shared by an owner/manager.</summary>
public class ProjectSavedView
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public bool IsShared { get; set; }
    public required string StateJson { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public Project Project { get; set; } = default!;
    public User Owner { get; set; } = default!;
}
