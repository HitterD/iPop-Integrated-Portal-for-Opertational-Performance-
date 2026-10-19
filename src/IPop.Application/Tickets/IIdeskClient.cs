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
    string? Url,
    IdeskVisit? Visit = null);

/// The ICT visit of a hardware-installation ticket (UTC).
public sealed record IdeskVisit(DateTime Start, DateTime End, string? HardwareType);

/// One public chat message of a ticket (GET /tickets/{no}/messages). Senders by name only.
public sealed record IdeskMessage(string Id, string? SenderName, bool FromRequester, string Content,
    IReadOnlyList<string> Attachments, DateTime CreatedAt);

public sealed record IdeskMessages(string? Url, IReadOnlyList<IdeskMessage> Messages);

/// A Zoom meeting booked in iDesk, as a calendar entry (section 7 of the iDesk contract).
public sealed record IdeskBooking(
    string BookingId,
    string? SeriesId,
    string Title,
    string? Description,
    DateTime Start,
    DateTime End,
    string? AccountName,
    string? JoinUrl,
    string? MeetingId,
    string? Passcode,
    IReadOnlyList<IdeskBookingAttendee> Attendees,
    string? Url);

/// Role = BOOKER | PARTICIPANT. Nik is only used server-side to find the iPop user.
public sealed record IdeskBookingAttendee(string? Nik, string Name, string Role);

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
    /// The public conversation, oldest first; internal notes never included.
    Task<IdeskResult<IdeskMessages>> GetMessagesAsync(string ticketNumber, CancellationToken ct = default);
    /// Posts a text reply to the ticket's public chat as the person with this NIK (requester side or the
    /// assigned agent; iDesk enforces it). ticketRef = ticket number or iDesk ticket id.
    Task<IdeskResult<bool>> ReplyAsync(string ticketRef, string nik, string content, CancellationToken ct = default);
    /// Zoom bookings the user booked or is invited to, dates inclusive in WIB, at most 92 days.
    Task<IdeskResult<IReadOnlyList<IdeskBooking>>> ListZoomBookingsAsync(string nik, DateOnly from, DateOnly to, CancellationToken ct = default);
}
