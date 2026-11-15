using System.Text;

namespace IPop.Notifier.Core;

/// The person's Notifier settings, saved on the server (ui-prefs "notifier") so they follow them to another PC.
/// The last copy is kept locally for an offline start.
public sealed class SettingsService(IPopClient api, LocalState state)
{
    public NotifierSettings Current { get; private set; } = NotifierSettings.Parse(state.SettingsJson);
    public event Action<NotifierSettings>? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            using var res = await api.SendAsync(HttpMethod.Get, "api/me/ui-prefs/notifier", null, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NoContent) { Apply(new NotifierSettings().Normalized()); return; }
            if (res.IsSuccessStatusCode) Apply(NotifierSettings.Parse(await res.Content.ReadAsStringAsync(ct)));
        }
        catch { /* offline: keep the cached copy */ }
    }

    public async Task<bool> SaveAsync(NotifierSettings next, CancellationToken ct = default)
    {
        next = next.Normalized();
        Apply(next);
        try
        {
            // Through the shared client: renews an expired token once and retries, same timeout and connection reuse.
            using var res = await api.SendAsync(HttpMethod.Put, "api/me/ui-prefs/notifier", System.Text.Json.Nodes.JsonNode.Parse(next.ToJson()), ct);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// "Off until" passes on its own: back to "on".
    public NotifierSettings Effective(DateTime now)
    {
        var s = Current;
        return s.Popups == "until" && s.OffUntil is { } until && until.ToUniversalTime() <= now ? s with { Popups = "on", OffUntil = null } : s;
    }

    private void Apply(NotifierSettings s)
    {
        Current = s;
        state.SettingsJson = s.ToJson();
        state.Save();
        Integration.SetStartWithWindows(s.StartWithWindows);
        Changed?.Invoke(s);
    }
}
