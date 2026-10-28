using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Windows;

namespace IPop.Notifier.Remote;

/// The user's side of remote help: the Allow prompt, then a hidden page that shares the screen through LiveKit and
/// plays the helper's input, the "ICT is helping you" window (excluded from capture) and the green frame.
/// Only one session runs at a time; a second ICT watching joins the same LiveKit room through its own session.
public sealed class RemoteHost(IPopClient api, Action<string, string, ToolTipIcon> balloon)
{
    private readonly InputInjector input = new();
    private HostPage? page;
    private HelpWindow? help;
    private SessionFrame? frame;
    private Guid? sessionId;
    private string mode = "control";
    private bool clipSync = true;
    private string helper = "ICT";
    private readonly List<string> files = [];
    private ClipWatcher? clip;
    private System.Windows.Forms.Timer? stat, alive;
    private string lastStat = "";
    public bool Active => sessionId is not null;
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
        mode = m["mode"]?.GetValue<string>() ?? "control";
        var joining = m["joining"]?.GetValue<bool>() == true;
        if (m["unattended"]?.GetValue<bool>() == true) { _ = StartAsync(id, initials); return; }
        if (sessionId is not null && !joining)
        {
            Log.Warn("remote", $"new request while {sessionId} is still marked here — closing the old one");
            Stop("replaced");
        }

