namespace IPop.Infrastructure.MeetingRooms;

/// All recording IO is confined to canonical descendant files of this environment's configured roots.
public static class RecordingPaths
{
    private static bool Segments(string value) => !value.Contains('\\') && !value.Contains('\0')
        && value.Split('/').All(s => s.Length > 0 && s is not "." and not ".." && !s.Contains(':'));

    public static string? NasFile(string? root, string? path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return null;
        root = root.TrimEnd('/');
        if (!root.StartsWith('/') || root.Length <= 1 || !Segments(root[1..])
            || !path.StartsWith('/') || !Segments(path[1..])) return null;
        return path.StartsWith(root + "/", StringComparison.Ordinal) ? path : null;
    }

    public static string? LocalFile(string? root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrEmpty(relative)) return null;
        relative = relative.Replace('\\', '/');
        if (relative.StartsWith('/') || !Segments(relative)) return null;
        try
        {
            var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            // Do not follow junctions/symlinks into a different environment's files.
            for (var p = Path.GetDirectoryName(full); p is not null && p.Length >= basePath.Length; p = Path.GetDirectoryName(p))
                if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return null;
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return null;
            return full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }
}
