using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace IPop.Notifier.Remote;

/// Clipboard both ways during remote help: text, pictures and files. Incoming items go on this PC's clipboard so the
/// person pastes with Ctrl+V as usual; incoming files are written to %TEMP%\iPopRemote first.
public static class Clip
{
    /// The last thing we put on the clipboard ourselves, so the change watcher doesn't send it straight back.
    public static string? LastSetHash { get; private set; }

    public static void Apply(JsonNode m)
    {
        try
        {
            switch (m["kind"]?.GetValue<string>())
            {
                case "text":
                    var text = m["text"]?.GetValue<string>() ?? "";
                    if (text.Length is 0 or > 2_000_000) return;
                    LastSetHash = Hash(text);
                    Clipboard.SetText(text);
                    break;
                case "image":
                    var bytes = Convert.FromBase64String(m["data"]?.GetValue<string>() ?? "");
                    if (bytes.Length is 0 or > 30_000_000) return;
                    using (var ms = new MemoryStream(bytes))
                    using (var img = Image.FromStream(ms))
                    {
                        LastSetHash = "img:" + bytes.Length;
                        Clipboard.SetImage(img);
                    }
                    break;
            }
        }
        catch { /* clipboard held by another app; the next copy works */ }
    }

    public static void SetFile(string name, byte[] bytes)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "iPopRemote");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Path.GetFileName(name));
            File.WriteAllBytes(path, bytes);
            LastSetHash = "file:" + path;
            Clipboard.SetFileDropList(new StringCollection { path });
        }
        catch { }
    }

    /// What is on the clipboard now, for the other side; null when it is ours, empty or too big.
    public static (JsonNode? Payload, string? FileName, byte[]? FileBytes)? Read()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var path = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault();
                if (path is null || !File.Exists(path) || "file:" + path == LastSetHash) return null;
                var info = new FileInfo(path);
                if (info.Length > 200L * 1024 * 1024) return null;
                LastSetHash = "file:" + path;
                return (null, info.Name, File.ReadAllBytes(path));
            }
            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img is null) return null;
                using var ms = new MemoryStream();
                img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var hash = "img:" + ms.Length;
                if (hash == LastSetHash) return null;
                LastSetHash = hash;
                return (new JsonObject { ["kind"] = "image", ["data"] = Convert.ToBase64String(ms.ToArray()) }, null, null);
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (text.Length is 0 or > 2_000_000 || Hash(text) == LastSetHash) return null;
                LastSetHash = Hash(text);
                return (new JsonObject { ["kind"] = "text", ["text"] = text }, null, null);
            }
        }
        catch { }
        return null;
    }

    private static string Hash(string s) => "txt:" + s.Length + ":" + s.GetHashCode();
}

/// Watches the clipboard (WM_CLIPBOARDUPDATE) while a session runs.
public sealed partial class ClipWatcher : NativeWindow, IDisposable
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint h);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint h);
    private readonly Action changed;

    public ClipWatcher(Action changed)
    {
        this.changed = changed;
        CreateHandle(new CreateParams());
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;
        if (m.Msg == WM_CLIPBOARDUPDATE) changed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != 0) { RemoveClipboardFormatListener(Handle); DestroyHandle(); }
    }
}

public static class Downloads
{
    /// Saves into the person's Downloads folder and never overwrites ("name (2).ext").
    public static string Save(string name, byte[] bytes)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(dir);
        var bad = Path.GetInvalidFileNameChars();
        var safe = string.Concat(Path.GetFileName(name).Select(c => bad.Contains(c) ? '_' : c));
        if (safe.Length == 0) safe = "file";
        var path = Path.Combine(dir, safe);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// The optional admin-installed "iPop Remote Helper" service (UAC prompts, lock screen, Ctrl+Alt+Del) is a later
/// add-on. Without it Ctrl+Alt+Del opens Task Manager instead and secure screens show black to the helper.
public static class HelperService
{
    public static bool Installed => false;
    public static bool SendSas() => false;
}
