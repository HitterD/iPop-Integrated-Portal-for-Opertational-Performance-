using System.Text.RegularExpressions;

namespace IPop.Web.Services;

/// Pure formatters (no I/O, no JS).
public static class ChatFormatters
{
    /// Renders lightweight markdown formatting (bold, italic, code, strikethrough) and preserves linebreaks.
    public static string FormatMarkdown(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // 1. Extract code blocks ```...``` first to preserve raw content and linebreaks
        var codeBlocks = new List<string>();
        const string blockPlaceholder = "\x1ACODEBLOCK_";
        html = Regex.Replace(html, @"```(?:\w+)?\r?\n?(.*?)```", m =>
        {
            var code = m.Groups[1].Value;
            codeBlocks.Add($"<pre class=\"chat-code\"><code>{code}</code></pre>");
            return $"{blockPlaceholder}{codeBlocks.Count - 1}\x1A";
        }, RegexOptions.Singleline);

        // 2. Inline code `...`
        html = Regex.Replace(html, @"`([^`\r\n]+)`", "<code class=\"chat-inline-code\">$1</code>");

        // 3. Bold **...** or *...*
        html = Regex.Replace(html, @"(?<=^|[^\w])\*\*([^\*\r\n]+?)\*\*(?=[^\w]|$)", "<strong>$1</strong>");
        html = Regex.Replace(html, @"(?<=^|[^\w])\*([^\* \r\n][^\*\r\n]*?)\*(?=[^\w]|$)", "<strong>$1</strong>");

        // 4. Italic _..._
        html = Regex.Replace(html, @"(?<=^|[^\w])_([^_ \r\n][^_\r\n]*?)_(?=[^\w]|$)", "<em>$1</em>");

        // 5. Strikethrough ~...~
        html = Regex.Replace(html, @"(?<=^|[^\w])~([^~ \r\n][^~\r\n]*?)~(?=[^\w]|$)", "<del>$1</del>");

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
