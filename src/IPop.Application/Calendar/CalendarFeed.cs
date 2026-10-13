using IPop.Application.Rbac;
namespace IPop.Application.Calendar;

/// Module read grants; personal entries and holidays are always available.
public sealed record CalendarFeedAccess(bool Projects, bool Mail, bool Chat, bool News, bool Tickets = true)
{
    public static readonly CalendarFeedAccess All = new(true, true, true, true, true);
    public static async Task<CalendarFeedAccess> ForAsync(IPermissionEvaluator perms, Guid userId, CancellationToken ct) => new(
        await perms.IsAllowedAsync(userId, "projects.read", null, ct),
        await perms.IsAllowedAsync(userId, "mail.read", null, ct),
        await perms.IsAllowedAsync(userId, "chat.read", null, ct),
        await perms.IsAllowedAsync(userId, "news.read", null, ct),
        await perms.IsAllowedAsync(userId, "tickets.read", null, ct));
}

/// A calendar entry shared by the calendar API and dashboard. Kind = feed sub-type (icon/colour); MeetingKind = the
/// meeting's voice | room | inperson (v3 §1) and RoomKey its hall room, for meeting items only.
public record CalendarFeedItem(string Id, string Source, string Kind, string Title, DateTime StartAt, DateTime EndAt, bool AllDay,
    string? Url, string? Subtitle, Guid? EventId, Guid? MeetingId, string? Rsvp, bool CanEdit, string? Location, string? Link,
    string? Visibility = null, string? MeetingKind = null, string? RoomKey = null);
