namespace IPop.Notifier.Core;

/// What the server pushes on /hubs/notification "notificationReceived" (NotificationDeliveryState).
public sealed record Delivery(Guid RequestId, Guid UserId, string Status, string? Channel, int AttemptCount,
    DateTime? DeliveredAt, DateTime? ReadAt, DateTime? AcknowledgedAt,
    string? Title, string? Body, string? EventType, string? Priority, string? TargetUrl, Guid? ActorId = null);

/// Chat toast ("desktopToast"): same shape as the browser's Web Push payload.
/// SenderId (1.5.0): who wrote it, so a leader's or VIP's chat opens a window instead of a toast.
public sealed record ToastPayload(string Kind, string Title, string Body, string? Url, string Tag, bool RequireInteraction = false, Guid? SenderId = null);

/// One row of /api/notifications/v1/inbox.
public sealed record InboxItem(Guid Id, string Type, string Title, string Body, string Source, DateTime At, bool IsRead,
    string Priority, string? TargetUrl = null, DateTime? SnoozedUntil = null,
    Guid? ActorId = null, string? ActorName = null, string? EventType = null);

public sealed record InboxPage(List<InboxItem> Items, int TotalCount, int UnreadCount, int Page, int PageSize);

/// A notification as the app handles it (live or caught up after being offline). Kind/Tier (1.5.0 priority inbox,
/// docs/notifier/PRIORITY-150.md) are filled by <see cref="Classified"/> with the current settings and leaders;
/// Leader = why the actor counts ("Manager", "BOD / GM level", "VIP"), null for everyone else.
public sealed record Notice(Guid Id, string Module, string EventType, string Title, string Body, string Priority, string? TargetUrl, DateTime At,
    Guid? ActorId = null, string? ActorName = null, string Kind = "system", string Tier = NotifierSettings.Msg, string? Leader = null)
{
    public bool NeedsAction => Tier == NotifierSettings.Act;

    public static Notice From(Delivery d) => new(d.RequestId, NotifierSettings.ModuleOf(d.EventType), d.EventType ?? "system",
        d.Title ?? "iPop", d.Body ?? "", d.Priority ?? "normal", d.TargetUrl, (d.DeliveredAt ?? DateTime.UtcNow).ToUniversalTime(), d.ActorId);

    public static Notice From(InboxItem i, string? eventType = null) => new(i.Id, ModuleOfInbox(i), eventType ?? EventTypeOfInbox(i), i.Title, i.Body,
        i.Priority, i.TargetUrl, i.At.ToUniversalTime(), i.ActorId, i.ActorName);

    /// Kind and tier with these settings: a leader or VIP, an emergency, a mandatory memo or priority high always
    /// needs action (NotifierSettings.AlwaysAct); otherwise the kind's tier from Settings › Categories.
    public Notice Classified(NotifierSettings s, Leaders? leaders)
    {
        var kind = NotifierSettings.KindOf(EventType, Priority, TargetUrl);
        var leader = leaders?.ReasonFor(ActorId, s);
        return this with { Kind = kind, Tier = s.TierFor(kind, EventType, Priority, leader is not null), Leader = leader };
    }

    /// The inbox only has a coarse type; a ticket chat message is recognisable by its link, and must stay one (iDesk
    /// toast with reply, "iDesk ticket chat" setting) when it comes back from the Inbox or a catch-up.
    private static string EventTypeOfInbox(InboxItem i)
    {
        // Servers since 1.5.0 send the full event type; older ones only the coarse module.
        if (!string.IsNullOrEmpty(i.EventType)) return i.EventType;
        var url = i.TargetUrl ?? "";
        // "/idesk/tickets/{id}#update" is a technician's status notification (assigned, reopened…), not a chat.
        if ((url.StartsWith("/idesk/tickets/") && !url.EndsWith("#update")) || (url.StartsWith("/tickets/") && url.EndsWith("#messages"))) return "ticket.message_created";
        return i.Type;
    }

    /// The inbox gives a coarse type; the target URL says more (an iDesk ticket is "system" there).
    private static string ModuleOfInbox(InboxItem i)
    {
        var url = i.TargetUrl ?? "";
        // Servers since 1.5.0 send the full event type: a meeting reminder linking to /calls?tab=rooms is still Calendar.
        var head = (i.EventType ?? "").Split('.', 2)[0].ToLowerInvariant();
        if (head is "meeting" or "calendar") return "calendar";
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
