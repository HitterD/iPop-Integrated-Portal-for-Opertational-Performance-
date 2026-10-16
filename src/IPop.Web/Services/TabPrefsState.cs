using IPop.Application.Shell;

namespace IPop.Web.Services;

/// The four pinned phone tabs for the signed-in account. Saved to the account
/// (/api/me/ui-prefs/tabs, so it follows the user to every device) and mirrored to per-account
/// localStorage (ipopUserStore "phone-tabs") so the bar renders the right tabs before the API answers.
public sealed class TabPrefsState(AuthState auth)
{
    private const string LocalKey = "phone-tabs";
    private const string Url = "/api/me/ui-prefs/tabs";

    public IReadOnlyList<string> Tabs { get; private set; } = PhoneTabs.Default;
    public event Action? Changed;

    public IReadOnlyList<ShellModule> Pinned => Tabs.Select(h => PhoneTabs.Find(h)!).ToList();

    /// Local mirror first (instant), then the account's saved list.
    public async Task LoadAsync()
    {
        var local = await auth.GetUserSettingAsync(LocalKey);
        if (!string.IsNullOrEmpty(local)) Apply(Parse(local));
        var server = await auth.GetAsync<string[]>(Url);
        if (server is not null)
        {
            Apply(server);
            await auth.SetUserSettingAsync(LocalKey, System.Text.Json.JsonSerializer.Serialize(Tabs));
        }
    }

    /// False when the list is not exactly four allowed modules (the bar is left unchanged).
    public async Task<bool> SaveAsync(IReadOnlyList<string> tabs)
    {
        if (!PhoneTabs.IsValid(tabs, auth.IsAdmin)) return false;
        var list = tabs.ToArray();
        Tabs = list;
        Changed?.Invoke();
        var json = System.Text.Json.JsonSerializer.Serialize(list);
        await auth.SetUserSettingAsync(LocalKey, json);
        try
        {
            using var res = await auth.SendAsync(HttpMethod.Put, Url, list);
            return res.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private void Apply(IReadOnlyList<string>? list)
    {
        var resolved = PhoneTabs.Resolve(list, auth.IsAdmin);
        if (resolved.SequenceEqual(Tabs)) return;
        Tabs = resolved;
        Changed?.Invoke();
    }

    private static string[]? Parse(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
