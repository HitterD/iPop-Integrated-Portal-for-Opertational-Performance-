using System.Text.Json.Nodes;
using IPop.Notifier.Core;
using IPop.Notifier.Windows;

namespace IPop.Notifier.Remote;

/// On the user's PC: first the Allow / Decline prompt, then "ICT is helping you" with the helper's media. Excluded from
/// screen capture, so the helper never sees it in the stream. Minimise from either side folds it to its title bar.
public sealed class HelpWindow : BridgedWindow
{
    private readonly RemoteHost host;
    private readonly bool asking;
    /// Allow / Decline, with what the user ticked ("control", "clipboard", "files").
    public event Action<bool, string[]>? Answered;
    private Size full;

    public HelpWindow(IPopClient api, RemoteHost host, bool asking)
        : base("remote-help.html", asking ? new Size(440, 380) : new Size(900, 600), api, noActivate: true, showInTaskbar: !asking)
    {
        this.host = host;
        this.asking = asking;
        Text = asking ? "iPop Notifier — Remote help request" : "ICT is helping you";
        TopMost = true;
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = asking
            ? new Point(area.Right - Width - 16, area.Bottom - Height - 16)
            : new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        full = ClientSize;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Stays visible to the user, never in what the helper receives.
        SessionFrame.SetWindowDisplayAffinity(Handle, SessionFrame.WDA_EXCLUDEFROMCAPTURE);
    }

    /// Media bytes become a file in the app's temp folder served on the virtual host, so big videos stream from disk.
    public Task<string?> PutMediaAsync(byte[] bytes, string mime)
    {
        try
        {
            var dir = WebHost.MediaCache;
            Directory.CreateDirectory(dir);
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + (mime.StartsWith("video") ? ".mp4" : ".img");
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            return Task.FromResult<string?>("http://media.notifier.ipop/" + name);
        }
        catch { return Task.FromResult<string?>(null); }
    }

    public void SetMinimised(bool min)
    {
        if (IsDisposed || asking) return;
        var area = Screen.PrimaryScreen!.WorkingArea;
        var scale = DeviceDpi / 96.0;
        if (min)
        {
            ClientSize = new Size((int)(340 * scale), (int)(46 * scale));
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        }
        else
        {
            ClientSize = full;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
        }
        Send(new { type = "min", min });
    }

    protected override void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "answer":
                var allowed = msg?["perms"] is System.Text.Json.Nodes.JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray() : [];
                Answered?.Invoke(msg?["allow"]?.GetValue<bool>() == true, allowed);
                break;
            case "mediaMin": SetMinimised(msg?["min"]?.GetValue<bool>() == true); break;
            case "chatSend": host.ChatFromUser(msg?["text"]?.GetValue<string>() ?? ""); break;
            case "sendFile": host.SendFileToIct(); break;
            case "xferAction": host.XferAction(msg?["id"]?.GetValue<string>() ?? "", msg?["action"]?.GetValue<string>() ?? ""); break;
            case "endSession": _ = host.EndAsync("user"); break;
            case "close": break; // Esc must not end the session or hide the window
            default: base.HandleMessage(type, msg); break;
        }
    }
}

/// ICT side: one window per session with the user's live screen (mockup remote §1, left).
public sealed class ViewerWindow : BridgedWindow
{
    private readonly object start;
    private ClipWatcher? clip;
    private bool clipOn = true;
    public Guid SessionId { get; }

    private readonly ViewerFiles files;

    public ViewerWindow(IPopClient api, Guid sessionId, object start) : base("remote-viewer.html", WindowSizes.Get("remote", new Size(1360, 860)), api)
    {
        files = new ViewerFiles(Send, () => api.Server);
        SessionId = sessionId;
        this.start = start;
        SizeKey = "remote";
        Text = "iPop Remote help";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        KeyPreview = false;
    }

