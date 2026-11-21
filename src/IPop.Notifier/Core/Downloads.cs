using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace IPop.Notifier.Core;

/// Download / Show in folder / Open (1.5.0): a page asks, the app streams the file with the person's token into
/// their Downloads folder. Only allow-listed attachment routes (<see cref="ApiBridge.IsDownload"/>); names are cleaned
/// and made unique ("x (1).pdf"); only files this app saved itself may be revealed or opened afterwards, and
/// programs/scripts are never opened from here (Show in folder only). Saved files carry the internet zone mark, so
/// Windows (SmartScreen, Office Protected View) treats them like any browser download.
public static partial class Downloads
{
    private static readonly HashSet<string> Saved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// Never opened by the app (the person can still open them from the folder, with Windows' own warnings).
    private static readonly HashSet<string> NeverOpen = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr", ".pif", ".msi", ".msp", ".msix", ".appx", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".cpl", ".reg", ".lnk", ".url", ".jar", ".msc", ".scf", ".inf", ".application",
        ".appref-ms", ".gadget", ".dll", ".sys", ".settingcontent-ms", ".library-ms", ".iso", ".img", ".vhd", ".vhdx",
    };

    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(ref Guid rfid, uint flags, nint token, out nint path);

    /// The person's Downloads folder (moved Downloads folders included), else %USERPROFILE%\Downloads.
    public static string Folder
    {
        get
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            try
            {
                if (SHGetKnownFolderPath(ref id, 0, 0, out var p) == 0)
                {
                    try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } s) return s; }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// A safe file name: no folders, no characters Windows forbids, no device names, at most 150 characters.
    public static string CleanName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('/', '\\').Split('\\').LastOrDefault() ?? "");
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) sb.Append(bad.Contains(c) || char.IsControl(c) ? '_' : c);
        n = sb.ToString().Trim().TrimEnd('.', ' ');
        if (n.Length == 0 || n.Trim('.', '_').Length == 0) n = "download";
        var stem = Path.GetFileNameWithoutExtension(n);
        if (Reserved.Contains(stem.Split('.')[0], StringComparer.OrdinalIgnoreCase)) n = "_" + n;
        if (n.Length > 150)
        {
            var ext = Path.GetExtension(n);
            if (ext.Length > 20) ext = "";
            n = n[..(150 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return n;
    }

    /// Streams <paramref name="path"/> (allow-listed) into Downloads; progress is 0–100 (-1 when the size is unknown).
    /// Returns the saved file's full path.
    public static async Task<string> SaveAsync(IPopClient api, string path, string? name, Action<int> progress, CancellationToken ct = default)
    {
        path = path.TrimStart('/');
        if (!ApiBridge.IsDownload(path)) throw new DownloadException("This file can't be downloaded here.");
        var dir = Folder;
        Directory.CreateDirectory(dir);
        var clean = CleanName(name);

        // Already fetched for the viewer: copy that instead of downloading again.
        if (PreviewCache.CachedOriginal(path) is { } cached)
        {
            var (file, stream) = CreateUnique(dir, clean);
            try
            {
                await using (stream)
                await using (var src = File.OpenRead(cached))
                    await CopyAsync(src, stream, src.Length, progress, ct);
            }
            catch { TryDelete(file); throw; }
            return Done(api, file);
        }

        HttpResponseMessage res;
        try { res = await api.SendLongAsync(HttpMethod.Get, path, null, ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException) { throw new DownloadException("Can't reach iPop. Check the network and try again."); }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new DownloadException(res.StatusCode == System.Net.HttpStatusCode.NotFound ? "This file is no longer available." : "The download didn't work. Try again.");
            var (file, stream) = CreateUnique(dir, clean);
            try
            {
                await using (stream)
                await using (var body = await res.Content.ReadAsStreamAsync(ct))
                    await CopyAsync(body, stream, res.Content.Headers.ContentLength, progress, ct);
            }
            catch (Exception ex) when (ex is not DownloadException)
            {
                TryDelete(file);
                throw new DownloadException(ex is OperationCanceledException ? "The download was cancelled." : "The download stopped. Try again.");
            }
            return Done(api, file);
        }
    }

    private static string Done(IPopClient api, string file)
    {
        MarkFromInternet(file, api.Server);
        lock (Gate) Saved.Add(Path.GetFullPath(file));
        return file;
    }

    private static async Task CopyAsync(Stream from, Stream to, long? total, Action<int> progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long done = 0;
        var last = -2;
        int n;
        progress(total > 0 ? 0 : -1);
        while ((n = await from.ReadAsync(buffer, ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            var p = total > 0 ? (int)Math.Min(100, done * 100 / total.Value) : -1;
            if (p != last) { last = p; progress(p); }
        }
    }

    /// "x.pdf", else "x (1).pdf", "x (2).pdf"… created exclusively (no race with another download of the same name).
    private static (string File, FileStream Stream) CreateUnique(string dir, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 0; i < 1000; i++)
        {
            var file = Path.Combine(dir, i == 0 ? name : $"{stem} ({i}){ext}");
            if (File.Exists(file) || Directory.Exists(file)) continue;
            try { return (file, new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true)); }
            catch (IOException) when (File.Exists(file)) { }
        }
        throw new DownloadException("Couldn't save the file in Downloads.");
    }

    /// Zone.Identifier (internet zone), as a browser writes it.
    private static void MarkFromInternet(string file, Uri server)
    {
        try { File.WriteAllText(file + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={server.GetLeftPart(UriPartial.Authority)}/\r\n"); }
        catch { }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    private static bool IsMine(string? file, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(file)) return false;
        try { full = Path.GetFullPath(file); } catch { return false; }
        lock (Gate) return Saved.Contains(full) && File.Exists(full);
    }

    /// Show in folder: File Explorer with the file selected. Only files this app saved.
    public static bool Reveal(string? file)
    {
        if (!IsMine(file, out var full)) return false;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    /// Open: the file's own Windows app. Only files this app saved, and never programs or scripts.
    public static string? Open(string? file)
    {
        if (!IsMine(file, out var full)) return "This file can't be opened from here.";
        if (NeverOpen.Contains(Path.GetExtension(full))) { Reveal(full); return "Programs and scripts aren't opened from iPop Notifier. It's in your Downloads folder."; }
        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return null;
        }
        catch { return "Windows has no app to open this file."; }
    }
}

public sealed class DownloadException(string message) : Exception(message);

/// The viewer's files (1.5.0): originals and their PDF previews in the WebView media cache, served read-only at
/// https://media.notifier.ipop/. An original is cached by its route (attachments never change); an Office file's PDF
/// by the original's content hash, so it is converted on iPop once. Files older than 3 days are removed.
public static class PreviewCache
{
    public const string MediaHost = "https://media.notifier.ipop/";
    public const long MaxPreviewBytes = 300L * 1024 * 1024;
    public const long MaxConvertBytes = 60L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, string> Originals = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<Result>>> Work = new(StringComparer.OrdinalIgnoreCase);
    private static int pruned;

    public sealed record Result(string Kind, string? Url, string? Error);

    private static string Dir => IPop.Notifier.Windows.WebHost.MediaCache;

    /// pdf | image | video | audio | office | none (download only). Mirrors Ui/files.js fileKind.
    public static string KindOf(string? name) => Path.GetExtension(name ?? "").ToLowerInvariant() switch
    {
        ".pdf" => "pdf",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "image",
        ".mp4" or ".webm" or ".mov" or ".m4v" => "video",
        ".mp3" or ".wav" or ".m4a" or ".ogg" or ".aac" => "audio",
        ".doc" or ".docx" or ".rtf" or ".odt" or ".pptx" or ".xlsx" or ".xls" or ".xlsm" or ".csv" => "office",
        _ => "none",
    };

    public static string? CachedOriginal(string path) =>
        Originals.TryGetValue(path.TrimStart('/'), out var f) && File.Exists(f) ? f : null;

    /// The URL the viewer shows for this file; <paramref name="converting"/> runs once when an Office file has to be
    /// converted on iPop first (the page shows "Preparing preview").
    public static Task<Result> GetAsync(IPopClient api, string path, string name, Action converting)
    {
        path = path.TrimStart('/');
        var kind = KindOf(name);
        if (kind == "none") return Task.FromResult(new Result("none", null, null));
        if (!ApiBridge.IsDownload(path)) return Task.FromResult(new Result("none", null, "This file can't be previewed here."));
        Prune();
        var key = path + "|" + kind;
        var lazy = Work.GetOrAdd(key, _ => new Lazy<Task<Result>>(() => LoadAsync(api, path, name, kind, converting)));
        var task = lazy.Value;
        // A failure may be retried (network back): forget it once it is done.
        _ = task.ContinueWith(t => { if (t.IsFaulted || t.Result.Error is not null) Work.TryRemove(key, out _); }, TaskScheduler.Default);
        return task;
    }

    private static async Task<Result> LoadAsync(IPopClient api, string path, string name, string kind, Action converting)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var ext = kind == "office" ? ".bin" : Path.GetExtension(name).ToLowerInvariant();
            var original = Path.Combine(Dir, "v-" + Hash(Encoding.UTF8.GetBytes(api.Server + path))[..24] + ext);
            if (!File.Exists(original))
            {
                using var res = await api.SendLongAsync(HttpMethod.Get, path);
                if (!res.IsSuccessStatusCode) return new(kind, null, res.StatusCode == System.Net.HttpStatusCode.NotFound ? "This file is no longer available." : "Couldn't load the file from iPop.");
                if (res.Content.Headers.ContentLength > MaxPreviewBytes) return new("none", null, "This file is too large to preview. Download it instead.");
                var part = original + ".part";
                await using (var body = await res.Content.ReadAsStreamAsync())
                await using (var fs = File.Create(part))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await body.ReadAsync(buffer)) > 0)
                    {
                        total += n;
                        if (total > MaxPreviewBytes) { fs.Close(); File.Delete(part); return new("none", null, "This file is too large to preview. Download it instead."); }
                        await fs.WriteAsync(buffer.AsMemory(0, n));
                    }
                }
                File.Move(part, original, overwrite: true);
            }
            Originals[path] = original;
            if (kind != "office") return new(kind, MediaHost + Path.GetFileName(original), null);

            if (new FileInfo(original).Length > MaxConvertBytes) return new("none", null, "This file is too large to preview. Download it instead.");
            string hash;
            await using (var fs = File.OpenRead(original)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            var pdf = Path.Combine(Dir, "p-" + hash[..32] + ".pdf");
            if (!File.Exists(pdf))
            {
                converting();
                using var res = await api.SendLongAsync(HttpMethod.Post, "api/notifier/convert-preview?name=" + Uri.EscapeDataString(name), () =>
                {
                    var c = new StreamContent(File.OpenRead(original));
                    c.Headers.ContentType = new("application/octet-stream");
                    return c;
                });
                if (!res.IsSuccessStatusCode)
                {
                    string? message = null;
                    try { message = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync())?["message"]?.GetValue<string>(); } catch { }
                    var none = (int)res.StatusCode is 415 or 413;
                    return new(none ? "none" : kind, null, message ?? "iPop couldn't prepare a preview of this file. Download it instead.");
                }
                var part = pdf + ".part";
                await using (var fs = File.Create(part)) await res.Content.CopyToAsync(fs);
                File.Move(part, pdf, overwrite: true);
            }
            return new("office", MediaHost + Path.GetFileName(pdf), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            return new(kind, null, "Couldn't load the file from iPop. Check the network and try again.");
        }
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// Once per run: viewer files (v-*, p-*) older than 3 days go; remote-help media is left alone.
    private static void Prune()
    {
        if (Interlocked.Exchange(ref pruned, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Dir))
                {
                    var n = Path.GetFileName(f);
                    if (!(n.StartsWith("v-") || n.StartsWith("p-"))) continue;
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-3)) { try { File.Delete(f); } catch { } }
                }
            }
            catch { }
        });
    }
}
