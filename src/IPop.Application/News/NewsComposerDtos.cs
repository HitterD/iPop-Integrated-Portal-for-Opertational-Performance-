namespace IPop.Application.News;

// ─────────────────────────────────────────────────────────────────────────────
// News composer (private draft) contracts.
//
// A composer draft is a complete, private snapshot of what the author is editing.
// Autosave writes drafts only — it never touches the live NewsPost, so an unfinished
// edit can never change what employees read. Promotion (a follow-on step, not part of
// the phase-1 foundation) copies the snapshot onto the live post under a compare-and-swap
// on both the draft Version and the live ContentVersion.
// ─────────────────────────────────────────────────────────────────────────────

/// A snapshot of the composer form. Every field is optional: autosave accepts incomplete content,
/// and only promotion (publish) validates. <see cref="BodyFormat"/> decides how Body is rendered:
/// "plain" is escaped and pre-wrapped, "html" is sanitized through the News allow-list.
public record NewsDraftSnapshotDto(
    string? Type = null,
    string? Title = null,
    string? Body = null,
    string BodyFormat = "plain",
    bool RequiresAcknowledgement = false,
    DateTime? AckDueAt = null,
    bool IsPinned = false,
    DateTime? PublishAt = null,
    Guid? CoverMediaId = null,
    string CoverMode = "auto",
    IReadOnlyList<NewsTargetInput>? Targets = null,
    IReadOnlyList<Guid>? AttachmentIds = null,
    IReadOnlyList<NewsDraftMediaRefDto>? Media = null);

/// One inline/cover media reference in a snapshot. Ids only — never a URL.
public record NewsDraftMediaRefDto(Guid MediaId, string Role = "inline", int SortOrder = 0);

/// Private draft as returned to its owner (or HR/Admin). Carries the CAS token the client must send
/// back on the next save.
public record NewsDraftDto(
    Guid Id,
    Guid OwnerId,
    string OwnerName,
    Guid? PostId,
    string PostTitle,            // live post title when PostId is set (context for "My drafts")
    int? BaseContentVersion,
    int Version,
    string State,
    NewsDraftSnapshotDto Snapshot,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreateNewsDraftRequest(Guid? PostId = null);

/// Versioned autosave. <see cref="BaseVersion"/> is the draft Version the client last saw; the save
/// only applies when it still matches, so two tabs can never silently overwrite each other.
/// <see cref="ExpectedPostContentVersion"/> is the live post ContentVersion the client last saw
/// (revision drafts only): when the live post moved on, the save is refused so the author is told
/// before their work is based on stale content.
public record SaveNewsDraftRequest(
    int BaseVersion,
    NewsDraftSnapshotDto Snapshot,
    int? ExpectedPostContentVersion = null);

public record NewsDraftResultDto(bool Success, NewsDraftDto? Draft, string? Error);

public record NewsDraftListDto(IReadOnlyList<NewsDraftDto> Items);

/// Sanitized preview of a draft body. <see cref="Html"/> is always the canonical, sanitized markup
/// (the same pipeline the reader uses); <see cref="PlainText"/> is the plain-text derivation used
/// for feed summaries and notifications.
public record NewsPreviewDto(Guid DraftId, string Title, string Html, string PlainText, string BodyFormat);

// ── Promotion ────────────────────────────────────────────────────────────────

/// Promote a private draft onto the live post. One call for both cases: the service decides from the
/// draft's own <see cref="NewsDraftDto.PostId"/> whether to insert a new post or update the existing
/// one, so the client never chooses the code path.
public record PromoteNewsDraftRequest(
    int BaseVersion,
    int? ExpectedPostContentVersion = null,
    bool ResetAcknowledgements = false,
    string Mode = "publish");   // "publish" (live now) | "schedule" (live at the snapshot's PublishAt) | "draft"

/// Promotion result. Carries the live post so the caller can navigate to /news/{id}.
public record NewsPostResultDto(bool Success, NewsDetailDto? News, string? Error);