    protected override void OnReady()
    {
        Log.Info("remote", "viewer ready " + SessionId);
        Send(start);
        clip = new ClipWatcher(OnClipboard);
    }

    public void Answer(string status) { Log.Info("remote", $"viewer answer {SessionId} {status}"); Send(new { type = "answer", status }); }
    public void Ended(string? by) => Send(new { type = "remoteEnded", endedBy = by });

    /// A connect that didn't start (offline, wrong password, locked…): the viewer shows the matching screen.
    public void Problem(int status, JsonNode? body) => Send(new { type = "problem", status, body });

    /// "Try again" on the offline / couldn't-connect screen.
    public event Action? RetryRequested;
    /// The ICT changed a viewer preference in the toolbar (e.g. Fit / 1:1 / Fill): remembered in Settings › Remote.
    public event Action<string, string>? RemotePref;

    /// The save folder and file limit for this session (Settings › Remote and the company policy).
    public void ConfigureFiles(string? saveDir, long maxBytes) => files.Configure(saveDir, maxBytes);

    private void OnClipboard()
    {
        // Whatever the helper copies on their own PC goes over, so they can paste it into the user's screen.
        if (!clipOn) return;
        var r = Clip.Read();
        if (r is not { } v) return;
        if (v.Payload is null && v.FileName is { } path && OutgoingFiles.Add(path) is { } token)
            Send(new { type = "clipFile", name = Path.GetFileName(path), size = new FileInfo(path).Length, url = OutgoingFiles.Url(token) });
        else if (v.Payload is not null) Send(new { type = "clipboard", kind = v.Payload["kind"]?.GetValue<string>(), text = v.Payload["text"]?.GetValue<string>(), data = v.Payload["data"]?.GetValue<string>() });
    }

    protected override async void HandleMessage(string type, JsonNode? msg)
    {
        switch (type)
        {
            case "log": Log.Info("viewer", msg?["text"]?.GetValue<string>() ?? ""); break;
            case "xferBegin" or "xferChunk" or "xferEnd" or "xferRefused": files.Handle(type, msg); break;
            case "remotePref": RemotePref?.Invoke(msg?["key"]?.GetValue<string>() ?? "", msg?["value"]?.GetValue<string>() ?? ""); break;
            case "retry": RetryRequested?.Invoke(); break;
            case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "fullscreen": SetFullScreen(msg?["on"]?.GetValue<bool>() == true); break;
            case "setClipboard": Clip.Apply(msg!); break;
            case "clipSync": clipOn = msg?["on"]?.GetValue<bool>() == true; break;
            case "uploadMedia":
                var result = await UploadMediaAsync(msg);
                Send(new { type = "mediaUploaded", error = result });
                break;
            default: base.HandleMessage(type, msg); break;
        }
    }

    private Rectangle? normalBounds;

    /// Covers the whole monitor (taskbar included) while on; back to the previous size after.
    private void SetFullScreen(bool on)
    {
        if (on)
        {
            normalBounds ??= WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;
        }
        else
        {
            TopMost = false;
            if (normalBounds is { } b) Bounds = b;
            normalBounds = null;
        }
    }

    private async Task<string?> UploadMediaAsync(JsonNode? msg)
    {
        try
        {
            var bytes = Convert.FromBase64String(msg?["data"]?.GetValue<string>() ?? "");
            using var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new(msg?["mime"]?.GetValue<string>() is { Length: > 0 } m ? m : "application/octet-stream");
            form.Add(part, "file", Path.GetFileName(msg?["name"]?.GetValue<string>() ?? "media"));
            using var res = await Api.SendContentAsync(HttpMethod.Post, "api/notifier/remote/media", form);
            if (res.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await res.Content.ReadAsStringAsync())?["message"]?.GetValue<string>() ?? "Upload failed.";
        }
        catch { return "Upload failed."; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ = Log.UploadAsync(Api);
        files.Close();
        clip?.Dispose();
        base.OnFormClosed(e);
    }
}
