using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPop.Web.Services;

/// One picker tab. Index matches the dataset's "c" field; Recent is the picker's own tab.
public sealed record EmojiCategory(string Id, string Label, string Icon)
{
    public static readonly EmojiCategory Recent = new("recent", "Recent", "🕘");

    /// The dataset's categories in tab order (Unicode groups; People & Body folds into Smileys).
    public static readonly IReadOnlyList<EmojiCategory> All =
    [
        new("smileys-people", "Smileys & People", "😀"),
        new("animals-nature", "Animals & Nature", "🐻"),
        new("food-drink", "Food & Drink", "🍔"),
        new("activities", "Activities", "⚽"),
        new("travel-places", "Travel & Places", "🚗"),
        new("objects", "Objects", "💡"),
        new("symbols", "Symbols", "🔣"),
        new("flags", "Flags", "🏳️"),
    ];
}

/// One emoji from the Unicode list. <see cref="Tones"/> holds the five uniform skin-tone
/// sequences (light to dark) when Unicode defines all of them, otherwise null.
public sealed record EmojiEntry(string Glyph, string Name, string Keywords, int Category, double Version, string[]? Tones, double ToneVersion)
{
    public static readonly string[] ToneNames = ["light skin tone", "medium-light skin tone", "medium skin tone", "medium-dark skin tone", "dark skin tone"];

    public bool HasTones => Tones is { Length: 5 };

    /// Tone 0 is the default (yellow) glyph; 1..5 pick the matching skin-tone sequence.
    public string WithTone(int tone) => HasTones && tone is >= 1 and <= 5 ? Tones![tone - 1] : Glyph;

    public string LabelWithTone(int tone) => HasTones && tone is >= 1 and <= 5 ? $"{Name}: {ToneNames[tone - 1]}" : Name;
}

/// The full Unicode emoji list (wwwroot/data/emoji, see LICENSE.txt there), fetched on the first
/// picker open and kept for the session. Chat and Mail share the one instance.
public sealed class EmojiCatalog
{
    public const string DatasetVersion = "18.0";
    public const string DatasetPath = "data/emoji/emoji-18.0.json";

    /// Newest emoji version the picker shows by default. Native fonts lag Unicode (Windows'
    /// Segoe UI Emoji stops around Emoji 15.0), and newer sequences would render as boxes or as
    /// split glyphs. The dataset itself stays complete; callers can raise this.
    public const double DefaultMaxVersion = 15.0;

    private readonly Func<CancellationToken, Task<string>> _load;
    private Task<bool>? _loading;
    private IReadOnlyList<EmojiEntry> _all = [];
    private IReadOnlyList<EmojiEntry>[] _byCategory = [];
    private Dictionary<string, (EmojiEntry Entry, int Tone)> _byGlyph = new(StringComparer.Ordinal);

    public EmojiCatalog(Func<CancellationToken, Task<string>> load) => _load = load;

    /// Production loader: the dataset is a static file next to index.html, not an API call.
    public static Func<CancellationToken, Task<string>> HttpLoader(string hostBase)
    {
        var http = new HttpClient { BaseAddress = new Uri(hostBase) };
        return ct => http.GetStringAsync(DatasetPath, ct);
    }

    public bool IsLoaded { get; private set; }
    public bool LoadFailed { get; private set; }
    public IReadOnlyList<EmojiEntry> All => _all;

