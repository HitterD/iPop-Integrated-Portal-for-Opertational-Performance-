namespace IPop.Core.Entities;

/// Durable "new mail" fan-out: written in the same transaction as the inbox rows, processed by
/// MailNotifyWorker with retry, so a send returns quickly and no recipient's notification is lost
/// to a crash or a full in-memory queue.
public class MailNotifyJob
{
    public const int MaxAttempts = 8;

    public Guid Id { get; set; }
    public Guid MailId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? DoneAt { get; set; }
    public string? LastError { get; set; }
}
