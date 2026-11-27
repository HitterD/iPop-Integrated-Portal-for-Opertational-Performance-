using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Remote;

/// The user's side of remote help: the Allow prompt (with what the helper may do), then a hidden page that shares the
/// screen through LiveKit and plays the helper's input, the "ICT is helping you" window (excluded from capture) and
/// the green frame. Only one session runs at a time; a second ICT watching joins the same LiveKit room through its
/// own session. What the helper sends is filtered here against what the user allowed (RemoteHost.Data.cs).
public sealed partial class RemoteHost(IPopClient api, Action<string, string, ToolTipIcon> balloon)
{
    private readonly InputInjector input = new();
    private HostPage? page;
    private HelpWindow? help;
    private SessionFrame? frame;
    private Guid? sessionId;
    private string mode = "control";
    private bool clipSync = true;
    private string helper = "ICT";
    private RemotePermissions perms = RemotePermissions.All;
    private int monitor;
    private readonly List<string> files = [];
    private ClipWatcher? clip;
    private System.Windows.Forms.Timer? stat, alive;
    private string lastStat = "";
    public bool Active => sessionId is not null;
    /// "control,clipboard" the person wants ticked by default (Settings › Remote › On the user's PC); null = company.
    public string? UserDefaults { get; set; }
    public Guid? SessionId => sessionId;
    public event Action<bool>? ActiveChanged;

