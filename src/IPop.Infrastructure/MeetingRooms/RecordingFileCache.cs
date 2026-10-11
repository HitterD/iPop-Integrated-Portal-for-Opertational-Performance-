using IPop.Application.Files;
using IPop.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.MeetingRooms;

public sealed class RecordingFileCache(IFileAccessService nas, IConfiguration config)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string Dir => config["MeetingRooms:CacheDir"]
        ?? Path.Combine(Path.GetDirectoryName((config["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool").TrimEnd('\\', '/'))!, "recordings-cache");

    public async Task<string?> OpenAsync(MeetingRecording row, CancellationToken ct)
    {
        if (row.NasPath is null) return null;
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, row.Id.ToString("N") + ".mp4");
        await Gate.WaitAsync(ct);
        try
        {
            foreach (var old in new DirectoryInfo(Dir).GetFiles("*.mp4").Where(f => f.LastAccessTimeUtc < DateTime.UtcNow.AddHours(-24) && f.FullName != path))
                try { old.Delete(); } catch (IOException) { }
            if (!File.Exists(path))
            {
                var tmp = path + ".part";
                try
                {
                    await using (var src = await nas.DownloadAsServiceAsync(row.NasPath, ct))
                    await using (var dst = File.Create(tmp))
                        await src.CopyToAsync(dst, ct);
                    File.Move(tmp, path, overwrite: true);
                }
                catch
                {
                    // Streams are disposed before cleanup; retain the original download error.
                    try { File.Delete(tmp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    throw;
                }
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        finally { Gate.Release(); }
    }
}
