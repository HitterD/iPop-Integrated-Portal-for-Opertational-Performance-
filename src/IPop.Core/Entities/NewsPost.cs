namespace IPop.Core.Entities;

/// Single table for corporate news / announcements / memos (type enum).
/// Approval state machine: draft -> pending -> published | rejected.
/// Scheduling via PublishAt (nullable) — a published row with future PublishAt is not visible
/// in the reader feed until that time.
public class NewsPost
{
    public Guid Id { get; set; }

    /// "news" | "announcement" | "memo" | "condolence"
    public required string Type { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }

    /// "plain" | "html". Existing rows and legacy callers stay "plain" (escaped, pre-wrapped);
    /// the composer writes "html", which is rendered through the News sanitizer allow-list.
    public string BodyFormat { get; set; } = "plain";

    /// Incremented on every content change. Private revision drafts compare-and-swap against this so
    /// a stale revision cannot overwrite a newer live edit.
    public int ContentVersion { get; set; }

    /// Cover image chosen for the feed card; null means "derive from the first inline image".
    public Guid? CoverMediaId { get; set; }

    /// "auto" | "manual" | "none" — keeps "never chosen" distinct from "explicitly removed".
    public string CoverMode { get; set; } = "auto";

    /// "draft" | "pending" | "published" | "rejected"
    public required string Status { get; set; }

    public Guid AuthorId { get; set; }
    public Guid? ApproverId { get; set; }
    public string? RejectionReason { get; set; }

    /// Optional scheduled publish. When set, the post only appears in reader feeds after this time.
    public DateTime? PublishAt { get; set; }

    /// Actual time the post entered published state (null until then).
    public DateTime? PublishedAt { get; set; }

    public bool IsPinned { get; set; }
    public DateTime? PinnedAt { get; set; }

    /// Mandatory read acknowledgement required from targeted readers.
    public bool RequiresAcknowledgement { get; set; }

    /// Optional deadline shown to readers ("Please read and confirm by ...").
    public DateTime? AckDueAt { get; set; }

    /// Last content edit made after the post was published (drives the "Edited" label).
    public DateTime? EditedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Author { get; set; } = default!;
    public User? Approver { get; set; }

    public ICollection<NewsTarget> Targets { get; set; } = [];
    public ICollection<NewsAcknowledgement> Acknowledgements { get; set; } = [];
    public ICollection<NewsAudit> Audits { get; set; } = [];
    public ICollection<NewsAttachment> Attachments { get; set; } = [];

    /// Normalized references to promoted composer media (inline + cover), for access checks and GC.
    public ICollection<NewsPostMedia> Media { get; set; } = [];
}
