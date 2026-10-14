using System.Text.Json;
using IPop.Application.Realtime;

namespace IPop.Web.Services;

/// One cache of GET /api/mail/{id}/receipts per mail, shared by the receipts row, the thread ticks, the recipients
/// panel and the sender's mass panel. The API pushes mailReceiptChanged { mailId } when someone sees a mail; the
/// store re-fetches that mail and tells every view (spec 2026-10-14 §1).
public sealed class MailReceiptsStore : IDisposable
{
    private readonly MailService mail;
    private readonly AuthState auth;
    private readonly Dictionary<Guid, Task<MailReceipts?>> cache = [];
    private IDisposable? sub;

    public MailReceiptsStore(MailService mail, AuthState auth)
    {
        this.mail = mail;
        this.auth = auth;
        sub = auth.On<JsonElement>(RealtimeEvents.MailReceiptChanged, e =>
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("mailId", out var v) && v.TryGetGuid(out var id)) OnHint(id);
        });
        auth.HubReconnected += Clear;
    }

    /// A receipts view for mailId should re-read (args: mailId).
    public event Action<Guid>? Changed;

    public Task<MailReceipts?> GetAsync(Guid mailId)
    {
        if (!cache.TryGetValue(mailId, out var t) || t.IsFaulted || (t.IsCompletedSuccessfully && t.Result is null))
            cache[mailId] = t = Load(mailId);
        return t;
    }

    public void OnHint(Guid mailId)
    {
        cache.Remove(mailId);
        Changed?.Invoke(mailId);
    }

    private void Clear()
    {
        var ids = cache.Keys.ToList();
        cache.Clear();
        foreach (var id in ids) Changed?.Invoke(id);
    }

    private async Task<MailReceipts?> Load(Guid id)
    {
        try { return await mail.GetReceiptsAsync(id); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    public void Dispose() { sub?.Dispose(); sub = null; auth.HubReconnected -= Clear; }
}
