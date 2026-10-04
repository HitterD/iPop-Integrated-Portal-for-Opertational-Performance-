using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Server-backed favorites with an account-specific offline cache.
public sealed class FavoriteContacts(IJSRuntime js, AuthState auth)
{
    private HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);
    private string? account;
    private Task? loading;
    public event Action? Changed;
    private string? CurrentAccount => auth.Me?.Nik;
    private static string Key(string nik) => $"ipop.directory.favorites.v1.{Uri.EscapeDataString(nik)}";
    private void SyncAccount()
    {
        if (account == CurrentAccount) return;
        account = CurrentAccount; favorites = new(StringComparer.OrdinalIgnoreCase); loading = null;
    }
    public IReadOnlySet<string> All { get { SyncAccount(); return favorites; } }
    public bool IsFavorite(string nik) { SyncAccount(); return favorites.Contains(nik); }
    public Task EnsureLoadedAsync()
    {
        SyncAccount();
        return account is null ? Task.CompletedTask : loading ??= LoadAsync(account);
    }
    private async Task LoadAsync(string owner)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("localStorage.getItem", Key(owner));
            if (owner != CurrentAccount) return;
            if (!string.IsNullOrWhiteSpace(raw)) favorites = (System.Text.Json.JsonSerializer.Deserialize<string[]>(raw) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        try
        {
            var server = await auth.GetAsync<string[]>("/api/directory/favorites");
            if (owner != CurrentAccount) return;
            if (server is not null) { favorites = server.ToHashSet(StringComparer.OrdinalIgnoreCase); await Persist(owner); }
        }
        catch { }
    }
    public async Task<bool> ToggleAsync(string nik)
    {
        await EnsureLoadedAsync();
        SyncAccount();
        var owner = account;
        if (owner is null) return false;
        var nowFav = !favorites.Contains(nik);
        if (nowFav) favorites.Add(nik); else favorites.Remove(nik);
        await Persist(owner);
        try
        {
            using var response = await auth.SendAsync(nowFav ? HttpMethod.Put : HttpMethod.Delete, $"/api/directory/favorites/{Uri.EscapeDataString(nik)}");
            if (!response.IsSuccessStatusCode && owner == CurrentAccount)
            {
                if (nowFav) favorites.Remove(nik); else favorites.Add(nik);
                await Persist(owner);
            }
        }
        catch { }
        // Notify after server mutation so server-filtered employee requests see the new state.
        if (owner == CurrentAccount) Changed?.Invoke();
        return owner == CurrentAccount && favorites.Contains(nik);
    }
    private async Task Persist(string owner)
    {
        if (owner != CurrentAccount) return;
        var raw = System.Text.Json.JsonSerializer.Serialize(favorites.ToArray());
        try { await js.InvokeVoidAsync("localStorage.setItem", Key(owner), raw); } catch { }
    }
}
