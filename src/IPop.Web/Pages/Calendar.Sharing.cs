// Calendar sharing — "Other calendars" overlay / side by side, the busy popover, per-event
// visibility, the sharing settings dialog and the Find a time assistant in the event form.
using System.Globalization;
using System.Net.Http.Json;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Web;

namespace IPop.Web.Pages;

public partial class Calendar
{
    // ── Client-side mirrors of CalendarSharingEndpoints records ──
    public record OtherCalendar(string Nik, string Name, string? Department, string Color, bool IsVisible);
    public record PersonBusy(string Nik, string Id, DateTime StartAt, DateTime EndAt, bool AllDay, bool Tentative,
        string Visibility, string? Title, string? Location, Guid? MeetingId);
    public record SharingSettings(string DefaultVisibility, bool NotifyDelegateChanges);
    public record FreeBusyBlock(DateTime StartAt, DateTime EndAt, bool Tentative);
    public record FreeBusyLane(string Nik, string Name, List<FreeBusyBlock> Blocks);
    public record FreeBusy(List<FreeBusyLane> Lanes, List<DateTime> Suggestions);

    private static readonly string[] OtherPalette = ["a", "b", "c", "d", "e", "f", "g", "h"];
    private static readonly (string Key, string Label, string Icon, string Hint)[] VisibilityOptions =
    [
        ("public", "Public", "globe", "Everyone sees the title and location"),
        ("busy", "Busy only", "clock", "Others see a Busy block"),
        ("private", "Private", "lock", "A Busy block, even for delegates"),
    ];
    private static readonly (string Key, string Label, string Hint)[] DefaultOptions =
    [
        ("public", "Titles and locations", "New events are Public: everyone sees what and where."),
        ("busy", "Busy only", "New events show as Busy blocks. You can make single events Public."),
        ("hidden", "Hide my calendar details", "Everything shows as Busy to others, even Public events. Invited people still see the meeting."),
    ];
    private const int FatFromHour = 7, FatHours = 12;

    private List<OtherCalendar> others = [];
    private List<FeedItem> otherItems = [];
    private SharingSettings sharing = new("busy", true);
    private bool addingOther, sideBySide, settingsOpen, savingSettings;
    private string draftDefault = "busy";
    private string? otherMenu;
    private CancellationTokenSource? peopleCts;

    /// One grid column: a day (overlay mode), or in Day "Side by side" one person's calendar.
    private sealed record GridCol(DateTime Day, bool Split, OtherCalendar? Person);

    private List<OtherCalendar> VisibleOthers => others.Where(o => o.IsVisible).ToList();
    private bool SideBySideActive => view == "day" && sideBySide && VisibleOthers.Count > 0;

    private List<GridCol> GridColumns(List<DateTime> days) => SideBySideActive
        ? [new GridCol(days[0], true, null), .. VisibleOthers.Select(o => new GridCol(days[0], true, o))]
        : days.Select(d => new GridCol(d, false, null)).ToList();

    private static bool InCol(GridCol? col, FeedItem i) => col is not { Split: true }
        || (col.Person is null ? i.OwnerNik is null : i.OwnerNik == col.Person.Nik);

    /// Overlay items of the checked people. A meeting I'm in already shows as my own item, so the
    /// overlay copy is dropped unless the columns are split per person.
    private IEnumerable<FeedItem> OverlayItems
    {
        get
        {
            var shown = VisibleOthers.Select(o => o.Nik).ToHashSet();
            var split = SideBySideActive;
            var mine = split ? [] : items.Where(i => i.MeetingId is not null).Select(i => i.MeetingId!.Value).ToHashSet();
            return otherItems.Where(o => shown.Contains(o.OwnerNik!) && (o.MeetingId is not { } m || !mine.Contains(m)));
        }
    }

    private string ColorOf(string? nik) => others.FirstOrDefault(o => o.Nik == nik)?.Color ?? "a";

    private string OtherClass(FeedItem i) => i.OwnerNik is null ? ""
        : $"oc-{ColorOf(i.OwnerNik)} busy{(i.Kind == "busy-tentative" ? " tentative" : "")}{(i.Visibility == "public" ? " has-title" : "")}";

    private string OtherName(string? nik) => others.FirstOrDefault(o => o.Nik == nik)?.Name ?? nik ?? "";

    // ── Loading ──

    private async Task LoadSharingAsync()
    {
        try
        {
            var o = Auth.GetAsync<List<OtherCalendar>>("/api/calendar/others");
            var s = Auth.GetAsync<SharingSettings>("/api/calendar/settings");
            others = await o ?? [];
            sharing = await s ?? sharing;
        }
        catch { /* sharing is additive: the own calendar still works without it */ }
    }

