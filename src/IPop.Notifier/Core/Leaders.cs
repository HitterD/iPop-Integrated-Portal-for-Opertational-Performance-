using System.Text.Json.Nodes;

namespace IPop.Notifier.Core;

/// My leaders and VIPs (1.5.0 priority inbox): anything they send always needs action. Loaded from
/// GET /api/notifier/leaders after sign-in, every 30 minutes and after a settings change (VIPs live in the settings,
/// so a VIP added in Settings counts at once even before the reload answers). The last answer is kept in memory only.
public sealed class Leaders(IPopClient api)
{
    public sealed record Person(Guid Id, string Name, string? Department, string Reason);

    private Dictionary<Guid, Person> leaders = [];
    private Dictionary<Guid, Person> vips = [];
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    public DateTime? LoadedAt { get; private set; }
    public event Action? Changed;

    public IReadOnlyCollection<Person> LeaderList => leaders.Values;
    public IReadOnlyCollection<Person> VipList => vips.Values;

    /// Why <paramref name="actor"/> counts: the leader reason ("Manager", "BOD / GM level"…), "VIP", or null.
    /// VIP ids come from the current settings (fresh), names from the last /leaders answer.
    public string? ReasonFor(Guid? actor, NotifierSettings s)
    {
        if (actor is not { } id || id == Guid.Empty) return null;
        if (leaders.TryGetValue(id, out var l)) return l.Reason;
        return s.Vips.Contains(id) || vips.ContainsKey(id) ? "VIP" : null;
    }

    public bool IsLeaderOrVip(Guid? actor, NotifierSettings s) => ReasonFor(actor, s) is not null;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var json = await api.GetJsonAsync("api/notifier/leaders", ct);
        if (json is null) return; // offline / older server: keep what we had
        leaders = Read(json["leaders"], withReason: true);
        vips = Read(json["vips"], withReason: false);
        LoadedAt = DateTime.UtcNow;
        Changed?.Invoke();
    }

    public void Reset()
    {
        leaders = [];
        vips = [];
        LoadedAt = null;
    }

    private static Dictionary<Guid, Person> Read(JsonNode? list, bool withReason)
    {
        var map = new Dictionary<Guid, Person>();
        if (list is not JsonArray arr) return map;
        foreach (var p in arr)
        {
            if (!Guid.TryParse(p?["id"]?.GetValue<string>(), out var id)) continue;
            map[id] = new Person(id, p?["name"]?.GetValue<string>() ?? "", p?["department"]?.GetValue<string>(),
                withReason ? p?["reason"]?.GetValue<string>() ?? "Leader" : "VIP");
        }
        return map;
    }
}
