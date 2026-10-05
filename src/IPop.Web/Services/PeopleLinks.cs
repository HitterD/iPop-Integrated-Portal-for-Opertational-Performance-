namespace IPop.Web.Services;

/// Office policy keeps NIKs out of the address bar, so in-app deep links (/chat?p=…, /mail?to=…,
/// /calls?callee=…, /directory/person/…) carry a per-tab token instead. Tokens live only in this
/// tab's memory: after a reload an old token resolves to nothing and the page simply opens plain.
/// A raw NIK still resolves too, so links bookmarked before the change keep working.
public sealed class PeopleLinks
{
    private readonly Dictionary<string, string> _nikByToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokenByNik = new(StringComparer.OrdinalIgnoreCase);

    public string Token(string nik)
    {
        if (_tokenByNik.TryGetValue(nik, out var t)) return t;
        do t = "p" + Random.Shared.NextInt64(0x10000000000, 0xFFFFFFFFFFFF).ToString("x");
        while (_nikByToken.ContainsKey(t));
        _tokenByNik[nik] = t;
        _nikByToken[t] = nik;
        return t;
    }

    public string Tokens(IEnumerable<string> niks) => string.Join(",", niks.Select(Token));

    /// The NIK behind a token, or the value itself when it isn't one of ours (legacy NIK links).
    public string? Nik(string? tokenOrNik)
    {
        if (string.IsNullOrWhiteSpace(tokenOrNik)) return null;
        var v = tokenOrNik.Trim();
        return _nikByToken.TryGetValue(v, out var nik) ? nik : v;
    }

    public List<string> Niks(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Nik).OfType<string>().ToList();

    public string Chat(string nik) => $"/chat?p={Token(nik)}";
    public string Mail(string nik) => $"/mail?to={Token(nik)}&compose=1";
    public string Profile(string nik) => $"/directory/person/{Token(nik)}";
    public string Calendar(string nik) => $"/calendar/person/{Token(nik)}";
    /// Calendar → New meeting with this person.
    public string Invite(string nik) => $"/calendar?invite={Token(nik)}";

    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public void RememberName(string nik, string name) => _names[nik] = name;

    /// Display name for a NIK (e.g. a news "user" target), looked up once per tab.
    public async Task<string> NameAsync(AuthState auth, string nik)
    {
        if (_names.TryGetValue(nik, out var n)) return n;
        try
        {
            var d = await auth.GetAsync<DirectoryEmployeeDto>($"/api/chat/users/{Uri.EscapeDataString(nik)}");
            if (d is not null) return _names[nik] = d.Name;
        }
        catch { }
        return "Unknown person";
    }
}