    private async Task LoadPeopleAsync()
    {
        peopleCts?.Cancel();
        var cts = peopleCts = new CancellationTokenSource();
        var niks = VisibleOthers.Select(o => o.Nik).ToList();
        if (niks.Count == 0 || loadedFrom == default) { otherItems = []; return; }
        try
        {
            var url = $"/api/calendar/people?niks={Uri.EscapeDataString(string.Join(',', niks))}" +
                $"&from={Uri.EscapeDataString(loadedFrom.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(loadedTo.ToUniversalTime().ToString("o"))}";
            var res = await Auth.GetAsync<List<PersonBusy>>(url, cts.Token);
            if (cts.IsCancellationRequested || res is null) return;
            otherItems = res.Select(ToFeedItem).ToList();
            StateHasChanged();
        }
        catch { /* overlay is best effort; the next navigation retries */ }
    }

    private FeedItem ToFeedItem(PersonBusy p) => new(
        $"other:{p.Nik}:{p.Id}", "other", p.Tentative ? "busy-tentative" : "busy",
        p.Title ?? (p.Tentative ? "Tentative" : "Busy"), p.StartAt, p.EndAt, p.AllDay,
        null, OtherName(p.Nik), null, p.MeetingId, null, false, p.Location, null, p.Visibility, p.Nik);

    // ── Other calendars list ──

    private async Task AddOtherAsync(MemberSearchRow row)
    {
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/calendar/others", new { nik = row.Nik });
        if (!res.IsSuccessStatusCode) { Toasts.Show(await ErrorAsync(res), ToastKind.Error); return; }
        others = await res.Content.ReadFromJsonAsync<List<OtherCalendar>>() ?? others;
        addingOther = false;
        await LoadPeopleAsync();
    }

    private Task ToggleOtherAsync(OtherCalendar o) => UpdateOtherAsync(o, new { isVisible = !o.IsVisible });

    private async Task SetOtherColorAsync(OtherCalendar o, string color)
    {
        await UpdateOtherAsync(o, new { color });
        otherMenu = null;
    }

    private async Task UpdateOtherAsync(OtherCalendar o, object body)
    {
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/calendar/others/{Uri.EscapeDataString(o.Nik)}", body);
        if (!res.IsSuccessStatusCode) { Toasts.Show("Could not update that calendar.", ToastKind.Error); return; }
        others = await res.Content.ReadFromJsonAsync<List<OtherCalendar>>() ?? others;
        await LoadPeopleAsync();
    }

