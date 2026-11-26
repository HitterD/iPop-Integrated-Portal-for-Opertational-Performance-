namespace IPop.Notifier.Core;

/// Ticket notifications open the ticket on the iDesk site, never an iPop page: iPop answers the iDesk link (signed in
/// through "Sign in with iPop"), and iDesk opens the ticket on its chat with the latest reply in view.
///   /tickets/{id}…        my own ticket      → GET api/tickets/{id}            → "url"
///   /idesk/tickets/{id}…  the assigned agent → GET api/tickets/idesk/{id}/link → "url"
public static class TicketLinks
{
    /// The API call that answers the iDesk link for a notification target, or null when it isn't a ticket.
    public static string? LinkRequest(string? target)
    {
        if (Id(target, "/tickets/") is { } mine) return $"api/tickets/{mine}";
        if (Id(target, "/idesk/tickets/") is { } agent) return $"api/tickets/idesk/{agent}/link";
        return null;
    }

    /// "/tickets/{guid}", optionally followed by "#messages", "?…" or "/…"; "/tickets/new" or "/tickets" are not tickets.
    private static Guid? Id(string? target, string prefix)
    {
        if (target is null || !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = target.AsSpan(prefix.Length);
        if (rest.Length < 36 || !Guid.TryParseExact(rest[..36], "D", out var id)) return null;
        return rest.Length == 36 || rest[36] is '#' or '?' or '/' ? id : null;
    }
}
