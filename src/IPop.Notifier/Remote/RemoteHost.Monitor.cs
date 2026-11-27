using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Windows;

namespace IPop.Notifier.Remote;

/// Which monitor is shared (hotfix 1.8.2). WebView2 shares one whole screen and picks it by name without a picker:
/// "Entire screen" with one monitor, "Screen 1", "Screen 2"… with several. The name is fixed per WebView2 environment,
/// so switching monitor = a new hidden host page in that monitor's environment, which joins the same LiveKit room with
/// a fresh token; the old page leaves once the new picture is up (the helper sees a short swap, never a dropped
/// session). Input and the green frame follow the shared monitor.
public sealed partial class RemoteHost
{
    private string preset = "auto";
    private HostPage? leaving;

    /// What the host page shares (it no longer crops anything): one whole screen.
    private static object Screen() => new { multi = false };

    private static string Preset(string? p) => p is "smooth" or "quality" ? p : "auto";

    /// The Screen.AllScreens order Chromium uses for "Screen N" is EnumDisplayMonitors order; Monitors.List() puts
    /// the main monitor first. Both are mapped through the monitor's bounds.
    private static string SourceFor(int index)
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        if (all.Length < 2) return WebHost.CaptureSourceFor(0, 1);
        var target = Monitors.List()[Math.Clamp(index, 0, all.Length - 1)];
        var n = Array.FindIndex(all, s => s.Bounds == target.Bounds);
        return WebHost.CaptureSourceFor(n < 0 ? 0 : n, all.Length);
    }

    private HostPage NewPage(Guid id, Action<HostPage> onReady)
    {
        var p = new HostPage(this, SourceFor(monitor));
        p.Ready += () => { if (!p.IsDisposed) onReady(p); };
        p.WebViewFailed += failure => Post(() =>
        {
            if (sessionId != id || !ReferenceEquals(p, page)) return;
            balloon("Remote help", "Your screen couldn't be shared. ICT has been told.", ToolTipIcon.Warning);
            _ = EndAsync("error");
        });
        p.CreateControl();
        _ = p.Handle;
        return p;
    }

    /// The helper picked Monitor 1 / Monitor 2 (no "All" yet): a host page for that screen joins the room, then the
    /// previous one leaves.
    private void SwitchMonitor(int index, bool force = false)
    {
        var count = System.Windows.Forms.Screen.AllScreens.Length;
        if (sessionId is not { } id || lastJoin is null || page is null) return;
        var next = Math.Clamp(index == Monitors.All ? 0 : index, 0, Math.Max(0, count - 1));
        if (!force && (count < 2 || next == monitor)) { SendMonitors(); return; }
        monitor = next;
        input.ReleaseAll();
        Log.Info("remote", $"monitor → {monitor} ({SourceFor(monitor)}) {Monitors.Bounds(monitor)}");
        _ = SwitchPageAsync(id);
    }

    private async Task SwitchPageAsync(Guid id)
    {
        // A fresh token: the old page is still in the room under the same identity until the new picture is up.
        JsonNode? join = null;
        try { join = await api.GetJsonAsync($"api/notifier/remote/sessions/{id}/join"); }
        catch (Exception ex) { Log.Warn("remote", "monitor switch: join failed " + ex.GetType().Name); }
        if (join is null || sessionId != id) { SendMonitors(); return; }
        lastJoin = join;
        Post(() =>
        {
            if (sessionId != id) return;
            try { leaving?.Dispose(); } catch { }
            leaving = page;
            page = NewPage(id, p => p.Send(new { type = "join", info = join, quality = preset, screen = Screen() }));
            // The old page leaves when the new one is sharing (Shared) or after 20 s, whatever comes first.
            var old = leaving;
            _ = Task.Delay(20_000).ContinueWith(_ => Post(() => Retire(old)));
        });
    }

    /// The current page shares its screen: input + frame go to that monitor, the previous page leaves.
    private void Shared(JsonNode? joined)
    {
        input.Monitor = Monitors.Bounds(monitor);
        if (frame is not null) frame.Bounds = input.Monitor;
        Retire(leaving);
        Log.Info("remote", $"sharing monitor {monitor} {input.Monitor} picture {joined?["w"]}x{joined?["h"]}@{joined?["fps"]}");
    }

    private void Retire(HostPage? old)
    {
        if (old is null || ReferenceEquals(old, page)) return;
        if (ReferenceEquals(old, leaving)) leaving = null;
        try { old.Send(new { type = "leave" }); } catch { }
        _ = Task.Delay(1500).ContinueWith(_ => Post(() => { try { old.Dispose(); } catch { } }));
    }

    private void SendMonitors() => page?.Send(new { type = "send", topic = "ctl", msg = Monitors.Describe(monitor, System.Windows.Forms.Screen.AllScreens.Length > 1) });
}