    private async Task RemoveOtherAsync(OtherCalendar o)
    {
        otherMenu = null;
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/calendar/others/{Uri.EscapeDataString(o.Nik)}");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Could not remove that calendar.", ToastKind.Error); return; }
        others = await res.Content.ReadFromJsonAsync<List<OtherCalendar>>() ?? others;
        otherItems.RemoveAll(i => i.OwnerNik == o.Nik);
    }

    private async Task SetSideBySideAsync(bool on)
    {
        sideBySide = on;
        await SavePrefsAsync();
    }

    // ── Busy popover ──

    private FeedItem? pop;
    private double popX, popY;

    private void OpenPopover(FeedItem item, MouseEventArgs? e)
    {
        pop = item;
        popX = (e?.ClientX ?? 200) + 8;
        popY = (e?.ClientY ?? 200) + 8;
    }

    private FeedItem? PopMine => pop?.MeetingId is { } mid ? items.FirstOrDefault(i => i.MeetingId == mid && i.EventId is not null) : null;

    private async Task OpenPopMineAsync()
    {
        var mine = PopMine; pop = null;
        if (mine is not null) await OpenItemAsync(mine);
    }

    private async Task MeetWithAsync(string nik, string name, DateTime? start = null)
    {
        pop = null; otherMenu = null;
        OpenCreate(start ?? DefaultStart());
        AddPerson(new MemberSearchRow(nik, name, null, null));
        await Task.CompletedTask;
    }

    private static string VisLabel(string? v) => v switch
    {
        "public" => "Public — everyone sees the title and location",
        "private" => "Private — others see Busy, even delegates",
        _ => "Busy only — others see a Busy block",
    };

    private static string VisIcon(string? v) => v switch { "public" => "globe", "private" => "lock", _ => "clock" };

    // ── Sharing settings ──

    private void OpenSettings()
    {
        draftDefault = sharing.DefaultVisibility;
        draftNotify = sharing.NotifyDelegateChanges;
        settingsOpen = true;
    }

    private async Task SaveSettingsAsync()
    {
        savingSettings = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Put, "/api/calendar/settings",
                new { defaultVisibility = draftDefault, notifyDelegateChanges = draftNotify });
            if (!res.IsSuccessStatusCode) { Toasts.Show(await ErrorAsync(res), ToastKind.Error); return; }
            sharing = await res.Content.ReadFromJsonAsync<SharingSettings>() ?? sharing;
            settingsOpen = false;
            Toasts.Show("Sharing settings saved.", ToastKind.Success);
        }
        finally { savingSettings = false; }
    }

    private string DefaultFormVisibility => sharing.DefaultVisibility == "public" ? "public" : "busy";

    // ── Find a time ──

    private FreeBusy? fat;
    private DateTime fatDay, fatFrom;
    private bool fatLoading;
    private string fatKey = "";

    private bool ShowFindATime => editing && !form.AllDay && string.IsNullOrEmpty(form.RoomIdText) && form.People.Count > 0;

    private int FormMinutes
    {
        get
        {
            var s = form.StartDate.Date.Add(form.StartTime.ToTimeSpan());
            var e = form.EndDate.Date.Add(form.EndTime.ToTimeSpan());
            return Math.Clamp((int)(e - s).TotalMinutes, 15, 480);
        }
    }

    private async Task LoadFreeBusyAsync(DateTime? day = null)
    {
        if (form.People.Count == 0) { fat = null; return; }
        fatDay = (day ?? form.StartDate).Date;
        if (fatDay < DateTime.Today) fatDay = DateTime.Today;
        var niks = string.Join(',', form.People.Select(p => p.Nik));
        var key = $"{niks}|{FormMinutes}|{form.MeetingId}";
        // Lanes cover a week from the first loaded day; stepping inside it needs no refetch.
        if (fat is not null && key == fatKey && fatDay >= fatFrom && fatDay < fatFrom.AddDays(7)) return;
        fatLoading = true;
        StateHasChanged();
        try
        {
            var from = fatDay; var to = fatDay.AddDays(7);
            var url = $"/api/calendar/freebusy?niks={Uri.EscapeDataString(niks)}&minutes={FormMinutes}" +
                $"&from={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}" +
                (form.MeetingId is { } mid ? $"&meeting={mid}" : "");
            var res = await Auth.GetAsync<FreeBusy>(url);
            if (res is not null) { fat = res; fatFrom = from; fatKey = key; }
        }
        catch { Toasts.Show("Could not load availability.", ToastKind.Error); }
        finally { fatLoading = false; }
    }

    private Task FatShiftAsync(int days)
    {
        var d = fatDay.AddDays(days);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(days < 0 ? -1 : 1);
        return d < DateTime.Today ? Task.CompletedTask : LoadFreeBusyAsync(d);
    }

    private void ApplySlot(DateTime utc)
    {
        var minutes = FormMinutes;
        var s = utc.ToLocalTime();
        var e = s.AddMinutes(minutes);
        form.StartDate = s.Date; form.StartTime = TimeOnly.FromDateTime(s);
        form.EndDate = e.Date; form.EndTime = TimeOnly.FromDateTime(e);
        fatDay = s.Date;
    }

    /// left/width (in % of the 07:00–19:00 track) of a block on <see cref="fatDay"/>, or null when outside.
    private (string Left, string Width)? FatSpan(DateTime startUtc, DateTime endUtc)
    {
        var t0 = fatDay.AddHours(FatFromHour);
        var s = Math.Max((startUtc.ToLocalTime() - t0).TotalHours, 0);
        var e = Math.Min((endUtc.ToLocalTime() - t0).TotalHours, FatHours);
        if (e <= s) return null;
        string P(double v) => (100 * v / FatHours).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        return (P(s), P(e - s));
    }

    private (string Left, string Width)? FatProposal()
    {
        var s = form.StartDate.Date.Add(form.StartTime.ToTimeSpan());
        var e = form.EndDate.Date.Add(form.EndTime.ToTimeSpan());
        return s.Date != fatDay || e <= s ? null : FatSpan(s.ToUniversalTime(), e.ToUniversalTime());
    }

    private bool ProposalClashes(FreeBusyLane lane)
    {
        var s = form.StartDate.Date.Add(form.StartTime.ToTimeSpan()).ToUniversalTime();
        var e = form.EndDate.Date.Add(form.EndTime.ToTimeSpan()).ToUniversalTime();
        return lane.Blocks.Any(b => b.StartAt < e && b.EndAt > s);
    }
}
