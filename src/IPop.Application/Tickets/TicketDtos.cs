namespace IPop.Application.Tickets;

/// iDesk ticket statuses as iPop shows them. The iDesk values are used verbatim; Sending and
/// Failed are iPop-only states for a ticket that iDesk has not accepted yet.
public static class TicketStatus
{
    public const string Sending = "SENDING";
    public const string Failed = "FAILED";
    public const string Todo = "TODO";
    public const string InProgress = "IN_PROGRESS";
    public const string WaitingVendor = "WAITING_VENDOR";
    public const string Resolved = "RESOLVED";
    public const string Closed = "CLOSED";
    public const string Cancelled = "CANCELLED";

    public static bool IsOpen(string s) => s is not (Closed or Cancelled);
}

/// One iDesk queue (GET /modules), used to fill the New ticket page.
public record TicketModuleDto(
    string Slug,
    string Name,
    string? Description,
    IReadOnlyList<string> Categories,
    string? DefaultCategory,
    IReadOnlyList<string> Priorities,
    bool SupportsHardwareInstallation);

public record TicketPersonDto(string Name);

/// Row in the My tickets list.
public record TicketListItemDto(
    Guid Id,
    string? TicketNumber,
    string? ExternalRef,
    string Origin,          // IPOP | IDESK
    string Status,          // TicketStatus
    string Priority,        // LOW | MEDIUM | HIGH | CRITICAL
    string Module,          // slug
    string ModuleName,
    string Category,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record TicketEventDto(
    string Kind,            // created | assigned | status_changed | resolved | closed | sent | send_failed
    string? FromStatus,
    string? ToStatus,
    string? Assignee,
    DateTime OccurredAt);

public record TicketAttachmentDto(string FileName, long Size);

public record TicketDetailDto(
    Guid Id,
    string? TicketNumber,
    string? ExternalRef,
    string Origin,
    string Status,
    string Priority,
    string Module,
    string ModuleName,
    string Category,
    string Title,
    string? Description,
    string? Device,
    string? Software,
    string? CriticalReason,
    string? HardwareType,
    string? ScheduledDate,  // yyyy-MM-dd
    string? ScheduledTime,  // HH:mm
    TicketPersonDto? Assignee,
    DateTime? SlaTarget,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ResolvedAt,
    DateTime? ClosedAt,
    string? Url,            // open in iDesk
    string? LastError,      // why sending failed
    IReadOnlyList<TicketAttachmentDto> Attachments,
    IReadOnlyList<TicketEventDto> Events);

public record TicketListDto(
    IReadOnlyList<TicketListItemDto> Tickets,
    DateTime? SyncedAt,     // last successful sync with iDesk for this user
    bool Offline,           // iDesk unreachable on the last sync attempt
    bool NotRegistered);    // iDesk answered 422 for this user's NIK

public record NewTicketAttachment(string FileName, string ContentBase64);

public record CreateTicketRequest(
    string Module,
    string? Category,
    string Priority,
    string Title,
    string Description,
    string? CriticalReason,
    string? Device,
    string? Software,
    string? HardwareType,
    string? ScheduledDate,
    string? ScheduledTime,
    IReadOnlyList<NewTicketAttachment>? Attachments);

/// 201 = sent and accepted by iDesk; 202 = kept as Sending and retried in the background.
/// Validation errors come back as 400 { code, message }; 422 { code: "not_registered" }.
public record CreateTicketResultDto(TicketDetailDto Ticket, bool Queued);

/// Allowed attachment rules, mirrored from iDesk (apps/backend/src/shared/core/config/upload.config.ts).
public static class TicketAttachmentRules
{
    public const int MaxFiles = 5;
    public const long MaxBytes = 10L * 1024 * 1024;
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".csv" };
    public static readonly IReadOnlyList<string> HardwareSlots = ["08:00", "09:00", "10:00", "11:00", "14:00", "15:00"];
}
