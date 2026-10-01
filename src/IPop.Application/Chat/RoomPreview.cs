using IPop.Application.Chat.Mentions;

namespace IPop.Application.Chat;

/// Room-list preview line. Shared by the server room list and the client's live bump so both
/// render the same text. Group rooms prefix the sender's first name ("Beny: tes", "You: ok");
/// system events, polls and deleted messages stay unprefixed.
public static class RoomPreview
{
    public static string Build(string roomType, string type, string content, bool isDeleted,
        string? senderName, bool isOwn, int max = 100)
    {
        if (isDeleted) return "This message was deleted";
        var body = type == "sticker" ? "🎨 Sticker"
            : content.StartsWith("[poll]", StringComparison.Ordinal) ? "📊 Poll"
            : content.StartsWith("[note]", StringComparison.Ordinal) ? "📄 Note"
            : MentionPlainText.Project(content, max);
        if (roomType != "group" || type is "system" or "emergency") return body;
        var who = isOwn ? "You" : PersonName.Short(senderName);
        return string.IsNullOrEmpty(who) ? body : $"{who}: {body}";
    }
}
