using IPop.Notifier.Core;
using Microsoft.Toolkit.Uwp.Notifications;

namespace IPop.Notifier.Windows;

/// Native Windows toasts with the iPop logo: chat messages (inline Reply) and modules set to "Toast".
/// Clicking opens the page in iPop; Reply posts straight to the chat room.
public static class Toasts
{
    public static event Action<string, string>? ChatReply;   // roomId, text
    public static event Action<string>? Open;                // iPop path

    public static void Init()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            args.TryGetValue("url", out var url);
            if (args.TryGetValue("action", out var action) && action == "reply"
                && args.TryGetValue("room", out var room) && e.UserInput.TryGetValue("reply", out var text)
                && text is string t && t.Trim().Length > 0)
            {
                ChatReply?.Invoke(room, t.Trim());
                return;
            }
            if (!string.IsNullOrEmpty(url)) Open?.Invoke(url);
        };
    }

    private static Uri Logo => new(Path.Combine(AppInfo.AssetsDir, "ipop.png"));

    public static void ShowChat(ToastPayload t)
    {
        var room = RoomOf(t.Url);
        var b = new ToastContentBuilder()
            .AddArgument("url", t.Url ?? "/chat")
            .AddAppLogoOverride(Logo, ToastGenericAppLogoCrop.Default)
            .AddAttributionText("iPop · Chat")
            .AddText(t.Title, hintMaxLines: 1)
            .AddText(t.Body);
        if (room is not null)
        {
            b.AddInputTextBox("reply", placeHolderContent: "Type a reply…")
             .AddButton(new ToastButton().SetContent("Send").AddArgument("action", "reply").AddArgument("room", room)
                 .AddArgument("url", t.Url ?? "/chat").SetTextBoxId("reply"));
        }
        if (t.RequireInteraction) b.SetToastScenario(ToastScenario.Reminder);
        Show(b, t.Tag);
    }

    /// A module set to "Toast" in Settings (short form; the full message is in the Inbox / iPop).
    public static void ShowNotice(Notice n, Uri server)
    {
        var b = new ToastContentBuilder()
            .AddArgument("url", n.TargetUrl ?? "/notifications")
            .AddAppLogoOverride(Logo, ToastGenericAppLogoCrop.Default)
            .AddAttributionText("iPop · " + ModuleName(n.Module))
            .AddText(n.Title, hintMaxLines: 1)
            .AddText(n.Body.Length > 200 ? n.Body[..200] + "…" : n.Body);
        Show(b, "n" + n.Id.ToString("N")[..16]);
    }

    private static void Show(ToastContentBuilder b, string tag)
    {
        try
        {
            // Sound is played by the app (iPop's own sounds), so the toast itself stays silent.
            b.AddAudio(new ToastAudio { Silent = true });
            b.Show(t => { t.Tag = tag.Length > 64 ? tag[..64] : tag; t.Group = "ipop"; });
        }
        catch { /* notifications disabled for the app in Windows settings */ }
    }

    public static string? RoomOf(string? url) =>
        url is not null && url.StartsWith("/chat/") && url.Length >= 42 && Guid.TryParse(url.AsSpan(6, 36), out var id) ? id.ToString() : null;

    public static string ModuleName(string module) => module switch
    {
        "mail" => "Mail",
        "tickets" => "iDesk",
        "calendar" => "Calendar",
        "chat" => "Chat",
        "projects" => "Projects",
        "news" => "News",
        "notes" => "Notes",
        "calls" => "Calls",
        _ => "System",
    };
}