    /// "remoteRequest" from the hub.
    public void OnRequest(JsonNode m, string deviceKey)
    {
        Log.Info("remote", $"request {m["sessionId"]} from {m["helper"]} mode={m["mode"]} unattended={m["unattended"]} mine={m["deviceKey"]?.GetValue<string>() == deviceKey}");
        if (m["deviceKey"]?.GetValue<string>() != deviceKey) return; // another PC of the same person
        var id = Guid.Parse(m["sessionId"]!.GetValue<string>());
        helper = m["helper"]?.GetValue<string>() ?? "ICT";
        var initials = m["helperInitials"]?.GetValue<string>() ?? "IT";
        mode = m["mode"]?.GetValue<string>() == "view" ? "view" : "control";
        var joining = m["joining"]?.GetValue<bool>() == true;
        if (m["unattended"]?.GetValue<bool>() == true) { _ = StartSafeAsync(id, initials, unattended: true); return; }
        if (sessionId is not null && !joining)
        {
            Log.Warn("remote", $"new request while {sessionId} is still marked here — closing the old one");
            Stop("replaced");
        }

        var ask = new HelpWindow(api, this, asking: true);
        var from = m["from"]?.GetValue<string>();
        var seconds = m["askSeconds"]?.GetValue<int>() is > 0 and var sec ? sec : 30;
        var max = m["max"]?.GetValue<string>() ?? "control,clipboard,files";
        // The person's own default ticks (Settings › Remote) win over the company default, within the PC's maximum.
        var defaults = UserDefaults is { } mine ? mine : m["defaults"]?.GetValue<string>() ?? "control,clipboard";
        ask.Shown += (_, _) => ask.Send(new { type = "ask", helper, initials, machine = Environment.MachineName, from, mode, joining, seconds, defaults, max });
        var answered = false;
        ask.Answered += async (allow, allowed) =>
        {
            answered = true;
            ask.Close();
            string? status;
            try
            {
                using var res = await api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/answer", new { allow, permissions = allowed });
                status = JsonNode.Parse(await res.Content.ReadAsStringAsync())?["status"]?.GetValue<string>();
                Log.Info("remote", $"answered allow={allow} perms={string.Join(',', allowed)} → {(int)res.StatusCode} status={status}");
            }
            catch (Exception ex) { Log.Error("remote", "answer failed", ex); balloon("Remote help", "Couldn't reach iPop to answer.", ToolTipIcon.Warning); return; }
            // Hotfix 1.8.1: starting the share is no longer inside the answer's try. In 1.8.0 any start error showed
            // "Couldn't reach iPop to answer" although iPop already had the Allow, and the helper waited forever.
            if (allow && status == "active" && !joining) await StartSafeAsync(id, initials, unattended: false);
            else if (allow && status != "active") balloon("Remote help", "The request had already expired.", ToolTipIcon.Info);
        };
        var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) => { timer.Dispose(); if (!answered && !ask.IsDisposed) { answered = true; ask.Close(); _ = api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/answer", new { allow = false }); } };
        timer.Start();
        ask.Show();
        Integration.Chime();
    }

    /// Start the session; whatever fails ends it on both sides with a reason (never a silent "Connecting…").
    private async Task StartSafeAsync(Guid id, string initials, bool unattended)
    {
        try { await StartAsync(id, initials, unattended); }
        catch (Exception ex)
        {
            Log.Error("remote", "start failed: " + ex);
            balloon("Remote help", "Remote help couldn't start on this PC. ICT has been told.", ToolTipIcon.Warning);
            if (sessionId == id) await EndAsync("error");
            else { try { using var _ = await api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/end", new { files = "" }); } catch { } }
        }
    }

    /// The hidden page must be sharing within this time, else the session ends instead of hanging.
    internal static readonly TimeSpan ShareDeadline = TimeSpan.FromSeconds(45);
    private bool sharing;

    private async Task StartAsync(Guid id, string initials, bool unattended)
    {
        sessionId = id;
        files.Clear();
        ActiveChanged?.Invoke(true);
        monitor = 0;
        preset = "auto";
        var bounds = Monitors.Bounds(monitor);
        input.Monitor = bounds;
        JsonNode? join;
        try { join = await api.GetJsonAsync($"api/notifier/remote/sessions/{id}/join"); }
        catch (Exception ex) { Log.Error("remote", "join failed", ex); join = null; }
        if (join is null) { Log.Warn("remote", "join returned nothing"); Stop("error"); balloon("Remote help", "Couldn't start remote help.", ToolTipIcon.Warning); return; }
        // The server's record of what the user allowed is the one that counts (it also covers unattended sessions).
        perms = RemotePermissions.Parse(join["permissions"]?.GetValue<string>());
        lastJoin = join;
        ApplyLimits(join);
        HookSystem();
        mode = perms.ClampMode(join["mode"]?.GetValue<string>() ?? mode);
        clipSync = perms.Clipboard;
        Log.Info("remote", $"joining room {join["room"]} url={join["url"]} role={join["role"]} mode={mode} perms={perms.ToList()}");

        clip = new ClipWatcher(() =>
        {
            if (Clip.Read() is { } c) ClipboardChanged(c.Payload ?? new JsonObject(), c.Payload is null && c.FileName is not null, c.FileName, c.FileBytes);
        });
        frame = new SessionFrame(bounds, $"ICT is connected · {helper}");
        frame.Show();
        help = new HelpWindow(api, this, asking: false);
        help.Shown += (_, _) => help.Send(new { type = "help", helper, initials, mode, unattended, perms = perms.ToList(), maxFileBytes });
        help.Show();
        stat = new System.Windows.Forms.Timer { Interval = 2000 };
        stat.Tick += (_, _) => SendInputStat();
        stat.Start();
        alive = new System.Windows.Forms.Timer { Interval = 30_000 };
        alive.Tick += async (_, _) => await AliveAsync(id);
        alive.Start();
        _ = Task.Delay(20_000).ContinueWith(_ => Log.UploadAsync(api));
        sharing = false;
        page = NewPage(id, p => p.Send(new { type = "join", info = join, quality = preset, screen = Screen() }));
        // WebView2 can fail to make the hidden page (0x8007139F) or the capture can hang: end with a reason after the
        // deadline instead of leaving the helper on "Connecting…".
        _ = Task.Delay(ShareDeadline).ContinueWith(_ => Post(() =>
        {
            if (sessionId != id || sharing) return;
            Log.Warn("remote", $"share did not start within {ShareDeadline.TotalSeconds:0} s");
            balloon("Remote help", "Your screen couldn't be shared. ICT has been told.", ToolTipIcon.Warning);
            _ = EndAsync("error");
        }));
    }

    /// Messages from the hidden LiveKit page.
    internal void FromPage(HostPage from, string type, JsonNode? m)
    {
        // A page being replaced (monitor switch) may still talk while it leaves: only the current page counts.
        if (!ReferenceEquals(from, page)) { if (type is "joined" or "failed") from.Send(new { type = "leave" }); return; }
        if (type is not ("data" or "file")) Log.Info("remote", "host page: " + type + " " + (m?.ToJsonString() is { } j ? j[..Math.Min(300, j.Length)] : ""));
        switch (type)
        {
            case "joined":
                JoinResult(true);
                sharing = true;
                Shared(m);
                // The helper learns what may be used and which monitors exist.
                page?.Send(new { type = "send", topic = "ctl", msg = new { t = "perms", list = perms.ToList(), mode } });
                SendMonitors();
                break;
            case "rejoinFailed":
                JoinResult(false);
                break;
            case "processFailed":
                OnPageCrashed(m?["kind"]?.GetValue<string>() ?? "");
                break;
            case "failed":
                if (pendingJoin is not null) { JoinResult(false); break; }
                Log.Error("remote", "share failed: " + (m?["message"]?.GetValue<string>() ?? ""));
                balloon("Remote help", "The screen couldn't be shared: " + (m?["message"]?.GetValue<string>() ?? ""), ToolTipIcon.Warning);
                _ = EndAsync("error");
                break;
            case "disconnected":
                // Not the end: LiveKit gave up its own reconnect, so we rejoin with a fresh token (BIGIS #8 v3).
                if (sessionId is not null && m?["reason"]?.GetValue<string>() is not ("CLIENT_INITIATED" or "1")) _ = RejoinAsync("network");
                break;
            case "xferBegin": if (m is not null) XferBegin(m); break;
            case "xferChunk": if (m is not null) XferChunk(m); break;
            case "xferEnd": if (m is not null) XferEnd(m); break;
            case "xferRefused": Refuse(m?["topic"]?.GetValue<string>()); break;
            case "xferOut": if (m is not null) XferOut(m); break;
            case "data":
                OnData(m?["topic"]?.GetValue<string>(), m?["msg"]);
                break;
            case "peer":
                if (m?["joined"]?.GetValue<bool>() == true && m["identity"]?.GetValue<string>()?.StartsWith("helper:") == true)
                {
                    HelperBack();
                    page?.Send(new { type = "send", topic = "ctl", msg = new { t = "perms", list = perms.ToList(), mode } });
                    SendMonitors();
                }
                // The helper left without saying bye (window closed, network): end after 30 s unless they come back.
                if (m?["joined"]?.GetValue<bool>() == false && m["identity"]?.GetValue<string>()?.StartsWith("helper:") == true)
                    HelperLeft();
                break;
        }
    }

    private void SendInputStat()
    {
        if (page is null) return;
        var fg = DesktopState.Foreground();
        var desk = DesktopState.InputDesktop();
        var reason = DesktopState.Reason(desk, fg);
        var key = $"{input.Played}|{input.Refused}|{input.LastError}|{fg}|{desk}";
        if (key == lastStat) return;
        lastStat = key;
        Log.Info("remote", $"input played={input.Played} refused={input.Refused} err={input.LastError} path={input.Path} fg={fg} desk={desk} {DesktopState.Environment()}");
        page.Send(new { type = "send", topic = "ctl", msg = new { t = "inStat", played = input.Played, refused = input.Refused, err = input.LastError, fg, desk, mode, path = input.Path, reason } });
    }

    private async Task AliveAsync(Guid id)
    {
        if (sessionId != id) return;
        try
        {
            using var res = await api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/alive");
            if ((int)res.StatusCode is 410 or 404 && sessionId == id)
            {
                Log.Info("remote", $"server says {id} is over ({(int)res.StatusCode})");
                Stop("server");
            }
        }
        catch (Exception ex) { Log.Warn("remote", "heartbeat failed: " + ex.GetType().Name); }
    }

    internal void ChatFromUser(string text) => page?.Send(new { type = "send", topic = "chat", msg = new { text } });
    internal void MediaMinimised(bool min) => help?.SetMinimised(min);

    public async Task EndAsync(string by, bool notifyServer = true)
    {
        var id = sessionId;
        if (id is null) return;
        try { page?.Send(new { type = "send", topic = "ctl", msg = new { t = "bye" } }); } catch { }
        if (notifyServer)
        {
            try { using var _ = await api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/end", new { files = string.Join('\n', files), reason = by == "lost" ? "lost" : null }); } catch { }
        }
        Stop(by);
        balloon("Remote help ended", $"{helper} is no longer connected. See Settings → Account for the history.", ToolTipIcon.Info);
    }

    /// The other side ended it ("remoteEnded" from the hub; "revoked" = an Administrator changed the access password).
    public void OnEnded(JsonNode m)
    {
        if (sessionId is null || m["sessionId"]?.GetValue<string>() != sessionId.ToString()) return;
        var revoked = m["endedBy"]?.GetValue<string>() == "revoked";
        try { if (revoked) page?.Send(new { type = "send", topic = "ctl", msg = new { t = "bye", reason = "revoked" } }); } catch { }
        Stop(revoked ? "revoked" : "helper");
        balloon("Remote help ended", revoked ? "Unattended access to this PC was changed by an Administrator." : $"{helper} ended the session.", ToolTipIcon.Info);
    }

    private void Stop(string by)
    {
        Log.Info("remote", $"stop by={by} played={input.Played} refused={input.Refused} lastErr={input.LastError}");
        input.ReleaseAll();
        sessionId = null;
        rejoinRun++;
        UnhookSystem();
        CloseFiles();
        try { page?.Send(new { type = "leave" }); } catch { }
        var p = page; page = null;
        _ = Task.Delay(1500).ContinueWith(_ => Post(() => p?.Dispose()));
        Retire(leaving);
        clip?.Dispose(); clip = null;
        stat?.Dispose(); stat = null;
        alive?.Dispose(); alive = null;
        help?.Close(); help = null;
        frame?.Close(); frame = null;
        ActiveChanged?.Invoke(false);
        _ = Task.Delay(2000).ContinueWith(_ => Log.UploadAsync(api));
    }

    private static void Post(Action a)
    {
        var f = System.Windows.Forms.Application.OpenForms.Count > 0 ? System.Windows.Forms.Application.OpenForms[0] : null;
        if (f is not null && !f.IsDisposed) f.BeginInvoke(a); else a();
    }
}
