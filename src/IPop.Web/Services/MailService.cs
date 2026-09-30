using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace IPop.Web.Services;

/// Thin authenticated client for /api/mail/*. All calls go through AuthState.SendAsync
/// which attaches the bearer token and silently refreshes once on 401 (the shared policy).
public sealed class MailService(AuthState Auth, HttpClient http)
{
    private static string F(MailFolder f) => f switch
    {
        MailFolder.Inbox => "inbox",
        MailFolder.Sent => "sent",
        MailFolder.Draft => "draft",
        MailFolder.Starred => "starred",
        MailFolder.Archive => "archive",
        MailFolder.Trash => "trash",
        MailFolder.Scheduled => "scheduled",
        _ => "inbox",
    };

    public Task<MailPaged<MailSummary>?> GetFolderAsync(MailFolder folder, int page, int pageSize, string? q = null, bool? starredOnly = null, bool? unreadOnly = null, string? label = null,
        DateTime? dateFrom = null, DateTime? dateTo = null, bool? hasAttachment = null, CancellationToken ct = default)
    {
        var url = $"/api/mail/folders/{F(folder)}?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(q)) url += $"&q={Uri.EscapeDataString(q)}";
        if (starredOnly is not null) url += $"&starredOnly={(starredOnly.Value ? "true" : "false")}";
        if (unreadOnly is not null) url += $"&unreadOnly={(unreadOnly.Value ? "true" : "false")}";
        if (!string.IsNullOrWhiteSpace(label)) url += $"&label={Uri.EscapeDataString(label)}";
        if (dateFrom is not null) url += $"&dateFrom={dateFrom.Value:yyyy-MM-dd}";
        if (dateTo is not null) url += $"&dateTo={dateTo.Value:yyyy-MM-dd}";
        if (hasAttachment == true) url += "&hasAttachment=true";
        return Auth.GetAsync<MailPaged<MailSummary>>(url, ct);
    }

    public Task<MailCounts?> GetCountsAsync(CancellationToken ct = default) => Auth.GetAsync<MailCounts>("/api/mail/counts", ct);
    public Task<List<MailLabel>?> GetLabelsAsync(CancellationToken ct = default) => Auth.GetAsync<List<MailLabel>>("/api/mail/labels", ct);
    public Task<MailDetail?> GetAsync(Guid id, CancellationToken ct = default) => Auth.GetAsync<MailDetail>($"/api/mail/{id}", ct);
    public Task<MailPaged<MailSummary>?> GetThreadAsync(Guid rootId, int page, int pageSize, CancellationToken ct = default)
        => Auth.GetAsync<MailPaged<MailSummary>>($"/api/mail/threads/{rootId}?page={page}&pageSize={pageSize}", ct);

    public async Task<SendMailResult?> SendAsync(MailSendRequest req, CancellationToken ct = default)
        => await PostAsync<SendMailResult>("/api/mail/send", req, ct);

    public async Task<SendMailResult?> SaveDraftAsync(MailSendRequest req, CancellationToken ct = default)
        => await PostAsync<SendMailResult>("/api/mail/drafts", req, ct);

    public async Task<SendMailResult?> UpdateDraftAsync(Guid id, MailSendRequest req, CancellationToken ct = default)
        => await PutAsync<SendMailResult>($"/api/mail/drafts/{id}", req, ct);

    public async Task<SendMailResult?> DiscardDraftAsync(Guid id, CancellationToken ct = default)
        => await DeleteAsync<SendMailResult>($"/api/mail/drafts/{id}", ct);

    public async Task<SendMailResult?> ForwardAsync(Guid id, MailSendRequest req, CancellationToken ct = default)
        => await PostAsync<SendMailResult>($"/api/mail/{id}/forward", req, ct);

