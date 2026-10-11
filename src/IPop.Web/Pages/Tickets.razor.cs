// Tickets — My tickets list + detail (Mail-style split). Approved mockup: docs/mockups/ipop-apple/tickets.html
// (tabs 1, 4, 5, 6). Cached-first: the last list renders at once, then the iPop mirror, then a sync
// with iDesk. "tickets.changed" { id } from the hub re-reads the list (and the open ticket) and
// plays the live-update motion: rows move section (FLIP), pills pop, new activity flashes.
using IPop.Application.Tickets;
using IPop.Web.Components.Tickets;
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Tickets
{
    [Parameter] public Guid? Id { get; set; }
    [CascadingParameter] public MainLayout? Layout { get; set; }

    [Inject] private AuthState Auth { get; set; } = default!;
    [Inject] private TicketApi Api { get; set; } = default!;
    [Inject] private PageCache Cache { get; set; } = default!;
    [Inject] private BadgeState Badges { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private ConfirmService Confirm { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private CalendarLinks CalLinks { get; set; } = default!;

    internal const string CacheKey = "tickets.list";
    private const string RowsSel = "#tk-rows";

    private TicketListDto? list;
    private TicketDetailDto? detail;
    private TicketMessagesDto? msgs;
    private int msgsUnread;
    private Guid? msgsFor;
    private bool detailMissing;
    private bool showAll, syncing, syncFailedOnce, copied, retrying;
    private string q = "";
    private string? retryNote;
    private Guid? grownCallout;
    private Guid? loadedId;
    private IDisposable? sub;
    private bool disposed, glideInstant = true, flipPending, needGlide = true;
    private CancellationTokenSource? liveCts;

    // Transient motion flags; cleared once their animation has played.
    private readonly HashSet<Guid> changedPills = [];
    private readonly HashSet<Guid> freshRows = [];
    private readonly HashSet<string> freshEvents = [];

    private bool Offline => list?.Offline == true;
    private bool NotRegistered => list?.NotRegistered == true;
    private int OpenCount => list?.Tickets.Count(t => TicketStatus.IsOpen(t.Status)) ?? 0;
    private int AllCount => list?.Tickets.Count ?? 0;

    private List<TicketListItemDto> Visible => list is null ? [] :
        list.Tickets.Where(t => (showAll || TicketStatus.IsOpen(t.Status)) && TicketUi.Matches(t, q)).ToList();

    private string SyncText
    {
        get
        {
            if (syncing) return Offline ? "Trying iDesk…" : "Syncing…";
            if (Offline) return syncFailedOnce ? "Still unreachable · showing saved copy" : "iDesk is unreachable · showing saved copy";
            if (list?.SyncedAt is not { } at) return "ICT · not synced with iDesk yet";
            var ago = DateTime.UtcNow - DateTime.SpecifyKind(at, DateTimeKind.Utc);
            return ago.TotalMinutes < 2 ? "ICT · synced with iDesk just now"
                : ago.TotalHours < 1 ? $"ICT · synced with iDesk {(int)ago.TotalMinutes} min ago"
                : $"ICT · synced with iDesk {TicketUi.Stamp(at)}";
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        Layout?.SetHeader("Tickets", "ICT tickets via iDesk");
        sub = Auth.On<TicketChanged>(TicketEvents.Changed, e => _ = InvokeAsync(() => OnLiveChangedAsync(e?.Id)));
        await Auth.LoadMeAsync();
        list = Cache.Peek<TicketListDto>(CacheKey);
        var mirror = await Api.ListAsync();
        if (mirror.Value is { } m) Apply(m);
        // The iDesk sync can take seconds; don't hold the selected ticket's detail behind it.
        _ = InvokeAsync(() => SyncAsync(userClicked: false));
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Id == loadedId) return;
        loadedId = Id;
        retryNote = null;
        copied = false;
        needGlide = true;
        if (Id is not { } id) { detail = null; detailMissing = false; return; }
        // Selecting a closed ticket from a link while "Open" is on switches to "All" so it is visible.
        if (!showAll && list?.Tickets.FirstOrDefault(t => t.Id == id) is { } row && !TicketStatus.IsOpen(row.Status)) showAll = true;
        await LoadDetailAsync(id);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        try
        {
            await JS.InvokeVoidAsync("ipop.icons");
            if (flipPending) { flipPending = false; await JS.InvokeVoidAsync("ipopTickets.flipPlay", RowsSel); }
            else if (needGlide || glideInstant) { await JS.InvokeVoidAsync("ipopTickets.glide", RowsSel, glideInstant); }
            if (list is not null) { needGlide = false; glideInstant = false; }
        }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { } // prerender / tests without JS
    }

    public void Dispose()
    {
        disposed = true;
        sub?.Dispose();
        liveCts?.Cancel();
    }

    private sealed record TicketChanged(Guid? Id);

    // ── Data ─────────────────────────────────────────────────────────────

    private void Apply(TicketListDto fresh)
    {
        list = fresh;
        Cache.Put(CacheKey, fresh, persist: true);
        Badges.Set(TicketEvents.BadgeKey, fresh.Tickets.Count(t => t.Status == TicketStatus.Resolved));
    }

    private async Task SyncAsync(bool userClicked)
    {
        syncing = true;
        StateHasChanged();
        var r = await Api.SyncAsync();
        syncing = false;
        if (r.Value is { } fresh)
        {
            await MoveRowsAsync(() => Apply(fresh));
            syncFailedOnce = userClicked && fresh.Offline;
        }
        else if (list is not null)
        {
            // The API itself didn't answer: keep the saved copy and say so.
            list = list with { Offline = true };
            syncFailedOnce = userClicked;
        }
        if (userClicked && Offline) await Replay("mi-shake", ".tk-sync");
        if (Id is { } id && detail?.Id == id) await LoadDetailAsync(id, live: true);
        StateHasChanged();
    }

    private async Task ResyncAsync()
    {
        if (syncing) return;
        await Replay("spin1", ".tk-sync svg");
        await SyncAsync(userClicked: true);
    }

    private async Task LoadDetailAsync(Guid id, bool live = false)
    {
        if (!live) { detailMissing = false; StateHasChanged(); }
        var r = await Api.GetAsync(id);
        if (Id != id) return; // the user moved on while we waited
        if (r.Value is { } d)
        {
            if (live && detail?.Id == id)
            {
                var before = TicketUi.Activity(detail).Select(a => a.Key).ToHashSet();
                foreach (var a in TicketUi.Activity(d)) if (!before.Contains(a.Key)) freshEvents.Add(a.Key);
                if (detail.Status != d.Status)
                {
                    changedPills.Add(id);
                    if (d.Status == TicketStatus.Resolved) grownCallout = id;
                }
            }
            detail = d;
            detailMissing = false;
            ClearMotionLater();
            if (msgsFor != id || (live && d.UnreadMessages > 0)) _ = InvokeAsync(() => LoadMessagesAsync(id));
        }
        else if (r.Status == TicketCallStatus.NotFound) { detail = null; detailMissing = true; }
        else if (!live && detail?.Id != id) Toasts.Show("Couldn't load the ticket.", ToastKind.Error);
    }

    /// The public chat from iDesk. Opening it clears the reply counter (server side); the "new" marks stay
    /// on the replies that were unread when it opened.
    private async Task LoadMessagesAsync(Guid id)
    {
        if (msgsFor != id) { msgs = null; msgsUnread = 0; }
        msgsFor = id;
        StateHasChanged();
        var r = await Api.GetMessagesAsync(id);
        if (msgsFor != id || r.Value is not { } m) return;
        msgsUnread = Math.Max(m.Unread, msgs is null ? 0 : msgsUnread);
        msgs = m;
        if (m.Unread > 0 && list is not null)
            list = list with { Tickets = list.Tickets.Select(t => t.Id == id ? t with { UnreadMessages = 0 } : t).ToList() };
        StateHasChanged();
        if (Nav.Uri.EndsWith("#messages", StringComparison.Ordinal))
            try { await JS.InvokeVoidAsync("ipop.scrollIntoView", "#messages"); } catch (JSException) { } catch (InvalidOperationException) { }
    }

    /// Re-reads the list after a change; rows that changed section slide there (FLIP).
    private async Task OnLiveChangedAsync(Guid? id)
    {
        if (disposed) return;
        var r = await Api.ListAsync();
        if (r.Value is not { } fresh) return;
        var old = list?.Tickets.ToDictionary(t => t.Id, t => t.Status) ?? [];
        await MoveRowsAsync(() => Apply(fresh));
        foreach (var t in fresh.Tickets)
        {
            if (!old.TryGetValue(t.Id, out var was)) freshRows.Add(t.Id);
            else if (was != t.Status) changedPills.Add(t.Id);
        }
        if (Id is { } open && (id is null || id == open)) await LoadDetailAsync(open, live: true);
        ClearMotionLater();
        StateHasChanged();
    }

    /// FLIP around a list change: measure, mutate, and let OnAfterRender play it.
    private async Task MoveRowsAsync(Action mutate)
    {
        try { await JS.InvokeVoidAsync("ipopTickets.flipFirst", RowsSel); } catch (JSException) { } catch (InvalidOperationException) { }
        mutate();
        flipPending = true;
    }

    private void ClearMotionLater()
    {
        liveCts?.Cancel();
        var cts = liveCts = new CancellationTokenSource();
        _ = InvokeAsync(async () =>
        {
            try { await Task.Delay(1800, cts.Token); } catch (OperationCanceledException) { return; }
            changedPills.Clear(); freshRows.Clear(); freshEvents.Clear(); grownCallout = null;
            if (!disposed) StateHasChanged();
        });
    }

    // ── List interactions ───────────────────────────────────────────────

    private void Select(Guid id)
    {
        if (id == Id) return;
        Nav.NavigateTo($"/tickets/{id}");
    }

    private void OnRowKey(KeyboardEventArgs e, Guid id)
    {
        if (e.Key is "Enter" or " ") Select(id);
    }

    private async Task SetFilter(bool all)
    {
        if (showAll == all) return;
        showAll = all;
        needGlide = true; glideInstant = true;
        StateHasChanged();
        await Task.Yield();
        try { await JS.InvokeVoidAsync("ipopTickets.stagger", RowsSel); } catch (JSException) { } catch (InvalidOperationException) { }
    }

    private void OnSearch(ChangeEventArgs e)
    {
        q = (e.Value?.ToString() ?? "").Trim();
        needGlide = true; glideInstant = true;
    }

    private void ClearSearch() { q = ""; needGlide = true; glideInstant = true; }

    private void BackToList() => Nav.NavigateTo("/tickets");

    // ── Detail actions ──────────────────────────────────────────────────

    private async Task CopyNumber()
    {
        if (detail?.TicketNumber is not { } no) return;
        try { await JS.InvokeVoidAsync("navigator.clipboard.writeText", no); } catch (JSException) { }
        copied = true;
        StateHasChanged();
        await Task.Delay(1600);
        copied = false;
    }

    private async Task RetryAsync()
    {
        if (detail is not { } t || retrying) return;
        retrying = true;
        retryNote = null;
        StateHasChanged();
        var r = await Api.RetryAsync(t.Id);
        retrying = false;
        switch (r.Status)
        {
            case TicketCallStatus.Ok when r.Value is { } sent:
                changedPills.Add(sent.Id);
                foreach (var a in TicketUi.Activity(sent)) if (!TicketUi.Activity(t).Any(x => x.Key == a.Key)) freshEvents.Add(a.Key);
                detail = sent;
                Toasts.ShowDetailed("Ticket sent to ICT", $"{sent.Title} · {TicketUi.Number(sent)}", ToastKind.Success, "life-buoy", $"/tickets/{sent.Id}");
                await OnLiveChangedAsync(sent.Id);
                return;
            case TicketCallStatus.NotPending:
                await OnLiveChangedAsync(t.Id);
                return;
            case TicketCallStatus.NotRegistered:
                if (list is not null) list = list with { NotRegistered = true };
                retryNote = "Your iDesk account isn't set up yet. Contact ICT, then try again — the ticket stays here.";
                break;
            case TicketCallStatus.Rejected:
                retryNote = $"iDesk refused it: {r.Error?.Message ?? "the ticket isn't valid any more"}. Edit it and send it again.";
                break;
            case TicketCallStatus.NotConfigured:
                retryNote = "iDesk isn't configured on this server yet. The ticket stays here — nothing is lost.";
                break;
            default:
                retryNote = "Still can't reach iDesk. The ticket stays here — nothing is lost.";
                break;
        }
        StateHasChanged();
        await Replay("mi-shake", "#tk-failed");
    }

    /// The API deletes for good (nothing to restore), so this asks first instead of offering Undo.
    private async Task DiscardAsync()
    {
        if (detail is not { } t) return;
        if (!await Confirm.AskAsync("Discard this ticket?", "It never reached iDesk, so ICT hasn't seen it. This can't be undone.", "Discard")) return;
        try { await JS.InvokeVoidAsync("ipopTickets.leave", $".tk[data-id=\"{t.Id}\"]"); } catch (JSException) { } catch (InvalidOperationException) { }
        var r = await Api.DiscardAsync(t.Id);
        if (r.Status is TicketCallStatus.Ok or TicketCallStatus.NotFound)
        {
            if (list is not null) Apply(list with { Tickets = list.Tickets.Where(x => x.Id != t.Id).ToList() });
            Toasts.Show("Ticket discarded", ToastKind.Success);
            Nav.NavigateTo("/tickets");
            return;
        }
        Toasts.Show(r.Status == TicketCallStatus.NotPending ? "This ticket already reached iDesk, so it can't be discarded." : "Couldn't discard the ticket.", ToastKind.Error);
        await OnLiveChangedAsync(t.Id);
    }

    private async Task Replay(string cls, string sel)
    {
        try { await JS.InvokeVoidAsync("ipopTickets.replay", sel, cls); }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }
}
