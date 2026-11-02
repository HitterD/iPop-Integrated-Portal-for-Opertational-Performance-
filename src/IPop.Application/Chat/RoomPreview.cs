using IPop.Application.Chat.Mentions;

namespace IPop.Application.Chat;

/// Room-list preview line. Shared by the server room list and the client's live bump so both
/// render the same text. Group rooms prefix the sender's first name ("Beny: tes", "You: ok");
/// system events, polls and deleted messages stay unprefixed.
public static class RoomPreview
{
    public static string Build(string roomType, string type, string content, bool isDeleted,
        string? senderName, bool isOwn, int max = 100, string? attachmentMime = null, int attachmentCount = 0)
    {
        if (isDeleted) return "This message was deleted";
        var body = type == "sticker" ? "🎨 Sticker"
            : content.StartsWith("[poll]", StringComparison.Ordinal) ? "📊 Poll"
            : content.StartsWith("[note]", StringComparison.Ordinal) ? "📄 Note"
            : IsAttachmentPlaceholder(content) ? FormatAttachmentPreview(content, attachmentMime, attachmentCount)
            : MentionPlainText.Project(content, max);
        if (roomType != "group" || type is "system" or "emergency") return body;
        var who = isOwn ? "You" : PersonName.Short(senderName);
        return string.IsNullOrEmpty(who) ? body : $"{who}: {body}";
    }

    internal static bool IsAttachmentPlaceholder(string? content) =>
        string.IsNullOrWhiteSpace(content)
        || content.Trim() == "(attachment)"
        || content.Trim() is "(attachment)" or "(voice)" or "[voice]";

    internal static string FormatAttachmentPreview(string? content, string? mime, int count)
    {
        var trimmed = content?.Trim();
        if (trimmed is "(voice)" or "[voice]" || mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            return "🎤 Voice message";
        if (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return count > 1 ? $"📷 {count} photos" : "📷 Photo";
        if (mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
            return count > 1 ? $"🎬 {count} videos" : "🎬 Video";
        if (count > 0 || !string.IsNullOrWhiteSpace(mime) || trimmed == "(attachment)")
            return count > 1 ? $"📎 {count} attachments" : "📎 Attachment";
        return "";
    }
}
