using System.Text.RegularExpressions;
using IPop.Application.Chat;

namespace IPop.Web.Services;

/// Pure formatters (no I/O, no JS).
public static class ChatFormatters
{
    /// How long a gap may be before a same-sender run is broken into a new block.
    /// 5 minutes matches Slack's default; long enough to keep a burst together,
    /// short enough that a reply hours later still gets its own header.
    public const double GroupWindowMinutes = 5;

    /// True when <paramref name="msg"/> continues <paramref name="prev"/>'s run: same sender,
    /// same local day, inside the window, neither is a system/emergency row, and the unread
    /// divider doesn't sit between them.
    public static bool ContinuesRun(MessageDto? prev, MessageDto msg, Guid? unreadDividerMsgId) =>
        prev is not null
        && prev.SenderNik == msg.SenderNik
        && msg.Id != unreadDividerMsgId
        && msg.Type is not ("system" or "emergency")
        && prev.Type is not ("system" or "emergency")
        && prev.CreatedAt.ToLocalTime().Date == msg.CreatedAt.ToLocalTime().Date
        && (msg.CreatedAt - prev.CreatedAt).TotalMinutes < GroupWindowMinutes;

    /// Position inside a run, for corner shaping and the tail on the run's last bubble.
    public static string RunClass(bool continuesPrev, bool continuesNext) => (continuesPrev, continuesNext) switch
    {
        (false, false) => "grp-single",
        (false, true) => "grp-first",
        (true, true) => "grp-mid",
        (true, false) => "grp-last",
    };

    /// Messages of only 1–3 emoji render big without a bubble; returns that count, else 0.
    /// Whitespace between emoji is allowed; any other character (text, digits) disqualifies.
    public static int JumboEmojiCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return 0;
        var count = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(text.Trim());
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            if (string.IsNullOrWhiteSpace(el)) continue;
            if (!IsEmojiElement(el) || ++count > 3) return 0;
        }
        return count;
    }

    private static bool IsEmojiElement(string el)
    {
        var pictographic = false;
        var keycap = el.Contains('⃣');
        foreach (var r in el.EnumerateRunes())
        {
            var v = r.Value;
            if (v is 0x200D or 0xFE0F or 0xFE0E or 0x20E3 || v is >= 0x1F3FB and <= 0x1F3FF || v is >= 0xE0020 and <= 0xE007F) continue;
            if (keycap && (v is '#' or '*' || v is >= '0' and <= '9')) { pictographic = true; continue; }
            if (v is >= 0x1F000 and <= 0x1FAFF   // pictographs, emoticons, transport, flags (regional indicators)…
                || v is >= 0x2600 and <= 0x27BF  // misc symbols, dingbats
                || v is >= 0x2B00 and <= 0x2BFF  // ⭐ ⬛ arrows
                || v is >= 0x2190 and <= 0x21FF || v is >= 0x2300 and <= 0x23FF
                || v is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299
                || v is >= 0x25A0 and <= 0x25FF || v is >= 0x2900 and <= 0x297F)
            { pictographic = true; continue; }
            return false;
        }
        return pictographic;
    }

    // Shared instances: Regex.Replace(string pattern) re-parses through a small static cache
    // that every chat render was churning. (Compiled is moot on WASM — no JIT.)
    private static readonly Regex RxCodeBlock = new(@"```(?:\w+)?\r?\n?(.*?)```", RegexOptions.Singleline);
    private static readonly Regex RxInlineCode = new(@"`([^`\r\n]+)`");
    private static readonly Regex RxBold2 = new(@"(?<=^|[^\w])\*\*([^\*\r\n]+?)\*\*(?=[^\w]|$)");
    private static readonly Regex RxBold1 = new(@"(?<=^|[^\w])\*([^\* \r\n][^\*\r\n]*?)\*(?=[^\w]|$)");
    private static readonly Regex RxItalic = new(@"(?<=^|[^\w])_([^_ \r\n][^_\r\n]*?)_(?=[^\w]|$)");
    private static readonly Regex RxStrike = new(@"(?<=^|[^\w])~([^~ \r\n][^~\r\n]*?)~(?=[^\w]|$)");

    /// Renders lightweight markdown formatting (bold, italic, code, strikethrough) and preserves linebreaks.
    public static string FormatMarkdown(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // 1. Extract code blocks ```...``` first to preserve raw content and linebreaks
        var codeBlocks = new List<string>();
        const string blockPlaceholder = "\x1ACODEBLOCK_";
        html = RxCodeBlock.Replace(html, m =>
        {
            var code = m.Groups[1].Value;
            codeBlocks.Add($"<pre class=\"chat-code\"><code>{code}</code></pre>");
            return $"{blockPlaceholder}{codeBlocks.Count - 1}\x1A";
        });

        // 2. Inline code `...`
        html = RxInlineCode.Replace(html, "<code class=\"chat-inline-code\">$1</code>");

        // 3. Bold **...** or *...*
        html = RxBold2.Replace(html, "<strong>$1</strong>");
        html = RxBold1.Replace(html, "<strong>$1</strong>");

        // 4. Italic _..._
        html = RxItalic.Replace(html, "<em>$1</em>");

        // 5. Strikethrough ~...~
        html = RxStrike.Replace(html, "<del>$1</del>");

        // 6. Convert newlines to <br /> (outside code blocks)
        html = html.Replace("\r\n", "\n").Replace("\n", "<br />");

        // 7. Reinsert code blocks
        for (int i = 0; i < codeBlocks.Count; i++)
        {
            html = html.Replace($"{blockPlaceholder}{i}\x1A", codeBlocks[i]);
        }

        return html;
    }
    /// Requirement 14.4 — waktu relatif balasan thread.
    public static string FormatRelative(DateTime threadTimeUtc, DateTime nowUtc)
    {
        var delta = nowUtc - threadTimeUtc;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d";
        return threadTimeUtc.ToLocalTime().ToString("d MMM yyyy");
    }

    /// Requirement 14.1 — thread reply button predicate.
    public static bool CanSendThreadReply(string? content)
        => !string.IsNullOrWhiteSpace(content?.Trim()) && content!.Trim().Length <= 4000;

    /// Requirement 17.8 — emergency broadcast predicate.
    public static bool CanSendEmergency(string? content)
    {
        var t = content?.Trim() ?? "";
        return t.Length >= 5 && t.Length <= 4000;
    }

    /// Requirement 16.x — create group button predicate.
    public static bool CanCreateGroup(string? name, int selectedCount)
    {
        if (selectedCount == 1) return true; // will create DM, label "Start DM"
        if (selectedCount < 2 || selectedCount > 255) return false;
        return !string.IsNullOrWhiteSpace(name?.Trim());
    }

    public static string CreateGroupLabel(int selectedCount)
        => selectedCount == 1 ? "Start DM" : "Create group";

    /// Requirement 3.10 / 18.x — content length guards.
    public static bool ExceedsContentLimit(string content, int limit = 4000)
        => content.Length > limit;

    /// Requirement 17.2 — Toast message truncation.
    public static string TruncateToast(string message, int limit = 200)
        => message.Length <= limit ? message : message[..limit];
}
