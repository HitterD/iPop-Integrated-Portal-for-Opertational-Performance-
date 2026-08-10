namespace IPop.Core.Entities;

/// Append-only, immutable (spec §5). Partitioned by created_at month at the DB level.
public class AuditLog
{
    public Guid Id { get; set; }
    public string? ActorNik { get; set; }
    public required string Action { get; set; }
    public string? Entity { get; set; }
    public string? EntityId { get; set; }
    public string? DetailJson { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}
