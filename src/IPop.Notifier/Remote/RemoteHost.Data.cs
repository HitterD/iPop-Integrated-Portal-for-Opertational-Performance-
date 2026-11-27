using System.Text.Json.Nodes;
using IPop.Notifier.Core;

namespace IPop.Notifier.Remote;

/// What arrives from the helper during a session, filtered by what the user allowed (BIGIS #8): mouse/keyboard only
/// with "control" (and in control mode), clipboard only with "clipboard", files only with "files". Refused data is
/// dropped here on the user's PC and logged — the helper's buttons are never the gate.
public sealed partial class RemoteHost
{
    private int refusedData;

    private void OnData(string? topic, JsonNode? msg)
    {
        if (msg is null || sessionId is null) return;
        if (!perms.Accepts(topic, mode)) { Refuse(topic); return; }
        if (topic == "in") { PlayInput(msg); return; }
        if (topic == "chat") { help?.Send(new { type = "chat", text = msg["text"]?.GetValue<string>() ?? "" }); return; }
        if (topic == "clip") { if (clipSync) Clip.Apply(msg); return; }
        switch (msg["t"]?.GetValue<string>())
        {
            case "mode":
                mode = perms.ClampMode(msg["mode"]?.GetValue<string>());
                if (mode == "view") input.ReleaseAll();
                break;
            case "quality": preset = Preset(msg["preset"]?.GetValue<string>()); page?.Send(new { type = "quality", preset }); break;
            case "clipSync": clipSync = perms.ClampClipSync(msg["on"]?.GetValue<bool>() == true); break;
            case "monitor": SwitchMonitor(msg["index"]?.GetValue<int>() ?? 0); break;
            case "media": _ = ShowMediaAsync(msg); break;
            case "bye": _ = EndAsync("helper", notifyServer: false); break;
        }
    }

    /// Logged once per topic burst (input arrives 100+ times a second).
    private void Refuse(string? topic)
    {
        if (refusedData++ % 200 == 0) Log.Warn("remote", $"refused {topic} from the helper: not allowed by the user (perms={perms.ToList()} mode={mode})");
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
        if (kind != "default" && Guid.TryParse(id, out var att) && sessionId is { } sid)
        {
            // Only this session's helper's own "My media" (the server checks owner + active session; review PR #5).
            var bytes = await api.GetBytesAsync($"api/notifier/remote/sessions/{sid}/media/{att}");
            if (bytes is { Length: > 0 }) url = await help.PutMediaAsync(bytes, kind == "video" ? "video/mp4" : "image/jpeg");
        }
        help.Send(new { type = "media", kind = url is null ? "default" : kind, url, sound = m["sound"]?.GetValue<bool>() == true });
    }

    /// The user copied something while sync is on (and the clipboard is allowed): send it to the helper.
    internal void ClipboardChanged(JsonNode payload, bool isFile, string? name, byte[]? fileBytes)
    {
        if (sessionId is null || !clipSync || !perms.Clipboard || page is null) return;
        if (isFile && name is not null) SendPath(name, "clip-file"); // a copied file: streamed from disk, never read whole
        else if (!isFile) page.Send(new { type = "send", topic = "clip", msg = payload });
    }
}
