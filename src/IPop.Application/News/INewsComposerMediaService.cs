namespace IPop.Application.News;

/// <summary>
/// Composer media for the News full-page editor: inline images/videos, cover images and regular
/// files. One upload creates exactly one <c>NewsComposerMedia</c> asset owned by the author, always
/// scoped to the draft it was uploaded into.
///
/// <para><b>Authorization is reference-based and re-checked on every read.</b> A stored body never
/// carries a URL — it carries <c>data-news-media="&lt;id&gt;"</c> — and the delivery layer mints a
/// short-lived ticket naming (media, user, context). Every stream/poster request re-authorizes the
/// ticket against the *current* reference graph, so revoking a draft, changing a post's audience or
/// deactivating the user closes access immediately. A ticket is never persisted anywhere.</para>
///
/// <para><b>Ordering.</b> The uploaded asset is not in the draft snapshot until the next autosave,
/// so the upload itself creates the draft ↔ media association (role "inline") in the shared join
/// table. That makes the asset readable in the editor right away and keeps every later request
/// reference-based. The draft autosave owns the join rows from then on, and an owned-but-unreferenced
/// asset stays readable to its uploader inside an authorized draft for the narrow window where a save
/// has cleared the reference but the next save has not re-added it.</para>
/// </summary>
public interface INewsComposerMediaService
{
    /// Upload one inline image, inline video or regular file into <paramref name="upload"/>.DraftId.
    /// Requires an edit-context draft (owner + still entitled) and an active user. The file is
    /// signature-checked, size-checked and stored; images/files go through the shared "news"
    /// attachment store, videos get a local streaming copy plus a NAS archive. Fails with
    /// "video_limit" when the draft or its live post already references a video.
    Task<NewsComposerMediaResult> UploadAsync(Guid actorId, NewsComposerMediaUpload upload, CancellationToken ct);

    /// <summary>
    /// Cheap pre-authorization so a request handler can reject an upload *before* reading its form
    /// body into memory/temp (the body may be 100 MB). Returns null when the actor may upload into the
    /// draft, otherwise the same error code <see cref="UploadAsync"/> would return. Authorization is
    /// intentionally re-run inside <see cref="UploadAsync"/>, which is the authority.
    /// </summary>
    Task<string?> AuthorizeUploadAsync(Guid actorId, Guid draftId, CancellationToken ct);

    /// Re-authorize a draft's media and return its metadata so the caller can mint a fresh URL
    /// (editor reload/reconnect). Null when the actor may not read it.
    Task<NewsComposerMediaResult?> GetForDraftAsync(Guid actorId, Guid draftId, Guid mediaId, CancellationToken ct);

    /// Authorized manifest for one draft: every live asset reachable from it, with fresh metadata so
    /// the caller can mint per-asset tickets. Null when the actor may not read the draft.
    Task<IReadOnlyList<NewsComposerMediaDto>?> ListDraftMediaAsync(Guid actorId, Guid draftId, CancellationToken ct);

    /// Authorized manifest for one live post: every asset a reader of that post may render. Null when
    /// the actor may not see the post.
    Task<IReadOnlyList<NewsComposerMediaDto>?> ListPostMediaAsync(Guid actorId, Guid postId, CancellationToken ct);

    /// Re-authorize a ticket's (media, user, context) triple against the *current* state and open the
    /// bytes. Null = denied, missing or soft-deleted. <paramref name="contextKind"/> is "draft" or
    /// "post"; <paramref name="poster"/> only applies to videos.
    Task<NewsComposerMediaFile?> OpenAsync(
        Guid mediaId, Guid userId, string contextKind, Guid contextId, bool poster, CancellationToken ct);

    /// Best-effort NAS archive of an uploaded video's local streaming copy. Runs after the upload
    /// response so a large transfer never blocks the editor; a failure leaves NasPath null and the
    /// local copy keeps serving it. True when the archive copy exists afterwards.
    Task<bool> ArchiveVideoAsync(Guid mediaId, CancellationToken ct);

    /// Delete composer media that has been unreferenced for at least the request's minimum age.
    /// Conservative by construction: never removes anything referenced by a live post or an active
    /// draft, never touches a file a post attachment still points at, and re-checks references
    /// immediately before each delete.
    Task<NewsComposerMediaCleanupResult> CleanupAsync(NewsComposerMediaCleanupRequest req, CancellationToken ct);
}

/// <param name="DraftId">Draft the upload belongs to (the id in the URL path, never a post id).</param>
/// <param name="Kind">"image" | "video" | "file".</param>
/// <param name="TempPath">Already-spooled upload on local disk; the service consumes or ignores it.</param>
/// <param name="DurationSeconds">Client-reported video duration (0 when unknown).</param>
public record NewsComposerMediaUpload(
    Guid DraftId,
    string Kind,
    string TempPath,
    string FileName,
    string? ContentType,
    int DurationSeconds = 0);

/// <summary>
/// Upload result / editor metadata. <see cref="StreamUrl"/> and <see cref="PosterUrl"/> are ephemeral
/// and minted by the API layer (tickets live there, never in Infrastructure); Infrastructure callers
/// see them null.
/// </summary>
public record NewsComposerMediaDto(
    Guid MediaId,
    string Kind,
    string FileName,
    string ContentType,
    long Size,
    int DurationSeconds,
    bool HasPoster,
    string ContextKind,
    Guid ContextId,
    string? StreamUrl = null,
    string? PosterUrl = null);

public record NewsComposerMediaResult(bool Success, NewsComposerMediaDto? Media, string? Error);

/// <summary>Exactly one of <see cref="FullPath"/> (local video, range-capable) or <see cref="Content"/> (store stream).</summary>
public sealed record NewsComposerMediaFile(string? FullPath, Stream? Content, string ContentType, string FileName);

/// <param name="MinAge">Only assets older than this are eligible (defaults to 24h).</param>
/// <param name="BatchSize">Upper bound per pass, so a worker never holds a long transaction.</param>
/// <param name="DryRun">Report what would be deleted without deleting or mutating rows.</param>
/// <param name="IncludeBareAttachments">
/// Also reclaim "news"-area <c>Attachment</c> rows that no composer media row owns (files uploaded
/// through the legacy <c>POST /api/news/attachments</c> path and then abandoned). They are the same
/// kind of orphan as an unlinked composer upload, just without a <c>NewsComposerMedia</c> row.
/// </param>
public record NewsComposerMediaCleanupRequest(
    TimeSpan MinAge, int BatchSize = 500, bool DryRun = false, bool IncludeBareAttachments = true)
{
    public static NewsComposerMediaCleanupRequest Default { get; } =
        new(NewsComposerMediaLimits.UnreferencedTtl);
}

public record NewsComposerMediaCleanupResult(int Scanned, int Deleted, int Retained, int Failed);

public static class NewsComposerMediaLimits
{
    /// Inline/cover images: jpg, png, gif, webp.
    public const long MaxImageBytes = 5L * 1024 * 1024;
    /// One inline video per draft/post: mp4, webm.
    public const long MaxVideoBytes = 100L * 1024 * 1024;
    /// Regular attachments (through the shared store) keep the store's own 25 MB limit.
    public static readonly TimeSpan UnreferencedTtl = TimeSpan.FromHours(24);

    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    public static readonly IReadOnlySet<string> VideoExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm" };

    public static readonly IReadOnlySet<string> FileExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The existing News regular-attachment office list, plus the images the composer also
            // accepts inline. No archives/executables: that would be an unapproved widening.
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".txt",
            ".png", ".jpg", ".jpeg", ".gif", ".webp",
        };
}