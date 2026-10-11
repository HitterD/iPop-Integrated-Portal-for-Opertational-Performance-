namespace IPop.Core.Entities;

/// <summary>
/// Local mirror of one ICT ticket in iDesk, owned by the requester. iPop-filed tickets exist here
/// before iDesk has them (Status SENDING/FAILED, <see cref="PendingPayload"/> set); iDesk-filed ones
/// arrive through sync or webhook. Statuses are iDesk's values verbatim (see TicketStatus).
/// </summary>
public class Ticket
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    /// Ticketing backend; only "idesk" for now (HC comes later through the same table).
    public required string Provider { get; set; }
    /// IPOP = filed from iPop, IDESK = filed directly in iDesk.
    public required string Origin { get; set; }
    /// IPOP-ICT-{yyyy}-{NNNNNN}; iDesk is idempotent on it, so retries reuse it. Null for IDESK origin.
    public string? ExternalRef { get; set; }
    public string? IdeskTicketId { get; set; }
    public string? TicketNumber { get; set; }
    public required string Status { get; set; }
    public required string Priority { get; set; }
    public required string Module { get; set; }
    public required string ModuleName { get; set; }
    public required string Category { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Device { get; set; }
    public string? Software { get; set; }
    public string? CriticalReason { get; set; }
    public string? HardwareType { get; set; }
    public string? ScheduledDate { get; set; }
    public string? ScheduledTime { get; set; }
    public string? AssigneeName { get; set; }
    public DateTime? SlaTarget { get; set; }
    /// ICT visit of a hardware installation as iDesk reports it (UTC); mirrored onto the requester's calendar.
    public DateTime? VisitStart { get; set; }
    public DateTime? VisitEnd { get; set; }
    /// Replies from ICT since the requester last opened the ticket in iPop, and the latest one's preview.
    public int UnreadMessages { get; set; }
    public string? LastMessagePreview { get; set; }
    public string? LastMessageSender { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    /// Soft delete: the ticket disappeared from iDesk (404 or missing from a full sync).
    public DateTime? RemovedAt { get; set; }
    /// Last time this row was compared with iDesk (detail refresh / reconcile).
    public DateTime? RefreshedAt { get; set; }

    // Outbox (SENDING/FAILED only).
    public int SendAttempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    /// The iDesk POST body including base64 attachments; dropped once iDesk accepts the ticket.
    public string? PendingPayload { get; set; }
    /// [{ fileName, size }] — what the detail view keeps after the files themselves are gone.
    public string? AttachmentsJson { get; set; }
}

/// One line of a ticket's timeline. Webhook events carry their iDesk event id (unique), which is
/// how at-least-once deliveries are deduplicated.
public class TicketEvent
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    /// created | assigned | status_changed | resolved | closed | message_created | visit_changed | sent | send_failed
    public required string Kind { get; set; }
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Assignee { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? WebhookEventId { get; set; }
}

/// Ids of iDesk webhook events that have no ticket timeline to live on (Zoom bookings), so an
/// at-least-once redelivery is applied once.
public class IdeskWebhookReceipt
{
    public required string EventId { get; set; }
    public DateTime ReceivedAt { get; set; }
}

/// Per-user outcome of the last iDesk sync, shown as banners on /tickets.
public class TicketUserState
{
    public Guid UserId { get; set; }
    public DateTime? SyncedAt { get; set; }
    public bool Offline { get; set; }
    /// iDesk answered 422 for this NIK; cleared by the next successful sync or submit.
    public bool NotRegistered { get; set; }
}

/// Row-per-year counter behind IPOP-ICT-{yyyy}-{NNNNNN}. Incremented with a single UPDATE inside a
/// transaction, so the row lock serialises concurrent submits.
public class TicketRefCounter
{
    public int Year { get; set; }
    public int Last { get; set; }
}
