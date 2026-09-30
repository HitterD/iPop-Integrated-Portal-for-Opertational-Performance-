namespace IPop.Web.Models;

public record NewsVideoDto(
    Guid Id, Guid NewsPostId, string Title, string Description, string FileName, string ContentType,
    long Size, int DurationSeconds, bool HasPoster, DateTime PublishedAt, string AuthorName);

/// Video plus ticketed URLs (relative to the API base) the media element can load directly.
public record NewsVideoViewDto(NewsVideoDto Video, string StreamUrl, string? PosterUrl, string DownloadUrl);

public record NewsVideoAdminDto(
    Guid Id, Guid NewsPostId, string Title, string FileName, long Size, int DurationSeconds, bool HasPoster,
    bool Archived, DateTime CreatedAt, DateTime? PopupUntil, bool PopupActive, List<NewsTargetDto> Targets,
    int SeenCount, int PlayedCount, int DownloadedCount);

/// Small reference on News list/detail rows so the feed can render the video card.
public record NewsVideoBriefDto(Guid Id, int DurationSeconds, bool HasPoster, string FileName, long Size);

public record NewsVideoPick(string Name, long Size, int Duration, bool Readable, string? Poster, double At);

public record NewsVideoUploadResult(int Status, string Error);

public static class NewsVideoFormat
{
    public static string Duration(int s) => s <= 0 ? "" : $"{s / 60}:{s % 60:00}";

    public static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024d:0} KB",
        _ => $"{b} B",
    };
}
