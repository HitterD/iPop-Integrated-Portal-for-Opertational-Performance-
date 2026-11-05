using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Files;

/// <summary>
/// Local disk copy of big NAS attachments for the API to serve with HTTP ranges. Without it every range request of
/// a PDF viewer or a video (5-20 per file) downloaded the whole file from the NAS again into a temp file - a 300 MB
/// attachment meant several full NAS transfers, and nothing reached the browser until each copy finished.
/// The first request downloads the file once (one download per file at a time); later ones are served from disk.
/// Copies not read for 24 h, and the oldest ones beyond the size cap, are removed.
/// </summary>
public sealed class AttachmentFileCache(IConfiguration config)
{
    public static readonly TimeSpan Unused = TimeSpan.FromHours(24);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static DateTime _lastSweep;

    private string Dir => config["Attachments:CacheDir"] ?? Path.Combine(Path.GetTempPath(), "ipop-attachment-cache");
    private long MaxBytes => (config.GetValue<long?>("Attachments:CacheMaxMb") ?? 20_000) * 1024 * 1024;

    /// Path of the local copy of <paramref name="key"/>, downloading it with <paramref name="download"/> when missing.
    public async Task<string> GetAsync(string key, Func<CancellationToken, Task<Stream>> download, CancellationToken ct)
    {
        var name = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name + ".bin");
        var gate = Gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path))
            {
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await using (var src = await download(ct))
                    await using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                        await src.CopyToAsync(dst, ct);
                    File.Move(tmp, path, overwrite: true);
                }
                catch
                {
                    try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw;
                }
            }
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
        }
        finally { gate.Release(); }
        Sweep(path);
        return path;
    }

    /// At most every 10 minutes: drop copies unread for 24 h, then the least recently read beyond the size cap.
    private void Sweep(string keep)
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(10)) return;
        _lastSweep = now;
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*.bin").Where(f => f.FullName != keep).OrderBy(f => f.LastAccessTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (f.LastAccessTimeUtc >= now - Unused && total <= MaxBytes) break;
                try { f.Delete(); total -= f.Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
    }
}
