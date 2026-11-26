using IPop.Application.Notifications;

namespace IPop.Api.Notifier;

/// Inbox messages from remote help (BIGIS #8 v3): Administrators when a PC locks after wrong passwords, and the
/// user after a session ends (who, how long, which files).
public static class RemoteNotices
{
    public static Task PcLockedAsync(INotificationGatewayService notifs, string machine, int minutes, CancellationToken ct) =>
        notifs.PublishInternalAsync("system", "remote.locked", $"{machine} locked after wrong passwords",
            $"Unattended access is locked for {minutes} minutes. Unlock it in Devices.", new NotificationTarget(Role: "Admin"), "high", null, ct);

    public static Task SummaryAsync(INotificationGatewayService notifs, Guid userId, string helper, TimeSpan? length, string mode, string? files, Guid sessionId, CancellationToken ct)
    {
        var list = (files ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var msg = $"{Length(length)} · {(mode == "view" ? "view only" : "control")}"
            + (list.Length > 0 ? $" · {list.Length} file{(list.Length > 1 ? "s" : "")}: {string.Join(", ", list)}" : " · no files");
        return notifs.PublishInternalAsync("system", "remote.summary", $"Remote help with {helper} ended", msg,
            new NotificationTarget(UserId: userId), null, "remote-summary-" + sessionId.ToString("N"), "normal", null, ct);
    }

    public static string Length(TimeSpan? d) => d is not { } t ? "—" : t.TotalSeconds < 60 ? $"{Math.Round(t.TotalSeconds)} s"
        : t.TotalHours < 1 ? $"{Math.Round(t.TotalMinutes)} min" : $"{(int)t.TotalHours} h {Math.Round(t.TotalMinutes % 60)} min";
}
