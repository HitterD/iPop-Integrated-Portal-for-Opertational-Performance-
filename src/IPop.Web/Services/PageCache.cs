using System.Text.Json;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Last-seen data per page, so a page opens on what the user saw last time and refreshes
/// behind it (stale-while-revalidate) instead of sitting on skeletons while the API answers.
///
/// Memory always; localStorage only when the caller asks (persist: true), and only for list
/// summaries: never mail or chat bodies, which must not outlive the session on a shared PC.
/// Keys are scoped to the signed-in NIK and everything is wiped on sign-out.
public sealed class PageCache
{
    private const string Prefix = "ipop.cache.";
    /// Big payloads stay in memory only; localStorage is ~5 MB for the whole origin.
    private const int MaxPersistChars = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AuthState _auth;
    private readonly IJSRuntime _js;
    private readonly Dictionary<string, object> _mem = [];

    public PageCache(AuthState auth, IJSRuntime js)
    {
        _auth = auth;
        _js = js;
        _auth.SignedOut += Clear;
    }

    private string? Scoped(string key) => _auth.Me?.Nik is { Length: > 0 } nik ? $"{Prefix}{nik}.{key}" : null;

    /// The cached value, or default. Synchronous so a page can render it on its first pass.
    public T? Peek<T>(string key)
    {
        if (Scoped(key) is not { } k) return default;
        if (_mem.TryGetValue(k, out var hit) && hit is T t) return t;
        if (_js is not IJSInProcessRuntime sync) return default;
        try
        {
            var json = sync.Invoke<string?>("localStorage.getItem", k);
            if (string.IsNullOrEmpty(json)) return default;
            var value = JsonSerializer.Deserialize<T>(json, Json);
            if (value is not null) _mem[k] = value;
            return value;
        }
        catch { return default; } // shape changed between builds, storage blocked: just miss
    }

    public void Put<T>(string key, T value, bool persist = false)
    {
        if (Scoped(key) is not { } k || value is null) return;
        _mem[k] = value;
        if (!persist || _js is not IJSInProcessRuntime sync) return;
        try
        {
            var json = JsonSerializer.Serialize(value, Json);
            if (json.Length <= MaxPersistChars) sync.InvokeVoid("localStorage.setItem", k, json);
            else sync.InvokeVoid("localStorage.removeItem", k);
        }
        catch { /* quota or private mode: the memory copy still serves this tab */ }
    }

    public void Remove(string key)
    {
        if (Scoped(key) is not { } k) return;
        _mem.Remove(k);
        if (_js is IJSInProcessRuntime sync) try { sync.InvokeVoid("localStorage.removeItem", k); } catch { }
    }

    /// Drops every cached page for every user of this browser.
    public void Clear()
    {
        _mem.Clear();
        if (_js is not IJSInProcessRuntime sync) return;
        try { sync.InvokeVoid("ipop.cacheClear", Prefix); } catch { }
    }
}
