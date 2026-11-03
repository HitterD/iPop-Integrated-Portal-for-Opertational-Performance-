using Microsoft.JSInterop;

namespace IPop.Web.Services;

public enum SessionLockMode { None, Locked, Yield }

/// One active iPop tab per browser, like WhatsApp Web (js/session-lock.js does the tab-to-tab
/// talk). The newest tab takes over; the old one drops its SignalR hubs and heartbeat and shows
/// a modal until "Gunakan di sini". A tab in a call keeps the session and the new tab yields.
/// Web Push toasts come from the service worker and are unaffected.
public sealed class SessionLockState(AuthState auth, ChatState chat, NotificationState notifs, CallState calls,
    BadgeState badges, TimerState timer, IJSRuntime js) : IDisposable
{
    private DotNetObjectReference<SessionLockState>? _ref;
    private bool _callHooked;

    public SessionLockMode Mode { get; private set; }
    /// A previous tab was forced to hand over during a call (shown in the locked modal).
    public bool EndedCallElsewhere { get; private set; }
    public bool IsWorking { get; private set; }
    public event Action? Changed;

    /// Called by MainLayout before any hub connects. True when this tab owns the session.
    public async Task<bool> StartAsync()
    {
        if (!_callHooked) { calls.StateChanged += OnCallChanged; _callHooked = true; }
        _ref ??= DotNetObjectReference.Create(this);
        string result;
        try { result = await js.InvokeAsync<string>("ipopSession.start", _ref); }
        catch { result = "active"; }   // no BroadcastChannel / script missing: behave as before
        if (result == "active") return true;
        // "yield": the other tab is in a call. "locked": another tab opened at the same moment and won.
        Suspend(result == "yield" ? SessionLockMode.Yield : SessionLockMode.Locked);
        return false;
    }

    /// Another tab claimed the session.
    [JSInvokable]
    public async Task OnSessionTaken(bool forced)
    {
        EndedCallElsewhere = false;
        if (forced && calls.HasLiveCall)
        {
            // Same choice as signing out: a ring is declined, my outgoing ring canceled, a call ended.
            try { await calls.EndForSignOutAsync(); } catch { /* the other tab starts fresh anyway */ }
            EndedCallElsewhere = true;
        }
        Suspend(SessionLockMode.Locked);
        await DisconnectAllAsync();
    }

    /// "Gunakan di sini" from the locked modal; may turn into the yield prompt if the other tab is in a call.
    public Task UseHereAsync() => ClaimAsync(force: false);

    /// "Tetap gunakan di sini": ends the other tab's call and takes over.
    public Task ForceHereAsync() => ClaimAsync(force: true);

    public async Task CloseTabAsync()
    {
        try { await js.InvokeVoidAsync("ipopSession.closeTab"); } catch { }
    }

    private async Task ClaimAsync(bool force)
    {
        if (IsWorking) return;
        IsWorking = true;
        Changed?.Invoke();
        try
        {
            var result = await js.InvokeAsync<string>("ipopSession.claim", force);
            if (result == "yield") { Suspend(SessionLockMode.Yield); return; }
            if (result != "active") { Suspend(SessionLockMode.Locked); return; }
            auth.IsSuspended = false;
            Mode = SessionLockMode.None;
            EndedCallElsewhere = false;
            Changed?.Invoke();
            OnCallChanged();
            await ReconnectAllAsync();
        }
        finally
        {
            IsWorking = false;
            Changed?.Invoke();
        }
    }

    private void Suspend(SessionLockMode mode)
    {
        auth.IsSuspended = true;
        Mode = mode;
        Changed?.Invoke();
    }

    private async Task DisconnectAllAsync()
    {
        await Safe(auth.StopPresenceAsync);
        await Safe(chat.DisconnectAsync);
        await Safe(notifs.DisconnectAsync);
        await Safe(calls.DisconnectAsync);
    }

    /// Same order as MainLayout's first start; anything pushed while locked is re-read.
    private async Task ReconnectAllAsync()
    {
        await Safe(auth.StartPresenceAsync);
        await Safe(badges.RefreshAsync);
        await Safe(timer.RefreshAsync);
        await Safe(chat.ConnectAsync);
        await Safe(notifs.ConnectAsync);
        await Safe(() => notifs.LoadRecentAsync(6));
        await Safe(calls.ConnectAsync);
    }

    private void OnCallChanged()
    {
        if (Mode != SessionLockMode.None) return;
        _ = js.InvokeVoidAsync("ipopSession.setBusy", calls.HasLiveCall).AsTask().ContinueWith(_ => { });
    }

    private static async Task Safe(Func<Task> step)
    {
        try { await step(); } catch { /* each connection has its own retry once resumed */ }
    }

    public void Dispose()
    {
        if (_callHooked) calls.StateChanged -= OnCallChanged;
        _ref?.Dispose();
    }
}
