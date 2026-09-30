namespace IPop.Core.Entities;

/// Launch-style video announcement: the video behind one "announcement" NewsPost. The NAS copy is
/// the archive; the API server keeps a local copy so the player can stream with range/seek.
/// While PopupUntil is in the future the newest such video pops up once for each targeted reader.
public class NewsVideo
{
    public Guid Id { get; set; }
    public Guid NewsPostId { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public int DurationSeconds { get; set; }

    /// Path relative to the local video root ("2026/09/{id}.mp4").
    public required string LocalPath { get; set; }
    /// NAS archive path; null while the NAS copy is missing (upload failed or not configured).
    public string? NasPath { get; set; }
    /// Poster image relative to the local video root; null = none (the player shows the first frame).
    public string? ThumbnailPath { get; set; }

    /// Popup window end; null = popup switched off (the News post stays).
    public DateTime? PopupUntil { get; set; }

    public Guid UploaderId { get; set; }
    public DateTime CreatedAt { get; set; }

    public NewsPost NewsPost { get; set; } = default!;
    public ICollection<NewsVideoView> Views { get; set; } = [];
}

/// One row per reader per video: when they saw the popup, first pressed play, and downloaded.
public class NewsVideoView
{
    public Guid NewsVideoId { get; set; }
    public Guid UserId { get; set; }
    public DateTime? PopupSeenAt { get; set; }
    public DateTime? PlayedAt { get; set; }
    public DateTime? DownloadedAt { get; set; }

    public NewsVideo NewsVideo { get; set; } = default!;
    public User User { get; set; } = default!;
}
