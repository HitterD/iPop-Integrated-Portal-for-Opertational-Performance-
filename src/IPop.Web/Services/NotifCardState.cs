namespace IPop.Web.Services;

/// Whether this account already answered the phone notification card (mockup v1 frame o).
/// Saved to the account (/api/me/ui-prefs/notif-card) so "Not now" holds on every device.
public sealed class NotifCardState(AuthState auth)
{
    private const string Url = "/api/me/ui-prefs/notif-card";
    public bool Loaded { get; private set; }
    public bool Answered { get; private set; }

    public async Task LoadAsync()
    {
        var v = await auth.GetAsync<string>(Url);
        Answered = v is "dismissed" or "enabled";
        Loaded = true;
    }

    /// "dismissed" (Not now / OK) or "enabled" (Turn on). Either way the card never shows again.
    public async Task AnswerAsync(string value)
    {
        Answered = true;
        try { using var _ = await auth.SendAsync(HttpMethod.Put, Url, value); } catch (HttpRequestException) { }
    }
}