    public Task<SendMailResult?> MarkReadAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/read", ct);
    public Task<SendMailResult?> MarkUnreadAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/unread", ct);
    public Task<SendMailResult?> ToggleStarAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/star", ct);
    public Task<SendMailResult?> TogglePinAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/pin", ct);
    public Task<SendMailResult?> CancelScheduleAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/cancel-schedule", ct);
    public Task<SendMailResult?> RescheduleAsync(Guid id, DateTime scheduledAt, CancellationToken ct = default) => PostAsync<SendMailResult>($"/api/mail/{id}/reschedule", new { scheduledAt }, ct);
    public Task<SendMailResult?> SendScheduledNowAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/send-now", ct);
    public Task<SendMailResult?> MoveAsync(Guid id, MailFolder f, CancellationToken ct = default)
        => PostAsync<SendMailResult>($"/api/mail/{id}/move", new { folder = F(f) }, ct);
    public Task<SendMailResult?> DeleteAsync(Guid id, CancellationToken ct = default) => DeleteEmpty($"/api/mail/{id}", ct);
    public Task<SendMailResult?> ExportPdfAsync(Guid id, CancellationToken ct = default) => PostEmpty($"/api/mail/{id}/export-pdf", ct);

    public async Task<bool> BulkMoveAsync(IReadOnlyList<Guid> ids, MailFolder f, CancellationToken ct = default)
        => (await PostAsync<object?>("/api/mail/bulk/move", new BatchMove(ids, F(f)), ct)) is not null;
    public async Task<bool> BulkStarAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
        => (await PostAsync<object?>("/api/mail/bulk/star", new BatchIds(ids), ct)) is not null;
    public async Task<bool> BulkDeleteAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
        => (await PostAsync<object?>("/api/mail/bulk/delete", new BatchIds(ids), ct)) is not null;
    public async Task<bool> BulkRestoreAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
        => (await PostAsync<object?>("/api/mail/bulk/restore", new BatchIds(ids), ct)) is not null;
    public async Task<bool> BulkReadAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
        => (await PostAsync<object?>("/api/mail/bulk/read-ids", new BatchIds(ids), ct)) is not null;
    public async Task<bool> MarkAllReadAsync(MailFolder f, CancellationToken ct = default)
        => (await PostAsync<object?>("/api/mail/bulk/read", new FolderReq(F(f)), ct)) is not null;

    public async Task<MailAttachment[]?> UploadAsync(MultipartFormDataContent content, CancellationToken ct = default)
        => (await UploadWithErrorAsync(content, ct)).Files;

    /// Upload that keeps the server's reason on failure (file too large, too many files, …).
    public async Task<(MailAttachment[]? Files, string? Error)> UploadWithErrorAsync(MultipartFormDataContent content, CancellationToken ct = default)
    {
        using var resp = await Auth.SendMultipartAsync("/api/mail/attachments/upload", content);
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<MailAttachment[]>(cancellationToken: ct), null);
        return (null, await ReadErrorAsync(resp, ct));
    }

    internal static async Task<string> ReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var key in new[] { "message", "detail", "title", "error" })
                    if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                        return v.GetString()!;
            }
        }
        catch { /* fall through to the status-based message */ }
        return (int)resp.StatusCode switch
        {
            413 => "The upload is too large. Attach at most 5 files of 50 MB each.",
            401 or 403 => "You don't have permission to attach files.",
            _ => $"Upload failed ({(int)resp.StatusCode}).",
        };
    }

    /// Applies or removes one of the user's labels on a mail. Returns whether the server accepted it.
    public async Task<bool> SetLabelAsync(Guid mailId, Guid labelId, bool on, CancellationToken ct = default)
    {
        using var resp = await Auth.SendAsync(on ? HttpMethod.Post : HttpMethod.Delete, $"/api/mail/{mailId}/labels/{labelId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<MailDeliveryState>?> GetDeliveryAsync(Guid id, CancellationToken ct = default)
        => await Auth.GetAsync<IReadOnlyList<MailDeliveryState>>($"/api/mail/{id}/delivery", ct);

    // ── helpers ──
    private async Task<T?> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        using var resp = await Auth.SendAsync(HttpMethod.Post, url, body);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
    private async Task<T?> PutAsync<T>(string url, object body, CancellationToken ct)
    {
        using var resp = await Auth.SendAsync(HttpMethod.Put, url, body);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
    private async Task<T?> DeleteAsync<T>(string url, CancellationToken ct)
    {
        using var resp = await Auth.SendAsync(HttpMethod.Delete, url);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
    private async Task<SendMailResult?> PostEmpty(string url, CancellationToken ct)
        => await PostAsync<SendMailResult>(url, new { }, ct);
    private async Task<SendMailResult?> DeleteEmpty(string url, CancellationToken ct)
        => await DeleteAsync<SendMailResult>(url, ct);
}
