using System.Collections.Concurrent;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// <summary>
/// Client-side state & SWR cache for the Files browser.
/// Persists across Blazor navigation so returning to /files is instant (0ms).
/// Retains last seen server, folder path, history stacks, and cached listings.
/// </summary>
public class FileState
{
    private const string StorageKey = "ipop_files_last_seen";
    private readonly AuthState auth;

    public FileState(AuthState auth)
    {
        this.auth = auth;
        auth.SignedOut += Reset;
    }

    /// Sign-out: the next account starts from a clean slate (folders, NAS credential, history).
    private void Reset()
    {
        ClearAllCache();
        CurrentServer = "spj";
        CurrentPath = "/";
        BackStack = [];
        ForwardStack = [];
        Search = "";
        TypeFilter = "";
        SortBy = "name";
        IsInitialized = false;
        Credential = null;
    }
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(3);

    public string CurrentServer { get; set; } = "spj";
    public string CurrentPath { get; set; } = "/";
    public List<string> BackStack { get; set; } = [];
    public List<string> ForwardStack { get; set; } = [];

    public string Search { get; set; } = "";
    public string TypeFilter { get; set; } = "";
    public string SortBy { get; set; } = "name";

    public bool IsInitialized { get; set; }
    public CredentialStatusDto? Credential { get; set; }

    private readonly ConcurrentDictionary<(string Server, string Path), (DateTime FetchedAt, List<SynoItemDto> Items)> _folderCache = new();
    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, List<string> Shares)> _sharesCache = new();

    public event Action? StateChanged;
    public void NotifyChanged() => StateChanged?.Invoke();

    public bool TryGetCachedItems(string server, string path, out List<SynoItemDto> items)
        => TryGetCachedItems(server, path, out items, out _);

    public bool TryGetCachedItems(string server, string path, out List<SynoItemDto> items, out bool isStale, TimeSpan? maxAge = null)
    {
        var ttl = maxAge ?? DefaultTtl;
        if (_folderCache.TryGetValue((server.ToLowerInvariant(), path), out var entry))
        {
            items = entry.Items;
            isStale = DateTime.UtcNow - entry.FetchedAt > ttl;
            return true;
        }
        items = [];
        isStale = true;
        return false;
    }

    public void SetCachedItems(string server, string path, List<SynoItemDto> items)
    {
        _folderCache[(server.ToLowerInvariant(), path)] = (DateTime.UtcNow, items);
    }

    public bool TryGetCachedShares(string server, out List<string> shares)
        => TryGetCachedShares(server, out shares, out _);

    public bool TryGetCachedShares(string server, out List<string> shares, out bool isStale, TimeSpan? maxAge = null)
    {
        var ttl = maxAge ?? DefaultTtl;
        if (_sharesCache.TryGetValue(server.ToLowerInvariant(), out var entry))
        {
            shares = entry.Shares;
            isStale = DateTime.UtcNow - entry.FetchedAt > ttl;
            return true;
        }
        shares = [];
        isStale = true;
        return false;
    }

    public void SetCachedShares(string server, List<string> shares)
    {
        _sharesCache[server.ToLowerInvariant()] = (DateTime.UtcNow, shares);
    }

    public void Invalidate(string server, string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            InvalidateServer(server);
        else
            InvalidateFolder(server, path);
    }

    public void InvalidateFolder(string server, string path)
    {
        _folderCache.TryRemove((server.ToLowerInvariant(), path), out _);
    }

    public void InvalidateServer(string server)
    {
        var s = server.ToLowerInvariant();
        _sharesCache.TryRemove(s, out _);
        foreach (var key in _folderCache.Keys.Where(k => k.Server == s).ToList())
        {
            _folderCache.TryRemove(key, out _);
        }
    }

    public void ClearAllCache()
    {
        _folderCache.Clear();
        _sharesCache.Clear();
    }

    public void PushHistory(string path)
    {
        if (BackStack.Count == 0 || BackStack[^1] != path)
            BackStack.Add(path);
        ForwardStack.Clear();
    }

    /// Per account: another user on this browser must not land in this user's folder.
    public async Task SaveLastSeenAsync(IJSRuntime js)
    {
        try
        {
            var value = $"{CurrentServer}|{CurrentPath}";
            await auth.SetUserSettingAsync(StorageKey, value);
        }
        catch
        {
            // Ignored in SSR/restricted environments
        }
    }

    public async Task<(string Server, string Path)?> LoadLastSeenAsync(IJSRuntime js)
    {
        try
        {
            var saved = await auth.GetUserSettingAsync(StorageKey);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                var parts = saved.Split('|', 2);
                if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    CurrentServer = parts[0].Trim().ToLowerInvariant();
                    CurrentPath = parts[1].Trim();
                    return (CurrentServer, CurrentPath);
                }
            }
        }
        catch
        {
            // Ignored
        }
        return null;
    }
}
