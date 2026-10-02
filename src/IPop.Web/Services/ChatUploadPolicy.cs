namespace IPop.Web.Services;

public static class ChatUploadPolicy
{
    public const int MaxFiles = 5;
    public const long MaxBytes = 50L * 1024 * 1024;
    public sealed record File(string Name, long Size);
    public sealed record Selection(IReadOnlyList<int> Accepted, string? Notice);

    public static Selection Plan(IReadOnlyList<File> files, int pending)
    {
        var remaining = Math.Max(0, MaxFiles - pending);
        var accepted = new List<int>();
        var tooBig = new List<string>();
        var empty = new List<string>();
        var excess = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            if (f.Size <= 0) { empty.Add(f.Name); continue; }
            if (f.Size > MaxBytes) { tooBig.Add(f.Name); continue; }
            if (accepted.Count >= remaining) { excess++; continue; }
            accepted.Add(i);
        }
        var notes = new List<string>();
        if (tooBig.Count > 0) notes.Add($"{string.Join(", ", tooBig)} exceeded the 50 MB limit.");
        if (empty.Count > 0) notes.Add($"{string.Join(", ", empty)} are empty files.");
        if (excess > 0) notes.Add($"You can attach up to {MaxFiles} files per message; {excess} file(s) were not added.");
        return new Selection(accepted, notes.Count == 0 ? null : string.Join(" ", notes));
    }
}
