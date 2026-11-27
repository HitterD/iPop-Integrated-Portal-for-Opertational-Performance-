using System.Net.Http.Json;
using IPop.Application.Tickets;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Web;

namespace IPop.Web.Pages;

public partial class Notifications
{
    private sealed record DetailMsg(string Name, bool Mine, string Text, DateTime At);
    private sealed record TicketFacts(string? Number, string Status, string? Priority, string? Assignee);
    private sealed record DetailData(Guid Id, IReadOnlyList<DetailMsg>? Messages, TicketFacts? Ticket, string? UnavailableNote = null);

    private DetailData? detail;
    private bool detailLoading;
    private int detailSeq;
    private string replyText = "";
    private bool replySending;

    private static string? TargetOf(NotificationItemDto item) => !string.IsNullOrEmpty(item.TargetUrl) ? item.TargetUrl : item.Category switch
    {
        "mention" or "chat" => "/chat",
        "mail" => "/mail",
        "task" => "/projects",
        "meeting" => item.EventType == "call.missed" ? "/calls?tab=recents" : "/calls?tab=scheduled",
        "news" => "/news",
        "tickets" => "/tickets",
        _ => null
    };

    private static string OpenLabel(NotificationItemDto n)
    {
        var u = n.TargetUrl ?? "";
        return n.Category switch
        {
            "chat" or "mention" when u.StartsWith("/notes") => "Open note",
            "chat" or "mention" => "Open in Chat",
            "mail" => "Read in Mail",
            "tickets" => "Open ticket",
            "task" => "Open task",
            "meeting" when u.Contains("join=") => "Join meeting",
            "meeting" when u.StartsWith("/calendar") => "Open in Calendar",
            "meeting" => "Open in Calls & Meetings",
            "news" => n.EventType.StartsWith("memo.") ? "Read full memo" : "Read in News",
            _ when u.StartsWith("/mail") => "Open in Mail",
            _ when u.StartsWith("/notes") => "Open note",
            _ => "Open in iPop",
        };
    }

    private static string TicketStatusClass(string status) => status.ToLowerInvariant() switch
    {
        "resolved" or "closed" => "g",
        "new" or "open" or "reopened" => "o",
        _ => "o",
    };

    private static Guid? ChatRoomOf(string? url) =>
        url is not null && url.StartsWith("/chat/") && url.Length >= 42 && Guid.TryParse(url.AsSpan(6, 36), out var id) ? id : null;

    /// My ticket (/tickets/{id}) or, as the assigned agent, the iDesk ticket (/idesk/tickets/{id}).
    private static (string Ref, bool Own)? TicketOf(string? url)
    {
        if (url is null) return null;
        var path = url.Split('?', '#')[0];
        foreach (var (prefix, own) in new[] { ("/tickets/", true), ("/idesk/tickets/", false) })
            if (path.StartsWith(prefix) && Guid.TryParse(path[prefix.Length..], out var id)) return (id.ToString(), own);
        return null;
    }

    private static bool CanReply(NotificationItemDto n) =>
        (n.Category is "chat" or "mention" && ChatRoomOf(n.TargetUrl) is not null && n.EventType != "chat.emergency")
        || (n.Category == "tickets" && TicketOf(n.TargetUrl) is not null);

    private static string ReplyPlaceholder(NotificationItemDto n) => n.Category == "tickets"
        ? "Reply in the ticket…"
        : $"Reply to {(n.EventType == "chat.message" ? FirstName(RowTitle(n)) : n.ActorName is { } a ? FirstName(a) : "the chat")}…";

