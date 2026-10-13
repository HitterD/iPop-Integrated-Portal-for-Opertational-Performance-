using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Components.Meetings;

/// Meeting scheduling v3 §2/§3: the booking sheet (replaces the v2 Use-now dialog). Times are held in UTC; the day,
/// "Until 17:00" and the limits are in <see cref="Zone"/> (default the browser's zone).
public partial class BookingSheet : IDisposable
{
    [Inject] private AuthState Auth { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private MeetingDraftHandoff Handoff { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    public const string ModeNow = "now", ModeBook = "book";

    [Parameter, EditorRequired] public string RoomKey { get; set; } = "";
    /// now = Use now (start now); book = the next free slot today (or PrefillStart).
    [Parameter] public string Mode { get; set; } = ModeBook;
    [Parameter] public DateTime? PrefillStart { get; set; }
    /// Closed without booking.
    [Parameter] public EventCallback OnClose { get; set; }
    /// Booked for now: the caller joins this room (MeetingRoomState) — the key may differ from RoomKey after a switch.
    [Parameter] public EventCallback<string> OnJoin { get; set; }
    /// Booked for later (one meeting or a series).
    [Parameter] public EventCallback<string> OnBooked { get; set; }
    /// Clock and zone for tests.
    [Parameter] public DateTime? Now { get; set; }
    [Parameter] public TimeZoneInfo? Zone { get; set; }
    /// Delay before "Other rooms free" is refreshed after a time change (a drag sends many changes). Tests use zero.
    [Parameter] public TimeSpan RefreshDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    internal const int DefaultMinutes = 60, LockLeadMinutes = 10, ReminderMinutes = 10;

    private string key = "";
    private string? keyParam;
    private string title = "";
    private DateTime date;            // local date (Kind unspecified)
    private DateTime start, end;      // UTC
    private RoomDayDto? day;
    private bool dayError;
    private int dayVersion;
    private List<RoomFreeDto> otherFree = [];
    private CancellationTokenSource? freeCts;
    private List<InvitePersonDto> people = [];
    private string repeat = "";
    private SeriesPreview? repeatCheck;
    private readonly Dictionary<DateOnly, string?> choices = [];
    private DateOnly? failedDate;
    private string? capHint;
    private string? error;
    private bool busy;
    private bool disposed;
    /// Phone (v3 Task 10, mockup "Phone"): a bottom sheet with a grab handle, no "More options…". From the app's one
    /// phone check (ipop.mobile.isMobile, max 767 px) so it agrees with every other phone layout.
    private bool phone;

    // ---------------------------------------------------------------- derived

    private TimeZoneInfo Tz => Zone ?? TimeZoneInfo.Local;
    private DateTime NowUtc => MeetingKinds.Utc(Now ?? DateTime.UtcNow);
    private DateTime NowMinute { get { var n = NowUtc; return new DateTime(n.Ticks - n.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc); } }
    private DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(MeetingKinds.Utc(utc), Tz);
    private DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Tz);
    private DateTime TodayLocal => Local(NowUtc).Date;

    private string RoomName => MeetingKinds.RoomName(key) ?? key;
    private string Accent => MeetingKinds.CssClass(MeetingKinds.Room, key);
    private int Capacity => day?.Capacity ?? HallLimits.RoomCapacity;
    private string DefaultTitle => $"{RoomName} meeting";
    private string TitleOrDefault => string.IsNullOrWhiteSpace(title) ? DefaultTitle : title.Trim();
    private TimeSpan Length => end - start;
    private TimeOnly StartLocal => TimeOnly.FromDateTime(Local(start));
    private TimeOnly EndLocal => TimeOnly.FromDateTime(Local(end));
    private string TimeText => $"{Hm(start)}–{Hm(end)}";

    /// "Book and join": the booking starts now (or would have started already), so it is a Use now.
    internal bool BooksNow => start <= NowMinute && repeat == "";
    private bool IsNowStart => start == NowMinute;
    private bool IsUntil17 => Local(end) == date.AddHours(17);
    private RoomBlockDto? Clash => MeetingKinds.Clash(day?.Blocks, start, end);

    /// The limit the server would refuse with (Use now starts now, so "in the past" does not apply to it).
    private string? LimitText
    {
        get
        {
            // An end before the start is a booking past midnight; a range that is over already is in the past
            // (not "too short", which is what measuring from now would say).
            if (end < start) return RoomBookingRules.Message(RoomBookingRules.DifferentDay);
            if (BooksNow && end <= NowMinute) return RoomBookingRules.Message(RoomBookingRules.InPast);
            var code = RoomBookingRules.Validate(BooksNow ? NowMinute : start, end, NowUtc, Tz);
            return code is null ? null : RoomBookingRules.Message(code);
        }
    }

    private bool CanBook => !busy && Clash is null && LimitText is null && end > start;

    // ---------------------------------------------------------------- lifecycle

    protected override async Task OnInitializedAsync()
    {
        try { phone = await JS.InvokeAsync<bool>("ipop.mobile.isMobile"); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException) { phone = false; }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (keyParam == RoomKey) return;
        keyParam = RoomKey;
        key = RoomKey;
        if (Mode == ModeNow)
        {
            date = TodayLocal;
            start = NowMinute;
            end = start.AddMinutes(DefaultMinutes);
        }
        else if (PrefillStart is { } ps)
        {
            start = MeetingKinds.Utc(ps);
            end = start.AddMinutes(DefaultMinutes);
            date = Local(start).Date;
        }
        else
        {
            // The next quarter hour today; moved to the next free slot once the day is loaded.
            date = TodayLocal;
            var n = NowUtc;
            var q = TimeSpan.FromMinutes(15).Ticks;
            start = new DateTime((n.Ticks + q - 1) / q * q, DateTimeKind.Utc);
            end = start.AddMinutes(DefaultMinutes);
        }
        await LoadDayAsync();
        if (disposed) return;
        if (day is not null)
        {
            if (Mode == ModeNow) CapToNextBooking();
            else if (PrefillStart is null) MoveToNextFree();
        }
        await RefreshFreeAsync(immediate: true);
    }

    private async Task LoadDayAsync()
    {
        var version = ++dayVersion;
        dayError = false;
        try
        {
            var r = await Auth.GetResultAsync<RoomDayDto>($"/api/meeting-rooms/{Uri.EscapeDataString(key)}/availability?date={date:yyyy-MM-dd}");
            if (disposed || version != dayVersion) return;
            if (r.Status == GetStatus.Ok && r.Value is not null) day = r.Value;
            else { day = null; dayError = true; }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (version == dayVersion) { day = null; dayError = true; }
        }
    }

    /// Use now: never into the next booking's lock window (StartAt − 10 min).
    private void CapToNextBooking()
    {
        capHint = null;
        if (day?.Blocks.Where(b => MeetingKinds.Utc(b.StartAt) >= start).OrderBy(b => b.StartAt).FirstOrDefault() is not { } next) return;
        var cap = MeetingKinds.Utc(next.StartAt).AddMinutes(-LockLeadMinutes);
        if (end <= cap) return;
        end = cap > start ? cap : start;
        capHint = $"Until {Hm(end)} (next booking)";
    }

    /// Book: the first free slot of the same length from the current start (today: the next quarter hour).
    private void MoveToNextFree()
    {
        if (day is null) return;
        if (MeetingKinds.NextSlot(day.Blocks, start, end, day.WindowEnd, NowUtc) is { } slot)
        {
            (start, end) = slot;
            var q = TimeSpan.FromMinutes(15).Ticks;
            var snapped = new DateTime((start.Ticks + q - 1) / q * q, DateTimeKind.Utc);
            if (MeetingKinds.Clash(day.Blocks, snapped, snapped + (end - start)) is null && snapped + (end - start) <= day.WindowEnd)
                (start, end) = (snapped, snapped + (end - start));
        }
    }

    // ---------------------------------------------------------------- edits

    private async Task SetTimesAsync(DateTime s, DateTime e, bool keepHint = false)
    {
        start = s; end = e; error = null; failedDate = null;
        if (!keepHint) capHint = null;
        await RefreshFreeAsync();
    }

    private Task OnTimelineAsync((DateTime Start, DateTime End) v) => SetTimesAsync(v.Start, v.End);

    private async Task SetDateAsync(DateTime d)
    {
        if (d.Date == date) return;
        var s = Local(start) - date;
        var e = Local(end) - date;
        date = d.Date;
        start = ToUtc(date + s); end = ToUtc(date + e);
        capHint = null; error = null;
        await LoadDayAsync();
        await RefreshFreeAsync(immediate: true);
    }

    /// Pill slots (v3 limits): a start not before this minute today; an end after the start, at most 8 h later and on the
    /// same day (00:00 = the end of the day).
    internal bool StartDisabled(TimeOnly t) => date == TodayLocal && ToUtc(date + t.ToTimeSpan()) < NowMinute;

    internal bool EndDisabled(TimeOnly t)
    {
        var e = ToUtc(t == TimeOnly.MinValue ? date.AddDays(1) : date + t.ToTimeSpan());
        var from = BooksNow ? NowMinute : start;
        return e <= from || e - from > TimeSpan.FromMinutes(RoomBookingRules.MaxMinutes);
    }

    private async Task SetStartTimeAsync(TimeOnly t)
    {
        if (StartDisabled(t)) return;
        var len = Length > TimeSpan.Zero ? Length : TimeSpan.FromMinutes(DefaultMinutes);
        var s = ToUtc(date + t.ToTimeSpan());
        await SetTimesAsync(s, s + len);
    }

    private async Task SetEndTimeAsync(TimeOnly t)
    {
        if (EndDisabled(t)) return;
        var e = ToUtc(date + t.ToTimeSpan());
        if (t == TimeOnly.MinValue) e = ToUtc(date.AddDays(1));
        await SetTimesAsync(start, e);
    }

    /// Now: start at this minute (today), keep the length (at least 30 min, at most 1 h), capped by the next booking.
    private async Task NowChipAsync()
    {
        var len = Math.Clamp(Length.TotalMinutes, 30, 60);
        if (date != TodayLocal) { date = TodayLocal; await LoadDayAsync(); }
        start = NowMinute; end = start.AddMinutes(len);
        error = null;
        CapToNextBooking();
        await RefreshFreeAsync(immediate: true);
    }

    private async Task LengthChipAsync(int minutes)
    {
        end = start.AddMinutes(minutes);
        error = null; capHint = null;
        if (BooksNow) CapToNextBooking();
        await RefreshFreeAsync();
    }

    private async Task UntilChipAsync()
    {
        end = ToUtc(date.AddHours(17));
        error = null; capHint = null;
        if (BooksNow) CapToNextBooking();
        await RefreshFreeAsync();
    }

    private async Task SwitchRoomAsync(string newKey)
    {
        key = newKey;
        error = null;
        await LoadDayAsync();
        if (BooksNow) CapToNextBooking();
        await RefreshFreeAsync(immediate: true);
    }

    /// "Other rooms free at this time" (GET /api/meeting-rooms/free), after a short pause while dragging.
    private async Task RefreshFreeAsync(bool immediate = false)
    {
        freeCts?.Cancel();
        var fromUtc = BooksNow ? NowMinute : start;
        if (end <= fromUtc) { otherFree = []; return; }
        var cts = freeCts = new CancellationTokenSource();
        try
        {
            if (!immediate && RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay, cts.Token);
            var from = fromUtc.ToString("o", En);
            var r = await Auth.GetResultAsync<List<RoomFreeDto>>(
                $"/api/meeting-rooms/free?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(end.ToString("o", En))}", cts.Token);
            if (cts.IsCancellationRequested || disposed) return;
            otherFree = r.Status == GetStatus.Ok && r.Value is not null ? r.Value.Where(x => x.Free && x.Key != key).ToList() : [];
            if (!immediate) StateHasChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or JsonException) { otherFree = []; }
    }

    // ---------------------------------------------------------------- repeat

    private int Iso(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
    private string WeekdayName => date.ToString("dddd", En);

    /// The repeat choices of the sheet (same rule format as the Calendar Repeat picker), counted so nobody books a year.
    private List<AppOption> RepeatOptions =>
    [
        new("", "Doesn't repeat"),
        new("weekday10", "Every weekday · 10 times"),
        new("weekly4", $"Weekly on {WeekdayName} · 4 times"),
        new("weekly12", $"Weekly on {WeekdayName} · 12 times"),
        new("monthly6", $"Monthly on the {date.Day}{Ord(date.Day)} · 6 times"),
    ];

    internal string? RepeatRule => repeat switch
    {
        "weekday10" => "W:1,2,3,4,5;C:10",
        "weekly4" => $"W:{Iso(date.DayOfWeek)};C:4",
        "weekly12" => $"W:{Iso(date.DayOfWeek)};C:12",
        "monthly6" => "M;C:6",
        _ => null,
    };

    private string RepeatLabel => repeat switch
    {
        "weekday10" => "Every weekday",
        "weekly4" or "weekly12" => $"Weekly on {WeekdayName}",
        "monthly6" => $"Monthly on the {date.Day}{Ord(date.Day)}",
        _ => "Doesn't repeat",
    };

    private static string Ord(int d) => d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    private void SetRepeat(string? v) { repeat = v ?? ""; error = null; }

    private string? Choice(DateOnly d) => choices.TryGetValue(d, out var c) ? c : null;
    private void SetChoice(DateOnly d, string? room) { choices[d] = room; failedDate = null; error = null; moveMenu = null; }

    // "Move to…" menu (v3 gaps): one open at a time; arrows move, Enter picks, Escape closes.
    private DateOnly? moveMenu;
    private readonly ElementReference[] moveRefs = new ElementReference[8];
    private int moveFocus;
    private bool focusMove;

    private void ToggleMoveMenu(DateOnly d)
    {
        moveMenu = moveMenu == d ? null : d;
        var occ = repeatCheck?.Occurrences.FirstOrDefault(o => o.Date == d);
        moveFocus = occ is null || Choice(d) is not { } c ? 0 : Math.Max(0, occ.FreeRooms.ToList().IndexOf(c));
        focusMove = moveMenu is not null;
    }

    private void CloseMoveMenu() => moveMenu = null;
    private void PickMove(DateOnly d, string room) => SetChoice(d, room);

    private async Task MoveMenuKeyAsync(KeyboardEventArgs e)
    {
        var n = repeatCheck?.Occurrences.FirstOrDefault(o => o.Date == moveMenu)?.FreeRooms.Count ?? 0;
        switch (e.Key)
        {
            case "Escape": moveMenu = null; return;
            case "ArrowDown": moveFocus = (moveFocus + 1) % Math.Max(1, n); break;
            case "ArrowUp": moveFocus = (moveFocus - 1 + n) % Math.Max(1, n); break;
            case "Home": moveFocus = 0; break;
            case "End": moveFocus = Math.Max(0, n - 1); break;
            default: return;
        }
        focusMove = true;
        await Task.CompletedTask;
    }

    private List<string> MovedTo => repeatCheck?.Occurrences.Where(o => !o.Free).Select(o => Choice(o.Date)).OfType<string>().Distinct().ToList() ?? [];
    private int KeptCount => repeatCheck is null ? 0 : repeatCheck.Occurrences.Count(o => o.Free || Choice(o.Date) is not null);
    private int MovedCount => repeatCheck?.Occurrences.Count(o => !o.Free && Choice(o.Date) is not null) ?? 0;

    private string RepeatButton
    {
        get
        {
            if (repeatCheck is not { } rc || rc.Booked == 0) return $"Book {KeptCount}";
            var moved = MovedTo;
            if (moved.Count == 0) return $"Skip clashes and book {KeptCount}";
            return moved.Count == 1 ? $"Book {KeptCount} · {MovedCount} in {MeetingKinds.RoomName(moved[0])}" : $"Book {KeptCount} · {MovedCount} moved";
        }
    }

    private void BackFromRepeat() { moveMenu = null; repeatCheck = null; error = null; failedDate = null; }

    private string DayText(DateOnly d) =>
        $"{d.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM", En)}{(d.Year != TodayLocal.Year ? " " + d.Year : "")}";

