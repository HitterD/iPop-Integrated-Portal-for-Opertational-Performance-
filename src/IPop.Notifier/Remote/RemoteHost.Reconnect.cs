using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using Microsoft.Win32;

namespace IPop.Notifier.Remote;

/// Keeping a session alive on the user's PC (BIGIS #8 v3):
///   • LiveKit drops → rejoin with a fresh token from /join, up to the policy's tries within its window
///   • the host page's renderer crashes → a new host page, rejoin
///   • the helper leaves without "bye" → wait 30 s; a helper who comes back cancels that
///   • sleep / resume, lock / unlock, monitors or resolution changing → the helper is told; screens are re-measured
public sealed partial class RemoteHost
{
    private RemoteRejoin rejoin = new();
    private readonly HelperLeaveTimer leave = new(HelperLeaveTimer.Default);
    private int rejoinRun;
    private bool sysHooked;
    private JsonNode? lastJoin;

    private void HookSystem()
    {
        if (sysHooked) return;
        sysHooked = true;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void UnhookSystem()
    {
        if (!sysHooked) return;
        sysHooked = false;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    /// "pc" state for the helper's viewer: asleep | awake | locked | unlocked (the viewer shows why the picture stops).
    private void TellState(string state) => page?.Send(new { type = "send", topic = "ctl", msg = new { t = "pc", state } });

    private void OnPower(object? s, PowerModeChangedEventArgs e) => Post(() =>
    {
        if (sessionId is null) return;
        Log.Info("remote", "power " + e.Mode);
        if (e.Mode == PowerModes.Suspend) TellState("asleep");
        else if (e.Mode == PowerModes.Resume) { TellState("awake"); _ = RejoinAsync("resume"); }
    });

    private void OnSessionSwitch(object? s, SessionSwitchEventArgs e) => Post(() =>
    {
        if (sessionId is null) return;
        if (e.Reason is SessionSwitchReason.SessionLock) TellState("locked");
        else if (e.Reason is SessionSwitchReason.SessionUnlock) TellState("unlocked");
    });

    /// A monitor came or went, or the resolution/DPI changed: input, frame and crop follow; the helper gets the new list.
    private void OnDisplayChanged(object? s, EventArgs e) => Post(() =>
    {
        if (sessionId is null) return;
        var count = System.Windows.Forms.Screen.AllScreens.Length;
        if (monitor != Monitors.All && monitor >= count) monitor = 0;
        if (count < 2 && monitor == Monitors.All) monitor = 0;
        var bounds = Monitors.Bounds(monitor);
        input.ReleaseAll();
        input.Monitor = bounds;
        if (frame is not null) frame.Bounds = bounds;
        page?.Send(new { type = "screen", screen = Screen() });
        page?.Send(new { type = "send", topic = "ctl", msg = new { t = "monchange", count } });
        SendMonitors();
        Log.Info("remote", $"displays changed → {count} monitor(s), using {monitor} {bounds}");
    });

    /// The page lost LiveKit (network, sleep) or crashed: rejoin with a fresh token until the policy's window runs out.
    private async Task RejoinAsync(string why)
    {
        var id = sessionId;
        if (id is null) return;
        var run = ++rejoinRun;
        var started = DateTime.UtcNow;
        Log.Info("remote", $"rejoin start ({why})");
        for (var n = 1; sessionId == id && run == rejoinRun; n++)
        {
            var wait = rejoin.DueAt(n) - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            if (sessionId != id || run != rejoinRun) return;
            if (rejoin.GiveUp(n - 1, DateTime.UtcNow - started)) break;
            JsonNode? join = null;
            try
            {
                using var res = await api.SendAsync(HttpMethod.Get, $"api/notifier/remote/sessions/{id}/join");
                if ((int)res.StatusCode is 404 or 409) { Log.Info("remote", "rejoin: session is over"); Post(() => Stop("server")); return; }
                if (res.IsSuccessStatusCode) join = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            }
            catch (Exception ex) { Log.Warn("remote", $"rejoin {n}: {ex.GetType().Name}"); }
            if (join is null) continue;
            lastJoin = join;
            var ok = await Task.Run(async () =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(() => { pendingJoin = tcs; EnsurePage(); page!.Send(new { type = "rejoin", info = join, screen = Screen() }); });
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(12)));
                return done == tcs.Task && tcs.Task.Result;
            });
            if (ok) { Log.Info("remote", $"rejoined on try {n}"); return; }
        }
        if (sessionId == id && run == rejoinRun) { Log.Warn("remote", "rejoin gave up"); Post(() => _ = EndAsync("lost")); }
    }

    private TaskCompletionSource<bool>? pendingJoin;

    private void JoinResult(bool ok) { pendingJoin?.TrySetResult(ok); pendingJoin = null; }

    /// A new hidden host page when the old one crashed or was never made.
    private void EnsurePage()
    {
        if (page is { IsDisposed: false } && !pageCrashed) return;
        pageCrashed = false;
        var old = page;
        page = new HostPage(this);
        page.Ready += () => page.Send(new { type = "noop" });
        page.CreateControl();
        _ = page.Handle;
        try { old?.Dispose(); } catch { }
    }

    private bool pageCrashed;

    /// WebView2 says the host page's renderer died: restart it and rejoin (the helper sees "Notifier restarting").
    private void OnPageCrashed(string kind)
    {
        Log.Error("remote", "host page crashed: " + kind);
        if (sessionId is null) return;
        pageCrashed = true;
        // The crashed page's streams are gone: their half-written files too.
        incoming.AbortAll();
        xfers.Clear();
        _ = RejoinAsync("crash");
    }

    /// The helper's LiveKit identity left: end after 30 s unless they come back (a reconnect, not a leave).
    private void HelperLeft()
    {
        var ticket = leave.Left();
        var id = sessionId;
        _ = Task.Delay(leave.Grace).ContinueWith(_ => Post(() =>
        {
            if (sessionId == id && id is not null && leave.ShouldEnd(ticket)) _ = EndAsync("helper");
        }));
    }

    private void HelperBack() => leave.Back();
}
