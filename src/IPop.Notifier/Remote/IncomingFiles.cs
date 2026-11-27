namespace IPop.Notifier.Remote;

/// Files arriving during remote help (BIGIS #8 v3), written to disk as they stream in: each transfer appends its
/// batches to a ".part" file in the target folder and becomes "<name>" (never overwriting: "name (2).ext") when the
/// last byte is in. Nothing is kept in memory beyond one batch. A transfer over the limit, cancelled or cut off is
/// deleted. Pure file I/O (no WinForms) so the tests compile it in.
///
/// Review PR #5: the sender's id only finds the entry — the ".part" name is a local random hex (no traversal, no
/// collisions); at most <see cref="MaxOpen"/> transfers per session; enough free disk is checked before starting;
/// Windows device names (CON, NUL, COM1…) are renamed; any I/O failure in Begin is a refusal, not a crash.
public sealed class IncomingFiles(Func<string> folder, long maxBytes, Func<string, long>? freeSpace = null)
{
    public sealed record Entry(string Id, string Name, long Size, string PartPath, FileStream Stream)
    {
        public long Written { get; set; }
    }

    public const int MaxOpen = 2;
    public const string PartExtension = ".ipop-part";
    /// Room left on the disk after the file (so a 2 GB file can't fill the disk to the last byte).
    public const long Headroom = 200L * 1024 * 1024;

    /// Why Begin refused (null = started).
    public enum Refusal { None, BadId, Busy, TooLarge, TooMany, NoSpace, Io }

    private readonly Dictionary<string, Entry> open = [];
    private readonly Func<string, long> free = freeSpace ?? FreeBytes;
    public long MaxBytes { get; set; } = maxBytes;
    public Refusal LastRefusal { get; private set; }

    /// Starts a transfer; null when refused (see <see cref="LastRefusal"/>).
    public Entry? Begin(string id, string? name, long size)
    {
        LastRefusal = Refusal.None;
        if (string.IsNullOrEmpty(id) || id.Length > 64) return Refuse(Refusal.BadId);
        if (open.ContainsKey(id)) return Refuse(Refusal.Busy);
        if (size < 0 || size > MaxBytes) return Refuse(Refusal.TooLarge);
        if (open.Count >= MaxOpen) return Refuse(Refusal.TooMany);
        try
        {
            var dir = folder();
            Directory.CreateDirectory(dir);
            var need = (size > 0 ? size : MaxBytes) + Headroom;
            if (free(dir) is var left and >= 0 && left < need) return Refuse(Refusal.NoSpace);
            var clean = Clean(name);
            // Local random name: the sender's id never reaches the path.
            var part = Path.Combine(dir, "." + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + PartExtension);
            var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: false);
            var e = new Entry(id, clean, size, part, fs);
            open[id] = e;
            return e;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            return Refuse(Refusal.Io);
        }
    }

    private Entry? Refuse(Refusal r) { LastRefusal = r; return null; }

    /// The word the pages show for a refusal: big | busy | space | fail.
    public static string Reason(Refusal r) => r switch
    {
        Refusal.TooLarge => "big", Refusal.TooMany => "busy", Refusal.NoSpace => "space", _ => "fail",
    };

    /// Appends one batch. False = refused (unknown id, or over the limit / the announced size): the transfer is gone.
    public bool Append(string id, ReadOnlySpan<byte> data)
    {
        if (!open.TryGetValue(id, out var e)) return false;
        if (e.Written + data.Length > MaxBytes || (e.Size > 0 && e.Written + data.Length > e.Size)) { Abort(id); return false; }
        try { e.Stream.Write(data); }
        catch (IOException) { Abort(id); return false; }
        e.Written += data.Length;
        return true;
    }

    /// Finishes: the ".part" becomes the real file when complete; otherwise it is deleted. Returns the final path or null.
    public string? End(string id, bool ok)
    {
        if (!open.Remove(id, out var e)) return null;
        try { e.Stream.Dispose(); } catch { }
        if (!ok || (e.Size > 0 && e.Written != e.Size)) { TryDelete(e.PartPath); return null; }
        try
        {
            var final = Unique(Path.GetDirectoryName(e.PartPath)!, e.Name);
            File.Move(e.PartPath, final);
            return final;
        }
        catch (IOException) { TryDelete(e.PartPath); return null; }
    }

    public void Abort(string id) => End(id, false);

    /// Session over (or the host page crashed): every unfinished transfer is deleted.
    public void AbortAll() { foreach (var id in open.Keys.ToList()) Abort(id); }

    public int Open => open.Count;

    /// Startup: ".part" files a crash or power loss left behind, older than a day, are deleted.
    public static int SweepOrphans(string dir, DateTime nowUtc)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*" + PartExtension))
                if (File.GetLastWriteTimeUtc(f) < nowUtc.AddDays(-1)) { TryDelete(f); n++; }
        }
        catch { }
        return n;
    }

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Clean(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('/', '\\').Split('\\').LastOrDefault() ?? "");
        // Windows' forbidden characters on every OS (the tests run on Linux too, where only '/' is forbidden).
        var bad = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        n = string.Concat(n.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (n.Length == 0) n = "file";
        if (n.Length > 150) n = n[..150];
        // "CON", "nul.txt", "COM1.log" open devices on Windows, not files: keep them as plain names.
        var stem = n.Split('.')[0].TrimEnd();
        if (Devices.Contains(stem)) n = "_" + n;
        return n;
    }

    public static string Unique(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    /// Free bytes on the folder's drive; -1 when unknown (then the check is skipped).
    public static long FreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