    // ---------------------------------------------------------------- book

    private async Task BookAsync()
    {
        if (!CanBook) return;
        busy = true; error = null;
        try
        {
            if (RepeatRule is { } rule) await PreviewAsync(rule);
            else if (BooksNow) await UseNowAsync();
            else await CreateAsync(null);
        }
        finally
        {
            busy = false;
            if (!disposed) StateHasChanged();
        }
    }

    private object EventBody(string? rule, IReadOnlyList<object>? overrides) => new
    {
        title = TitleOrDefault,
        startAt = start,
        endAt = end,
        location = HallBooking.Location(RoomName),
        kind = MeetingKinds.Room,
        attendeeNiks = people.Select(p => p.Key).ToList(),
        reminderMinutes = ReminderMinutes,
        recurrence = rule,
        overrides,
    };

    private async Task UseNowAsync()
    {
        var body = new { endAt = end, title = TitleOrDefault, attendeeNiks = people.Select(p => p.Key).ToList() };
        using var res = await SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{Uri.EscapeDataString(key)}/use-now", body);
        if (res is null) { error = "The room couldn't be booked right now. Try again."; return; }
        if (res.IsSuccessStatusCode) { await OnJoin.InvokeAsync(key); return; }
        var err = await ReadErrorAsync(res);
        if (err.Code == "room_booked")
        {
            await LoadDayAsync();
            if (err.MaxMinutes is >= RoomBookingRules.MinMinutes and var max)
            {
                start = NowMinute; end = start.AddMinutes(max);
                capHint = $"Until {Hm(end)} (next booking)";
                error = $"The next booking starts soon. You can use {RoomName} until {Hm(end)}.";
            }
            else error = "Someone just booked this room. Pick another room or book a later time.";
            await RefreshFreeAsync(immediate: true);
        }
        else error = err.Message ?? "The room couldn't be booked right now. Try again.";
    }