    private static string FirstName(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? s;

    private async Task LoadDetailAsync(NotificationItemDto n)
    {
        var seq = ++detailSeq;
        detail = null;
        if (!(n.Category is "chat" or "mention" || n.Category == "tickets")) return;
        detailLoading = true;
        try
        {
            DetailData? d = null;
            if (ChatRoomOf(n.TargetUrl) is { } room && n.Category is "chat" or "mention")
            {
                var page = await Auth.GetAsync<PagedResult<MessageDto>>($"/api/chat/rooms/{room}/messages?page=1&pageSize=6");
                var me = Auth.Me?.Nik;
                var msgs = (page?.Items ?? []).Where(m => !m.IsDeleted && m.Type != "system" && m.ThreadParentId == null)
                    .OrderBy(m => m.CreatedAt).TakeLast(5)
                    .Select(m => new DetailMsg(m.SenderName, me is not null && m.SenderNik == me,
                        IPop.Application.Chat.Mentions.MentionPlainText.Project(m.Content ?? "", 400) is { Length: > 0 } t ? t
                            : m.Attachments.Count > 0 ? "📎 Attachment" : m.Type == "sticker" ? "🎨 Sticker" : "",
                        m.CreatedAt))
                    .ToList();
                d = new DetailData(n.Id, msgs, null);
            }
            else if (TicketOf(n.TargetUrl) is { Own: true } ownRef)
            {
                var tk = await Auth.GetAsync<TicketDetailDto>($"/api/tickets/{ownRef.Ref}");
                var tm = await Auth.GetAsync<TicketMessagesDto>($"/api/tickets/{ownRef.Ref}/messages?peek=1");
                var msgs = (tm?.Messages ?? []).TakeLast(5).Select(m => new DetailMsg(m.Mine ? "You" : m.Sender ?? "ICT", m.Mine, m.Content, m.CreatedAt)).ToList();
                d = new DetailData(n.Id, msgs.Count > 0 ? msgs : null,
                    tk is null ? null : new TicketFacts(tk.TicketNumber is { } num ? "#" + num.TrimStart('#') : TicketNumber(n), Humanize(tk.Status), Humanize(tk.Priority), tk.Assignee?.Name));
            }
            else if (TicketOf(n.TargetUrl) is { Own: false } agentRef)
            {
                d = await LoadIdeskAgentDetailAsync(n, agentRef.Ref);
            }
            if (seq == detailSeq) detail = d;
        }
        catch
        {
            if (seq == detailSeq && TicketOf(n.TargetUrl) is { Own: false })
                detail = new DetailData(n.Id, null, null, "Ticket details and conversation preview aren’t available right now.");
        }
        finally
        {
            if (seq == detailSeq) { detailLoading = false; StateHasChanged(); }
        }
    }

    private async Task<DetailData> LoadIdeskAgentDetailAsync(NotificationItemDto n, string ideskTicketId)
    {
        using var res = await Auth.SendAsync(HttpMethod.Get, $"/api/tickets/idesk/{ideskTicketId}/preview", null);
        if (!res.IsSuccessStatusCode)
            return new DetailData(n.Id, null, null, "Ticket details and conversation preview aren’t available right now.");

        var preview = await res.Content.ReadFromJsonAsync<IdeskTicketPreviewDto>();
        if (preview is null)
            return new DetailData(n.Id, null, null, "Ticket details and conversation preview aren’t available right now.");

        var facts = new TicketFacts(
            preview.TicketNumber is { } num ? "#" + num.TrimStart('#') : TicketNumber(n),
            Humanize(preview.Status),
            Humanize(preview.Priority),
            preview.AssigneeName);

        var msgs = (preview.Messages ?? [])
            .TakeLast(5)
            .Select(m => new DetailMsg(
                m.Mine ? "You" : m.Sender ?? "Requester",
                m.Mine,
                !string.IsNullOrWhiteSpace(m.Content) ? m.Content : m.Attachments.Count > 0 ? "📎 " + string.Join(", ", m.Attachments) : "",
                m.CreatedAt))
            .ToList();

        var note = preview.MessagesUnavailable ? "Conversation preview isn’t available right now." : null;
        return new DetailData(n.Id, msgs.Count > 0 ? msgs : null, facts, note);
    }

    private static string Humanize(string? s) => string.IsNullOrWhiteSpace(s) ? "" :
        char.ToUpperInvariant(s[0]) + s[1..].Replace('_', ' ').Replace('-', ' ');

    private async Task ReplyKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey) await SendReply();
    }

    private async Task SendReply()
    {
        var n = selectedItem;
        var text = replyText.Trim();
        if (n is null || text.Length == 0 || replySending) return;
        replySending = true;
        try
        {
            HttpResponseMessage res;
            if (n.Category == "tickets" && TicketOf(n.TargetUrl) is { } t)
                res = await Auth.SendAsync(HttpMethod.Post, $"/api/tickets/{t.Ref}/reply", new { content = text });
            else if (ChatRoomOf(n.TargetUrl) is { } room)
            {
                res = await Auth.SendAsync(HttpMethod.Post, $"/api/chat/rooms/{room}/messages", new { content = text, idempotencyKey = Guid.NewGuid() });
                if (res.IsSuccessStatusCode) _ = Notifs.MarkReadByTargetAsync("chat", room);
            }
            else return;

            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show(n.Category == "tickets" ? "Your reply wasn’t sent. Open the ticket to try again." : "Your reply wasn’t sent. Try again.", ToastKind.Error);
                return;
            }
            replyText = "";
            var to = n.Category == "tickets" ? "the ticket" : n.EventType == "chat.message" ? FirstName(RowTitle(n)) : n.ActorName ?? "the chat";
            Toasts.ShowDetailed("Sent", $"Your reply went to {to}.", ToastKind.Success, "send");
            if (!n.IsRead) await MarkReadAsync(n.Id);
            await LoadDetailAsync(n);
        }
        catch { Toasts.Show("Your reply wasn’t sent. Try again.", ToastKind.Error); }
        finally { replySending = false; }
    }

    /// Recents has the call button per person (the inbox never carries anyone's NIK).
    private async Task CallBack(NotificationItemDto n)
    {
        if (!n.IsRead) await MarkReadAsync(n.Id);
        Nav.NavigateTo("/calls?tab=recents");
    }

    private void NavigateToTarget(NotificationItemDto item)
    {
        if (TargetOf(item) is { } target) Nav.NavigateTo(target);
    }

    private record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
    private record TicketDetailDto(Guid Id, string? TicketNumber, string Status, string Priority, TicketPerson? Assignee);
    private record TicketPerson(string Name);
    private record TicketMessagesDto(IReadOnlyList<TicketMessage> Messages);
    private record TicketMessage(string Id, string? Sender, bool Mine, string Content, DateTime CreatedAt);
    private record MessageDto(Guid Id, string SenderName, string SenderNik, string Type, string? Content, DateTime CreatedAt, bool IsDeleted,
        Guid? ThreadParentId, IReadOnlyList<object> Attachments);
}
