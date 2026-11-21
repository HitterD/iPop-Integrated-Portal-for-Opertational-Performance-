namespace IPop.Core.Entities;

/// <summary>A built-in automation rule switched on or off for one project (no free-form scripting).</summary>
public class ProjectAutomation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    /// Key from the fixed catalogue (ProjectAutomationCatalogue).
    public required string Kind { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public bool IsEnabled { get; set; }
    public int RunCount { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Project Project { get; set; } = default!;
}
