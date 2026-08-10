namespace IPop.Core.Entities;

/// Append-only result of one HRIS pull; failures remain visible to administrators.
public class HrisSyncRun
{
    public Guid Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
}
