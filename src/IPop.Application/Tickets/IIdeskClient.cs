namespace IPop.Application.Tickets;

/// How an iDesk call ended. Callers branch on this instead of catching exceptions.
public enum IdeskOutcome
{
    Ok,
    /// 400 (or another non-retryable 4xx): iDesk refused the input; Message says why.
    Rejected,
    /// 422: the requester's NIK is not registered or inactive in iDesk.
    NotRegistered,
    /// 404 on a single ticket: deleted in iDesk.
    NotFound,
    /// Timeout, network error, 5xx, 429 or auth trouble: worth trying again later.
    Unreachable,
    /// IDesk:ClientSecret missing or shorter than 32 chars: nothing is sent (fail closed).
    NotConfigured,
}

public sealed record IdeskResult<T>(IdeskOutcome Outcome, T? Value = default, string? Message = null)
{
    public bool Ok => Outcome == IdeskOutcome.Ok;
}

/// A ticket as iDesk returns it (POST /tickets, GET /tickets/..., webhook "ticket").
public sealed record IdeskTicket(
    string TicketId,
    string? TicketNumber,
    string? ExternalRef,
    string Origin,
    string? RequesterNik,
    string? RequesterName,
    string Status,
    string Priority,
    string Module,
    string? Category,
    string Title,
    string? AssigneeName,
    DateTime? SlaTarget,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    string? Url);

/// The iDesk "ipop" integration API (docs/integrations/ipop-api.md in iDesk). Every request is
/// HMAC-signed; implementations never throw to callers.
public interface IIdeskClient
{
    bool IsConfigured { get; }
    Task<IdeskResult<IReadOnlyList<TicketModuleDto>>> GetModulesAsync(CancellationToken ct = default);
    /// Posts a body the caller serialised itself, so a retry resends exactly the same bytes
    /// (iDesk is idempotent on its externalRef).
    Task<IdeskResult<IdeskTicket>> CreateTicketAsync(string jsonBody, CancellationToken ct = default);
    Task<IdeskResult<IdeskTicket>> GetTicketAsync(string ticketNumber, CancellationToken ct = default);
    /// Newest first, at most 100 (iDesk's cap). state = "open" | "all".
    Task<IdeskResult<IReadOnlyList<IdeskTicket>>> ListByNikAsync(string nik, string state, CancellationToken ct = default);
}
