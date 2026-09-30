namespace IPop.Core.Entities;

/// <summary>A custom field definition owned by one project.</summary>
public class ProjectField
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    /// <summary>Text, Number, Date, Dropdown, Person, or Link.</summary>
    public required string Type { get; set; }
    /// <summary>JSON string array for Dropdown; null for all other types.</summary>
    public string? OptionsJson { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }

    public Project Project { get; set; } = default!;
    public ICollection<ProjectTaskFieldValue> Values { get; set; } = [];
}
