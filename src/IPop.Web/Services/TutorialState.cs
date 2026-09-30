using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Tours, the help panel and practice mode. Seen/dismissed state lives on the server so the
/// first-visit invite doesn't come back on another device; practice state lives only in this
/// tab (sessionStorage survives the reload that swaps sample data in and out).
public sealed class TutorialState(AuthState auth, PracticeSandbox sandbox, ChatState chat, CallState calls,
    NotificationState notifs, IJSRuntime js, NavigationManager nav)
{
    private const string PracticeKey = "ipop-practice";
    private Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public event Action? Changed;

    public bool HelpOpen { get; private set; }
    public bool InviteDismissedThisVisit { get; private set; }
    public int HeldNotifications { get; private set; }

    public string? PracticeModule => sandbox.Module;
    public bool Practicing => sandbox.Active;

    /// "admin" | "hr" | "emp" — picks the role-specific "Good to know" list and sample data.
    public string Role => auth.Me?.Roles is { } r
        ? r.Contains("Admin") ? "admin" : r.Contains("HR") ? "hr" : "emp"
        : "emp";

    public bool IsDone(string key) => _seen.TryGetValue(key, out var s) && s == "done";
    public bool IsSeen(string key) => _seen.ContainsKey(key);

    public async Task LoadAsync()
    {
        if (_loaded) return;
        var seen = await auth.GetAsync<Dictionary<string, string>>("/api/me/tutorials");
        _seen = new(seen ?? [], StringComparer.OrdinalIgnoreCase);
        _loaded = true;
        Changed?.Invoke();
    }

    /// Resume practice after the reload that entered it. Synchronous and called first thing in
    /// the layout, because the page starts loading its data before the layout's first await ends.
    public void ResumePractice()
    {
        if (sandbox.Active || js is not IJSInProcessRuntime sync) return;
        var saved = sync.Invoke<string?>("sessionStorage.getItem", PracticeKey);
        if (string.IsNullOrEmpty(saved) || TutorialRegistry.Get(saved) is not { HasPractice: true }) return;
        sandbox.Start(saved, () => Role);
        chat.PracticeSend = sandbox.SendChat;
        chat.PracticeHeld += HoldNotification;
        calls.PracticeCall = sandbox.InterceptCall;
        notifs.PracticeHold = HoldNotification;
    }

    /// Whether the first-visit invite should show on this module.
    public bool ShouldInvite(TutorialModule m) => _loaded && !IsSeen(m.Key) && !InviteDismissedThisVisit && !Practicing && !InviteHeld;

    /// The launch-video popup holds the invite back until it closes, so the two never stack.
    public bool InviteHeld { get; private set; }
    public void HoldInvite(bool held) { if (InviteHeld == held) return; InviteHeld = held; Changed?.Invoke(); }

    public async Task MarkAsync(string key, string status)
    {
        if (status == "done" || !IsSeen(key)) _seen[key] = status;
        Changed?.Invoke();
        try { using var _ = await auth.SendAsync(HttpMethod.Put, $"/api/me/tutorials/{key}", new { status }); }
        catch { /* offline: the invite may reappear next visit, which is harmless */ }
    }

    public async Task DismissInviteAsync(string key)
    {
        InviteDismissedThisVisit = true;
        await MarkAsync(key, "dismissed");
    }

    public async Task StartTourAsync(TutorialModule m)
    {
        CloseHelp();
        InviteDismissedThisVisit = true;
        var steps = m.Steps.Select(s => new
        {
            el = s.El,
            title = s.Title,
            body = s.Limits ? "<ul>" + string.Concat(m.LimitsFor(Role).Select(l => $"<li>{System.Net.WebUtility.HtmlEncode(l)}</li>")) + "</ul>" : s.Body,
            limits = s.Limits,
        });
        _pendingTour = m.Key;
        Changed?.Invoke();
        await js.InvokeVoidAsync("ipopTutorial.start", steps, m.Color, _selfRef ??= DotNetObjectReference.Create(this));
    }

    private string? _pendingTour;
    private DotNetObjectReference<TutorialState>? _selfRef;

    [JSInvokable]
    public Task TourEnded(bool finished) =>
        _pendingTour is { } key ? MarkAsync(key, finished ? "done" : "dismissed") : Task.CompletedTask;

    public void OpenHelp() { HelpOpen = true; Changed?.Invoke(); }
    public void CloseHelp() { if (!HelpOpen) return; HelpOpen = false; Changed?.Invoke(); }
    public void ToggleHelp() { HelpOpen = !HelpOpen; Changed?.Invoke(); }

    // ── Practice mode ──
    // Entering and leaving reload the page: every module keeps its own caches (rooms, folders,
    // boards), and a fresh load is the one way to be sure no sample row leaks into real mode.

    public async Task EnterPracticeAsync(string key)
    {
        await js.InvokeVoidAsync("sessionStorage.setItem", PracticeKey, key);
        InviteDismissedThisVisit = true;
        await MarkAsync(key, "dismissed");
        nav.NavigateTo(PracticeHome(key), forceLoad: true);
    }

    public async Task ExitPracticeAsync()
    {
        var key = sandbox.Module;
        await js.InvokeVoidAsync("sessionStorage.removeItem", PracticeKey);
        sandbox.Stop();
        nav.NavigateTo(key is null ? nav.Uri : "/" + key, forceLoad: true);
    }

    public void ResetPractice() => nav.NavigateTo(PracticeHome(sandbox.Module ?? "dashboard"), forceLoad: true);

    /// Where practice opens, so the tour and the sample have something to show straight away.
    private static string PracticeHome(string key) => key switch
    {
        "chat" => $"/chat/{PracticeSandbox.Data.RoomRina}",
        "projects" => $"/projects/{PracticeSandbox.Data.ProjectId}",
        _ => "/" + key,
    };

    /// Real notifications that arrived while practising: counted, not shown, until exit.
    public void HoldNotification() { HeldNotifications++; Changed?.Invoke(); }
}
