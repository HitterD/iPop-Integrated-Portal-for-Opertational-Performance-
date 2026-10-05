// Calendar everywhere (approved mockup docs/mockups/ipop-apple/calendar-everywhere.html): the one meeting form
// opened prefilled from any module (CalendarLinks builds the address), the Repeat picker, and the scope
// question when changing or cancelling one meeting of a repeating series.
using System.Globalization;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;

namespace IPop.Web.Pages;

public partial class Calendar
{
    [SupplyParameterFromQuery(Name = "new")] public int? NewQuery { get; set; }
    [SupplyParameterFromQuery(Name = "title")] public string? TitleQuery { get; set; }
    [SupplyParameterFromQuery(Name = "people")] public string? PeopleQuery { get; set; }
    [SupplyParameterFromQuery(Name = "room")] public Guid? RoomQuery { get; set; }
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
        if (ChatSvc.GroupRooms.Count == 0 && RoomQuery is not null) { try { await ChatSvc.LoadRoomsAsync(); } catch { } }
        var start = DateTime.TryParse(StartQuery, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s) ? s.ToLocalTime() : DefaultStart();
        anchor = start.Date;
        OpenCreate(start);
        if (MinsQuery is > 0 and <= 600)
        {
            var e = start.AddMinutes(MinsQuery.Value);
            form.EndDate = e.Date; form.EndTime = TimeOnly.FromDateTime(e);
        }
        if (!string.IsNullOrWhiteSpace(TitleQuery)) form.Title = TitleQuery.Trim().Length > 200 ? TitleQuery.Trim()[..200] : TitleQuery.Trim();
        if (!string.IsNullOrWhiteSpace(NotesQuery)) form.Notes = NotesQuery;
        if (PrivateQuery == 1) form.Visibility = "private";
        var added = 0;
        var wholeGroup = false;
        if (RoomQuery is { } room && ChatSvc.GroupRooms.Any(r => r.Id == room))
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

    private bool CanJoinDetail => detail is { MeetingId: not null, MeetingStatus: not "cancelled" }
        && CallsSchedule.IsOnline(detail.Location)
        && CallsSchedule.CanJoin(detail.StartAt, detail.EndAt, DateTime.UtcNow);

    private string? DetailJoinUrl => detail?.MeetingId is { } id ? $"/meet/meeting-{id:N}" : null;

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
