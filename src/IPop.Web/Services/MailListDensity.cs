namespace IPop.Web.Services;

/// Mail list density (Mail settings): Compact = 2 lines (sender + date / subject — preview), the default;
/// Comfortable = 3 lines (sender + date / subject / preview). Saved per user per device.
public static class MailListDensity
{
    public const string Key = "ipop.mail.density";
    public const string Compact = "compact";
    public const string Comfortable = "comfortable";

    public static string Parse(string? stored) => stored == Comfortable ? Comfortable : Compact;
}

/// What a quick reply or "Open full editor" carries: the message replied to, the typed text, the mode, attachments.
public sealed record MailReplyStart(MailDetail Target, string Text, bool All, IReadOnlyList<MailAttachment> Attachments);
