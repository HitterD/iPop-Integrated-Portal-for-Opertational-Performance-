namespace IPop.Web.Services;

public static class DirectorySuggest
{
    // Only loaded names are considered: no guessed people or hidden-directory lookups.
    public static string? Find(string query, IEnumerable<string> names)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length < 3) return null;
        return names.Distinct().Select(name => (Name: name, Distance: Distance(query, name.ToLowerInvariant())))
            .Where(x => x.Distance is > 0 and <= 2).OrderBy(x => x.Distance).ThenBy(x => x.Name).Select(x => x.Name).FirstOrDefault();
    }

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (var j = 1; j <= b.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
