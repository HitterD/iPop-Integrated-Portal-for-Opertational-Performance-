namespace IPop.Core.Entities;

/// Queued outgoing email (v6 foundation spec §5). The worker retries until MaxAttempts,
/// leaving LastError visible for failed sends.
public class EmailOutboxMessage
{
    public const int MaxAttempts = 3;

    public Guid Id { get; set; }
    public required string ToJson { get; set; }
    public required string Subject { get; set; }
    public required string HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}