    private async Task CreateAsync(IReadOnlyList<object>? overrides)
    {
        using var res = await SendAsync(HttpMethod.Post, "/api/calendar/events", EventBody(RepeatRule, overrides));
        if (res is null) { error = "The room couldn't be booked right now. Try again."; return; }
        if (res.IsSuccessStatusCode)
        {
            Toasts.Show(RepeatRule is null ? $"{RoomName} is booked {DateLabel(start)}, {TimeText}." : $"{KeptCount} meetings booked.", ToastKind.Success);
            await OnBooked.InvokeAsync(key);
            return;
        }
        var err = await ReadErrorAsync(res);
        if (err.Date is { } d && repeatCheck is not null)
        {
            failedDate = d;
            error = err.Code == "room_booked"
                ? $"{DayText(d)} was just booked by someone else. Skip it or move it, then book again."
                : $"{DayText(d)}: {err.Message ?? "this date can't be booked."}";
            return;
        }
        if (err.Code == "room_booked")
        {
            await LoadDayAsync();
            error = "Someone just booked this time. Pick another time or another room.";
            await RefreshFreeAsync(immediate: true);
        }
        else error = err.Message ?? "The room couldn't be booked right now. Try again.";
    }

    private async Task PreviewAsync(string rule)
    {
        using var res = await SendAsync(HttpMethod.Post, "/api/calendar/series/preview", new { roomKey = key, startAt = start, endAt = end, recurrence = rule });
        if (res is null) { error = "The dates couldn't be checked right now. Try again."; return; }
        if (!res.IsSuccessStatusCode) { error = (await ReadErrorAsync(res)).Message ?? "The dates couldn't be checked right now. Try again."; return; }
        try { repeatCheck = await res.Content.ReadFromJsonAsync<SeriesPreview>(); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { repeatCheck = null; }
        if (repeatCheck is null) { error = "The dates couldn't be checked right now. Try again."; return; }
        choices.Clear();
        failedDate = null;
    }

    private async Task SaveSeriesAsync()
    {
        if (repeatCheck is not { } rc || busy || KeptCount == 0) return;
        busy = true; error = null; failedDate = null;
        try
        {
            var overrides = rc.Occurrences.Where(o => !o.Free)
                .Select(o => Choice(o.Date) is { } room ? (object)new { date = o.Date, skip = false, roomKey = room } : new { date = o.Date, skip = true })
                .ToList();
            await CreateAsync(overrides);
        }
        finally
        {
            busy = false;
            if (!disposed) StateHasChanged();
        }
    }

    // ---------------------------------------------------------------- more options / close

    /// The full Calendar form, prefilled. People go through MeetingDraftHandoff, never the address.
    internal void MoreOptions()
    {
        Handoff.Set(people);
        var q = new List<string>
        {
            "new=1", "kind=room", "room=" + Uri.EscapeDataString(key),
            "start=" + Uri.EscapeDataString((BooksNow ? NowMinute : start).ToString("o", En)),
            "end=" + Uri.EscapeDataString(end.ToString("o", En)),
        };
        if (!string.IsNullOrWhiteSpace(title)) q.Add("title=" + Uri.EscapeDataString(title.Trim()));
        Nav.NavigateTo("/calendar?" + string.Join('&', q));
    }

    private Task CloseAsync() => busy ? Task.CompletedTask : OnClose.InvokeAsync();

    private async Task KeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") { if (moveMenu is not null) { moveMenu = null; return; } await CloseAsync(); }
    }

