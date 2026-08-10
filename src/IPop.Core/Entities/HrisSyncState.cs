namespace IPop.Core.Entities;

/// Singleton row (Id = 1). LastUpdated is a timestamp, not the page cursor —
/// a crashed job re-queries from it and re-upserts idempotently (spec §4).
public class HrisSyncState
{
    public int Id { get; set; }
    public DateTime? LastUpdated { get; set; }
    public DateTime? LastFullSyncAt { get; set; }
    public DateTime? LastIncrementalAt { get; set; }
}
