namespace IPop.Notifier.Remote;

/// Files arriving during remote help (BIGIS #8 v3), written to disk as they stream in: each transfer appends its
/// batches to "<name>.part" in the target folder and becomes "<name>" (never overwriting: "name (2).ext") when the
/// last byte is in. Nothing is kept in memory beyond one batch. A transfer over the limit, cancelled or cut off is
/// deleted. Pure file I/O (no WinForms) so the tests compile it in.
public sealed class IncomingFiles(Func<string> folder, long maxBytes)
{
    public sealed record Entry(string Id, string Name, long Size, string PartPath, FileStream Stream)
    {
        public long Written { get; set; }
    }

    private readonly Dictionary<string, Entry> open = [];
    public long MaxBytes { get; set; } = maxBytes;

    /// Starts a transfer; null when the announced size is over the limit or the id is already in use.
    public Entry? Begin(string id, string? name, long size)
    {
        if (id.Length is 0 or > 64 || open.ContainsKey(id) || size < 0 || size > MaxBytes) return null;
        var dir = folder();
        Directory.CreateDirectory(dir);
        var clean = Clean(name);
        var part = Path.Combine(dir, clean + "." + id[..Math.Min(8, id.Length)] + ".part");
        var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: false);
        var e = new Entry(id, clean, size, part, fs);
        open[id] = e;
        return e;
    }

    /// Appends one batch. False = refused (unknown id, or over the limit / the announced size): the transfer is gone.
    public bool Append(string id, ReadOnlySpan<byte> data)
    {
        if (!open.TryGetValue(id, out var e)) return false;
        if (e.Written + data.Length > MaxBytes || (e.Size > 0 && e.Written + data.Length > e.Size)) { Abort(id); return false; }
        e.Stream.Write(data);
        e.Written += data.Length;
        return true;
    }

    /// Finishes: the ".part" becomes the real file when complete; otherwise it is deleted. Returns the final path or null.
    public string? End(string id, bool ok)
    {
        if (!open.Remove(id, out var e)) return null;
        e.Stream.Dispose();
        if (!ok || (e.Size > 0 && e.Written != e.Size)) { TryDelete(e.PartPath); return null; }
        var final = Unique(Path.GetDirectoryName(e.PartPath)!, e.Name);
        File.Move(e.PartPath, final);
        return final;
    }

    public void Abort(string id) => End(id, false);

    /// Session over: every unfinished transfer is deleted.
    public void AbortAll() { foreach (var id in open.Keys.ToList()) Abort(id); }

    public int Open => open.Count;

    public static string Clean(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('/', '\\').Split('\\').LastOrDefault() ?? "");
        // Windows' forbidden characters on every OS (the tests run on Linux too, where only '/' is forbidden).
        var bad = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        n = string.Concat(n.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (n.Length == 0) n = "file";
        return n.Length > 150 ? n[..150] : n;
    }

    public static string Unique(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        return path;
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
}
