using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// POST /api/notifier/devices/log: the Notifier's own log tail (remote help, support) saved for ICT as
/// {machine}-{user}.log. Bounded so a signed-in user can't fill the disk (review 2026-11-26, H3):
///   - the machine must be one of the caller's registered PCs (NotifierDevices), never a free name;
///   - at most <see cref="MaxFilesPerUser"/> files per person (the oldest goes);
///   - the folder stays under <see cref="MaxFolderBytes"/> (oldest files go first);
///   - a request carries at most <see cref="MaxChars"/> characters; the rate limit sits on the endpoint (Program.cs).
public static class NotifierDeviceLogs
{
    public const string RateLimitPolicy = "notifier-log";
    public const int MaxChars = 65_536;
    public const int MaxFilesPerUser = 5;
    public const long MaxFolderBytes = 200L * 1024 * 1024;

    /// 204 saved, 404 the machine isn't one of the caller's PCs.
    public static async Task<int> SaveAsync(IPopDbContext db, Guid userId, string? machine, TextReader body, string dir, CancellationToken ct)
    {
        var wanted = (machine ?? "").Trim();
        if (wanted.Length is 0 or > 64) return 404;
        var known = await db.NotifierDevices.AsNoTracking()
            .Where(d => d.UserId == userId && d.MachineName.ToLower() == wanted.ToLower())
            .Select(d => d.MachineName).FirstOrDefaultAsync(ct);
        if (known is null) return 404;

        var buf = new char[MaxChars];
        var n = await body.ReadBlockAsync(buf, ct);
        var safe = string.Concat(known.Where(char.IsAsciiLetterOrDigit).Take(40));
        if (safe.Length == 0) safe = "pc";
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{safe}-{userId:N}.log");
        await File.WriteAllTextAsync(file, new string(buf, 0, n), ct);
        Prune(dir, userId, file);
        return 204;
    }

    /// The person's own files beyond the newest <see cref="MaxFilesPerUser"/>, then the oldest files of anyone until the
    /// folder is under <see cref="MaxFolderBytes"/>. The file just written always stays.
    public static void Prune(string dir, Guid userId, string keep)
    {
        var all = new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var mine = all.Where(f => f.Name.EndsWith($"-{userId:N}.log", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var f in mine.Skip(MaxFilesPerUser).Where(f => !Same(f, keep))) TryDelete(f, all);
        var total = all.Sum(f => f.Length);
        for (var i = all.Count - 1; i >= 0 && total > MaxFolderBytes; i--)
        {
            if (Same(all[i], keep)) continue;
            total -= all[i].Length;
            TryDelete(all[i], all);
        }
    }

    private static bool Same(FileInfo f, string path) => string.Equals(f.FullName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(FileInfo f, List<FileInfo> all)
    {
        try { f.Delete(); all.Remove(f); } catch { }
    }
}
