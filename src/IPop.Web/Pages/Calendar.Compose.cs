// Calendar everywhere (approved mockup docs/mockups/ipop-apple/calendar-everywhere.html): the one meeting form
// opened prefilled from any module (CalendarLinks builds the address), the Repeat picker, and the scope
// question when changing or cancelling one meeting of a repeating series.
using System.Globalization;
using System.Net.Http.Json;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;

namespace IPop.Web.Pages;

public partial class Calendar
{
    [SupplyParameterFromQuery(Name = "new")] public int? NewQuery { get; set; }
    [SupplyParameterFromQuery(Name = "title")] public string? TitleQuery { get; set; }
    [SupplyParameterFromQuery(Name = "location")] public string? LocationQuery { get; set; }
    [SupplyParameterFromQuery(Name = "people")] public string? PeopleQuery { get; set; }
    /// A chat group id (CalendarLinks.NewMeeting) or a hall room key (booking sheet "More options…": kind=room&amp;room=kalosi).
    [SupplyParameterFromQuery(Name = "room")] public string? RoomQuery { get; set; }
    [SupplyParameterFromQuery(Name = "kind")] public string? KindQuery { get; set; }
    [SupplyParameterFromQuery(Name = "end")] public string? EndQuery { get; set; }
    [Inject] private MeetingDraftHandoff Handoff { get; set; } = default!;

