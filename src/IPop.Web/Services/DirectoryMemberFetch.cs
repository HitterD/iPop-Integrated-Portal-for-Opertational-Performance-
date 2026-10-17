namespace IPop.Web.Services;

public static class DirectoryMemberFetch
{
    public static async Task<List<string>?> LoadAsync(Func<int, Task<DirectoryPageDto?>> fetchPage, Func<bool> isCurrent)
    {
        var members = new List<string>();
        for (var page = 1; ; page++)
        {
            if (!isCurrent()) return null;
            var result = await fetchPage(page);
            if (!isCurrent() || result is null) return null;
            members.AddRange(result.Items.Select(person => person.Nik));
            if (members.Count >= result.Total || result.Items.Length == 0) break;
        }
        return isCurrent() ? members : null;
    }
}