    /// Loads once; concurrent callers share the request. A failed load is forgotten so the next
    /// call retries. <paramref name="ct"/> only stops this caller waiting (the shared fetch goes
    /// on for the others) and then throws <see cref="OperationCanceledException"/>.
    public async Task<bool> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return true;
        var task = _loading ??= LoadAsync();
        var ok = await task.WaitAsync(ct);
        // Reset here, not inside LoadAsync: a loader that fails synchronously completes LoadAsync
        // before ??= assigns it, and clearing there would be overwritten by the failed task.
        if (!ok && ReferenceEquals(_loading, task)) _loading = null;
        return ok;
    }

    private async Task<bool> LoadAsync()
    {
        try
        {
            Use(Parse(await _load(CancellationToken.None)));
            LoadFailed = false;
            return true;
        }
        catch (Exception)
        {
            LoadFailed = true;
            return false;
        }
    }

    internal void Use(IReadOnlyList<EmojiEntry> entries)
    {
        var byCategory = Enumerable.Range(0, EmojiCategory.All.Count).Select(_ => new List<EmojiEntry>()).ToArray();
        var byGlyph = new Dictionary<string, (EmojiEntry, int)>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Category >= 0 && e.Category < byCategory.Length) byCategory[e.Category].Add(e);
            AddGlyph(byGlyph, e.Glyph, e, 0);
            if (e.HasTones)
                for (var t = 0; t < 5; t++) AddGlyph(byGlyph, e.Tones![t], e, t + 1);
        }
        _all = entries;
        _byCategory = byCategory;
        _byGlyph = byGlyph;
        IsLoaded = true;
    }

    private static void AddGlyph(Dictionary<string, (EmojiEntry, int)> map, string glyph, EmojiEntry e, int tone)
    {
        map.TryAdd(glyph, (e, tone));
        map.TryAdd(StripVs16(glyph), (e, tone)); // stored text may have lost U+FE0F
    }

    private static string StripVs16(string s) => s.Replace("️", "", StringComparison.Ordinal);

    public IReadOnlyList<EmojiEntry> InCategory(int category, double? maxVersion = DefaultMaxVersion)
    {
        if (category < 0 || category >= _byCategory.Length) return [];
        var list = _byCategory[category];
        return maxVersion is { } max ? list.Where(e => e.Version <= max).ToList() : list;
    }

    /// The emoji a glyph belongs to (any tone, with or without U+FE0F), or null if not in the list.
    public (EmojiEntry Entry, int Tone)? Find(string glyph)
    {
        if (string.IsNullOrEmpty(glyph)) return null;
        if (_byGlyph.TryGetValue(glyph, out var hit) || _byGlyph.TryGetValue(StripVs16(glyph), out hit)) return hit;
        return null;
    }

    /// Whether this glyph is offered at <paramref name="maxVersion"/> (tone sequences can be newer than their base).
    public static bool Offered(EmojiEntry e, int tone, double? maxVersion) =>
        maxVersion is not { } max || (e.Version <= max && (tone == 0 || e.ToneVersion <= max));

    /// Whether tones are offered for this emoji at <paramref name="maxVersion"/>.
    public static bool TonesOffered(EmojiEntry e, double? maxVersion) => e.HasTones && Offered(e, 1, maxVersion);

    /// English search over names and CLDR keywords. Every query word must prefix-match a word;
    /// whole-name and name-prefix hits rank first, then name words, then keywords.
    public IReadOnlyList<EmojiEntry> Search(string query, int limit = 160, double? maxVersion = DefaultMaxVersion)
    {
        var terms = Words(query);
        if (terms.Length == 0 || limit <= 0) return [];
        var hits = new List<(EmojiEntry Entry, int Rank, int Order)>();
        for (var i = 0; i < _all.Count; i++)
        {
            var e = _all[i];
            if (maxVersion is { } max && e.Version > max) continue;
            var name = e.Name.ToLowerInvariant();
            var nameWords = Words(name);
            var keyWords = Words(e.Keywords);
            var rank = 0;
            var all = true;
            foreach (var t in terms)
            {
                if (nameWords.Any(w => w.StartsWith(t, StringComparison.Ordinal))) continue;
                if (keyWords.Any(w => w.StartsWith(t, StringComparison.Ordinal))) { rank = Math.Max(rank, 3); continue; }
                all = false;
                break;
            }
            if (!all) continue;
            if (rank == 0)
            {
                var q = string.Join(' ', terms);
                rank = name == q ? 0 : name.StartsWith(q, StringComparison.Ordinal) ? 1 : 2;
            }
            hits.Add((e, rank, i));
        }
        return hits.OrderBy(h => h.Rank).ThenBy(h => h.Order).Take(limit).Select(h => h.Entry).ToList();
    }

    private static readonly char[] Separators = [' ', ':', ',', '-', '.', '’', '\'', '(', ')', '!', '“', '”', '&', '_', '/', '"'];

    private static string[] Words(string? s) =>
        string.IsNullOrWhiteSpace(s) ? [] : s.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    // ---- dataset ----

    private sealed record Row(
        [property: JsonPropertyName("e")] string? E,
        [property: JsonPropertyName("n")] string? N,
        [property: JsonPropertyName("k")] string? K,
        [property: JsonPropertyName("c")] int C,
        [property: JsonPropertyName("v")] double V,
        [property: JsonPropertyName("t")] string[]? T,
        [property: JsonPropertyName("tv")] double? Tv);

    private sealed record Dataset(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("emoji")] Row[]? Emoji);

    /// Parses emoji-&lt;version&gt;.json (tools/emoji/build-emoji-data.mjs). Malformed rows are skipped.
    public static IReadOnlyList<EmojiEntry> Parse(string json)
    {
        var data = JsonSerializer.Deserialize<Dataset>(json) ?? throw new InvalidDataException("empty emoji dataset");
        if (data.Emoji is null || data.Emoji.Length == 0) throw new InvalidDataException("emoji dataset has no rows");
        var list = new List<EmojiEntry>(data.Emoji.Length);
        foreach (var r in data.Emoji)
        {
            if (string.IsNullOrEmpty(r.E) || string.IsNullOrEmpty(r.N)) continue;
            var tones = r.T is { Length: 5 } t && t.All(x => !string.IsNullOrEmpty(x)) ? t : null;
            list.Add(new EmojiEntry(r.E, r.N, r.K ?? "", r.C, r.V, tones, tones is null ? 0 : Math.Max(r.V, r.Tv ?? r.V)));
        }
        return list;
    }
}
