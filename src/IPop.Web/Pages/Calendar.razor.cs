// Calendar page — feed loading, view ranges, day-grid layout and the event drawer.
// _Imports.razor only reaches .razor files, so usings are restated here.
using System.Globalization;
using System.Net.Http.Json;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Calendar
{
    [CascadingParameter] public IPop.Web.Layout.MainLayout Layout { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "date")] public string? DateQuery { get; set; }
    [SupplyParameterFromQuery(Name = "event")] public Guid? EventQuery { get; set; }
    [SupplyParameterFromQuery(Name = "manage")] public string? ManageQuery { get; set; }
    [SupplyParameterFromQuery(Name = "invite")] public string? InviteQuery { get; set; }
    /// With event=: open its form (Calls › Scheduled "Edit"), when the viewer may edit it.
    [SupplyParameterFromQuery(Name = "edit")] public int? EditQuery { get; set; }

    // ── Client-side mirrors of the API records (Web does not reference IPop.Api) ──
    public record FeedItem(string Id, string Source, string Kind, string Title, DateTime StartAt, DateTime EndAt, bool AllDay,
        string? Url, string? Subtitle, Guid? EventId, Guid? MeetingId, string? Rsvp, bool CanEdit, string? Location, string? Link,
        string? Visibility = null, string? OwnerNik = null, string? MeetingKind = null, string? RoomKey = null)
    {
        /// Timed items are shown in local time; all-day items are calendar dates (stored as UTC midnight).
        public DateTime Start => AllDay ? StartAt.Date : StartAt.ToLocalTime();
        public DateTime End => AllDay ? EndAt.Date : EndAt.ToLocalTime();
    }
    public record Person(Guid UserId, string Nik, string Name, string Rsvp, string? RespondedBy = null);
    public record EventDetail(Guid Id, string Title, DateTime StartAt, DateTime EndAt, bool AllDay, string? Location, string? Link,
        string? Notes, int? ReminderMinutes, Guid? RoomId, string? RoomName, Guid? MeetingId, string? MeetingStatus,
        string OwnerName, bool CanEdit, List<Person> Attendees, string Visibility = "busy",
        string? OwnerNik = null, string? CreatedBy = null, string? SentBy = null, string? UpdatedBy = null, string? DelegateLevel = null,
        string? DelegateForNik = null, string? DelegateForName = null, Guid? SeriesId = null, string? Recurrence = null, string? RecurrenceText = null,
        ExternalInfo? External = null, string? MeetingKind = null, string? RoomKey = null);
    /// Booked in iDesk (Zoom or an ICT visit): read-only here; Url manages it there. People by name only.
    public record ExternalInfo(string Source, string? Url, List<ExternalPerson> People, bool CanSetPrivacy);
    public record ExternalPerson(string Name, Guid? UserId, bool Booker, bool Me);

    private sealed record SourceDef(string Key, string Label, string Icon);
    private static readonly SourceDef[] Sources =
    [
        new("personal", "My events", "calendar"),
        new("meetings", "Meetings", "video"),
        new("chat", "Chat groups", "message-circle"),
        new("projects", "Projects", "kanban"),
        new("mail", "Scheduled mail", "mail"),
        new("news", "News & Memos", "newspaper"),
        new("tickets", "Tickets", "life-buoy"),
        new("idesk", "iDesk bookings", "video"),
        new("holidays", "Holidays", "party-popper"),
    ];
    private static readonly (string, string)[] Views = [("day", "Day"), ("week", "Week"), ("month", "Month"), ("agenda", "Agenda")];
    private static readonly (string, string)[] RsvpOptions = [("accept", "Yes"), ("maybe", "Maybe"), ("decline", "No")];
    // Built on first use: a static initializer here would run before ReminderOptions below is set.
    private static List<AppOption>? reminderSelect;
    private static List<AppOption> ReminderSelect => reminderSelect ??= ReminderOptions.Select(r => new AppOption(r.Item1.ToString(), r.Item2)).ToList();

    private List<AppOption> ChatGroupSelect =>
        [new("", "None", "Just you, or invite people below", "user"),
         .. ChatSvc.GroupRooms.Select(r => new AppOption(r.Id.ToString(), r.Name ?? "Group", "Everyone in the group is invited", "message-circle"))];

    private static readonly (int, string)[] ReminderOptions =
        [(0, "None"), (5, "5 minutes before"), (10, "10 minutes before"), (15, "15 minutes before"), (30, "30 minutes before"),
         (60, "1 hour before"), (120, "2 hours before"), (1440, "1 day before")];
    private static readonly string[] WeekdayShort = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private const double HourPx = 48;
    private const string PrefsKey = "ipop.calendar.prefs";

    private string view = "week";
    private DateTime anchor = DateTime.Today;
    private DateTime miniMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private List<FeedItem> items = [];
    private HashSet<string> hidden = [];
    private bool loadingFeed, loadError, scrollPending = true;
    private DateTime loadedFrom, loadedTo;
    private CancellationTokenSource? loadCts;
    private System.Threading.Timer? nowTimer;

    // Drawer state
    private bool drawerOpen, editing, saving;
    // Exit animation: the drawer markup stays for DrawerExitMs after closing (micro-interactions #4).
    private const int DrawerExitMs = 180;
    private bool drawerClosing;
    private CancellationTokenSource? drawerExitCts;
    // #10: the event just saved pops onto the grid once.
    private Guid? justSavedId;
    private EventDetail? detail;
    private EventForm form = new();
    private string? formError;

    protected override async Task OnInitializedAsync()
    {
        Layout.SetHeader("Calendar", "Everything scheduled across iPop");
        if (DateTime.TryParseExact(DateQuery, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) anchor = d.Date;
        miniMonth = new(anchor.Year, anchor.Month, 1);
        await LoadPrefsAsync();
        await Task.WhenAll(LoadSharingAsync(), LoadManagedAsync(), LoadIntroAsync(), LoadPersonAsync());
        if (PLinks.Nik(ManageQuery) is { } mq && managed.FirstOrDefault(m => m.Nik == mq) is { } mc) managing = mc;
        await LoadAsync();
        // Now line and "Awaiting reply" drift with the clock; refresh every minute.
        nowTimer = new System.Threading.Timer(_ => InvokeAsync(() => { clockNow = DateTime.UtcNow; StateHasChanged(); }), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _ = ChatSvc.GroupRooms.Count == 0 ? ChatSvc.LoadRoomsAsync() : Task.CompletedTask;
        calendarSub = Auth.On(IPop.Application.Realtime.RealtimeEvents.CalendarChanged, OnCalendarChanged);
        // The first address is applied here, once the calendar is loaded; later ones in OnParametersSetAsync.
        lastCalendarAddress = Nav.Uri + "|" + PersonNik; lastPersonNik = PersonNik;
        initialized = true;
        await ApplyAddressQueryAsync();
    }

    /// "Invite to meeting" from a person's calendar: the form opens on the main calendar.
    private static (string Nik, string Name, DateTime? Start)? pendingInvite;

    private string? lastPersonNik;

    /// The form's times in UTC (for the kind picker's room free/busy and timeline, and the invite picker's free/busy).
    private DateTime FormStartUtc => form.StartDate.Date.Add(form.StartTime.ToTimeSpan()).ToUniversalTime();
    private DateTime FormEndUtc => form.EndDate.Date.Add(form.EndTime.ToTimeSpan()).ToUniversalTime();

    /// The kind picker's room timeline moved the meeting.
    // ── Date/time pills (v3 gaps, mockup screen 6) ──

    private (DateTime? Min, DateTime? Max) FormDateRange =>
        IPop.Web.Components.Meetings.CalendarKinds.StartDateRange(form.Id is null, form.Kind, DateTime.Today);
    private bool FormStartDisabled(TimeOnly t) =>
        IPop.Web.Components.Meetings.CalendarKinds.StartTimeDisabled(form.Id is null, form.StartDate, t, DateTime.Now);
    private bool FormEndDisabled(TimeOnly t) =>
        IPop.Web.Components.Meetings.CalendarKinds.EndTimeDisabled(form.Kind, form.StartDate.Date.Add(form.StartTime.ToTimeSpan()), form.EndDate, t);

    /// Moving the start keeps the length (like Apple Calendar), so the end never lands before it.
    private void ShiftStart(DateTime newStart)
    {
        var oldStart = form.StartDate.Date.Add(form.StartTime.ToTimeSpan());
        var len = form.EndDate.Date.Add(form.EndTime.ToTimeSpan()) - oldStart;
        if (form.AllDay) len = form.EndDate.Date - form.StartDate.Date;
        if (len < TimeSpan.Zero) len = TimeSpan.FromHours(1);
        var e = newStart + len;
        form.StartDate = newStart.Date; form.StartTime = TimeOnly.FromDateTime(newStart);
        if (form.AllDay) form.EndDate = e.Date;
        else { form.EndDate = e.Date; form.EndTime = TimeOnly.FromDateTime(e); }
        roomCheck = null;
    }

    private void SetFormStartDate(DateTime d) => ShiftStart(d.Date.Add(form.StartTime.ToTimeSpan()));
    private void SetFormStartTime(TimeOnly t) => ShiftStart(form.StartDate.Date.Add(t.ToTimeSpan()));

    private void SetFormEndTime(TimeOnly t)
    {
        if (FormEndDisabled(t)) return;
        // A room booking's 00:00 is the end of its day.
        if (form.Kind == IPop.Web.Components.Meetings.MeetingKinds.Room && t == TimeOnly.MinValue && form.EndDate.Date == form.StartDate.Date)
            form.EndDate = form.StartDate.Date.AddDays(1);
        form.EndTime = t;
        roomCheck = null;
    }

    private void SetFormTimes((DateTime Start, DateTime End) t)
    {
        var s = t.Start.ToLocalTime(); var e = t.End.ToLocalTime();
        form.StartDate = s.Date; form.StartTime = TimeOnly.FromDateTime(s);
        form.EndDate = e.Date; form.EndTime = TimeOnly.FromDateTime(e);
        roomCheck = null;
    }

    /// The InvitePicker's list (kept as one instance so the picker keeps its state); mirrors form.People.
    private List<InvitePersonDto> invitees = [];
    private void SyncInvitees() => invitees = form.People.Select(p => new InvitePersonDto(p.Nik, p.Name, p.Department)).ToList();

    private void SetInvitees(List<InvitePersonDto> list)
    {
        invitees = list;
        form.People = list.Select(p => new MemberSearchRow(p.Key, p.Name, null, p.Department)).ToList();
        fat = null;
        if (form.People.Count > 0 && !form.AllDay) _ = InvokeAsync(async () => { await LoadFreeBusyAsync(); StateHasChanged(); });
    }

    private void SetFormKind(string kind)
    {
        form.Kind = kind ?? "";
        formError = null; roomCheck = null;
        if (form.Kind == IPop.Web.Components.Meetings.MeetingKinds.Room && form.AllDay) form.AllDay = false;
    }

    /// Clock for the meeting detail's countdown and Join window (the minute timer refreshes it).
    private DateTime clockNow = DateTime.UtcNow;

    private string? lastCalendarAddress;
    private string? mobilePersonAddress;

    protected override async Task OnParametersSetAsync()
    {
        // Query navigation reuses this component too, not just person navigation.
        // Blazor can call this with the new address before the route parameter catches up; key on both.
        var address = Nav.Uri + "|" + PersonNik;
        if (!initialized || lastCalendarAddress == address) return;
        lastCalendarAddress = address;
        var personChanged = PersonNik != lastPersonNik;
        lastPersonNik = PersonNik;
        if (personChanged)
        {
            mobilePersonAddress = null;
            CloseDrawer(); pop = null; otherItems = []; otherMenu = null; listMenu = false;
            if (!PersonMode) Layout.SetHeader("Calendar", "Everything scheduled across iPop");
            await LoadPersonAsync();
        }
        var dateChanged = DateTime.TryParseExact(DateQuery, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) && anchor != d.Date;
        if (dateChanged) { anchor = d.Date; miniMonth = new(anchor.Year, anchor.Month, 1); }
        var nextManaging = PLinks.Nik(ManageQuery) is { } mq ? managed.FirstOrDefault(m => m.Nik == mq) : null;
        var managerChanged = managing != nextManaging;
        managing = nextManaging;
        if (personChanged || dateChanged || managerChanged) await LoadAsync();
        await ApplyAddressQueryAsync();
    }

    private bool initialized;

    /// Opens whatever the address asks for: an event, a person to invite, or a prefilled new meeting.
    private async Task ApplyAddressQueryAsync()
    {
        await OpenFromQueryAsync();
        if (!PersonMode && pendingInvite is { } pi)
        {
            pendingInvite = null; await MeetWithAsync(pi.Nik, pi.Name, pi.Start);
        }
        else if (PLinks.Nik(InviteQuery) is { } inviteNik)
        {
            var who = await Auth.GetAsync<PersonInfo>($"/api/calendar/person/{Uri.EscapeDataString(inviteNik)}");
            if (who is not null && !who.Self) await MeetWithAsync(who.Nik, who.Name);
        }
        if (EventQuery is { } eid)
        {
            await OpenEventAsync(eid);
            if (EditQuery == 1 && detail is { CanEdit: true }) StartEdit();
        }
        else await OpenFromComposeQueryAsync();
    }

    // ── Live refresh (calendarChanged) ──
    // Someone created/moved/cancelled an event I own or attend. Re-fetch the loaded window
    // quietly (no spinner, no scroll reset); an open drawer keeps its state, and a read-only
    // drawer re-reads its event. Debounced so a burst triggers one reload.
    private IDisposable? calendarSub;
    private CancellationTokenSource? liveCts;

    private void OnCalendarChanged()
    {
        liveCts?.Cancel(); liveCts?.Dispose();
        var t = new CancellationTokenSource(); liveCts = t;
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(400, t.Token); } catch (OperationCanceledException) { return; }
            await LiveRefreshAsync();
        });
    }

    private async Task LiveRefreshAsync()
    {
        if (loadingFeed || items.Count == 0 && loadError) return;
        var from = loadedFrom; var to = loadedTo;
        if (from == default || to <= from) return;
        try
        {
            var who = managing?.Nik;
            var res = await Auth.GetAsync<List<FeedItem>>(FeedUrl(from, to));
            // A navigation-triggered load (or a switch of managed calendar) started meanwhile: it owns the result.
            if (who != managing?.Nik) return;
            if (res is null || loadingFeed || from != loadedFrom || to != loadedTo) return;
            items = res;
            _ = LoadPeopleAsync();

            // Refresh a read-only drawer in place; never touch the editor or a save in flight.
            if (drawerOpen && !editing && !saving && detail is { } open)
            {
                var d = await Auth.GetAsync<EventDetail>($"/api/calendar/events/{open.Id}");
                if (drawerOpen && !editing && detail?.Id == open.Id)
                {
                    if (d is not null) detail = d;
                    else { CloseDrawer(); Toasts.Show("That event was removed.", ToastKind.Info); }
                }
            }
            StateHasChanged();
        }
        catch { /* background refresh — the next event or navigation will catch up */ }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await JS.InvokeVoidAsync("ipop.icons");
        if (PersonMode && mobilePersonAddress != PersonNik)
        {
            mobilePersonAddress = PersonNik;
            if (await JS.InvokeAsync<bool>("eval", "window.matchMedia('(max-width: 768px)').matches") && view != "day")
            {
                // Person calendars start in Day on phones without overwriting my saved preference.
                view = "day"; scrollPending = true;
                await LoadAsync();
                StateHasChanged();
            }
        }
        if (scrollPending && view is "day" or "week" && !loadingFeed)
        {
            scrollPending = false;
            // Start the hour grid at 07:00 so the working day is in view.
            const int hour = 7;
            await JS.InvokeVoidAsync("eval", $"(function(){{var el=document.getElementById('cal-grid-scroll');if(el)el.scrollTop={hour * HourPx};}})()");
        }
    }

    public void Dispose()
    {
        nowTimer?.Dispose();
        loadCts?.Cancel();
        loadCts?.Dispose();
        calendarSub?.Dispose();
        liveCts?.Cancel();
        liveCts?.Dispose();
        peopleCts?.Cancel();
        drawerExitCts?.Cancel();
    }

    // ── Preferences (view + hidden sources) in localStorage ──

    private sealed class Prefs { public string? View { get; set; } public List<string>? Hidden { get; set; } public bool SideBySide { get; set; } }

    private async Task LoadPrefsAsync()
    {
        try
        {
            var json = await Auth.GetUserSettingAsync(PrefsKey);
            if (string.IsNullOrEmpty(json)) return;
            var p = System.Text.Json.JsonSerializer.Deserialize<Prefs>(json);
            if (p?.View is "day" or "week" or "month" or "agenda") view = p.View;
            if (p?.Hidden is not null) hidden = p.Hidden.ToHashSet();
            sideBySide = p?.SideBySide ?? false;
        }
        catch { /* corrupt prefs: keep defaults */ }
    }

    private async Task SavePrefsAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Prefs { View = view, Hidden = hidden.ToList(), SideBySide = sideBySide });
            await Auth.SetUserSettingAsync(PrefsKey, json);
        }
        catch { }
    }

    private async Task ToggleSourceAsync(string key)
    {
        if (!hidden.Remove(key)) hidden.Add(key);
        await SavePrefsAsync();
    }

    // ── Ranges & navigation ──

    private static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private (DateTime From, DateTime To) ViewRange() => view switch
    {
        "day" => (anchor, anchor.AddDays(1)),
        "week" => (StartOfWeek(anchor), StartOfWeek(anchor).AddDays(7)),
        "month" => (MonthCells(anchor).First(), MonthCells(anchor).Last().AddDays(1)),
        _ => (anchor, anchor.AddDays(30)),
    };

    private IEnumerable<DateTime> VisibleDays()
    {
        var (from, to) = ViewRange();
        for (var d = from; d < to; d = d.AddDays(1)) yield return d;
    }

    private static List<DateTime> MonthCells(DateTime month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var start = StartOfWeek(first);
        return Enumerable.Range(0, 42).Select(i => start.AddDays(i)).ToList();
    }

    private bool InRange(DateTime d) { var (f, t) = ViewRange(); return view != "agenda" && d >= f && d < t; }

    private string RangeLabel
    {
        get
        {
            var (from, to) = ViewRange();
            var last = to.AddDays(-1);
            return view switch
            {
                "day" => anchor.ToString("dddd, d MMMM yyyy"),
                "month" => anchor.ToString("MMMM yyyy"),
                _ when from.Year != last.Year => $"{from:d MMM yyyy} – {last:d MMM yyyy}",
                _ when from.Month != last.Month => $"{from:d MMM} – {last:d MMM yyyy}",
                _ => $"{from.Day} – {last:d MMMM yyyy}",
            };
        }
    }

    private async Task ShiftAsync(int dir)
    {
        anchor = view switch
        {
            "day" => anchor.AddDays(dir),
            "week" => anchor.AddDays(7 * dir),
            "month" => new DateTime(anchor.Year, anchor.Month, 1).AddMonths(dir),
            _ => anchor.AddDays(30 * dir),
        };
        miniMonth = new(anchor.Year, anchor.Month, 1);
        await EnsureLoadedAsync();
    }

    private async Task GoToday()
    {
        anchor = DateTime.Today;
        miniMonth = new(anchor.Year, anchor.Month, 1);
        scrollPending = true;
        await EnsureLoadedAsync();
    }

    private async Task SetViewAsync(string v)
    {
        view = v;
        scrollPending = true;
        await SavePrefsAsync();
        await EnsureLoadedAsync();
    }

    private async Task PickDate(DateTime d)
    {
        anchor = d;
        if (view == "month" && d.Month != miniMonth.Month) miniMonth = new(d.Year, d.Month, 1);
        await EnsureLoadedAsync();
    }

    private async Task ShowAgendaAsync(DateTime d)
    {
        anchor = d;
        await SetViewAsync("agenda");
    }

    private async Task PickDayView(DateTime d)
    {
        anchor = d;
        await SetViewAsync("day");
    }

    // ── Loading ──

    /// Loads the visible range padded to whole months so the mini calendar dots and quick
    /// navigation reuse one fetch; refetches only when the view leaves the loaded window.
    private async Task EnsureLoadedAsync()
    {
        var (from, to) = ViewRange();
        var miniFrom = MonthCells(miniMonth).First();
        var miniTo = MonthCells(miniMonth).Last().AddDays(1);
        if (items.Count > 0 && !loadError && from >= loadedFrom && to <= loadedTo && miniFrom >= loadedFrom && miniTo <= loadedTo) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var (from, to) = ViewRange();
        var windowFrom = new[] { from, MonthCells(miniMonth).First(), MonthCells(anchor).First() }.Min();
        var windowTo = new[] { to, MonthCells(miniMonth).Last().AddDays(1), MonthCells(anchor).Last().AddDays(1) }.Max();
        loadCts?.Cancel();
        loadCts = new CancellationTokenSource();
        var ct = loadCts.Token;
        loadingFeed = true; loadError = false;
        StateHasChanged();
        try
        {
            var res = await Auth.GetAsync<List<FeedItem>>(FeedUrl(windowFrom, windowTo), ct);
            if (ct.IsCancellationRequested) return;
            if (res is null) { loadError = true; return; }
            items = res;
            loadedFrom = windowFrom; loadedTo = windowTo;
            _ = LoadPeopleAsync();
        }
        catch (OperationCanceledException) { }
        catch { loadError = true; }
        finally
        {
            if (!ct.IsCancellationRequested) { loadingFeed = false; StateHasChanged(); }
        }
    }

    // ── Item queries ──

    /// On someone's calendar page my own items only show (faintly) when "Show my calendar too" is on.
    private IEnumerable<FeedItem> Visible => PersonMode
        ? (showMineBehind ? items.Where(i => i.Source is "personal" or "meetings" or "chat") : [])
        : items.Where(i => !hidden.Contains(i.Source));

    private static bool IsAllDayLike(FeedItem i) => i.AllDay || (i.End - i.Start).TotalHours >= 24;

    /// withOthers adds the checked people's busy blocks (day/week/month grids only); col narrows to
    /// one person's column in Day "Side by side".
    private IEnumerable<FeedItem> ItemsOn(DateTime day, bool withOthers = false, GridCol? col = null) =>
        (withOthers ? Visible.Concat(OverlayItems) : Visible)
        .Where(i => InCol(col, i))
        .Where(i => i.Start < day.AddDays(1) && (i.End > day || (i.End == i.Start && i.Start.Date == day)))
        .OrderBy(i => IsAllDayLike(i) ? 0 : 1).ThenBy(i => i.Start).ThenBy(i => i.Title);

    private IEnumerable<FeedItem> AllDayOn(DateTime day, GridCol? col = null) => ItemsOn(day, true, col).Where(IsAllDayLike);

    private bool HasItems(DateTime day) => day >= loadedFrom && day < loadedTo && ItemsOn(day).Any();

    private List<FeedItem> UpcomingPending => items
        .Where(i => i.Rsvp == "pending" && i.End > DateTime.Now)
        .OrderBy(i => i.Start).Take(5).ToList();

    /// Item is null for the "+N more" marker that stands in for columns beyond MaxCols.
    private sealed record Placed(FeedItem? Item, double Top, double Height, int Col, int Cols, int Hidden = 0);
    private const int MaxCols = 3;

    /// Timed items for one day, clipped to the day, laid out in side-by-side columns per overlap cluster.
    private List<Placed> LayoutDay(DateTime day, GridCol? gridCol = null)
    {
        var dayEnd = day.AddDays(1);
        var timed = ItemsOn(day, true, gridCol).Where(i => !IsAllDayLike(i))
            .Select(i => (Item: i, S: i.Start < day ? day : i.Start, E: i.End > dayEnd ? dayEnd : (i.End <= i.Start ? i.Start.AddMinutes(30) : i.End)))
            .OrderBy(x => x.S).ThenByDescending(x => x.E).ToList();
        var placed = new List<Placed>();
        var cluster = new List<(FeedItem Item, DateTime S, DateTime E, int Col)>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            var cols = cluster.Count == 0 ? 1 : cluster.Max(c => c.Col) + 1;
            var overflow = cols > MaxCols;
            var shown = overflow ? MaxCols : cols;
            foreach (var c in cluster)
            {
                if (overflow && c.Col >= MaxCols - 1) continue;
                var top = (c.S - day).TotalHours * HourPx;
                var height = Math.Max((c.E - c.S).TotalHours * HourPx, 20);
                placed.Add(new Placed(c.Item, top, height - 2, c.Col, shown));
            }
            if (overflow)
            {
                // Too many parallel items to read: collapse the rest into one marker spanning the cluster.
                var rest = cluster.Where(c => c.Col >= MaxCols - 1).ToList();
                var s = rest.Min(c => c.S); var e = rest.Max(c => c.E);
                placed.Add(new Placed(null, (s - day).TotalHours * HourPx, Math.Max((e - s).TotalHours * HourPx, 20) - 2, MaxCols - 1, shown, rest.Count));
            }
            cluster.Clear();
        }

        foreach (var x in timed)
        {
            if (cluster.Count > 0 && x.S >= clusterEnd) Flush();
            var used = cluster.Where(c => c.E > x.S).Select(c => c.Col).ToHashSet();
            var col = 0;
            while (used.Contains(col)) col++;
            cluster.Add((x.Item, x.S, x.E, col));
            if (x.E > clusterEnd || cluster.Count == 1) clusterEnd = cluster.Max(c => c.E);
        }
        Flush();
        return placed;
    }

    private IEnumerable<(DateTime Day, List<FeedItem> Items)> AgendaDays()
    {
        for (var d = anchor; d < anchor.AddDays(30); d = d.AddDays(1))
        {
            var list = ItemsOn(d).ToList();
            if (list.Count > 0) yield return (d, list);
        }
    }

    // ── Formatting ──

    private static string Px(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + "px";
    private static string Pct(int n, int of) => (100.0 * n / of).ToString("0.###", CultureInfo.InvariantCulture) + "%";

    private static string TimeRange(FeedItem i) => i.End <= i.Start || i.Source is "mail" or "news"
        ? i.Start.ToString("HH:mm")
        : $"{i.Start:HH:mm} – {i.End:HH:mm}";

    private static string When(FeedItem i) => IsAllDayLike(i) ? i.Start.ToString("ddd d MMM") : i.Start.ToString("ddd d MMM, HH:mm");

    private static string Tooltip(FeedItem i) =>
        $"{i.Title}\n{(IsAllDayLike(i) ? "All day" : TimeRange(i))} · {(i.OwnerNik is null ? SourceLabel(i.Source) : i.Subtitle)}{(i.Subtitle is null ? "" : $"\n{i.Subtitle}")}";

    private static string SourceLabel(string source) => Sources.FirstOrDefault(s => s.Key == source)?.Label ?? source;

    /// Meeting items open the shared meeting detail (v3 §6); iDesk bookings keep their own panel.
    private bool ShowsMeetingDetail => !editing && detail is { MeetingId: not null, External: null };

    private bool SelectedItem(FeedItem i) => drawerOpen && detail is { } d && i.EventId == d.Id;

    private static string IconFor(FeedItem i) => IPop.Web.Components.Meetings.CalendarKinds.ItemIcon(i) ?? i.Kind switch
    {
        "meeting" => i.Source == "chat" ? "message-circle" : "video",
        "task" => "circle-check",
        "task-overdue" => "alert-circle",
        "milestone" => "flag",
        "milestone-done" => "flag",
        "holiday" or "offday" => i.Source == "holidays" ? "party-popper" : "palmtree",
        "company-holiday" => "party-popper",
        "ticket-due" => "life-buoy",
        "scheduled-mail" => "send",
        "ack-due" => "clipboard-check",
        "news-scheduled" => "clock",
        "memo" => "file-text",
        "busy" or "busy-tentative" => i.Visibility == "private" ? "lock" : "user",
        _ when i.Source == "news" => "newspaper",
        _ => "calendar",
    };

    private static string DetailWhen(EventDetail d)
    {
        if (d.AllDay)
        {
            var last = d.EndAt.Date.AddDays(-1);
            return last <= d.StartAt.Date ? d.StartAt.ToString("dddd, d MMMM yyyy") + " · all day"
                : $"{d.StartAt:d MMM} – {last:d MMM yyyy} · all day";
        }
        var s = d.StartAt.ToLocalTime(); var e = d.EndAt.ToLocalTime();
        return s.Date == e.Date ? $"{s:dddd, d MMMM yyyy} · {s:HH:mm} – {e:HH:mm}" : $"{s:d MMM HH:mm} – {e:d MMM yyyy HH:mm}";
    }

    private static string ReminderLabel(int minutes) => minutes switch
    {
        >= 1440 when minutes % 1440 == 0 => $"{minutes / 1440} day{(minutes == 1440 ? "" : "s")}",
        >= 60 when minutes % 60 == 0 => $"{minutes / 60} hour{(minutes == 60 ? "" : "s")}",
        _ => $"{minutes} minutes",
    };

    private static string RsvpLabel(string r) => r switch { "accept" => "Going", "maybe" => "Maybe", "decline" => "Declined", _ => "Invited" };

    private string? MyRsvp => detail?.Attendees.FirstOrDefault(a => a.Nik == Auth.Me?.Nik)?.Rsvp;

    // ── Opening items ──

    private async Task OpenItemAsync(FeedItem item, MouseEventArgs? e = null)
    {
        if (item.OwnerNik is not null) { OpenPopover(item, e); return; }
        // Own/invited events open in the drawer; everything else deep-links into its module.
        if (item.EventId is { } eid) { await OpenEventAsync(eid); return; }
        if (item.Url is not null) Nav.NavigateTo(item.Url);
    }

    /// iDesk entries keep their times and details from iDesk; only Normal/Private is the owner's choice.
    private async Task ToggleExternalPrivacyAsync()
    {
        if (detail is not { External: not null } d) return;
        var next = d.Visibility == "private" ? "busy" : "private";
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/calendar/events/{d.Id}",
            new { title = d.Title, startAt = d.StartAt, endAt = d.EndAt, visibility = next });
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't change the privacy. Try again.", ToastKind.Error); return; }
        detail = await res.Content.ReadFromJsonAsync<EventDetail>() ?? d;
        Toasts.Show(next == "private" ? "Now private — others only see Busy" : "Back to normal", ToastKind.Success);
        await LoadAsync();
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..1].ToUpperInvariant() : $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}";
    }

    private async Task OpenEventAsync(Guid id)
    {
        CancelDrawerExit();
        drawerOpen = true; editing = false; detail = null; formError = null;
        StateHasChanged();
        detail = await Auth.GetAsync<EventDetail>($"/api/calendar/events/{id}");
        if (detail is null)
        {
            drawerOpen = false;
            Toasts.Show("That event is no longer available.", ToastKind.Error);
            return;
        }
        _ = Notifs.MarkReadByTargetAsync("calendar", id);
        if (detail.MeetingId is { } mid) _ = Notifs.MarkReadByTargetAsync("meeting", mid);
    }

    private void CloseDrawer()
    {
        if (!drawerOpen) { editing = false; detail = null; return; }
        drawerOpen = false;
        _ = PlayDrawerExitAsync();
    }

    private async Task PlayDrawerExitAsync()
    {
        drawerExitCts?.Cancel();
        var cts = drawerExitCts = new CancellationTokenSource();
        drawerClosing = true;
        try { await Task.Delay(DrawerExitMs, cts.Token); } catch (TaskCanceledException) { return; }
        drawerClosing = false;
        // Content is cleared only after the slide-out so the drawer doesn't flash a spinner while leaving.
        if (!drawerOpen) { editing = false; detail = null; }
        StateHasChanged();
    }

    /// Opening (or re-opening mid-exit) cancels a pending exit.
    private void CancelDrawerExit() { drawerExitCts?.Cancel(); drawerClosing = false; }

    private string JustSaved(FeedItem i) => justSavedId is { } id && i.EventId == id ? "cal-evpop" : "";

    private async Task PopSavedAsync(Guid id)
    {
        justSavedId = id;
        StateHasChanged();
        await Task.Delay(600);
        if (justSavedId == id) { justSavedId = null; StateHasChanged(); }
    }

    private void DrawerKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") CloseDrawer();
    }

    private async Task RsvpAsync(string response, string? onBehalfOf = null)
    {
        if (detail?.MeetingId is not { } mid) return;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/meetings/{mid}/rsvp", new { response, onBehalfOf });
        if (!res.IsSuccessStatusCode) { Toasts.Show(onBehalfOf is null ? "Could not save your reply." : await ErrorAsync(res), ToastKind.Error); return; }
        await OpenEventAsync(detail.Id);
        await LoadAsync();
    }

    // ── Create / edit ──

    private sealed class EventForm
    {
        public Guid? Id { get; set; }
        public Guid? MeetingId { get; set; }
        public string Title { get; set; } = "";
        public bool AllDay { get; set; }
        public DateTime StartDate { get; set; } = DateTime.Today;
        public TimeOnly StartTime { get; set; } = new(9, 0);
        public DateTime EndDate { get; set; } = DateTime.Today;
        public TimeOnly EndTime { get; set; } = new(10, 0);
        /// "" = Event, voice, room, inperson (v3 kind tiles).
        public string Kind { get; set; } = "";
        public string? RoomKey { get; set; }
        public string? Location { get; set; }
        public string? Link { get; set; }
        public string? Notes { get; set; }
        public int Reminder { get; set; } = 15;
        public string RoomIdText { get; set; } = "";
        public List<MemberSearchRow> People { get; set; } = [];
        public string Visibility { get; set; } = "busy";
    }

    private DateTime DefaultStart()
    {
        var baseDay = view is "day" ? anchor : DateTime.Today;
        var next = DateTime.Now.AddHours(1);
        return baseDay == DateTime.Today ? DateTime.Today.AddHours(Math.Min(next.Hour, 23)) : baseDay.AddHours(9);
    }

    private void OpenCreate(DateTime start, bool allDay = false)
    {
        if (!CanCreate) return; // a view delegate looking at someone else's calendar
        form = new EventForm
        {
            AllDay = allDay,
            StartDate = start.Date, StartTime = TimeOnly.FromDateTime(start),
            EndDate = start.AddHours(1).Date, EndTime = TimeOnly.FromDateTime(start.AddHours(1)),
            Reminder = allDay ? 0 : 15,
            Visibility = DefaultFormVisibility,
        };
        fat = null; prefillSource = null; roomCheck = null;
        ResetRepeat();
        SyncInvitees();
        if (form.EndDate > form.StartDate && !allDay) { form.EndDate = form.StartDate; form.EndTime = new TimeOnly(23, 59); }
        detail = null; formError = null;
        CancelDrawerExit();
        drawerOpen = true; editing = true;
    }

    private void StartEdit()
    {
        if (detail is null) return;
        var s = detail.AllDay ? detail.StartAt.Date : detail.StartAt.ToLocalTime();
        var e = detail.AllDay ? detail.EndAt.Date.AddDays(-1) : detail.EndAt.ToLocalTime();
        form = new EventForm
        {
            Id = detail.Id, MeetingId = detail.MeetingId, Title = detail.Title, AllDay = detail.AllDay,
            StartDate = s.Date, StartTime = TimeOnly.FromDateTime(s), EndDate = e.Date < s.Date ? s.Date : e.Date, EndTime = TimeOnly.FromDateTime(e),
            Location = CallsSchedule.IsOnline(detail.Location) || IPop.Application.Hall.HallBooking.RoomKey(detail.Location) is not null ? null : detail.Location,
            Link = detail.Link, Notes = detail.Notes, Reminder = detail.ReminderMinutes ?? 0,
            RoomIdText = detail.RoomId?.ToString() ?? "",
            People = detail.Attendees.Where(a => a.Nik != (detail.OwnerNik ?? Auth.Me?.Nik)).Select(a => new MemberSearchRow(a.Nik, a.Name, null, null)).ToList(),
            Visibility = detail.Visibility == "private" ? "private" : "busy",
        };
        (form.Kind, form.RoomKey) = IPop.Web.Components.Meetings.CalendarKinds.FormKind(detail.MeetingKind, detail.RoomKey, detail.Location,
            detail.Attendees.Count > 0, isMeeting: detail.MeetingId is not null);
        formError = null;
        editing = true;
        fat = null; prefillSource = null; roomCheck = null;
        ResetRepeat(detail.Recurrence);
        SyncInvitees();
        if (form.People.Count > 0 && !form.AllDay) _ = InvokeAsync(async () => { await LoadFreeBusyAsync(); StateHasChanged(); });
    }

    private void CancelEdit()
    {
        if (form.Id is null) CloseDrawer();
        else editing = false;
    }

    private void AddPerson(MemberSearchRow row)
    {
        if (row.Nik == Auth.Me?.Nik || form.People.Any(p => p.Nik == row.Nik)) return;
        form.People.Add(row);
        SyncInvitees();
        fat = null;
        if (!form.AllDay) _ = InvokeAsync(async () => { await LoadFreeBusyAsync(); StateHasChanged(); });
    }

    private void RemovePerson(MemberSearchRow row)
    {
        form.People.Remove(row);
        SyncInvitees();
        fat = null;
        if (form.People.Count > 0 && !form.AllDay) _ = InvokeAsync(async () => { await LoadFreeBusyAsync(); StateHasChanged(); });
    }

    private Components.AddressBookDialog? addressBook;

    private async Task OpenAddressBookAsync()
    {
        if (addressBook is null) return;
        foreach (var row in await addressBook.PickAsync("Invite participants", form.People.Select(p => p.Nik)))
            AddPerson(row);
        StateHasChanged();
    }

    private async Task SaveAsync()
    {
        formError = null;
        DateTime start, end;
        if (form.AllDay)
        {
            start = DateTime.SpecifyKind(form.StartDate.Date, DateTimeKind.Utc);
            end = DateTime.SpecifyKind(form.EndDate.Date.AddDays(1), DateTimeKind.Utc);
        }
        else
        {
            start = form.StartDate.Date.Add(form.StartTime.ToTimeSpan()).ToUniversalTime();
            end = form.EndDate.Date.Add(form.EndTime.ToTimeSpan()).ToUniversalTime();
        }
        if (end <= start) { formError = "End must be after start."; return; }
        if (IPop.Web.Components.Meetings.CalendarKinds.Validate(form.Kind, form.RoomKey, form.Location) is { } kindError) { formError = kindError; return; }
        var (place, placeLink, kind) = IPop.Web.Components.Meetings.CalendarKinds.SavePlace(form.Kind, form.RoomKey, form.Location, form.Link);
        // A new repeating room booking checks every date first (v3 §3): clashes are skipped or moved before saving.
        List<object>? overrides = null;
        if (form.Id is null && kind == IPop.Web.Components.Meetings.MeetingKinds.Room && RepeatRule is { } rule)
        {
            if (roomCheck is null || roomCheckKey != $"{form.RoomKey}|{start:o}|{end:o}|{rule}")
            {
                await CheckRoomSeriesAsync(start, end, rule);
                if (roomCheck is { Booked: > 0 }) return;    // the form now shows the clashes; Save again books
            }
            overrides = RoomOverrides();
        }
        Guid? roomId = Guid.TryParse(form.RoomIdText, out var rid) ? rid : null;
        var body = new
        {
            title = form.Title, startAt = start, endAt = end, allDay = form.AllDay,
            location = place, link = placeLink, notes = form.Notes, kind,
            reminderMinutes = form.Reminder > 0 ? form.Reminder : (int?)null,
            roomId, visibility = form.Visibility,
            // Managing someone's calendar: a new event belongs to them (the API records me as "on behalf").
            ownerNik = form.Id is null ? managing?.Nik : null,
            // Meeting edits resend the full list (organizer stays); personal events only send when inviting.
            attendeeNiks = roomId is null && (form.MeetingId is not null || form.People.Count > 0) ? form.People.Select(p => p.Nik).ToList() : null,
            // New events only: a series is created once; later edits change it through the scope question.
            recurrence = form.Id is null ? RepeatRule : null,
            overrides,
        };
        var scope = "this";
        if (form.Id is not null && InSeries)
        {
            var picked = await AskScopeAsync("Change");
            if (picked is null) return;
            scope = picked;
        }
        saving = true;
        try
        {
            var res = form.Id is { } id
                ? await Auth.SendAsync(HttpMethod.Put, $"/api/calendar/events/{id}?scope={scope}", body)
                : await Auth.SendAsync(HttpMethod.Post, "/api/calendar/events", body);
            if (!res.IsSuccessStatusCode)
            {
                formError = await ErrorAsync(res);
                return;
            }
            roomCheck = null;
            var saved = await res.Content.ReadFromJsonAsync<EventDetail>();
            var repeats = form.Id is null && RepeatRule is not null;
            Toasts.Show(form.Id is null
                ? repeats ? $"{RepeatOccurrences} {(saved?.MeetingId is null ? "events" : "meetings")} created · {RepeatLabel}" : saved?.MeetingId is null ? "Event created." : "Meeting created — invitations sent."
                : scope == "this" ? "Event updated." : scope == "all" ? "All meetings in the series updated." : "This and the following meetings updated.", ToastKind.Success);
            editing = false;
            detail = saved;
            if (saved is null) CloseDrawer();
            anchor = form.StartDate.Date;
            await LoadAsync();
            if (saved is not null) _ = InvokeAsync(() => PopSavedAsync(saved.Id));
        }
        catch (Exception ex) { formError = ex.Message; }
        finally { saving = false; }
    }

    private async Task DeleteAsync()
    {
        if (detail is null) return;
        var meeting = detail.MeetingId is not null;
        var scope = "this";
        if (InSeries)
        {
            var picked = await AskScopeAsync(meeting ? "Cancel" : "Delete");
            if (picked is null) return;
            scope = picked;
        }
        else
        {
            var ok = await Confirm.AskAsync(meeting ? "Cancel this meeting?" : "Delete this event?",
                meeting ? "Participants will be notified that it was cancelled." : $"\"{detail.Title}\" will be removed from your calendar.",
                meeting ? "Cancel meeting" : "Delete");
            if (!ok) return;
        }
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/calendar/events/{detail.Id}?scope={scope}");
        if (!res.IsSuccessStatusCode) { Toasts.Show(await ErrorAsync(res), ToastKind.Error); return; }
        Toasts.Show(scope != "this" ? (meeting ? "Meetings cancelled." : "Events deleted.") : meeting ? "Meeting cancelled." : "Event deleted.", ToastKind.Success);
        CloseDrawer();
        await LoadAsync();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage res)
    {
        try
        {
            var b = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
            if (b?.GetValueOrDefault("message")?.ToString() is { Length: > 0 } m) return m;
        }
        catch { }
        return $"Request failed ({(int)res.StatusCode}).";
    }
}