        var ask = new HelpWindow(api, this, asking: true);
        ask.Shown += (_, _) => ask.Send(new { type = "ask", helper, initials, machine = Environment.MachineName, mode, joining, seconds = 30 });
        var answered = false;
        ask.Answered += async allow =>
        {
            answered = true;
            ask.Close();
            try
            {
                using var res = await api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/answer", new { allow });
                var status = JsonNode.Parse(await res.Content.ReadAsStringAsync())?["status"]?.GetValue<string>();
                Log.Info("remote", $"answered allow={allow} → {(int)res.StatusCode} status={status}");
                if (allow && status == "active" && !joining) await StartAsync(id, initials);
                else if (allow && status != "active") balloon("Remote help", "The request had already expired.", ToolTipIcon.Info);
            }
            catch (Exception ex) { Log.Error("remote", "answer failed", ex); balloon("Remote help", "Couldn't reach iPop to answer.", ToolTipIcon.Warning); }
        };
        var timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        timer.Tick += (_, _) => { timer.Dispose(); if (!answered && !ask.IsDisposed) { answered = true; ask.Close(); _ = api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/answer", new { allow = false }); } };
        timer.Start();
        ask.Show();
        Integration.Chime();
    }

    private async Task StartAsync(Guid id, string initials)
    {
        sessionId = id;
        files.Clear();
        ActiveChanged?.Invoke(true);
        var monitor = Screen.PrimaryScreen!.Bounds;
        input.Monitor = monitor;
        JsonNode? join;
        try { join = await api.GetJsonAsync($"api/notifier/remote/sessions/{id}/join"); }
        catch (Exception ex) { Log.Error("remote", "join failed", ex); join = null; }
        if (join is null) { Log.Warn("remote", "join returned nothing"); Stop("error"); balloon("Remote help", "Couldn't start remote help.", ToolTipIcon.Warning); return; }
        Log.Info("remote", $"joining room {join["room"]} url={join["url"]} role={join["role"]} mode={join["mode"]}");

        clip = new ClipWatcher(() =>
        {
            if (Clip.Read() is { } c) ClipboardChanged(c.Payload ?? new JsonObject(), c.FileBytes is not null, c.FileName, c.FileBytes);
        });
        frame = new SessionFrame(monitor, $"ICT is connected · {helper}");
        frame.Show();
        help = new HelpWindow(api, this, asking: false);
        help.Shown += (_, _) => help.Send(new { type = "help", helper, initials, mode });
        help.Show();
        stat = new System.Windows.Forms.Timer { Interval = 2000 };
        stat.Tick += (_, _) => SendInputStat();
        stat.Start();
        alive = new System.Windows.Forms.Timer { Interval = 30_000 };
        alive.Tick += async (_, _) => await AliveAsync(id);
        alive.Start();
        _ = Task.Delay(20_000).ContinueWith(_ => Log.UploadAsync(api));
        page = new HostPage(this);
        page.Ready += () => page.Send(new { type = "join", info = join, quality = "smooth" });
        page.CreateControl();
        _ = page.Handle;
    }

    /// Messages from the hidden LiveKit page.
    internal void FromPage(string type, JsonNode? m)
    {
        if (type is not ("data" or "file")) Log.Info("remote", "host page: " + type + " " + (m?.ToJsonString() is { } j ? j[..Math.Min(300, j.Length)] : ""));
        switch (type)
        {
            case "failed":
                Log.Error("remote", "share failed: " + (m?["message"]?.GetValue<string>() ?? ""));
                balloon("Remote help", "The screen couldn't be shared: " + (m?["message"]?.GetValue<string>() ?? ""), ToolTipIcon.Warning);
                _ = EndAsync("error");
                break;
            case "disconnected":
                if (sessionId is not null) _ = EndAsync("error");
                break;
            case "data":
                OnData(m?["topic"]?.GetValue<string>(), m?["msg"]);
                break;
            case "peer":
                // The helper left without saying bye (window closed, network): end here too after a grace period.
                if (m?["joined"]?.GetValue<bool>() == false && m["identity"]?.GetValue<string>()?.StartsWith("helper:") == true)
                    _ = Task.Delay(8000).ContinueWith(_ => { if (sessionId is not null) Post(() => _ = EndAsync("helper")); });
                break;
            case "file":
                SaveIncoming(m);
                break;
        }
    }

    private void OnData(string? topic, JsonNode? msg)
    {
        if (msg is null || sessionId is null) return;
        if (topic == "in" && mode == "control") { PlayInput(msg); return; }
        if (topic == "chat") { help?.Send(new { type = "chat", text = msg["text"]?.GetValue<string>() ?? "" }); return; }
        if (topic == "clip" && clipSync) { Clip.Apply(msg); return; }
        switch (msg["t"]?.GetValue<string>())
        {
            case "mode": mode = msg["mode"]?.GetValue<string>() == "view" ? "view" : "control"; if (mode == "view") input.ReleaseAll(); break;
            case "quality": page?.Send(new { type = "quality", preset = msg["preset"]?.GetValue<string>() }); break;
            case "clipSync": clipSync = msg["on"]?.GetValue<bool>() == true; break;
            case "media": _ = ShowMediaAsync(msg); break;
            case "bye": _ = EndAsync("helper", notifyServer: false); break;
        }
    }

    private void PlayInput(JsonNode m)
    {
        double D(string k) => m[k]?.GetValue<double>() ?? 0;
        switch (m["t"]?.GetValue<string>())
        {
            case "m": input.Move(D("x"), D("y")); break;
            case "b": input.Button(D("x"), D("y"), (int)D("b"), D("d") > 0); break;
            case "w": input.Wheel(D("dx"), D("dy")); break;
            case "k": input.Key((ushort)D("vk"), D("d") > 0, D("x") > 0); break;
            case "release": input.ReleaseAll(); break;
            case "combo": Combo(m["k"]?.GetValue<string>()); break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseDesktop(nint h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(nint h, int index, System.Text.StringBuilder buf, int len, out int needed);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private static string DeskName(nint h)
    {
        if (h == 0) return "";
        var sb = new System.Text.StringBuilder(64);
        return GetUserObjectInformationW(h, 2, sb, sb.Capacity * 2, out _) ? sb.ToString() : "?";
    }

    /// Facts that decide whether Windows lets this app play input: integrity level, which session is on the
    /// console, and whether our thread sits on the input desktop.
    private static string Environment2()
    {
        var il = "?";
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            il = id.Groups?.FirstOrDefault(g => g.Value.StartsWith("S-1-16-"))?.Value switch
            {
                "S-1-16-4096" => "low", "S-1-16-8192" => "medium", "S-1-16-8448" => "medium+", "S-1-16-12288" => "high", "S-1-16-16384" => "system", var v => v ?? "?",
            };
        }
        catch { }
        var mySession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return $"il={il} session={mySession} console={WTSGetActiveConsoleSessionId()} threadDesk={DeskName(GetThreadDesktop(GetCurrentThreadId()))} fgHwnd={GetForegroundWindow()}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// Why mouse/keyboard may not reach this PC right now:
    ///   "ok"        the person's desktop is in front
    ///   "locked"    lock screen, sign-in, UAC or Ctrl+Alt+Del (a secure desktop)
    ///   "rdp-idle"  this Windows session is a Remote Desktop session that nobody is looking at (window minimised
    ///               or disconnected) — Windows stops drawing it and refuses input
    ///   "rdp"       a Remote Desktop session that is being viewed (works, but frame rate is limited by RDP)
    ///   "idle"      no window has the focus (screen off / nothing in front)
    private static string Reason(string desk, string fg)
    {
        const int SM_REMOTESESSION = 0x1000;
        var rdp = GetSystemMetrics(SM_REMOTESESSION) != 0;
        if (desk != "Default") return "locked";
        if (GetForegroundWindow() == 0 || fg == "Idle") return rdp ? "rdp-idle" : "idle";
        return rdp ? "rdp" : "ok";
    }

    /// "Default" while the person's desktop is in front; "Winlogon"/"" when the PC is locked or a UAC prompt is up.
    private static string InputDesktop()
    {
        var h = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (h == 0) return "";                     // access denied = a secure desktop is in front
        try
        {
            var sb = new System.Text.StringBuilder(64);
            return GetUserObjectInformationW(h, 2, sb, sb.Capacity * 2, out _) ? sb.ToString() : "";
        }
        finally { CloseDesktop(h); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);

    private void SendInputStat()
    {
        if (page is null) return;
        string fg = "";
        try { GetWindowThreadProcessId(GetForegroundWindow(), out var p); fg = System.Diagnostics.Process.GetProcessById((int)p).ProcessName; } catch { }
        var desk = InputDesktop();
        var reason = Reason(desk, fg);
        var key = $"{input.Played}|{input.Refused}|{input.LastError}|{fg}|{desk}";
        if (key == lastStat) return;
        lastStat = key;
        Log.Info("remote", $"input played={input.Played} refused={input.Refused} err={input.LastError} path={input.Path} fg={fg} desk={desk} {Environment2()}");
        page.Send(new { type = "send", topic = "ctl", msg = new { t = "inStat", played = input.Played, refused = input.Refused, err = input.LastError, fg, desk, mode, path = input.Path, reason } });
    }

    private void Combo(string? k)
    {
        void Tap(params ushort[] keys) { foreach (var v in keys) input.Key(v, true, v is 0x5B); foreach (var v in keys.Reverse()) input.Key(v, false, v is 0x5B); }
        switch (k)
        {
            case "win": Tap(0x5B); break;
            case "alttab": Tap(0x12, 0x09); break;
            case "taskmgr": Tap(0x11, 0x10, 0x1B); break;
            case "lock": Tap(0x5B, 0x4C); break;
            // Ctrl+Alt+Del can only come from the secure helper service; without it Windows ignores injected SAS.
            case "cad": if (!HelperService.SendSas()) Tap(0x11, 0x10, 0x1B); break;
        }
    }

    private async Task ShowMediaAsync(JsonNode m)
    {
        if (help is null || help.IsDisposed) return;
        var show = m["show"]?.GetValue<bool>() != false;
        help.SetMinimised(m["minimised"]?.GetValue<bool>() == true || !show);
        var kind = m["kind"]?.GetValue<string>() ?? "default";
        var id = m["id"]?.GetValue<string>();
        string? url = null;
        if (kind != "default" && Guid.TryParse(id, out var att))
        {
            var bytes = await api.GetBytesAsync($"api/attachments/{att}");
            if (bytes is { Length: > 0 }) url = await help.PutMediaAsync(bytes, kind == "video" ? "video/mp4" : "image/jpeg");
        }
        help.Send(new { type = "media", kind = url is null ? "default" : kind, url, sound = m["sound"]?.GetValue<bool>() == true });
    }

    private void SaveIncoming(JsonNode? m)
    {
        if (m is null) return;
        var name = Path.GetFileName(m["name"]?.GetValue<string>() ?? "file");
        var bytes = Convert.FromBase64String(m["data"]?.GetValue<string>() ?? "");
        if (m["topic"]?.GetValue<string>() == "clip-file") { Clip.SetFile(name, bytes); return; }
        var path = Downloads.Save(name, bytes);
        files.Add(name);
        balloon("File from ICT", $"{name} was saved to Downloads.", ToolTipIcon.Info);
        _ = path;
    }

    /// The user copied something while sync is on: send it to the helper.
    internal void ClipboardChanged(JsonNode payload, bool isFile, string? name, byte[]? fileBytes)
    {
        if (sessionId is null || !clipSync || page is null) return;
        if (isFile && name is not null && fileBytes is not null)
            page.Send(new { type = "sendFile", topic = "clip-file", id = Guid.NewGuid().ToString("N"), name, mime = "application/octet-stream", data = Convert.ToBase64String(fileBytes) });
        else page.Send(new { type = "send", topic = "clip", msg = payload });
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
            try { using var _ = await api.SendAsync(HttpMethod.Post, $"api/notifier/remote/sessions/{id}/end", new { files = string.Join('\n', files) }); } catch { }
        }
        Stop(by);
        balloon("Remote help ended", $"{helper} is no longer connected. See Settings → Account for the history.", ToolTipIcon.Info);
    }

    /// The other side ended it ("remoteEnded" from the hub).
    public void OnEnded(JsonNode m)
    {
        if (sessionId is null || m["sessionId"]?.GetValue<string>() != sessionId.ToString()) return;
        Stop("helper");
        balloon("Remote help ended", $"{helper} ended the session.", ToolTipIcon.Info);
    }

    private void Stop(string by)
    {
        Log.Info("remote", $"stop by={by} played={input.Played} refused={input.Refused} lastErr={input.LastError}");
        input.ReleaseAll();
        sessionId = null;
        try { page?.Send(new { type = "leave" }); } catch { }
        var p = page; page = null;
        _ = Task.Delay(1500).ContinueWith(_ => Post(() => p?.Dispose()));
        clip?.Dispose(); clip = null;
        stat?.Dispose(); stat = null;
        alive?.Dispose(); alive = null;
        help?.Close(); help = null;
        frame?.Close(); frame = null;
        ActiveChanged?.Invoke(false);
        _ = Task.Delay(2000).ContinueWith(_ => Log.UploadAsync(api));
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void QaLog(string line)
    {
        if (Environment.GetEnvironmentVariable("IPOP_NOTIFIER_QA_DIR") is not { Length: > 0 }) return;
        try { File.AppendAllText(Path.Combine(SessionStore.Folder, "qa-events.log"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
    }

    private static void Post(Action a)
    {
        var f = System.Windows.Forms.Application.OpenForms.Count > 0 ? System.Windows.Forms.Application.OpenForms[0] : null;
        if (f is not null && !f.IsDisposed) f.BeginInvoke(a); else a();
    }

    /// Hidden window hosting the LiveKit page. Answers getDisplayMedia with the primary monitor (no picker).
    private sealed class HostPage : IPop.Notifier.Windows.HtmlWindow
    {
        private readonly RemoteHost host;
        public event Action? Ready;

        public HostPage(RemoteHost host) : base("remote-host.html", new Size(320, 200), noActivate: true, showInTaskbar: false)
        {
            this.host = host;
            Opacity = 0;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Show();
        }

        protected override void OnReady() => Ready?.Invoke();

        protected override void HandleMessage(string type, JsonNode? msg)
        {
            if (type is "ready" or "drag" or "minimize" or "close" or "openUrl") { base.HandleMessage(type, msg); return; }
            host.FromPage(type, msg);
        }
    }
}
