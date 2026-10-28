using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Browser-local nonce + app-held PKCE link. Address-based presence only suggests when to prepare;
/// it never authorizes sign-in. The fixed browser callback must consume this browser's nonce before a
/// challenge-bound code is minted. A different-account transition also requires local app confirmation.
public sealed class NotifierLink(AuthState auth, IJSRuntime js)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    private CancellationTokenSource? loop;

    private sealed record Should(bool Link, string? Reason);
    private sealed record LinkCode(string Code, int ExpiresIn);
    private sealed record Callback(string Challenge, string RequestId, string Scheme);

    public void Start(bool force = false)
    {
        loop?.Cancel();
        var cts = loop = new CancellationTokenSource();
        _ = RunAsync(force, cts.Token);
    }

    public void Stop() => loop?.Cancel();

    private async Task RunAsync(bool force, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(force, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
            force = false;
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task CheckAsync(bool force = false, CancellationToken ct = default)
    {
        if (!auth.IsAuthenticated || auth.MustChangePassword || auth.IsSuspended) return;
        // Capture and strip callback parameters before any network await. The nonce must have been created
        // by this browser profile; another PC's prepare link cannot authorize an account here.
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.supported")) return;
        var callback = await js.InvokeAsync<Callback?>("ipop.notifierLink.consumeCallback");
        if (callback is not null)
        {
            using var result = await auth.SendAsync(HttpMethod.Post, "/api/notifier/link/code", new { challenge = callback.Challenge, requestId = callback.RequestId }, ct);
            if (!result.IsSuccessStatusCode) return;
            var issued = await result.Content.ReadFromJsonAsync<LinkCode>(ct);
            if (issued is not null) await js.InvokeVoidAsync("ipop.notifierLink.complete", callback.Scheme, issued.Code, callback.RequestId);
            return;
        }
        var should = await auth.GetAsync<Should>("/api/notifier/link/should", ct);
        if (!force && should?.Link != true) return;
        // One link per few minutes across tabs: several open tabs must not each pop the browser prompt.
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.claim", force)) return;
        await js.InvokeVoidAsync("ipop.notifierLink.prepare");
    }
}