    // ---------------------------------------------------------------- helpers

    private string Hm(DateTime utc) => Local(utc).ToString("HH:mm", En);

    private string DateLabel(DateTime utc)
    {
        var d = Local(utc).Date;
        return d == TodayLocal ? "today" : d == TodayLocal.AddDays(1) ? "tomorrow" : "on " + d.ToString("ddd d MMM", En);
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string url, object body)
    {
        try { return await Auth.SendAsync(method, url, body); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    internal sealed record ApiError(string? Code, string? Message, int? MaxMinutes, DateOnly? Date);

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage res)
    {
        try
        {
            var e = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (e.ValueKind != JsonValueKind.Object) return new(null, null, null, null);
            string? Str(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? max = e.TryGetProperty("maxMinutes", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mm) ? mm : null;
            DateOnly? date = DateOnly.TryParseExact(Str("date"), "yyyy-MM-dd", En, DateTimeStyles.None, out var d) ? d : null;
            return new(Str("code"), Str("message"), max, date);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException) { return new(null, null, null, null); }
    }

    /// Client mirror of SeriesPreviewDto (POST /api/calendar/series/preview).
    internal sealed record SeriesPreview(int Total, int Booked, IReadOnlyList<SeriesOccurrence> Occurrences);
    internal sealed record SeriesOccurrence(DateOnly Date, DateTime StartAt, DateTime EndAt, bool Free, string? BookedBy, IReadOnlyList<string> FreeRooms, string? Reason = null);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusMove && moveMenu is not null)
        {
            focusMove = false;
            try { await moveRefs[Math.Clamp(moveFocus, 0, moveRefs.Length - 1)].FocusAsync(); }
            catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException) { }
        }
        try { await JS.InvokeVoidAsync("ipop.icons"); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException) { }
    }

    public void Dispose()
    {
        disposed = true;
        freeCts?.Cancel();
    }
}
