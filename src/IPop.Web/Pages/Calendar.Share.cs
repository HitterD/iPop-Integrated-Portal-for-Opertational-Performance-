// The Share dialog (toolbar "Share" button): two tabs — "Who sees what" with a live preview of my
// week as a colleague or a delegate sees it, and "Delegates". Plus the one-time intro card and
// "Ask to see details" on a colleague's Busy block.
using System.Net.Http.Json;
using IPop.Web.Services;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Calendar
{
    public record PreviewItem(string Id, DateTime StartAt, DateTime EndAt, bool AllDay, bool Tentative, string Visibility,
        string YourTitle, string? TheySee)
    {
        public DateTime Start => AllDay ? StartAt.Date : StartAt.ToLocalTime();
        public DateTime End => AllDay ? EndAt.Date : EndAt.ToLocalTime();
    }

    [Microsoft.AspNetCore.Components.SupplyParameterFromQuery(Name = "share")] public int? ShareQuery { get; set; }

    private const string IntroKey = "ipop.calendar.shareIntro";
    private string shareTab = "who";
    private string previewAs = "colleague";
    private bool applyToUpcoming, showIntro;
    private List<PreviewItem>? preview;
    private CancellationTokenSource? previewCts;
    private readonly HashSet<string> askedFor = [];

    private static readonly (string Key, string Label, string Icon)[] ShareTabs =
        [("who", "Who sees what", "eye"), ("delegates", "Delegates", "user-cog")];

    /// One line for the toolbar Share button tooltip and the intro card.
    private string ShareSummary => sharing.DefaultVisibility switch
    {
        "public" => "Colleagues see your event titles and locations",
        "hidden" => "Colleagues only see when you're busy",
        _ => "Colleagues see when you're busy, not what",
    };

    private async Task LoadIntroAsync()
    {
        try { showIntro = await Auth.GetUserSettingAsync(IntroKey) is null; }
        catch { showIntro = false; }
    }

    private async Task DismissIntroAsync()
    {
        showIntro = false;
        try { await Auth.SetUserSettingAsync(IntroKey, "1"); } catch { }
    }

    private async Task OpenShareAsync(string tab = "who")
    {
        OpenSettings();
        shareTab = tab;
        applyToUpcoming = false;
        if (showIntro) await DismissIntroAsync();
        await LoadPreviewAsync();
    }

    private async Task SetDraftDefaultAsync(string key)
    {
        draftDefault = key;
        await LoadPreviewAsync();
    }

    private async Task SetPreviewAsAsync(string who)
    {
        previewAs = who;
        await LoadPreviewAsync();
    }

    /// This week (Mon–Sun) from the server, so the preview uses the real visibility rule.
    private async Task LoadPreviewAsync()
    {
        previewCts?.Cancel();
        var cts = previewCts = new CancellationTokenSource();
        var from = PreviewFrom; var to = from.AddDays(7);
        try
        {
            var url = $"/api/calendar/preview?as={previewAs}&hidden={(draftDefault == "hidden" ? "true" : "false")}" +
                $"&from={Uri.EscapeDataString(from.ToUniversalTime().ToString("o"))}&to={Uri.EscapeDataString(to.ToUniversalTime().ToString("o"))}";
            var res = await Auth.GetAsync<List<PreviewItem>>(url, cts.Token);
            if (!cts.IsCancellationRequested) preview = res ?? [];
        }
        catch (OperationCanceledException) { }
        catch { if (!cts.IsCancellationRequested) preview = []; }
        if (!cts.IsCancellationRequested) StateHasChanged();
    }

    private static DateTime PreviewFrom
    {
        get
        {
            var today = DateTime.Today;
            return today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        }
    }

    private IEnumerable<IGrouping<DateTime, PreviewItem>> PreviewDays =>
        (preview ?? []).Where(i => i.End > DateTime.Now.Date).GroupBy(i => i.Start.Date).OrderBy(g => g.Key).Take(4);

    /// The Busy-only line in the dialog: switching from Busy to Public can re-label what's already there.
    private bool ApplyOffered => draftDefault != sharing.DefaultVisibility && draftDefault != "hidden";

    // ── Ask to see details ──

    private async Task AskForDetailsAsync(string nik, string name)
    {
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/calendar/people/{Uri.EscapeDataString(nik)}/ask");
        if (!res.IsSuccessStatusCode) { Toasts.Show(await ErrorAsync(res), ToastKind.Error); return; }
        askedFor.Add(nik);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, bool>>();
        Toasts.Show(body?.GetValueOrDefault("sent") == true ? $"Asked {name}. They'll get a notification." : $"You already asked {name} today.",
            ToastKind.Success);
    }

    /// Why a colleague's block shows no title — shown in the Busy popover.
    private static string WhyBusy(FeedItem po, string name) => po.Visibility switch
    {
        "private" => $"{name} marked this event Private.",
        _ => $"{name} shares Busy only, so titles stay hidden.",
    };
}
