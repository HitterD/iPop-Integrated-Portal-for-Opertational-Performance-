// New ICT ticket — one calm page (docs/mockups/ipop-apple/tickets.html, tabs 2, 3, 5).
// Draft autosaves per account (PageCache: localStorage scoped to the NIK, wiped on sign-out);
// attachments stay in memory only, validated with TicketAttachmentRules before upload.
// /tickets/new?from={id} prefills from a ticket that couldn't be sent ("Edit").
using System.Globalization;
using IPop.Application.Tickets;
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class TicketNew
{
    [CascadingParameter] public MainLayout? Layout { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public Guid? From { get; set; }

    [Inject] private AuthState Auth { get; set; } = default!;
    [Inject] private TicketApi Api { get; set; } = default!;
    [Inject] private PageCache Cache { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ConfirmService Confirm { get; set; } = default!;

    private const string DraftKey = "tickets.draft";
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    /// The draft as stored. Files are not part of it (they can be 50 MB).
    public sealed record Draft
    {
        public string Module { get; set; } = "it-support";
        public string? Category { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Device { get; set; } = "";
        public string Software { get; set; } = "";
        public string Priority { get; set; } = "MEDIUM";
        public string CriticalReason { get; set; } = "";
        public string? HardwareType { get; set; }
        public string? ScheduledDate { get; set; }
        public string? ScheduledTime { get; set; }

        public bool IsBlank => Title.Length == 0 && Description.Length == 0 && Device.Length == 0 && Software.Length == 0
            && CriticalReason.Length == 0 && HardwareType is null && ScheduledDate is null;
    }

    private sealed record PickedFile(string Id, string Name, long Size, bool Ok, string? Error, string? Base64);

    private Draft s = new();
    private List<TicketModuleDto>? modules;
    private bool modulesFailed;
    private readonly List<PickedFile> files = [];

    private string saveState = "";   // "", "saving", "saved"
    private int savedTick, fadeKey;
    private CancellationTokenSource? saveCts;

    private string? errKey, errMsg;
    private (string Title, string Body)? formError;
    private bool notRegistered, nrFlash, submitting, dropFull, catStagger, disposed;
    private string? popModule, popDay, shakeDay, popSlot, popTemplate;
    /// The quick template last applied, and its text, so switching templates over an untouched one needs no confirm.
    private string? template, lastTemplateTitle, lastTemplateDescription;
    private CreateTicketResultDto? sheet;
    private bool sheetClosing;
    private string? focusErrorSel;

    private DateOnly calMonth;
    private int calDir;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    private static DateOnly FirstMonth => new(Today.Year, Today.Month, 1);

    private TicketModuleDto? Module => modules?.FirstOrDefault(m => m.Slug == s.Module);
    private bool IsInstall => s.Category == TicketUi.HardwareInstallation;
    private IReadOnlyList<string> Priorities => Module?.Priorities is { Count: > 0 } p ? p : TicketUi.Priorities;
    private string MyName => IPop.Application.PersonName.Display(Auth.Me?.Name) is { Length: > 0 } n ? n : "you";

    private string ScheduleSummary => s.ScheduledDate is null ? "Pick a date and time"
        : s.ScheduledTime is null ? $"{TicketUi.ScheduleLabel(s.ScheduledDate, null)} · now pick a time"
        : $"{TicketUi.ScheduleLabel(s.ScheduledDate, s.ScheduledTime)} · ICT confirms the slot in iDesk";

    private string Err(string key) => errKey == key ? "err" : "";

    // ── Lifecycle ────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        Layout?.SetHeader("Tickets", "New ICT ticket");
        calMonth = FirstMonth;
        await Auth.LoadMeAsync();
        if (Cache.Peek<Draft>(DraftKey) is { } saved) { s = saved; if (!s.IsBlank) saveState = "saved"; }
        if (From is { } from) await PrefillFromAsync(from);
        if (s.ScheduledDate is { } sd && DateOnly.TryParseExact(sd, "yyyy-MM-dd", out var d) && d >= Today) calMonth = new(d.Year, d.Month, 1);
        var list = Cache.Peek<TicketListDto>(Tickets.CacheKey);
        notRegistered = list?.NotRegistered == true;
        await LoadModulesAsync();
    }

    private async Task LoadModulesAsync()
    {
        modulesFailed = false;
        var r = await Api.GetModulesAsync();
        if (r.Value is { Count: > 0 } m)
        {
            modules = m;
            if (Module is null) s.Module = modules.FirstOrDefault(x => x.Slug == "it-support")?.Slug ?? modules[0].Slug;
            FixCategory();
        }
        else modulesFailed = true;
    }

    /// A single-category queue always files under its fixed category; a stale draft category is dropped.
    private void FixCategory()
    {
        if (Module is not { } m) return;
        if (m.Categories.Count <= 1) s.Category = m.DefaultCategory ?? m.Categories.FirstOrDefault();
        else if (s.Category is not null && !m.Categories.Contains(s.Category)) s.Category = m.DefaultCategory;
        if (!m.SupportsHardwareInstallation && s.Category == TicketUi.HardwareInstallation) s.Category = null;
        if (!Priorities.Contains(s.Priority)) s.Priority = Priorities.Contains("MEDIUM") ? "MEDIUM" : Priorities[0];
    }

    private async Task PrefillFromAsync(Guid id)
    {
        var r = await Api.GetAsync(id);
        if (r.Value is not { } t) return;
        s = new Draft
        {
            Module = t.Module, Category = t.Category, Title = t.Title, Description = t.Description ?? "",
            Device = t.Device ?? "", Software = t.Software ?? "", Priority = t.Priority, CriticalReason = t.CriticalReason ?? "",
            HardwareType = t.HardwareType, ScheduledDate = t.ScheduledDate, ScheduledTime = t.ScheduledTime,
        };
        SaveNow();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        try
        {
            await JS.InvokeVoidAsync("ipop.icons");
            if (focusErrorSel is { } sel) { focusErrorSel = null; await JS.InvokeVoidAsync("ipopTickets.focusError", sel); }
        }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        disposed = true;
        if (saveCts is not null) { saveCts.Cancel(); SaveNow(); }
    }

    // ── Draft autosave: "Saving…" → check pop "Draft saved" 650 ms after the last change ──

    private void Dirty()
    {
        saveState = "saving";
        saveCts?.Cancel();
        var cts = saveCts = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(650, cts.Token); } catch (OperationCanceledException) { return; }
            saveCts = null;
            SaveNow();
            if (disposed) return;
            saveState = s.IsBlank ? "" : "saved";
            savedTick++;
            StateHasChanged();
        });
    }

    private void SaveNow()
    {
        if (s.IsBlank) Cache.Remove(DraftKey);
        else Cache.Put(DraftKey, s, persist: true);
    }

    private void ClearErr(string key)
    {
        if (errKey == key) { errKey = null; errMsg = null; }
    }

    // ── Pickers ─────────────────────────────────────────────────────────

    private void PickModule(TicketModuleDto m)
    {
        if (m.Slug == s.Module) return;
        s.Module = m.Slug;
        s.Category = m.DefaultCategory;
        FixCategory();
        popModule = m.Slug;
        template = null;
        catStagger = true;
        ClearErr("category"); ClearErr("module");
        Dirty();
        _ = ResetAfter(450, () => { popModule = null; catStagger = false; });
    }

    private void PickCategory(string c)
    {
        s.Category = c;
        ClearErr("category");
        Dirty();
    }

    /// Same as iDesk: a template fills title, description, priority (and category for IT Support);
    /// tapping the selected one again only clears the highlight. The caret lands at the end of the
    /// description, where every template leaves a blank to complete ("Area/Lantai: ").
    private async Task PickTemplate(TicketUi.QuickTemplate t)
    {
        if (template == t.Label) { template = null; return; }
        if (s.Title.Length > 0 && s.Title != lastTemplateTitle || s.Description.Length > 0 && s.Description != lastTemplateDescription)
        {
            if (!await Confirm.AskAsync("Replace what you've written?", "The template overwrites the title and description.", "Replace", danger: false))
                return;
        }
        template = t.Label;
        lastTemplateTitle = t.Subject;
        lastTemplateDescription = t.Description;
        s.Title = t.Subject;
        s.Description = t.Description;
        s.Priority = t.Priority;
        if (t.Category is { } c && Module is { } m && m.Categories.Contains(c)) s.Category = c;
        errKey = null; errMsg = null; formError = null;
        popTemplate = t.Label;
        Dirty();
        _ = ResetAfter(450, () => popTemplate = null);
        try { await JS.InvokeVoidAsync("ipopTickets.caretEnd", "#tk-desc"); } catch (JSException) { } catch (InvalidOperationException) { }
    }

    private void PickDevice(string v) { s.Device = s.Device == v ? "" : v; ClearErr("device"); Dirty(); }
    private void PickSoftware(string v) { s.Software = s.Software == v ? "" : v; ClearErr("software"); Dirty(); }

    private void PickHardware(string name) { s.HardwareType = name; ClearErr("hardwareType"); Dirty(); }

    private async Task PickPriority(string p)
    {
        s.Priority = p;
        Dirty();
        if (p == "CRITICAL")
        {
            try { await JS.InvokeVoidAsync("ipopTickets.focus", "#tk-reason", 300); } catch (JSException) { } catch (InvalidOperationException) { }
        }
    }

    private void MoveMonth(int d)
    {
        var next = calMonth.AddMonths(d);
        if (next < FirstMonth) return;
        calMonth = next;
        calDir = d;
    }

    private static bool IsOffDay(DateOnly d) => d < Today || d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private void PickDay(DateOnly d)
    {
        var iso = d.ToString("yyyy-MM-dd");
        calDir = 0;
        if (IsOffDay(d))
        {
            shakeDay = iso;
            _ = ResetAfter(420, () => shakeDay = null);
            return;
        }
        s.ScheduledDate = iso;
        popDay = iso;
        ClearErr("scheduledDate");
        Dirty();
        _ = ResetAfter(460, () => popDay = null);
    }

    private void PickSlot(string t)
    {
        s.ScheduledTime = t;
        popSlot = t;
        ClearErr("scheduledTime");
        Dirty();
        _ = ResetAfter(400, () => popSlot = null);
    }

    private void OnText(ChangeEventArgs e, Action<string> set, string key)
    {
        set(e.Value?.ToString() ?? "");
        ClearErr(key);
        formError = null;
        Dirty();
    }

    private async Task ResetAfter(int ms, Action reset)
    {
        await Task.Delay(ms);
        reset();
        if (!disposed) await InvokeAsync(StateHasChanged);
    }

    // ── Attachments ─────────────────────────────────────────────────────

    private async Task OnFilesAsync(InputFileChangeEventArgs e)
    {
        ClearErr("attachments");
        foreach (var f in e.GetMultipleFiles(50))
        {
            var reason = TicketUi.RejectReason(f.Name, f.Size);
            if (reason is null && files.Count(x => x.Ok) >= TicketAttachmentRules.MaxFiles)
            {
                dropFull = true;
                _ = ResetAfter(2200, () => dropFull = false);
                await Shake("[data-k=attachments]");
                continue;
            }
            string? b64 = null;
            if (reason is null)
            {
                try
                {
                    await using var stream = f.OpenReadStream(TicketAttachmentRules.MaxBytes);
                    using var ms = new MemoryStream((int)f.Size);
                    await stream.CopyToAsync(ms);
                    b64 = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or JSException)
                {
                    reason = "Couldn't read this file";
                }
            }
            files.Add(new(Guid.NewGuid().ToString("N")[..8], f.Name, f.Size, reason is null, reason, b64));
            StateHasChanged();
        }
    }

    private async Task RemoveFileAsync(PickedFile f)
    {
        try { await JS.InvokeVoidAsync("ipopTickets.leave", $"[data-fid=\"{f.Id}\"]"); } catch (JSException) { } catch (InvalidOperationException) { }
        files.Remove(f);
        if (errKey == "attachments" && files.All(x => x.Ok)) ClearErr("attachments");
    }

    // ── Validation + submit ─────────────────────────────────────────────

    /// Same rules as the API (TicketService.Validate), checked here so the user sees them at once.
    private (string Key, string Message)? Validate()
    {
        if (Module is not { } m) return ("module", "Choose a queue.");
        if (m.Categories.Count > 1 && s.Category is null) return ("category", "Pick what it's about");
        if (IsInstall)
        {
            if (string.IsNullOrEmpty(s.HardwareType)) return ("hardwareType", "Pick what should be installed");
            if (s.ScheduledDate is null) return ("scheduledDate", "Pick a date");
            if (DateOnly.TryParseExact(s.ScheduledDate, "yyyy-MM-dd", out var d) && IsOffDay(d)) return ("scheduledDate", "Pick a weekday from today on");
            if (s.ScheduledTime is null) return ("scheduledTime", "Pick a time");
        }
        if (s.Title.Trim().Length < 5) return ("title", "At least 5 characters");
        if (s.Description.Trim().Length < 3) return ("description", "Tell ICT a little more");
        if (s.Priority == "CRITICAL" && s.CriticalReason.Trim().Length < 3) return ("criticalReason", "Say why this is critical");
        if (files.Any(f => !f.Ok)) return ("attachments", "Remove the files iDesk won't accept");
        return null;
    }

    private void Flag(string key, string message)
    {
        errKey = key;
        errMsg = message;
        focusErrorSel = $"[data-k=\"{key}\"]";
    }

    private async Task SubmitAsync()
    {
        if (submitting) return;
        formError = null;
        if (Validate() is { } bad) { Flag(bad.Key, bad.Message); return; }

        submitting = true;
        StateHasChanged();
        var req = new CreateTicketRequest(
            s.Module, s.Category, s.Priority, s.Title.Trim(), s.Description.Trim(),
            s.Priority == "CRITICAL" ? s.CriticalReason.Trim() : null,
            NullIfEmpty(s.Device), NullIfEmpty(s.Software),
            IsInstall ? s.HardwareType : null, IsInstall ? s.ScheduledDate : null, IsInstall ? s.ScheduledTime : null,
            files.Count == 0 ? null : files.Where(f => f.Ok).Select(f => new NewTicketAttachment(f.Name, f.Base64!)).ToList());
        var r = await Api.CreateAsync(req);
        submitting = false;

        switch (r.Status)
        {
            case TicketCallStatus.Ok or TicketCallStatus.Queued when r.Value is { } done:
                notRegistered = false;
                Cache.Remove(DraftKey);
                saveCts?.Cancel(); saveCts = null;
                sheet = done;
                break;
            case TicketCallStatus.Invalid:
                var field = r.Error?.Field is { Length: > 0 } fk ? fk : null;
                if (field is not null && field != "module") Flag(field, r.Error?.Message ?? "Check this field");
                else formError = ("Check the form", r.Error?.Message ?? "Something in the form isn't valid.");
                break;
            case TicketCallStatus.Rejected:
                formError = ("iDesk didn't accept the ticket", r.Error?.Message ?? "Check the details and try again.");
                await Scroll();
                break;
            case TicketCallStatus.NotRegistered:
                notRegistered = true;
                nrFlash = true;
                SaveNow();
                await Scroll();
                _ = ResetAfter(1400, () => nrFlash = false);
                break;
            case TicketCallStatus.NotConfigured:
                formError = ("Tickets aren't set up yet", "iDesk isn't configured on this server. Your draft is saved — tell ICT.");
                await Scroll();
                break;
            default:
                Toasts.Show("Couldn't reach iPop. Your draft is saved — try again in a moment.", ToastKind.Error);
                break;
        }
    }

    private static string? NullIfEmpty(string v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private async Task Scroll()
    {
        StateHasChanged();
        try { await JS.InvokeVoidAsync("ipopTickets.scrollTop", "#tk-form"); } catch (JSException) { } catch (InvalidOperationException) { }
    }

    private async Task Shake(string sel)
    {
        try { await JS.InvokeVoidAsync("ipopTickets.replay", sel, "mi-shake"); } catch (JSException) { } catch (InvalidOperationException) { }
    }

    private async Task CloseSheetAsync(Guid? view)
    {
        if (sheet is null || sheetClosing) return;
        sheetClosing = true;
        StateHasChanged();
        await Task.Delay(200);
        sheet = null;
        sheetClosing = false;
        if (view is { } id) { Nav.NavigateTo($"/tickets/{id}"); return; }
        ResetForm();
    }

    private void ResetForm()
    {
        var module = s.Module;
        s = new Draft { Module = module };
        FixCategory();
        files.Clear();
        errKey = null; errMsg = null; formError = null; saveState = "";
        fadeKey++;
    }

    /// Discard keeps a snapshot for the Undo toast (the draft lives only in this browser).
    private Task DiscardDraftAsync()
    {
        var snap = s with { };
        var snapFiles = files.ToList();
        saveCts?.Cancel(); saveCts = null;
        Cache.Remove(DraftKey);
        ResetForm();
        Toasts.ShowUndo("Draft discarded", () => InvokeAsync(() =>
        {
            s = snap;
            files.Clear(); files.AddRange(snapFiles);
            SaveNow();
            saveState = "saved";
            fadeKey++;
            StateHasChanged();
        }));
        return Task.CompletedTask;
    }
}
