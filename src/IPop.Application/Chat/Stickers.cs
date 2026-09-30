namespace IPop.Application.Chat;

public record StickerDto(Guid Id, bool IsOfficial, bool IsMine);
/// Recent and Favorites hold sticker keys (the message content: "custom:{id:N}", "fluent:{slug}"
/// or a legacy Lucide name), newest first, so they cover every kind of sticker.
public record StickerListDto(IReadOnlyList<StickerDto> Mine, IReadOnlyList<StickerDto> Official,
    IReadOnlyList<string> Recent, IReadOnlyList<string> Favorites);
public record StickerResultDto(bool Success, string? Error, StickerDto? Sticker = null);
public record StickerFile(string Server, string Path);

public interface IStickerService
{
    /// "My stickers" (newest first), the official pack, and my recent + favourite sticker keys.
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
    /// Stars / un-stars a sticker key. False when the key is not a valid sticker key.
    Task<bool> SetFavoriteAsync(Guid userId, string key, bool favorite, CancellationToken ct);
    /// Bumps a sticker key to the top of Recent (the chat send path does this for sticker messages).
    Task<bool> RecordUseAsync(Guid userId, string key, CancellationToken ct);
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
    public const string FluentPrefix = "fluent:";
    public const int MaxKeyLength = 80;
    public const int MaxRecent = 24;

    public static string Content(Guid id) => ContentPrefix + id.ToString("N");

    /// The sticker id of a "custom:{id}" sticker message, or null for a built-in (Lucide) sticker.
    public static Guid? CustomId(string? type, string? content) =>
        type == "sticker" && content is not null && content.StartsWith(ContentPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(content[ContentPrefix.Length..], "N", out var id) ? id : null;

    /// The slug of a "fluent:{slug}" sticker message (Fluent Emoji 3D default pack), else null.
    public static string? FluentSlug(string? type, string? content) =>
        type == "sticker" && content is not null && content.StartsWith(FluentPrefix, StringComparison.Ordinal)
        && IsSlug(content.AsSpan(FluentPrefix.Length), 60) ? content[FluentPrefix.Length..] : null;

    /// A sticker message's content: custom:{id:N}, fluent:{slug}, or a legacy Lucide icon name.
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return false;
        if (key.StartsWith(ContentPrefix, StringComparison.Ordinal))
            return Guid.TryParseExact(key[ContentPrefix.Length..], "N", out _);
        if (key.StartsWith(FluentPrefix, StringComparison.Ordinal))
            return IsSlug(key.AsSpan(FluentPrefix.Length), 60);
        return IsSlug(key, 40);
    }

    private static bool IsSlug(ReadOnlySpan<char> s, int max)
    {
        if (s.Length == 0 || s.Length > max) return false;
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')) return false;
        return true;
    }
}
