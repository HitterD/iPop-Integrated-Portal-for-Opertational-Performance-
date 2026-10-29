using IPop.Notifier.Core;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace IPop.Notifier.Windows;

/// Native Windows toasts: chat messages (inline Reply), modules set to "Toast", and iDesk ticket chat.
/// iDesk toasts come from their own Windows identity ("iDesk", iDesk icon, own Action Center group) so they never
/// look like iPop's. Clicking opens the page; Reply posts straight to the chat room or the ticket.
public static class Toasts
{
    public static string? Account { get; set; }
    public static void ClearAll()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { }
        try { ToastNotificationManager.History.Clear(IdeskAumid); } catch { }
    }
    public static event Action<string, string>? ChatReply;    // roomId, text
    public static event Action<string, string>? TicketReply;  // ticket ref (my ticket id or iDesk ticket id), text
    public static event Action<string>? Open;                 // iPop path
    public static event Action<string>? OpenIdesk;            // my ticket's id: open it on the iDesk site

    /// Second toast identity for iDesk. Registered per user under HKCU (no admin), activated through the same
    /// COM activator the toolkit registered for the app, so clicks and replies land here.
    public static string IdeskAumid => AppInfo.AppUserModelId + ".idesk";

    public static void Init()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (Account is null || !args.TryGetValue("account", out var account) || account != Account) return;
            args.TryGetValue("url", out var url);
            if (args.TryGetValue("action", out var action) && e.UserInput.TryGetValue("reply", out var text)
                && text is string t && t.Trim().Length > 0)
            {
                if (action == "reply" && args.TryGetValue("room", out var room)) { ChatReply?.Invoke(room, t.Trim()); return; }
                if (action == "ticket-reply" && args.TryGetValue("ticket", out var ticket)) { TicketReply?.Invoke(ticket, t.Trim()); return; }
            }
            if (!string.IsNullOrEmpty(url))
            {
                // "Open in iDesk" on the requester's own ticket: the iDesk site, not the iPop Tickets page.
                if (args.TryGetValue("site", out var site) && site == "idesk" && url.StartsWith("/tickets/") && TicketRefOf(url) is { } mine)
                    OpenIdesk?.Invoke(mine);
                else Open?.Invoke(url);
            }
        };
        RegisterIdesk();
    }

    private static Uri Logo => new(Path.Combine(AppInfo.AssetsDir, "ipop.png"));
    private static Uri IdeskLogo => new(Path.Combine(AppInfo.AssetsDir, "idesk.png"));

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
        if (n.Module == "tickets") { ShowTicket(n); return; }
        var b = new ToastContentBuilder()
            .AddArgument("url", n.TargetUrl ?? "/notifications")
            .AddAppLogoOverride(Logo, ToastGenericAppLogoCrop.Default)
            .AddAttributionText("iPop · " + ModuleName(n.Module))
            .AddText(n.Title, hintMaxLines: 1)
            .AddText(n.Body.Length > 200 ? n.Body[..200] + "…" : n.Body);
        Show(b, "n" + n.Id.ToString("N")[..16]);
    }

    /// Signed in from the browser (or moved to another account by it): always said out loud.
    public static void ShowSignedIn(string name, bool switched)
    {
        var b = new ToastContentBuilder()
            .AddArgument("url", "/notifications")
            .AddAppLogoOverride(Logo, ToastGenericAppLogoCrop.Default)
            .AddAttributionText("iPop Notifier")
            .AddText(switched ? $"iPop Notifier is now signed in as {name}" : $"Signed in as {name}", hintMaxLines: 2)
            .AddText(switched ? "You signed in to iPop with another account on this PC." : "Your iPop and iDesk notifications will appear here.");
        Show(b, "signin");
    }

    /// iDesk ticket notifications, from the "iDesk" identity. A chat message gets a reply box and "Open in iDesk";
    /// one toast per ticket, replaced by the next message (the title carries the running count).
    public static void ShowTicket(Notice n)
    {
        var ticket = TicketRefOf(n.TargetUrl);
        var chat = n.EventType == "ticket.message_created" && ticket is not null;
        var (person, text, number) = TicketText(n.Body);
        // Header = who (the PIC, or the reporter on the technician's side); body = what they said / what happened;
        // attribution = which ticket: "<problem title> · #number". Never a GUID.
        var b = new ToastContentBuilder()
            .AddArgument("url", n.TargetUrl ?? "/tickets")
            .AddAppLogoOverride(IdeskLogo, ToastGenericAppLogoCrop.Default)
            .AddAttributionText(number is null ? n.Title : $"{n.Title} · {number}")
            .AddText(person ?? n.Title, hintMaxLines: 1)
            .AddText(text.Length > 200 ? text[..200] + "…" : text);
        if (chat)
        {
            b.AddInputTextBox("reply", placeHolderContent: "Reply in the ticket chat…")
             .AddButton(new ToastButton().SetContent("Send").AddArgument("action", "ticket-reply").AddArgument("ticket", ticket!)
                 .AddArgument("url", n.TargetUrl ?? "/tickets").SetTextBoxId("reply"))
             .AddButton(new ToastButton().SetContent("Open in iDesk").AddArgument("url", n.TargetUrl ?? "/tickets").AddArgument("site", "idesk"));
        }
        // Same tag per ticket: a newer message replaces the toast instead of stacking another.
        var tag = chat ? "t" + Hash(ticket!) : "n" + n.Id.ToString("N")[..16];
        Show(b, tag, ideskIdentity: true);
    }

    /// Hides the toast of a ticket whose chat was just read or answered.
    public static void RemoveTicket(string ticketRef)
    {
        var tag = "t" + Hash(ticketRef);
        try { ToastNotificationManager.History.Remove(tag, "idesk", IdeskAumid); } catch { }
        try { ToastNotificationManagerCompat.History.Remove(tag, "idesk"); } catch { }
    }

    private static void Show(ToastContentBuilder b, string tag, bool ideskIdentity = false)
    {
        try
        {
            if (Account is null) return;
            b.AddArgument("account", Account);
            // Sound is played by the app (iPop's own sounds), so the toast itself stays silent.
            b.AddAudio(new ToastAudio { Silent = true });
            tag = tag.Length > 64 ? tag[..64] : tag;
            if (ideskIdentity && IdeskRegistered)
            {
                var toast = new ToastNotification(b.GetToastContent().GetXml()) { Tag = tag, Group = "idesk" };
                ToastNotificationManager.CreateToastNotifier(IdeskAumid).Show(toast);
                return;
            }
            b.Show(t => { t.Tag = tag; t.Group = ideskIdentity ? "idesk" : "ipop"; });
        }
        catch { /* notifications disabled for the app in Windows settings */ }
    }

    private static bool IdeskRegistered;

    /// HKCU\Software\Classes\AppUserModelId\{app}.idesk with DisplayName "iDesk", the iDesk icon and the app's own
    /// CustomActivator (copied from the toolkit's registration), so Windows shows "iDesk" and routes clicks here.
    private static void RegisterIdesk()
    {
        try
        {
            const string root = @"Software\Classes\AppUserModelId\";
            using var main = Registry.CurrentUser.OpenSubKey(root + AppInfo.AppUserModelId);
            if (main?.GetValue("CustomActivator") is not string activator) return;
            using var k = Registry.CurrentUser.CreateSubKey(root + IdeskAumid);
            k.SetValue("DisplayName", "iDesk");
            k.SetValue("IconUri", Path.Combine(AppInfo.AssetsDir, "idesk.png"));
            k.SetValue("IconBackgroundColor", "FFFFFFFF");
            k.SetValue("CustomActivator", activator);
            IdeskRegistered = true;
        }
        catch { IdeskRegistered = false; }
    }

    public static void UnregisterIdesk()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\" + IdeskAumid, throwOnMissingSubKey: false); } catch { }
    }

    /// Ticket notification body "Person · Role · Unit\nsentence\n#number" (TicketNotificationText on the server) →
    /// ("Person · your PIC", toast text, "#number"). A chat sentence "Rizky: “text”" becomes just the text (the header
    /// already says who) and its running count ("… · 3 new messages") moves to the header. Older bodies
    /// ("Ticket title\nWho: “text”") still read: no number, the first line as the header.
    internal static (string? Person, string Text, string? Number) TicketText(string body)
    {
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        string? number = lines.Count > 1 && lines[^1].StartsWith('#') ? lines[^1] : null;
        if (number is not null) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 2) return (null, lines.FirstOrDefault() ?? "", number);
        var parts = lines[0].Split(" · ");
        var person = parts.Length > 1 && parts[1] == "Your PIC" ? parts[0] + " · your PIC" : parts[0];
        var text = string.Join(" ", lines.Skip(1));
        var count = System.Text.RegularExpressions.Regex.Match(text, @" · (\d+) new (messages|replies)$");
        if (count.Success) { text = text[..count.Index]; person += $" · {count.Groups[1].Value} new"; }
        var quote = System.Text.RegularExpressions.Regex.Match(text, "^[^:“]{1,60}: “(.*)”$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (quote.Success) text = quote.Groups[1].Value;
        return (person, text, number);
    }

    /// The ticket a notification is about: my ticket (/tickets/{id}…) or, for the assigned agent, the iDesk ticket
    /// (/idesk/tickets/{id}). Both are what /api/tickets/{ref}/reply accepts.
    public static string? TicketRefOf(string? url)
    {
        if (url is null) return null;
        foreach (var prefix in new[] { "/tickets/", "/idesk/tickets/" })
            if (url.StartsWith(prefix) && url.Length >= prefix.Length + 36 && Guid.TryParse(url.AsSpan(prefix.Length, 36), out var id))
                return id.ToString();
        return null;
    }

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..16];

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
