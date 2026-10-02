namespace IPop.Application.News;

/// <summary>
/// Private News composer drafts (see news-compose mockup). Drafts are owned, authorized
/// independently of the live post and versioned for compare-and-swap autosave.
///
/// <para>Phase-1 foundation: create / resume / list / versioned autosave / discard / preview.
/// Promotion to a live post and composer media upload/GC are follow-on methods that reuse these
/// contracts; they are not part of this interface yet.</para>
/// </summary>
public interface INewsComposerService
{
    /// Start a new private draft. With <paramref name="req"/>.PostId set, the draft revises that live
    /// post and is seeded from it. Creation requires HR/Admin (news.publish), or — for a revision —
    /// the caller must also be able to edit that post. New drafts never create a public NewsPost row.
    Task<NewsDraftResultDto> CreateDraftAsync(Guid ownerId, CreateNewsDraftRequest req, CancellationToken ct);

    /// Resume a draft. Only the owner, or HR/Admin, may read it.
    Task<NewsDraftResultDto> GetDraftAsync(Guid actorId, Guid draftId, CancellationToken ct);

    /// The caller's own private drafts, including drafts frozen as "scheduled" (so a future
/// publication can be located, reviewed and cancelled rather than silently disappearing).
    /// HR/Admin do not get a global draft list here — a draft is private to its author.
    Task<NewsDraftListDto> ListDraftsAsync(Guid ownerId, CancellationToken ct);

    /// Versioned autosave. Incomplete content is allowed and the live post is never touched.
    /// Fails with "version_conflict" on a stale <see cref="SaveNewsDraftRequest.BaseVersion"/>,
    /// and with "post_content_conflict" when a revision's live post has moved on.
    Task<NewsDraftResultDto> SaveDraftAsync(Guid actorId, Guid draftId, SaveNewsDraftRequest req, CancellationToken ct);

    /// Discard a draft (soft: state becomes "discarded"). Never affects the live post.
    Task<NewsDraftResultDto> DiscardDraftAsync(Guid actorId, Guid draftId, CancellationToken ct);

    /// Sanitized preview of a draft, using the same rendering pipeline as the reader.
    Task<NewsPreviewDto?> PreviewAsync(Guid actorId, Guid draftId, CancellationToken ct);

    /// <summary>
    /// Promote a draft onto the live post: insert when the draft has no post yet, update when it
    /// revises one. Only HR/Admin (news.publish) may publish — an author without that right cannot
    /// bypass the gate through a draft. The whole promotion (post write, references, targets,
    /// optional acknowledgement reset, draft state) is one transaction guarded by a compare-and-swap
    /// on the draft Version and, for a revision, on the live ContentVersion.
    /// </summary>
    /// <param name="Mode">"publish" (live now) | "schedule" (freeze until the snapshot's PublishAt) | "draft" (promote as an unpublished draft).</param>
    /// <param name="ResetAcknowledgements">
    /// Only honoured for an explicit publish/update of an already-published post: when true the
    /// existing acknowledgements are cleared so the audience must confirm again.
    /// </param>
    Task<NewsPostResultDto> PromoteDraftAsync(Guid actorId, Guid draftId, PromoteNewsDraftRequest req, CancellationToken ct);

    /// <summary>
    /// Apply a frozen "scheduled" draft whose time has come. Re-checks the promoter's permission
    /// (the original author may have left or lost the right) and only then performs the same
    /// promotion. Returns the promoted draft, or null when it is not due / no longer authorized.
    /// </summary>
    Task<NewsDraftResultDto?> ApplyScheduledDraftAsync(Guid draftId, CancellationToken ct);

    /// <summary>
    /// Cancel a frozen schedule and hand the draft back as an editable "active" draft.
    /// Author-only, same as discard; Schedules are cancellable until the moment they are applied,
    /// so this is the way out of a mistaken schedule.
    /// </summary>
    Task<NewsDraftResultDto> ResumeScheduledDraftAsync(Guid actorId, Guid draftId, CancellationToken ct);
}