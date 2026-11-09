namespace IPop.Core.Entities;

/// Queued outgoing email (v6 foundation spec §5). The worker retries until MaxAttempts,
/// leaving LastError visible for failed sends.
public class EmailOutboxMessage
{
    /// 1, 2, 4 … capped at 15 min between tries: ~40 min of retries in total. Only password-reset links use the
    /// outbox and they expire after 30 min, so an SMTP outage of up to half an hour (a rotated password, a mail
    /// server restart) no longer loses the mail; it used to give up after 3 tries / ~6 minutes.
    public const int MaxAttempts = 8;

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
