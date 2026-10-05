// Calendar access (approved mockup docs/mockups/ipop-apple/calendar-access.html): the ⚙ settings sheet, the
// one-time "now shows only busy times" card, Allow links from a request notification, "Ask to see details"
// on someone's Busy block, and viewing one person's calendar at /calendar/person/{nik}.
using System.Net.Http.Json;
using IPop.Web.Services;

namespace IPop.Web.Pages;

public partial class Calendar
{
    public record PersonInfo(string Nik, string Name, string? Department, string? Site, string? Access, string AccessLabel,
        bool Following, bool Self, DateTime? NowBusyUntil, DateTime? NextBusyAt);
    public record Follower(string Nik, string Name, string? Department);

    [Microsoft.AspNetCore.Components.Parameter] public string? PersonNik { get; set; }
    [Microsoft.AspNetCore.Components.SupplyParameterFromQuery(Name = "share")] public int? ShareQuery { get; set; }
    [Microsoft.AspNetCore.Components.SupplyParameterFromQuery(Name = "allow")] public string? AllowQuery { get; set; }

    private bool settingsOpen, showIntro, introBusy;
    private (string Nik, string Name)? allowFor;
    private List<Follower> introFollowers = [];
    private readonly HashSet<string> askedFor = [];

    // ── Someone's calendar ──
    private PersonInfo? person;
    private bool showMineBehind = true;
    private string? loadedPersonNik;

    private bool PersonMode => !string.IsNullOrEmpty(PersonNik);
    /// /calendar/person/{token}: links carry a per-tab token, not the NIK (office policy); old NIK links still work.
    private string? PersonKey => PLinks.Nik(PersonNik);

    private async Task LoadPersonAsync()
    {
        if (!PersonMode) { person = null; loadedPersonNik = null; return; }
        if (loadedPersonNik == PersonNik && person is not null) return;
        loadedPersonNik = PersonNik;
        person = await Auth.GetAsync<PersonInfo>($"/api/calendar/person/{Uri.EscapeDataString(PersonKey!)}");
        if (person is null) { Toasts.Show("That person was not found.", ToastKind.Error); Nav.NavigateTo("/calendar"); return; }
        if (person.Self) { Nav.NavigateTo("/calendar"); return; }
        Layout.SetHeader("Calendar", $"{person.Name}'s calendar");
    }

    /// Items shown on the person page: their blocks, plus mine faintly behind when asked.
    private IEnumerable<FeedItem> PersonItems => otherItems.Where(i => i.OwnerNik == person?.Nik);

    private async Task FollowPersonAsync()
    {
        if (person is null) return;
        if (person.Following)
        {
            var existing = others.FirstOrDefault(o => o.Nik == person.Nik);
            if (existing is not null) await RemoveOtherAsync(existing);
            person = person with { Following = false };
            Toasts.Show($"Stopped following {person.Name}.", ToastKind.Info);
            return;
        }
        await AddOtherAsync(new MemberSearchRow(person.Nik, person.Name, null, person.Department));
        person = person with { Following = true };
        followPop = true;
        Toasts.Show($"{person.Name} is in your Calendars list.", ToastKind.Success);
    }

    private bool followPop;

    private string PersonPronounBusy => person?.Access switch
    {
        "titles" => "You can see event titles",
        "view" => "You can see all details",
        "edit" => "You can edit their events",
        "full" => "You act on their behalf",
        _ => "You can see when they're busy",
    };

    private string PersonAccessIcon => person?.Access switch
    {
        "titles" => "text", "view" => "file-text", "edit" => "pencil", "full" => "user-cog", _ => "clock",
    };

    private string? FreeLine(PersonInfo p)
    {
        var now = DateTime.UtcNow;
        if (p.NowBusyUntil is { } until && until > now) return $"Busy until {until.ToLocalTime():HH:mm}";
        if (p.NextBusyAt is { } next) return $"Free now · busy {next.ToLocalTime():HH:mm}";
        return "Free for the rest of today";
    }

    // ── Settings sheet ──

    private void OpenSettings(string? allowNik = null, string? allowName = null)
    {
        allowFor = allowNik is null ? null : (allowNik, allowName ?? allowNik);
        settingsOpen = true;
    }

    private async Task OnSettingsChangedAsync()
    {
        introFollowers = [];
        await LoadSharingAsync();
        await LoadManagedAsync();
    }

    private async Task OpenFromQueryAsync()
    {
        if (PLinks.Nik(AllowQuery) is { } allowNik)
        {
            var who = await Auth.GetAsync<PersonInfo>($"/api/calendar/person/{Uri.EscapeDataString(allowNik)}");
            if (who is not null) OpenSettings(who.Nik, who.Name);
        }
        else if (ShareQuery == 1) OpenSettings();
    }

    // ── One-time card ──

    private async Task LoadIntroAsync()
    {
        try
        {
            var s = await Auth.GetAsync<SharingSettings>("/api/calendar/settings");
            if (s is null || s.SharingIntroSeen) { showIntro = false; return; }
            introFollowers = await Auth.GetAsync<List<Follower>>("/api/calendar/followers") ?? [];
            showIntro = true;
        }
        catch { showIntro = false; }
    }

    private async Task IntroShareTitlesAsync()
    {
        introBusy = true;
        try
        {
            var done = 0;
            foreach (var f in introFollowers)
            {
                using var res = await Auth.SendAsync(HttpMethod.Put, $"/api/calendar/delegates/{Uri.EscapeDataString(f.Nik)}", new { level = "titles" });
                if (res.IsSuccessStatusCode) done++;
            }
            await DismissIntroAsync();
            Toasts.Show(done == 1 ? "1 person can see your event titles again." : $"{done} people can see your event titles again.", ToastKind.Success);
        }
        finally { introBusy = false; }
    }

    private async Task IntroChooseAsync()
    {
        await DismissIntroAsync();
        OpenSettings();
    }

    private async Task DismissIntroAsync()
    {
        showIntro = false;
        try { using var _ = await Auth.SendAsync(HttpMethod.Put, "/api/calendar/settings", new { sharingIntroSeen = true }); } catch { }
    }

    // ── Ask to see details ──

    private async Task AskForDetailsAsync(string nik, string name)
    {
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/calendar/people/{Uri.EscapeDataString(nik)}/ask");
        if (!res.IsSuccessStatusCode) { Toasts.Show(await ErrorAsync(res), ToastKind.Error); return; }
        askedFor.Add(nik);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Toasts.Show(body?.GetValueOrDefault("sent") == true ? $"Request sent to {name}." : $"You already asked {name} today.", ToastKind.Success);
    }

    /// Why someone's block shows no title — shown in the Busy popover.
    private static string WhyBusy(FeedItem po, string name) => po.Visibility switch
    {
        "private" => $"{name} marked this event Private.",
        _ => $"{name} only shares when they're busy. Ask if you need to see what it is.",
    };
}