    private Guid? ChatRoomQuery => Guid.TryParse(RoomQuery, out var g) ? g : null;
    [SupplyParameterFromQuery(Name = "start")] public string? StartQuery { get; set; }
    [SupplyParameterFromQuery(Name = "mins")] public int? MinsQuery { get; set; }
    [SupplyParameterFromQuery(Name = "private")] public int? PrivateQuery { get; set; }
    [SupplyParameterFromQuery(Name = "notes")] public string? NotesQuery { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public string? FromQuery { get; set; }

    /// "From chat ICT Team · 6 members added" — where a prefilled form came from; × clears the prefill.
    private string? prefillSource;

    /// Opens the form from the address another module built (CalendarLinks.NewMeeting).
    private async Task OpenFromComposeQueryAsync()
    {
        if (NewQuery != 1) return;
        if (ChatSvc.GroupRooms.Count == 0 && ChatRoomQuery is not null) { try { await ChatSvc.LoadRoomsAsync(); } catch { } }
        var start = DateTime.TryParse(StartQuery, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s) ? s.ToLocalTime() : DefaultStart();
        anchor = start.Date;
        OpenCreate(start);
        if (DateTime.TryParse(EndQuery, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var eq) && eq.ToLocalTime() > start
            && eq.ToLocalTime() - start <= TimeSpan.FromHours(24))
        {
            var e = eq.ToLocalTime();
            form.EndDate = e.Date; form.EndTime = TimeOnly.FromDateTime(e);
        }
        else if (MinsQuery is > 0 and <= 600)
        {
            var e = start.AddMinutes(MinsQuery.Value);
            form.EndDate = e.Date; form.EndTime = TimeOnly.FromDateTime(e);
        }
        var (qKind, qRoom) = IPop.Web.Components.Meetings.CalendarKinds.FromQuery(KindQuery, RoomQuery);
        if (qKind is not null) { form.Kind = qKind; form.RoomKey = qRoom; }
        if (!string.IsNullOrWhiteSpace(TitleQuery)) form.Title = TitleQuery.Trim().Length > 200 ? TitleQuery.Trim()[..200] : TitleQuery.Trim();
        if (!string.IsNullOrWhiteSpace(LocationQuery) && qKind is null)
        {
            // Old links: a hall place opens on Meeting room (the bare "SJA Hall · " prefix asks for a room), else In person.
            var loc = LocationQuery.Trim();
            if (loc.StartsWith(IPop.Application.Hall.HallBooking.Prefix.Trim(), StringComparison.OrdinalIgnoreCase))
            { form.Kind = IPop.Web.Components.Meetings.MeetingKinds.Room; form.RoomKey = IPop.Application.Hall.HallBooking.RoomKey(loc); }
            else { form.Kind = IPop.Web.Components.Meetings.MeetingKinds.InPerson; form.Location = loc; }
        }
        if (!string.IsNullOrWhiteSpace(NotesQuery)) form.Notes = NotesQuery;
        if (PrivateQuery == 1) form.Visibility = "private";
        var added = 0;
        var wholeGroup = false;
        if (ChatRoomQuery is { } room && ChatSvc.GroupRooms.Any(r => r.Id == room))
        {
            form.RoomIdText = room.ToString();
            wholeGroup = true;
        }
        foreach (var nik in PLinks.Niks(PeopleQuery).Where(n => n != Auth.Me?.Nik).Distinct().Take(100))
        {
            var name = await PLinks.NameAsync(Auth, nik);
            form.People.Add(new MemberSearchRow(nik, name, null, null));
            added++;
        }
        // People picked in the booking sheet travel in memory, never in the address.
        foreach (var p in Handoff.Take() ?? [])
        {
            if (p.Key == Auth.Me?.Nik || form.People.Any(x => x.Nik == p.Key)) continue;
            form.People.Add(new MemberSearchRow(p.Key, p.Name, null, p.Department));
            added++;
        }
        SyncInvitees();
        if (string.IsNullOrWhiteSpace(FromQuery) && qKind == IPop.Web.Components.Meetings.MeetingKinds.Room)
            prefillSource = added > 0 ? $"From Meeting Rooms · {added} {(added == 1 ? "person" : "people")} added" : "From Meeting Rooms";
        if (!string.IsNullOrWhiteSpace(FromQuery))
            prefillSource = wholeGroup ? $"{FromQuery} · the whole group is invited"
                : added > 0 ? $"{FromQuery} · {added} {(added == 1 ? "person" : "people")} added" : FromQuery;
        if (form.People.Count > 0 && !form.AllDay) await LoadFreeBusyAsync();
        // Drop the query so a reload doesn't open the form again.
        Nav.NavigateTo($"/calendar?date={anchor:yyyy-MM-dd}" + (managing is null ? "" : $"&manage={Uri.EscapeDataString(ManageQuery ?? managing.Nik)}"), replace: true);
    }

    private void ClearPrefill()
    {
        prefillSource = null;
        form.People.Clear(); form.RoomIdText = ""; form.Notes = null; fat = null;
        SyncInvitees();
    }

    // ── Repeat ──

    private bool repeatOpen;
    private string repeatFreq = "";          // "" = doesn't repeat, D, W, M
    private string repeatMonthly = "date"; // date | weekday | last
    private int MonthlyOrdinal => repeatMonthly == "last" ? -1 : (form.StartDate.Day - 1) / 7 + 1;
    private HashSet<int> repeatDays = [];    // ISO 1 = Mon … 7 = Sun
    private string repeatEnd = "never";      // never | until | count
    private DateTime repeatUntil = DateTime.Today.AddMonths(3);
    private int repeatCount = 10;

    private static readonly (int Iso, string Short)[] WeekDays = [(1, "M"), (2, "T"), (3, "W"), (4, "T"), (5, "F"), (6, "S"), (7, "S")];
    private static int Iso(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

    private void ResetRepeat(string? rule = null)
    {
        repeatOpen = false; repeatFreq = ""; repeatMonthly = "date"; repeatDays = []; repeatEnd = "never"; repeatCount = 10;
        repeatUntil = form.StartDate.AddMonths(3);
        if (string.IsNullOrWhiteSpace(rule)) return;
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            switch (kv[0])
            {
                case "D": repeatFreq = "D"; break;
                case "M":
                    repeatFreq = "M";
                    if (kv.Length > 1) repeatMonthly = kv[1].StartsWith("-1,") ? "last" : "weekday";
                    break;
                case "W": repeatFreq = "W"; if (kv.Length > 1) repeatDays = kv[1].Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n is >= 1 and <= 7).ToHashSet(); break;
                case "U" when kv.Length > 1 && DateTime.TryParseExact(kv[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u): repeatEnd = "until"; repeatUntil = u; break;
                case "C" when kv.Length > 1 && int.TryParse(kv[1], out var c): repeatEnd = "count"; repeatCount = c; break;
            }
        }
    }

    private void SetRepeatFreq(string f)
    {
        repeatFreq = f;
        if (f == "W" && repeatDays.Count == 0) repeatDays = [Iso(form.StartDate.DayOfWeek)];
    }

    private void ToggleRepeatDay(int iso)
    {
        if (!repeatDays.Remove(iso)) repeatDays.Add(iso);
        if (repeatDays.Count == 0) repeatDays.Add(Iso(form.StartDate.DayOfWeek));
    }

    private string? RepeatRule => repeatFreq switch
    {
        "" => null,
        "M" when repeatMonthly != "date" => $"M:{MonthlyOrdinal},{Iso(form.StartDate.DayOfWeek)}" + RepeatEndRule,
        "W" => $"W:{string.Join(',', repeatDays.Order())}" + RepeatEndRule,
        _ => repeatFreq + RepeatEndRule,
    };

    private string RepeatEndRule => repeatEnd switch
    {
        "until" => $";U:{repeatUntil:yyyy-MM-dd}",
        "count" => $";C:{Math.Clamp(repeatCount, 2, 100)}",
        _ => "",
    };

    private static readonly string[] DayNames = ["", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// The plain-words line under the picker and on the Repeat row.
    private string RepeatLabel
    {
        get
        {
            if (repeatFreq == "") return "Doesn't repeat";
            var what = repeatFreq switch
            {
                "D" => "Every day",
                "M" when repeatMonthly != "date" => $"Every month on the {(MonthlyOrdinal == -1 ? "last" : MonthlyOrdinal + Ord(MonthlyOrdinal))} {form.StartDate:dddd}",
                "M" => $"Every month on the {form.StartDate.Day}{Ord(form.StartDate.Day)}",
                _ => "Every " + Join(repeatDays.Order().Select(d => DayNames[d]).ToList()),
            };
            return what + repeatEnd switch
            {
                "until" => $", until {repeatUntil:d MMM yyyy}",
                "count" => $", {Math.Clamp(repeatCount, 2, 100)} times",
                _ => "",
            };
        }
    }

    /// How many meetings the rule makes (shown in the picker so nobody creates 100 by accident).
    private int RepeatOccurrences
    {
        get
        {
            if (repeatFreq == "") return 1;
            var first = form.StartDate.Date;
            var end = repeatEnd == "until" ? repeatUntil.Date : first.AddDays(366);
            var max = repeatEnd == "count" ? Math.Clamp(repeatCount, 2, 100) : 100;
            var n = 0;
            for (var d = first; d <= end && n < max; d = d.AddDays(1))
            {
                var hit = repeatFreq switch { "D" => true, "M" => MonthlyHit(d, first), _ => repeatDays.Contains(Iso(d.DayOfWeek)) };
                if (hit) n++;
            }
            return n;
        }
    }

    private bool MonthlyHit(DateTime day, DateTime first) => repeatMonthly == "date" ? day.Day == first.Day
        : day.DayOfWeek == first.DayOfWeek && (MonthlyOrdinal == -1
            ? day.AddDays(7).Month != day.Month : (day.Day - 1) / 7 + 1 == MonthlyOrdinal);

    // ── Room series check (v3 §3) before saving a new repeating room booking ──

    private SeriesCheck? roomCheck;
    private string roomCheckKey = "";
    private readonly Dictionary<DateOnly, string?> roomChoices = [];

    /// Client mirror of SeriesPreviewDto (POST /api/calendar/series/preview).
    public sealed record SeriesCheck(int Total, int Booked, IReadOnlyList<SeriesCheckDate> Occurrences);
    public sealed record SeriesCheckDate(DateOnly Date, DateTime StartAt, DateTime EndAt, bool Free, string? BookedBy, IReadOnlyList<string> FreeRooms, string? Reason = null);

    private async Task CheckRoomSeriesAsync(DateTime start, DateTime end, string rule)
    {
        roomCheck = null; roomChoices.Clear();
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/calendar/series/preview", new { roomKey = form.RoomKey, startAt = start, endAt = end, recurrence = rule });
        if (!res.IsSuccessStatusCode) { formError = await ErrorAsync(res); return; }
        roomCheck = await res.Content.ReadFromJsonAsync<SeriesCheck>();
        roomCheckKey = $"{form.RoomKey}|{start:o}|{end:o}|{rule}";
    }

    private string? RoomChoice(DateOnly d) => roomChoices.TryGetValue(d, out var c) ? c : null;
    private void SetRoomChoice(DateOnly d, string? room) => roomChoices[d] = room;

    /// Clashing dates: skipped, or moved to the picked room.
    private List<object> RoomOverrides() => roomCheck?.Occurrences.Where(o => !o.Free)
        .Select(o => RoomChoice(o.Date) is { } r ? (object)new { date = o.Date, skip = false, roomKey = r } : new { date = o.Date, skip = true })
        .ToList() ?? [];

    private static string Ord(int d) => d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    private static string Join(List<string> n) => n.Count <= 1 ? string.Concat(n) : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];

    // ── Scope (this / following / all) ──

    private TaskCompletionSource<string?>? scopeTcs;
    private string scopeChoice = "this";
    private string scopeVerb = "Change";

    private bool InSeries => detail?.SeriesId is not null;

    /// Asks which meetings of a repeating series to change or cancel; null = cancelled.
    private Task<string?> AskScopeAsync(string verb)
    {
        scopeVerb = verb; scopeChoice = "this";
        scopeTcs = new TaskCompletionSource<string?>();
        StateHasChanged();
        return scopeTcs.Task;
    }

    private void AnswerScope(string? scope)
    {
        var t = scopeTcs; scopeTcs = null;
        t?.TrySetResult(scope);
    }
}
