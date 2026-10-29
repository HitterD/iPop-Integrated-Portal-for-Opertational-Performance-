using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// iPop Notifier "Sign in with browser" (1.4.2). The app registers a connect request; the server pushes it to the
/// open iPop tabs on the same address (AppHub "notifierLinkIntent"). Exactly one tab claims it (the one in front
/// first) and navigates itself, in-app, to the /notifier/connect card, remembering the page the person was on so the
/// card can go back there afterwards. The person clicks Connect (or Cancel) on the card; the app collects the code by
/// polling. Nothing here launches ipop-notifier:// any more: Chrome/Edge silently block a protocol link started by a
/// script without a user gesture (often in a background tab), which left 1.4.1 on "Waiting for browser…".
/// A web sign-in as a different account than the Notifier on this PC changes nothing in the app.
public sealed class NotifierLink(AuthState auth, IJSRuntime js, NavigationManager nav)
{
    public const string IntentEvent = "notifierLinkIntent";
    public const string ConnectPath = "/notifier/connect";
    private CancellationTokenSource? loop;
    private IDisposable? pushed;
    private readonly SemaphoreSlim busy = new(1, 1);

    /// The page this tab showed before it switched to the connect card (null = the app opened this tab for it).
    public string? BackUrl { get; private set; }

    private sealed record ClaimResult(bool Claimed, string? Request);

    /// <paramref name="force"/> is kept for the layout's call shape and ignored.
    public void Start(bool force = false)
    {
        loop?.Cancel();
        var cts = loop = new CancellationTokenSource();
        pushed?.Dispose();
        pushed = auth.On(IntentEvent, () => { if (!cts.IsCancellationRequested) _ = CheckQuietlyAsync(cts.Token); });
        // A click from just before this tab connected to the hub is still waiting for a tab.
        _ = CheckQuietlyAsync(cts.Token);
    }

    public void Stop()
    {
        loop?.Cancel();
        pushed?.Dispose();
        pushed = null;
    }

    private async Task CheckQuietlyAsync(CancellationToken ct)
    {
        // One claim per tab at a time: a push arriving while this tab already claims is the same click.
        if (!await busy.WaitAsync(0, ct)) return;
        try { await ClaimAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        finally { busy.Release(); }
    }

    private async Task ClaimAsync(CancellationToken ct)
    {
        if (!auth.IsAuthenticated || auth.MustChangePassword || auth.IsSuspended) return;
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.supported")) return;
        // Already on a card: this tab is busy with a request.
        if (OnCard(nav.Uri)) return;
        // The tab in front goes first, so the card shows where the person is looking.
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.visible")) await Task.Delay(1500, ct);
        using var res = await auth.SendAsync(HttpMethod.Post, "/api/notifier/link/claim", new { }, ct);
        if (!res.IsSuccessStatusCode) return;
        var claim = await res.Content.ReadFromJsonAsync<ClaimResult>(cancellationToken: ct);
        if (claim is not { Claimed: true, Request: { Length: 43 } request } || ct.IsCancellationRequested) return;
        BackUrl = "/" + nav.ToBaseRelativePath(nav.Uri);
        nav.NavigateTo(ConnectPath + "?request=" + Uri.EscapeDataString(request));
    }

    /// The card is done: back to the page from before (once), or null when there was none.
    public string? TakeBackUrl()
    {
        var back = BackUrl;
        BackUrl = null;
        return back is { Length: > 1 } && !OnCard(back) ? back : null;
    }

    public static bool OnCard(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url.Split('?', 2)[0];
        return path.TrimEnd('/').EndsWith(ConnectPath, StringComparison.OrdinalIgnoreCase);
    }
}
