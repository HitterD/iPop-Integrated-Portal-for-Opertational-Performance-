using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// Mail read receipts, client side (spec 2026-10-14 §3). js/mail-seen.js decides WHEN a message counts as
/// seen (its shown body, on screen, tab visible AND focused, 2 s continuously); this reports it to the API
/// once per message per session (scoped = one browser tab). Mark read / search / notifications never call it.
public sealed class MailSeen(MailService mail)
{
    private readonly HashSet<Guid> reported = [];
    private readonly HashSet<Guid> inFlight = [];

    /// Whether a shown message may record a receipt: someone else's delivered mail (not my own copy,
    /// not a draft or a scheduled one). Bodies that pass carry data-mail-seen="{id}".
    public static bool Applies(MailDetail? d, string? myNik)
        => d is not null
           && d.Folder is not ("draft" or "scheduled" or "sent")
           && !string.Equals(d.SenderNik, myNik, StringComparison.Ordinal);

    /// Called by the JS tracker. True when this call posted successfully.
    [JSInvokable("OnMailSeen")]
    public Task<bool> OnMailSeenAsync(string id) => ReportAsync(id);

    public async Task<bool> ReportAsync(string id)
    {
        if (!Guid.TryParse(id, out var mailId) || reported.Contains(mailId) || !inFlight.Add(mailId)) return false;
        try
        {
            var ok = await mail.PostSeenAsync(mailId);
            if (ok) reported.Add(mailId);
            return ok;
        }
        catch (HttpRequestException) { return false; }
        finally { inFlight.Remove(mailId); }
    }
}
