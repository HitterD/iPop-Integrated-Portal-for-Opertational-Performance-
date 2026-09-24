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
    /// Rows whose HRIS changes were not applied because an admin edited them (protected).
    public int Skipped { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    /// NIK of the admin who pressed Sync; null for scheduled runs.
    public string? TriggeredBy { get; set; }
    /// Human-readable step log, one line per step, shown in the admin run detail.
    public string? Log { get; set; }
}
