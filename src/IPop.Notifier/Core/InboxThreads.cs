using System.Text.RegularExpressions;

namespace IPop.Notifier.Core;

/// The Inbox's thread rule in C# (twin of Ui/inbox-model.js threadKey + needsAction), so the tray tooltip and taskbar
/// badge count Needs action the way the Inbox shows it: one per conversation (chat room, ticket, project task), and
/// only while it is unread. Pure, no Windows parts (compiled into IPop.Tests).
public static partial class InboxThreads
{
    /// "chat:{room}", "ticket:{id}", "task:{id}", or null when the notification stands alone.
    public static string? ThreadKey(string? targetUrl)
    {
        var u = targetUrl ?? "";
        if (ChatUrl().Match(u) is { Success: true } c) return "chat:" + c.Groups[1].Value.ToLowerInvariant();
        if (TicketUrl().Match(u) is { Success: true } t) return "ticket:" + t.Groups[1].Value.ToLowerInvariant();
        if (TaskUrl().Match(u) is { Success: true } k) return "task:" + k.Groups[1].Value.ToLowerInvariant();
        return null;
    }

    /// Needs action threads among unread notifications: a thread counts once when any of its unread updates is
    /// in the act tier (the thread takes the highest tier of its unread updates).
    public static int CountNeedsAction(IEnumerable<(Guid Id, string? TargetUrl, bool Act)> unread) =>
        unread.Where(n => n.Act).Select(n => ThreadKey(n.TargetUrl) ?? "n:" + n.Id).Distinct().Count();

    [GeneratedRegex("^/chat/([0-9a-fA-F-]{36})")]
    private static partial Regex ChatUrl();
    [GeneratedRegex("^/(?:idesk/)?tickets/([0-9a-fA-F-]{36})")]
    private static partial Regex TicketUrl();
    [GeneratedRegex(@"^/projects/[0-9a-fA-F-]{36}\?(?:.*&)?task=([0-9a-fA-F-]{36})")]
    private static partial Regex TaskUrl();
}
