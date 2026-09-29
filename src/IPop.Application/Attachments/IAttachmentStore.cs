namespace IPop.Application.Attachments;

public record StoredAttachment(Guid Id, string FileName, string ContentType, long Size, string Sha256);

public class AttachmentRejectedException(string code, string message) : Exception(message)
{
    /// "too_large" | "blocked_type" | "empty"
    public string Code { get; } = code;
}

public interface IAttachmentStore
{
    Task<StoredAttachment> PutAsync(string area, string fileName, string contentType, Stream content, Guid uploaderId, CancellationToken ct);
    /// Null when the id is unknown or soft-deleted.
    Task<(StoredAttachment Meta, Stream Content)?> OpenAsync(Guid id, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// One per area (chat, news, mail, projects). The download endpoint asks the policy for the
/// attachment's area; an area with no registered policy is never downloadable.
public interface IAttachmentAccessPolicy
{
    string Area { get; }
    Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct);
}

public static class AttachmentAreas
{
    public const string Chat = "chat";
    public const string News = "news";
    public const string Mail = "mail";
    public const string Projects = "projects";
    public const string Notes = "notes";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Chat, News, Mail, Projects, Notes };
}
