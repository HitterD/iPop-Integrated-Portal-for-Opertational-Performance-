namespace IPop.Application.Chat;

public record StickerDto(Guid Id, bool IsOfficial, bool IsMine);
public record StickerListDto(IReadOnlyList<StickerDto> Mine, IReadOnlyList<StickerDto> Official);
public record StickerResultDto(bool Success, string? Error, StickerDto? Sticker = null);
public record StickerFile(string Server, string Path);

public interface IStickerService
{
    /// "Stiker saya" (newest first) and the official pack.
    Task<StickerListDto> ListAsync(Guid userId, CancellationToken ct);
    /// Registers an already-stored sticker file and puts it in the creator's collection.
    Task<StickerResultDto> CreateAsync(Guid userId, StickerFile file, long sizeBytes, CancellationToken ct);
    /// "Simpan stiker": adds someone else's sticker to my collection (idempotent).
    Task<StickerResultDto> SaveAsync(Guid userId, Guid stickerId, CancellationToken ct);
    /// Removes a sticker from my collection. The sticker itself stays: sent messages still show it.
    Task<bool> RemoveAsync(Guid userId, Guid stickerId, CancellationToken ct);
    /// Admin: promote to / withdraw from the official pack.
    Task<bool> SetOfficialAsync(Guid stickerId, bool official, CancellationToken ct);
    Task<StickerFile?> GetFileAsync(Guid stickerId, CancellationToken ct);
}

public static class StickerLimits
{
    /// Source uploads may be phone photos: the server fits and re-encodes them anyway.
    public const long MaxUploadBytes = 10L * 1024 * 1024;
    /// A chat image turned into a sticker is normalized anyway, so the source may be bigger.
    public const long MaxSourceBytes = 20L * 1024 * 1024;
    /// Stored stickers are Size×Size WebP (older ones are 256; they render the same).
    public const int Size = 512;
    /// Soft output targets: quality (and for animations, frame count) drops until the file fits.
    public const long TargetStaticBytes = 300 * 1024;
    public const long TargetAnimatedBytes = 1024 * 1024;
    public const int MaxPerUser = 200;
    public const string ContentPrefix = "custom:";

    public static string Content(Guid id) => ContentPrefix + id.ToString("N");

    /// The sticker id of a "custom:{id}" sticker message, or null for a built-in (Lucide) sticker.
    public static Guid? CustomId(string? type, string? content) =>
        type == "sticker" && content is not null && content.StartsWith(ContentPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(content[ContentPrefix.Length..], "N", out var id) ? id : null;
}
