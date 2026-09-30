namespace IPop.Core.Entities;

/// One row per reader who has ever opened a post (the views count). ReadAt is cleared by
/// "Mark as unread" so the post returns to the Unread filter without losing the view.
public class NewsRead
{
    public Guid NewsPostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime FirstReadAt { get; set; }
    public DateTime? ReadAt { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public User User { get; set; } = default!;
}

/// Reader's "Saved for later" bookmark.
public class NewsBookmark
{
    public Guid NewsPostId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public User User { get; set; } = default!;
}

/// File attached to a post. The bytes live in the shared attachment store (area "news").
public class NewsAttachment
{
    public Guid NewsPostId { get; set; }
    public Guid AttachmentId { get; set; }
    public int SortOrder { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public Attachment Attachment { get; set; } = default!;
}
