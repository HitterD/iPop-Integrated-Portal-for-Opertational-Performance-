using IPop.Application.Chat.Mentions;

namespace IPop.Application.Chat;

/// Text of a reply's quote. Shared by the server's ReplyPreview and the client's optimistic bubble
/// and composer bar, so a quote never shows raw "@[Name](u:id)" markup or half a mention.
public static class ReplyQuote
{
    public const int MaxLength = 150;

    public static string Text(string type, string content, bool isDeleted, int max = MaxLength,
        string? attachmentMime = null, int attachmentCount = 0)
    {
        if (isDeleted) return "Original message was deleted";
        if (type == "sticker") return "🎨 Sticker";
        if (content.StartsWith("[poll]", StringComparison.Ordinal)) return "📊 Poll";
        if (content.StartsWith("[note]", StringComparison.Ordinal)) return "📄 Note";
        if (RoomPreview.IsAttachmentPlaceholder(content))
        {
            var att = RoomPreview.FormatAttachmentPreview(content, attachmentMime, attachmentCount);
            if (!string.IsNullOrEmpty(att)) return att;
        }
        var plain = PlainWithNames(content);
        return plain.Length <= max ? plain : plain[..max] + "...";
    }

    /// Mentions as "@Name" in display casing ("@Alphin Santoso Hermanthan, S.T.").
    private static string PlainWithNames(string content)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in MentionParser.Parse(content))
            sb.Append(s switch
            {
                MentionSegment.Text t => t.Value,
                MentionSegment.User u => "@" + PersonName.Display(u.Name),
                MentionSegment.All => "@all",
                MentionSegment.Project p => "@" + p.Name,
                MentionSegment.Task t => "@" + t.Title,
                _ => "",
            });
        return sb.ToString();
    }
}
