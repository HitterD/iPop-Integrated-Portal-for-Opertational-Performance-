using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Favorite directory contacts (Kontak Prioritas). Sumber kebenaran adalah server
/// (GET/PUT/DELETE /api/directory/favorites); localStorage hanya cache baca cepat
/// + fallback saat server tak terjangkau, disinkron ulang saat EnsureLoaded.
public sealed class FavoriteContacts
{
    private const string StorageKey = "ipop.directory.favorites";
    private readonly IJSRuntime _js;
    private readonly AuthState _auth;
    private HashSet<string> _favorites = [];
    private bool _loaded;

    public FavoriteContacts(IJSRuntime js, AuthState auth)
    {
        _js = js;
        _auth = auth;
    }

    public event Action? Changed;

    public IReadOnlySet<string> All => _favorites;

    public bool IsFavorite(string nik) => _favorites.Contains(nik);

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        // Cache lokal dulu agar UI langsung terisi, lalu timpa dengan data server.
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(raw))
                _favorites = (System.Text.Json.JsonSerializer.Deserialize<string[]>(raw) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        try
        {
            var server = await _auth.GetAsync<string[]>("/api/directory/favorites");
            if (server is not null)
            {
                _favorites = server.ToHashSet(StringComparer.OrdinalIgnoreCase);
                await PersistLocalAsync();
            }
        }
        catch { /* offline / server lama — pakai cache lokal apa adanya */ }
    }

    /// Toggle favorit. Mengembalikan status favorit SETELAH toggle.
    public async Task<bool> ToggleAsync(string nik)
    {
        await EnsureLoadedAsync();
        var nowFav = !_favorites.Contains(nik);
        if (nowFav) _favorites.Add(nik); else _favorites.Remove(nik);
        await PersistLocalAsync();
        Changed?.Invoke();
        try
        {
            if (nowFav)
                await _auth.SendAsync(HttpMethod.Put, $"/api/directory/favorites/{Uri.EscapeDataString(nik)}");
            else
                await _auth.SendAsync(HttpMethod.Delete, $"/api/directory/favorites/{Uri.EscapeDataString(nik)}");
        }
        catch
        {
            // Server gagal — cache lokal tetap jadi sumber sementara; sinkron ulang
            // pada EnsureLoaded berikutnya (sesi baru / refresh).
        }
        return nowFav;
    }

    private async Task PersistLocalAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, System.Text.Json.JsonSerializer.Serialize(_favorites.ToArray())); }
        catch { }
    }
}
