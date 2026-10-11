namespace IPop.Web.Services;

/// Builds the address that opens the one Calendar meeting form prefilled from another module (Chat, Mail,
/// Projects, Tickets, Notes, people cards). People go as per-tab tokens (<see cref="PeopleLinks"/>), never NIKs.
public sealed class CalendarLinks(PeopleLinks people)
{
    /// <param name="from">Shown on the form: "From chat ICT Team", "From mail “Budget review”".</param>
    public string NewMeeting(string? title = null, IEnumerable<string>? niks = null, Guid? roomId = null, DateTime? start = null,
        int? minutes = null, bool isPrivate = false, string? notes = null, string? from = null, string? location = null)
    {
        var q = new List<string> { "new=1" };
        if (!string.IsNullOrWhiteSpace(title)) q.Add("title=" + Uri.EscapeDataString(Trim(title, 200)));
        var list = niks?.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(100).ToList();
        if (list is { Count: > 0 }) q.Add("people=" + people.Tokens(list));
        if (roomId is { } r) q.Add("room=" + r);
        if (start is { } s) q.Add("start=" + Uri.EscapeDataString(s.ToUniversalTime().ToString("o")));
        if (minutes is > 0) q.Add("mins=" + minutes);
        if (isPrivate) q.Add("private=1");
        if (!string.IsNullOrWhiteSpace(notes)) q.Add("notes=" + Uri.EscapeDataString(Trim(notes, 1500)));
        if (!string.IsNullOrWhiteSpace(from)) q.Add("from=" + Uri.EscapeDataString(Trim(from, 80)));
        if (!string.IsNullOrWhiteSpace(location)) q.Add("location=" + Uri.EscapeDataString(Trim(location, 300)));
        return "/calendar?" + string.Join('&', q);
    }

    /// The event itself on the calendar (reminders, chat cards, Calls list, Dashboard).
    public static string Event(Guid eventId, DateTime? startUtc = null) =>
        $"/calendar?event={eventId}" + (startUtc is { } s ? $"&date={s.ToLocalTime():yyyy-MM-dd}" : "");

    private static string Trim(string s, int max) { s = s.Trim(); return s.Length > max ? s[..max] : s; }
}
