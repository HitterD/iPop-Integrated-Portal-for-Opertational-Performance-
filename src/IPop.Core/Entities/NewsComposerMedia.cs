namespace IPop.Core.Entities;

/// <summary>
/// One uploaded asset owned by the composer (inline image, inline video, or a regular file).
///
/// <para>Images and files reuse the shared attachment store through <see cref="AttachmentId"/>
/// (area "news"), so they inherit the existing NAS service-account storage, local fallback and
/// "news" read policy. Videos are too large for that path and carry their own local stream copy
/// plus NAS archive metadata.</para>
///
/// <para>Content never stores a URL: it stores <c>data-news-media="&lt;Id&gt;"</c> and the delivery
/// layer mints a short-lived, access-rechecked URL at render time. <see cref="DeletedAt"/> is a
/// soft delete — the 24h cleanup refuses to drop anything still referenced by a live post or a
/// non-discarded draft.</para>
/// </summary>
public class NewsComposerMedia
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }

    /// Draft this asset was uploaded into. Lets the editor read an upload before the first autosave
    /// without the upload having to write a content reference (that join is owned by the versioned
    /// autosave, and an upload writing it would race the reference replacement). Null once the draft
    /// is gone; the asset itself is never deleted by the draft.
    public Guid? UploadDraftId { get; set; }

    /// "image" | "video" | "file"
    public required string Kind { get; set; }

    /// Shared-store link for image/file kinds (area "news"); null for videos.
    public Guid? AttachmentId { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }

    /// Video only: path relative to the local video root ("2026/10/{id}.mp4").
    public string? LocalPath { get; set; }
    /// Video only: NAS archive path; null while the archive copy is missing.
    public string? NasPath { get; set; }
    /// Video only: poster path relative to the local video root.
    public string? ThumbnailPath { get; set; }
    public int DurationSeconds { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public User Owner { get; set; } = default!;
    public NewsComposerDraft? UploadDraft { get; set; }
    public Attachment? Attachment { get; set; }
}