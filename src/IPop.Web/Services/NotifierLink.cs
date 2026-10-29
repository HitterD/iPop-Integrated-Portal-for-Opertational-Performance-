using System.Net;
using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Browser-local nonce + app-held PKCE link. Address-based presence only suggests when to prepare; it never
/// authorizes sign-in. The browser asks "Open iPop Notifier?" only when it was asked for (1.4.0):
///
///   reason "intent": the app's "Sign in with browser" click (or first run) in the last 2 minutes. The app's click is
///     pushed to the open tabs here (AppHub "notifierLinkIntent"), exactly one tab claims it, launches
///     ipop-notifier://prepare?nonce=… (nonce kept in this browser), takes the app's PKCE request for that nonce from
///     the server, mints the challenge-bound code and hands it back with ipop-notifier://signin. It then learns from
///     the server that the app is connected: a tab the app opened shows "connected" and closes itself; a tab the person
///     was already using stays where it is with a small toast. One tab, one prompt.
///   reason "other": this browser signed in (within a minute) as someone other than the app on this PC. Kept from
///     1.3.0: the app answers prepare with its own callback tab, and confirms the account switch locally first.
public sealed class NotifierLink(AuthState auth, IJSRuntime js, ToastService toasts)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    /// How long the tab waits for the app to answer prepare / to say it is connected.
    private static readonly TimeSpan AppAnswer = TimeSpan.FromSeconds(90);
    public const string IntentEvent = "notifierLinkIntent";
    private CancellationTokenSource? loop;
    private IDisposable? pushed;
    private readonly SemaphoreSlim busy = new(1, 1);

    private sealed record Should(bool Link, string? Reason);
    private sealed record ClaimResult(bool Claimed, string? Scheme);
    private sealed record Taken(string RequestId, string Challenge);
    private sealed record LinkCode(string Code, int ExpiresIn);
    private sealed record LinkStatus(bool Connected, string? Name, bool Known);
    private sealed record Callback(string Challenge, string RequestId, string Scheme);

    /// <paramref name="force"/> is kept for the layout's old call shape; whether the app opened this tab is read from
    /// the tab itself (ipop.notifierLink.openedTab, which survives the login redirect).
    public void Start(bool force = false)
    {
        loop?.Cancel();
        var cts = loop = new CancellationTokenSource();
        pushed?.Dispose();
        pushed = auth.On(IntentEvent, () => { if (!cts.IsCancellationRequested) _ = CheckQuietlyAsync(cts.Token); });
        _ = RunAsync(cts.Token);
    }

    public void Stop()
    {
        loop?.Cancel();
        pushed?.Dispose();
        pushed = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckQuietlyAsync(ct);
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckQuietlyAsync(CancellationToken ct)
    {
        // One flow per tab at a time: a push arriving while this tab already links is the same click.
        if (!await busy.WaitAsync(0, ct)) return;
        try { await CheckAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { }
        finally { busy.Release(); }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!auth.IsAuthenticated || auth.MustChangePassword || auth.IsSuspended) return;
        // Capture and strip callback parameters before any network await. The nonce must have been created
        // by this browser profile; another PC's prepare link cannot authorize an account here.
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.supported")) return;
        var callback = await js.InvokeAsync<Callback?>("ipop.notifierLink.consumeCallback");
        if (callback is not null)
        {
            await MintAsync(callback.Scheme, callback.Challenge, callback.RequestId, ct);
            return;
        }
        var opened = await js.InvokeAsync<bool>("ipop.notifierLink.openedTab");
        var should = await auth.GetAsync<Should>("/api/notifier/link/should" + (opened ? "?opened=true" : ""), ct);
        if (should?.Link != true) return;
        if (should.Reason == "intent") { await LinkThisTabAsync(opened, ct); return; }
        // "legacy" (a 1.3.0 app opened this tab) and "other" (web account switch): the 1.3.0 flow, where the app
        // answers with its own callback tab. One link per few minutes across tabs (10 s for the app's own tab).
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.claim", should.Reason == "legacy")) return;
        if (should.Reason == "legacy") await js.InvokeVoidAsync("ipop.notifierLink.forgetOpened");
        await js.InvokeAsync<string?>("ipop.notifierLink.prepare", (string?)null);
    }

    private async Task LinkThisTabAsync(bool opened, CancellationToken ct)
    {
        // Exactly one tab (of every browser on this address) wins the click. The tab in front goes first, so the
        // "Open iPop Notifier?" prompt shows where the person is looking.
        if (!opened && !await js.InvokeAsync<bool>("ipop.notifierLink.visible")) await Task.Delay(1500, ct);
        using (var res = await auth.SendAsync(HttpMethod.Post, "/api/notifier/link/claim", new { opened }, ct))
        {
            if (!res.IsSuccessStatusCode) return;
            var claim = await res.Content.ReadFromJsonAsync<ClaimResult>(cancellationToken: ct);
            if (claim is not { Claimed: true, Scheme: { } scheme }) return;
            var nonce = await js.InvokeAsync<string?>("ipop.notifierLink.prepare", scheme);
            if (nonce is null) return;

            // The app answers prepare by posting its PKCE request for this nonce; only this account can take it.
            Taken? taken = null;
            var until = DateTime.UtcNow + AppAnswer;
            while (taken is null && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                using var t = await auth.SendAsync(HttpMethod.Post, "/api/notifier/link/take", new { nonce }, ct);
                if (t.StatusCode == HttpStatusCode.OK) taken = await t.Content.ReadFromJsonAsync<Taken>(cancellationToken: ct);
                else if (!t.IsSuccessStatusCode && t.StatusCode != HttpStatusCode.TooManyRequests) return;
            }
            // An app older than 1.4.0 answers with its own callback tab instead; that tab uses the nonce.
            if (taken is null) return;
            await js.InvokeVoidAsync("ipop.notifierLink.forget", nonce);
            if (!await MintAsync(scheme, taken.Challenge, taken.RequestId, ct)) return;
            await WaitConnectedAsync(taken.RequestId, opened, ct);
        }
    }

    private async Task<bool> MintAsync(string scheme, string challenge, string requestId, CancellationToken ct)
    {
        using var result = await auth.SendAsync(HttpMethod.Post, "/api/notifier/link/code", new { challenge, requestId }, ct);
        if (!result.IsSuccessStatusCode) return false;
        var issued = await result.Content.ReadFromJsonAsync<LinkCode>(cancellationToken: ct);
        if (issued is null) return false;
        await js.InvokeVoidAsync("ipop.notifierLink.complete", scheme, issued.Code, requestId);
        return true;
    }

    /// The app confirms with its new session (after any local account-switch prompt). A cancelled switch never
    /// confirms, so the tab just stops waiting.
    private async Task WaitConnectedAsync(string requestId, bool opened, CancellationToken ct)
    {
        var until = DateTime.UtcNow + AppAnswer;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            await Task.Delay(1500, ct);
            var st = await auth.GetAsync<LinkStatus>("/api/notifier/link/status?request=" + Uri.EscapeDataString(requestId), ct);
            if (st is { Known: false }) return;
            if (st is not { Connected: true }) continue;
            if (opened) await js.InvokeVoidAsync("ipop.notifierLink.connected", st.Name ?? auth.Me?.Name ?? "");
            else toasts.Show("iPop Notifier connected", ToastKind.Success);
            return;
        }
    }
}
