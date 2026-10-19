namespace IPop.Notifier.Core;

/// What the server pushes on /hubs/notification "notificationReceived" (NotificationDeliveryState).
public sealed record Delivery(Guid RequestId, Guid UserId, string Status, string? Channel, int AttemptCount,
    DateTime? DeliveredAt, DateTime? ReadAt, DateTime? AcknowledgedAt,
    string? Title, string? Body, string? EventType, string? Priority, string? TargetUrl);

/// Chat toast ("desktopToast"): same shape as the browser's Web Push payload.
public sealed record ToastPayload(string Kind, string Title, string Body, string? Url, string Tag, bool RequireInteraction = false);

/// One row of /api/notifications/v1/inbox.
public sealed record InboxItem(Guid Id, string Type, string Title, string Body, string Source, DateTime At, bool IsRead,
    string Priority, string? TargetUrl = null, DateTime? SnoozedUntil = null);

public sealed record InboxPage(List<InboxItem> Items, int TotalCount, int UnreadCount, int Page, int PageSize);

/// A notification as the app handles it (live or caught up after being offline).
public sealed record Notice(Guid Id, string Module, string EventType, string Title, string Body, string Priority, string? TargetUrl, DateTime At)
{
    public bool Urgent => Priority is "high" or "urgent" or "critical" || EventType is "meeting.reminder" or "chat.emergency" or "memo.mandatory";

    public static Notice From(Delivery d) => new(d.RequestId, NotifierSettings.ModuleOf(d.EventType), d.EventType ?? "system",
        d.Title ?? "iPop", d.Body ?? "", d.Priority ?? "normal", d.TargetUrl, (d.DeliveredAt ?? DateTime.UtcNow).ToUniversalTime());

    public static Notice From(InboxItem i, string? eventType = null) => new(i.Id, ModuleOfInbox(i), eventType ?? EventTypeOfInbox(i), i.Title, i.Body,
        i.Priority, i.TargetUrl, i.At.ToUniversalTime());

    /// The inbox only has a coarse type; a ticket chat message is recognisable by its link, and must stay one (iDesk
    /// toast with reply, "iDesk ticket chat" setting) when it comes back from the Inbox or a catch-up.
    private static string EventTypeOfInbox(InboxItem i)
    {
        var url = i.TargetUrl ?? "";
        if (url.StartsWith("/idesk/tickets/") || (url.StartsWith("/tickets/") && url.EndsWith("#messages"))) return "ticket.message_created";
        return i.Type;
    }

    /// The inbox gives a coarse type; the target URL says more (an iDesk ticket is "system" there).
    private static string ModuleOfInbox(InboxItem i)
    {
        var url = i.TargetUrl ?? "";
        if (url.StartsWith("/tickets") || url.StartsWith("/idesk/tickets")) return "tickets";
        if (url.StartsWith("/calls?tab=scheduled") || url.StartsWith("/calls?tab=rooms&join") || url.StartsWith("/calendar")) return "calendar";
        if (url.StartsWith("/calls")) return "calls";
        if (url.StartsWith("/notes")) return "notes";
        if (url.StartsWith("/mail")) return "mail";
        if (url.StartsWith("/news")) return "news";
        if (url.StartsWith("/projects")) return "projects";
        if (url.StartsWith("/chat")) return "chat";
        return NotifierSettings.ModuleOf(i.Type == "project" ? "project" : i.Type);
    }
}
