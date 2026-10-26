using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Signed in here → the iPop Notifier on this PC signs in too (grilled 2026-10-26). The server knows when an app
/// on this PC's address is waiting (signed out) or is signed in as someone else; only then this tab asks for a
/// one-time code and opens ipop-notifier://signin?code=… — the browser asks "Open iPop Notifier?" once
/// ("Always allow" makes it silent). Checked when the shell starts and every two minutes while the tab is open.
public sealed class NotifierLink(AuthState auth, IJSRuntime js)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    private CancellationTokenSource? loop;

    private sealed record Should(bool Link, string? Reason);
    private sealed record LinkCode(string Code, int ExpiresIn);

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
        // Windows desktop only: the app doesn't exist anywhere else.
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.supported")) return;
        var should = await auth.GetAsync<Should>("/api/notifier/link/should", ct);
        if (should?.Link != true) return;
        // One link per few minutes across tabs: several open tabs must not each pop the browser prompt.
        if (!await js.InvokeAsync<bool>("ipop.notifierLink.claim", force)) return;
        using var res = await auth.SendAsync(HttpMethod.Post, "/api/notifier/link/code", null, ct);
        if (!res.IsSuccessStatusCode) return;
        var code = await res.Content.ReadFromJsonAsync<LinkCode>(ct);
        if (code is null) return;
        await js.InvokeVoidAsync("ipop.notifierLink.open", "ipop-notifier://signin?code=" + Uri.EscapeDataString(code.Code));
    }
}
