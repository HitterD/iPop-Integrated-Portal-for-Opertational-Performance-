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

    /// Once, on a PC upgraded from a version where "Start with Windows" roamed: keep what it had (the cached copy).
    /// Then the Run key follows this PC's choice on every start (an update may move the exe).
    public void ApplyStartWithWindows()
    {
        if (!state.StartWithWindowsMigrated)
        {
            state.StartWithWindows ??= NotifierSettings.Parse(state.SettingsJson).StartWithWindows;
            state.StartWithWindowsMigrated = true;
            state.Save();
        }
        Integration.SetStartWithWindows(StartWithWindows);
    }

    private void Apply(NotifierSettings s)
    {
        Current = s;
        state.SettingsJson = s.ToJson();
        state.Save();
        Changed?.Invoke(s);
    }

    /// "Start with Windows" on this PC only (review M1): saved in LocalState, never on the server.
    public bool StartWithWindows => state.StartWithWindows ?? true;

    public void SetStartWithWindows(bool on)
    {
        state.StartWithWindows = on;
        state.StartWithWindowsMigrated = true;
        state.Save();
        Integration.SetStartWithWindows(on);
    }

    /// "Open iPop when Windows starts" on this PC only (default on; IPopAppLauncher).
    public bool OpenIPopAtLogin => state.OpenIPopAtLogin ?? true;

    public void SetOpenIPopAtLogin(bool on)
    {
        state.OpenIPopAtLogin = on;
        state.Save();
    }

    /// A sign-in that didn't ask (browser sign-in): turn autostart on only when nobody chose "off" on this PC.
    public void EnsureStartWithWindowsDefault() => Integration.SetStartWithWindows(StartWithWindows);
}
