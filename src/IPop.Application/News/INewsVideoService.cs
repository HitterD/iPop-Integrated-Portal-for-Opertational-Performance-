namespace IPop.Application.News;

/// Launch-style video announcements: upload creates a published "announcement" News post carrying
/// the video; the newest one with an open popup window pops up once per targeted reader.
public interface INewsVideoService
{
    /// Admin only. Video (and optional poster) are already on local disk as temp files; the service
    /// moves them into its store, creates the post and archives the video to the NAS.
    Task<NewsVideoResult> UploadAsync(Guid actorId, NewsVideoUpload upload, CancellationToken ct);

    Task<IReadOnlyList<NewsVideoAdminDto>> ListAsync(Guid actorId, CancellationToken ct);

    /// Null <paramref name="popupUntil"/> switches the popup off; the News post stays.
    Task<NewsVideoResult> SetPopupAsync(Guid actorId, Guid videoId, DateTime? popupUntil, CancellationToken ct);

    /// Deletes the post, the video row and every stored copy.
    Task<NewsVideoResult> DeleteAsync(Guid actorId, Guid videoId, CancellationToken ct);

    /// The popup this user still has to see, or null.
    Task<NewsVideoDto?> GetPendingPopupAsync(Guid userId, CancellationToken ct);

    /// Null when the user may not see the video's post.
    Task<NewsVideoDto?> GetAsync(Guid videoId, Guid userId, CancellationToken ct);

    /// kind: "seen" (popup shown) | "played" | "downloaded". Idempotent per user.
    Task<bool> TrackAsync(Guid userId, Guid videoId, string kind, CancellationToken ct);

    /// Local file for streaming; restores it from the NAS archive when the local copy is gone.
    Task<NewsVideoFile?> OpenFileAsync(Guid videoId, bool poster, CancellationToken ct);
}

public record NewsVideoUpload(
    string Title,
    string Description,
    string VideoTempPath,
    string FileName,
    string ContentType,
    int DurationSeconds,
    string? PosterTempPath,
    DateTime? PopupUntil,
    IReadOnlyList<NewsTargetInput>? Targets);

public record NewsVideoDto(
    Guid Id,
    Guid NewsPostId,
    string Title,
    string Description,
    string FileName,
    string ContentType,
    long Size,
    int DurationSeconds,
    bool HasPoster,
    DateTime PublishedAt,
    string AuthorName);

public record NewsVideoAdminDto(
    Guid Id,
    Guid NewsPostId,
    string Title,
    string FileName,
    long Size,
    int DurationSeconds,
    bool HasPoster,
    bool Archived,
    DateTime CreatedAt,
    DateTime? PopupUntil,
    bool PopupActive,
    IReadOnlyList<NewsTargetDto> Targets,
    int SeenCount,
    int PlayedCount,
    int DownloadedCount);

/// Small reference carried by News list/detail DTOs so the feed can render the video card.
public record NewsVideoBriefDto(Guid Id, int DurationSeconds, bool HasPoster, string FileName, long Size);

public record NewsVideoFile(string FullPath, string ContentType, string FileName);

public record NewsVideoResult(bool Success, NewsVideoAdminDto? Video, string? Error);

public static class NewsVideoLimits
{
    public const long MaxBytes = 500L * 1024 * 1024;
    public const long MaxPosterBytes = 5L * 1024 * 1024;
    public const int DefaultPopupDays = 14;
}
