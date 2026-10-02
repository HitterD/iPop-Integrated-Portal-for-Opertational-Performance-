namespace IPop.Core.Entities;

/// <summary>
/// Private composer draft for a News post. The draft is a complete snapshot (title/body/audience/
/// ack/pin/schedule/attachments/media) owned by one user and authorized independently of the live
/// post. Nothing here is reader-visible: autosave never touches the live <see cref="NewsPost"/>,
/// promotion does. Multiple active drafts per owner are allowed (no single-draft restriction), and
/// a draft may be incomplete at any point.
///
/// <para><see cref="Version"/> is both the EF concurrency token and the compare-and-swap predicate
/// for autosave, so a stale tab can never overwrite a newer snapshot. <see cref="BaseContentVersion"/>
/// records the live post's ContentVersion the draft was forked from; promotion refuses when the live
/// post has moved on.</para>
/// </summary>
public class NewsComposerDraft
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }

    /// Live post this draft revises; null for a brand-new post (which has no public row yet).
    public Guid? PostId { get; set; }

    /// ContentVersion of <see cref="PostId"/> when the draft was created/reset. Null for new posts.
    public int? BaseContentVersion { get; set; }

    /// Concurrency token: incremented by every successful save; the CAS predicate is (Id, Version).
    public int Version { get; set; }

    /// "active" | "promoted" | "discarded"
    public required string State { get; set; }

    // ── Snapshot ─────────────────────────────────────────────────────────
    /// "news" | "announcement" | "memo" | "condolence" | "duka"
    public string? Type { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }

    /// "plain" | "html" — how <see cref="Body"/> must be sanitized/rendered. Defaults to "plain",
    /// matching the live post and the column default.
    public string BodyFormat { get; set; } = "plain";

    public bool RequiresAcknowledgement { get; set; }
    public DateTime? AckDueAt { get; set; }
    public bool IsPinned { get; set; }

    /// Scheduled publish intent (kept in the snapshot; promotion applies it). Null = publish now.
    public DateTime? PublishAt { get; set; }

    /// Chosen cover media (manual override); null means "derive from the first inline image".
    public Guid? CoverMediaId { get; set; }

    /// "auto" | "manual" | "none" — distinguishes "never chosen" from "explicitly removed",
    /// so an auto-derived cover cannot reappear after the author removed it.
    public string CoverMode { get; set; } = "auto";

    /// Audience snapshot as JSON (array of {kind,value}). Structured settings live here; media and
    /// regular attachments stay normalized in the ref tables below so they can be indexed/GC'd.
    public string? TargetsJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PromotedAt { get; set; }
    public DateTime? DiscardedAt { get; set; }

    /// <summary>
    /// How many times the schedule worker has tried to apply this frozen draft. A draft that cannot
    /// be applied (owner deactivated, permission lost, live post moved on) is backed off rather than
    /// retried immediately, so it can never starve later drafts in the due queue.
    /// </summary>
    public int ScheduleAttemptCount { get; set; }

    /// <summary>Earliest time the worker may try this draft again (null = no backoff pending).</summary>
    public DateTime? NextScheduleAttemptAt { get; set; }

    /// <summary>
    /// Acknowledgement-reset intent captured at freeze time. A scheduled promotion has no request to
    /// read it from when the worker applies it later, so the choice is part of the frozen snapshot.
    /// </summary>
    public bool ResetAcknowledgementsOnPromote { get; set; }

    public User Owner { get; set; } = default!;
    public NewsPost? Post { get; set; }

    public ICollection<NewsComposerDraftMedia> Media { get; set; } = [];
    public ICollection<NewsComposerDraftAttachment> Attachments { get; set; } = [];
}

/// <summary>
/// Normalized reference from a draft to an inline/cover composer media asset. Kept out of the JSON
/// snapshot so media can be authorized and cleaned up by query rather than by parsing drafts.
/// </summary>
public class NewsComposerDraftMedia
{
    public Guid DraftId { get; set; }
    public Guid MediaId { get; set; }

    /// "inline" | "cover"
    public required string Role { get; set; }
    public int SortOrder { get; set; }

    public NewsComposerDraft Draft { get; set; } = default!;
    public NewsComposerMedia Media { get; set; } = default!;
}

/// <summary>
/// Normalized reference from a draft to a regular (non-inline) attachment. Drafts point at shared
/// "news" attachments the same way live posts do, so an autosave can never detach/delete the file
/// a published post is still serving.
/// </summary>
public class NewsComposerDraftAttachment
{
    public Guid DraftId { get; set; }
    public Guid AttachmentId { get; set; }
    public int SortOrder { get; set; }

    public NewsComposerDraft Draft { get; set; } = default!;
    public Attachment Attachment { get; set; } = default!;
}

/// <summary>
/// Normalized reference from a live post to a promoted composer media asset, so cleanup and access
/// checks can see which assets are still in use without re-parsing stored HTML.
/// </summary>
public class NewsPostMedia
{
    public Guid NewsPostId { get; set; }
    public Guid MediaId { get; set; }

    /// "inline" | "cover"
    public required string Role { get; set; }
    public int SortOrder { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public NewsComposerMedia Media { get; set; } = default!;
}