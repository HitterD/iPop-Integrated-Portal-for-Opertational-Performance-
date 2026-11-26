using IPop.Notifier.Core;

namespace IPop.Notifier.Windows;

/// "Sign in with browser" (OAuth loopback), split out of NotifierApp.cs unchanged (file size).
public sealed partial class NotifierApp
{
    // ── "Sign in with browser": OAuth loopback (1.4.3; flow in NotifierLoopback) ─────────────────────────
    //  click → PKCE verifier + state (kept here) → listen on http://127.0.0.1:{random}/callback → open the default
    //  browser ONCE at /notifier/connect?state&challenge&redirect&pc → the person clicks Authorize (or Decline)
    //  → the tab comes back to the loopback with code + state → state checked, code redeemed with the verifier
    //  → the tab gets "iPop Notifier is connected". Fallback: the short code from the page, pasted in this window.
    //  Clicks while an attempt runs do nothing; after 20 s "Open browser again" starts a fresh attempt + one tab.
    //  The listener stops after one handled callback or 5 minutes. Nothing is broadcast; no address is compared.

    private sealed class BrowserSignIn(IPopClient.BrowserAttempt attempt, string server)
    {
        public IPopClient.BrowserAttempt Attempt { get; } = attempt;
        public string Server { get; } = server;
        public LoopbackCallbackServer? Listener { get; set; }
        private int finished;
        /// One way out per attempt: the callback, a pasted code, a timeout, a new attempt or a password sign-in.
        public bool TryFinish() => Interlocked.Exchange(ref finished, 1) == 0;
        public bool IsFinished => Volatile.Read(ref finished) == 1;
        public void Stop() => Listener?.Stop();
    }

    private BrowserSignIn? browser;

    private void OpenBrowserSignIn() => OpenBrowserSignIn(again: false);

    private void OpenBrowserSignIn(bool again)
    {
        if (api.IsSignedIn) return;
        if (browser is { IsFinished: false } live && (!again || DateTime.UtcNow - live.Attempt.StartedAt < TimeSpan.FromSeconds(20)))
        {
            BrowserState("opened");
            signIn?.Activate();
            return;
        }
        EndBrowserSignIn();
        var b = new BrowserSignIn(IPopClient.NewBrowserAttempt(), api.Server.ToString());
        try { b.Listener = LoopbackCallbackServer.Start(b.Attempt.State, cb => OnBrowserCallbackAsync(b, cb), IPopClient.AttemptLifetime); }
        catch (Exception ex)
        {
            // No loopback port (security software): the short code from the page still works.
            Log.Warn("signin", "browser: loopback listener failed: " + ex.Message);
        }
        browser = b;
        // Without a listener the page still shows the short code; its redirect then reaches nothing.
        var redirect = b.Listener?.RedirectUri ?? "http://127.0.0.1:1024/callback";
        var url = NotifierLoopback.ConnectUrl(api.Web, b.Attempt.State, b.Attempt.Challenge, redirect, Environment.MachineName);
        Log.Info("signin", "browser: opened the connect page" + (b.Listener is null ? " (no loopback)" : $" (port {b.Listener.Port})"));
        Integration.OpenUrl(url);
        BrowserState("opened");
        var watch = b.Listener?.Finished ?? Task.Delay(IPopClient.AttemptLifetime);
        watch.ContinueWith(_ => Post(() =>
        {
            if (browser != b || !b.TryFinish()) return;
            browser = null;
            Log.Info("signin", "browser: no answer within 5 minutes");
            BrowserState("expired", "The sign-in timed out. Choose Sign in with browser to try again.");
        }), TaskScheduler.Default);
    }

    private void EndBrowserSignIn()
    {
        var b = browser;
        browser = null;
        if (b is null) return;
        b.TryFinish();
        b.Stop();
    }

    private void BrowserState(string state, string? message = null) => Post(() => signIn?.Send(new { type = "browserState", state, message }));

    /// GET /callback on the loopback with the right state (the listener checks it before this runs).
    private async Task<LoopbackAnswer> OnBrowserCallbackAsync(BrowserSignIn b, LoopbackCallback cb)
    {
        if (cb.Error is not null)
        {
            if (!b.TryFinish()) return new(LoopbackPages.Handled(), true);
            Post(() => { if (browser == b) browser = null; });
            Log.Info("signin", "browser: declined in the browser");
            BrowserState("denied", "Sign-in cancelled in the browser. You can try again or sign in here.");
            return new(LoopbackPages.Cancelled(), true);
        }
        if (cb.Code is not { Length: > 0 } code)
            return new(LoopbackPages.Error("This sign-in link is incomplete. Choose Sign in with browser in iPop Notifier again."), false);
        if (!b.TryFinish()) return new(LoopbackPages.Handled(), true);
        Log.Info("signin", "browser: Authorize was clicked");
        BrowserState("approving");
        var r = await RedeemAsync(b, code, null, pasted: false);
        return new(r.Ok ? LoopbackPages.Success(r.Name ?? api.Session?.Name) : LoopbackPages.Error(r.Error ?? "Couldn't sign in from the browser."), true);
    }

    /// "Paste code from browser": the short code shown under "Not connected?" on the page.
    private async void OnPastedCode(string? typed)
    {
        var b = browser;
        if (b is null || b.IsFinished)
        {
            signIn?.Send(new { type = "pasteResult", ok = false, message = "Choose Sign in with browser first, then paste the code from that page." });
            return;
        }
        if (NotifierLoopback.NormalizeShortCode(typed) is null)
        {
            signIn?.Send(new { type = "pasteResult", ok = false, message = "The code has 8 letters and digits, like ABCD-2345." });
            return;
        }
        Log.Info("signin", "browser: pasted code");
        var r = await RedeemAsync(b, null, typed, pasted: true);
        if (!r.Ok) signIn?.Send(new { type = "pasteResult", ok = false, message = r.Error ?? "That code didn't work." });
    }

    private async Task<IPopClient.LinkResult> RedeemAsync(BrowserSignIn b, string? code, string? shortCode, bool pasted)
    {
        var previous = api.Session?.Nik;
        IPopClient.LinkResult r;
        try { r = await api.RedeemBrowserCodeAsync(b.Server, b.Attempt, code, shortCode, CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { r = new(false, "Can't reach iPop. Check the network and try again.", null, false, "network"); }
        // A mistyped short code can be fixed and pasted again (the server burns it after 5 wrong tries).
        var retry = pasted && !r.Ok && r.Code is "invalid" or "busy" or "network";
        if (!retry) b.TryFinish();
        Post(() =>
        {
            if (retry) return;
            if (browser == b) { browser = null; b.Stop(); }
            if (!r.Ok)
            {
                Log.Warn("signin", "browser sign-in refused: " + (r.Code ?? r.Error));
                if (api.IsSignedIn) return; // a late answer after this PC already signed in another way
                if (signIn is { IsDisposed: false }) BrowserState("failed", r.Error ?? "Couldn't sign in from the browser.");
                else tray.ShowBalloonTip(5000, "iPop Notifier", r.Error ?? "Couldn't sign in from the browser.", ToolTipIcon.Warning);
                return;
            }
            if (signIn is { IsDisposed: false }) signIn.Close();
            _ = StartSessionAsync();
            Integration.SetStartWithWindows(true);
            Toasts.ShowSignedIn(r.Name ?? api.Session?.Name ?? "", switched: previous is not null && r.Switched);
        });
        return r;
    }
}
