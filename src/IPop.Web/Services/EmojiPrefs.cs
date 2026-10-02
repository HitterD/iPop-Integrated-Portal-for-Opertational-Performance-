using System.Text.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Per-account emoji picker preferences: the 30 most recently picked emoji (distinct, newest
/// first) and the chosen skin tone. Shared by Chat and Mail, kept in this browser's
/// localStorage under the signed-in NIK so they survive sign-out and other accounts on the same
/// browser keep their own. Sign-out only drops the in-memory copy.
///
/// Storage is best effort: unreadable or corrupt data reads as empty, and a full (quota) or
/// unavailable storage keeps the change in memory for this session instead of failing the pick.
public sealed class EmojiPrefs
{
    public const int MaxRecent = 30;
    public const int MaxGlyphLength = 64;   // longest real ZWJ sequence is ~35 UTF-16 units
    public const string KeyPrefix = "ipop.emoji.v1.";

    private readonly IJSRuntime _js;
    private readonly AuthState _auth;
    private List<string> _recent = [];
    private int _tone;
    private string? _loadedFor;

    public EmojiPrefs(IJSRuntime js, AuthState auth)
    {
        _js = js;
        _auth = auth;
        _auth.SignedOut += Unload;
    }

    public IReadOnlyList<string> Recent => _recent;

    /// 0 = default (yellow), 1..5 = light .. dark.
    public int Tone => _tone;

    /// Also list emoji newer than <see cref="EmojiCatalog.DefaultMaxVersion"/>, which this
    /// device's emoji font may not draw yet.
    public bool ShowNewer => _showNewer;
    private bool _showNewer;

    public event Action? Changed;

    private string? Nik => string.IsNullOrWhiteSpace(_auth.Me?.Nik) ? null : _auth.Me!.Nik.Trim();

    public static string KeyFor(string nik) => KeyPrefix + nik;

    /// Reads the signed-in account's prefs once (again after an account switch).
    public async Task EnsureLoadedAsync()
    {
        var nik = Nik;
        if (nik is null || nik == _loadedFor) return;
        _loadedFor = nik;
        _recent = [];
        _tone = 0;
        _showNewer = false;
        string? raw = null;
        try { raw = await _js.InvokeAsync<string?>("localStorage.getItem", KeyFor(nik)); }
        catch { /* storage blocked: start empty */ }
        if (nik != _loadedFor) return;   // account switched while reading
        (_recent, _tone, _showNewer) = Decode(raw);
    }

    public async Task SetShowNewerAsync(bool on)
    {
        await EnsureLoadedAsync();
        if (on == _showNewer) return;
        _showNewer = on;
        Changed?.Invoke();
        await SaveAsync();
    }

    /// Records a pick: moves it to the front, keeps 30 distinct.
    public async Task AddRecentAsync(string glyph)
    {
        if (!IsPlausibleGlyph(glyph)) return;
        await EnsureLoadedAsync();
        _recent.Remove(glyph);
        _recent.Insert(0, glyph);
        if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        Changed?.Invoke();
        await SaveAsync();
    }

    public async Task SetToneAsync(int tone)
    {
        tone = Math.Clamp(tone, 0, 5);
        await EnsureLoadedAsync();
        if (tone == _tone) return;
        _tone = tone;
        Changed?.Invoke();
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        var nik = Nik;
        if (nik is null || nik != _loadedFor) return;
        // On a full storage retry with a shorter list before giving up; memory keeps the full one.
        foreach (var keep in new[] { _recent.Count, Math.Min(_recent.Count, 10), 0 })
        {
            try
            {
                await _js.InvokeVoidAsync("localStorage.setItem", KeyFor(nik), Encode(_recent.Take(keep), _tone, _showNewer));
                return;
            }
            catch (JSException) { /* QuotaExceededError and friends: try smaller */ }
            catch { return; }   // interop unavailable (prerender, disposed circuit)
        }
    }

    private void Unload()
    {
        _loadedFor = null;
        _recent = [];
        _tone = 0;
        _showNewer = false;
    }

    // ---- format: {"v":1,"recent":["😀",...],"tone":0,"newer":false} ----

    private sealed record Stored(int V, string[]? Recent, int Tone, bool Newer);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Encode(IEnumerable<string> recent, int tone, bool showNewer = false) =>
        JsonSerializer.Serialize(new Stored(1, recent.ToArray(), tone, showNewer), Json);

    /// Tolerates anything: null, non-JSON, wrong shapes, junk entries, oversize lists.
    internal static (List<string> Recent, int Tone, bool ShowNewer) Decode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ([], 0, false);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ([], 0, false);
            var recent = new List<string>();
            if (doc.RootElement.TryGetProperty("recent", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var g = item.GetString();
                    if (g is null || !IsPlausibleGlyph(g) || recent.Contains(g)) continue;
                    recent.Add(g);
                    if (recent.Count == MaxRecent) break;
                }
            }
            var tone = doc.RootElement.TryGetProperty("tone", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) && n is >= 0 and <= 5 ? n : 0;
            var newer = doc.RootElement.TryGetProperty("newer", out var nw) && nw.ValueKind == JsonValueKind.True;
            return (recent, tone, newer);
        }
        catch (JsonException) { return ([], 0, false); }
    }

    /// Cheap sanity check: short, no ASCII letters/digits/controls, contains a non-BMP or symbol char.
    internal static bool IsPlausibleGlyph(string? g)
    {
        if (string.IsNullOrEmpty(g) || g.Length > MaxGlyphLength) return false;
        foreach (var ch in g)
            if (ch < 0x20 || (ch < 0x7F && char.IsLetter(ch))) return false;
        return g.Any(ch => ch > 0x7F);
    }
}
