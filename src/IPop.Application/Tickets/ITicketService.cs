namespace IPop.Application.Tickets;

/// Realtime event sent to the ticket owner (payload { id }) after any change to their tickets.
public static class TicketEvents
{
    public const string Changed = "tickets.changed";
    public const string BadgeKey = "tickets";
}

public enum TicketCreateKind { Created, Queued, Invalid, Rejected, NotRegistered, NotConfigured }

/// Created = 201, Queued = 202 (kept as SENDING, retried in the background), Invalid = our own
/// validation (400), Rejected = iDesk said 400, NotRegistered = 422, NotConfigured = 503.
public sealed record TicketCreateOutcome(TicketCreateKind Kind, TicketDetailDto? Ticket = null, string? Message = null, string? Field = null);

public enum TicketRetryKind { Sent, NotFound, NotFailed, Unreachable, Rejected, NotRegistered, NotConfigured }

public sealed record TicketRetryOutcome(TicketRetryKind Kind, TicketDetailDto? Ticket = null, string? Message = null);

public enum TicketDiscardKind { Discarded, NotFound, NotPending }

public enum TicketWebhookKind { Applied, Duplicate, UnknownRequester, Invalid }

public interface ITicketService
{
    /// Null when iDesk is unreachable or not configured and nothing is cached.
    Task<IReadOnlyList<TicketModuleDto>?> GetModulesAsync(CancellationToken ct = default);
    Task<TicketListDto> ListAsync(Guid userId, bool openOnly, CancellationToken ct = default);
    /// Pulls ?nik=&state=all from iDesk into the mirror, then lists like ListAsync.
    Task<TicketListDto> SyncAsync(Guid userId, bool openOnly, CancellationToken ct = default);
    Task<TicketDetailDto?> GetAsync(Guid userId, Guid id, CancellationToken ct = default);
    /// The ticket's public chat (read from iDesk); clears its unread reply count. Null = not my ticket.
    Task<TicketMessagesDto?> GetMessagesAsync(Guid userId, Guid id, CancellationToken ct = default);
    /// Re-reads one ticket from iDesk into the mirror (no notifications). True when something changed.
    Task<bool> RefreshAsync(Guid id, CancellationToken ct = default);
    Task<TicketCreateOutcome> CreateAsync(Guid userId, CreateTicketRequest req, CancellationToken ct = default);
    Task<TicketRetryOutcome> RetryAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<TicketDiscardKind> DiscardAsync(Guid userId, Guid id, CancellationToken ct = default);
    /// The body must already be signature-checked.
    Task<TicketWebhookKind> ApplyWebhookAsync(string body, CancellationToken ct = default);
    /// One outbox pass: resend due SENDING tickets, give up on those older than 24 h. Returns tickets touched.
    Task<int> ProcessOutboxAsync(DateTime now, CancellationToken ct = default);
    /// One reconcile pass over open tickets (silent). Returns tickets changed.
    Task<int> ReconcileAsync(CancellationToken ct = default);
}
