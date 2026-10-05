using System.Net;
using System.Net.Http.Json;
using IPop.Application.Tickets;

namespace IPop.Web.Services;

/// What a ticket call came back with. Error bodies are { code, message, field? } (docs/specs/idesk-tickets.md).
public enum TicketCallStatus
{
    Ok,
    /// 202: kept as Sending, iPop retries in the background.
    Queued,
    /// 400 "invalid": our own validation; Error.Field names the form field.
    Invalid,
    /// 400 "rejected": iDesk said no (its own message).
    Rejected,
    /// 422 "not_registered": the user's NIK isn't in iDesk.
    NotRegistered,
    /// 503 "not_configured": the server has no iDesk secret.
    NotConfigured,
    /// 503 "unreachable", a transport failure or a timeout.
    Unreachable,
    /// 409 "not_pending": retry/discard on a ticket that already reached iDesk.
    NotPending,
    NotFound,
    Failed,
}

public sealed record TicketApiError(string? Code, string? Message, string? Field);

public readonly record struct TicketCall<T>(TicketCallStatus Status, T? Value, TicketApiError? Error = null)
{
    public bool IsOk => Status is TicketCallStatus.Ok or TicketCallStatus.Queued;
}

/// HTTP client for /api/tickets. Never throws for HTTP or network failures: every outcome is a status.
public sealed class TicketApi(AuthState auth)
{
    public Task<TicketCall<List<TicketModuleDto>>> GetModulesAsync(CancellationToken ct = default)
        => GetAsync<List<TicketModuleDto>>("/api/tickets/modules", ct);

    /// The local mirror: fast, never talks to iDesk.
    public Task<TicketCall<TicketListDto>> ListAsync(bool openOnly = false, CancellationToken ct = default)
        => GetAsync<TicketListDto>($"/api/tickets?state={(openOnly ? "open" : "all")}", ct);

    /// Pulls the user's tickets from iDesk into the mirror and returns the list (Offline/NotRegistered inside).
    public Task<TicketCall<TicketListDto>> SyncAsync(bool openOnly = false, CancellationToken ct = default)
        => SendAsync<TicketListDto>(HttpMethod.Post, $"/api/tickets/sync?state={(openOnly ? "open" : "all")}", null, ct);

    public Task<TicketCall<TicketDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
        => GetAsync<TicketDetailDto>($"/api/tickets/{id}", ct);

    public Task<TicketCall<CreateTicketResultDto>> CreateAsync(CreateTicketRequest req, CancellationToken ct = default)
        => SendAsync<CreateTicketResultDto>(HttpMethod.Post, "/api/tickets", req, ct);

    public Task<TicketCall<TicketDetailDto>> RetryAsync(Guid id, CancellationToken ct = default)
        => SendAsync<TicketDetailDto>(HttpMethod.Post, $"/api/tickets/{id}/retry", null, ct);

    public async Task<TicketCall<bool>> DiscardAsync(Guid id, CancellationToken ct = default)
    {
        var r = await SendAsync<object>(HttpMethod.Delete, $"/api/tickets/{id}", null, ct);
        return new(r.Status, r.Status == TicketCallStatus.Ok, r.Error);
    }

    private async Task<TicketCall<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        var r = await auth.GetResultAsync<T>(url, ct);
        return r.Status switch
        {
            GetStatus.Ok => new(TicketCallStatus.Ok, r.Value),
            GetStatus.Unreachable => new(TicketCallStatus.Unreachable, default),
            GetStatus.NotFound or GetStatus.Forbidden => new(TicketCallStatus.NotFound, default),
            _ => new(TicketCallStatus.Failed, default),
        };
    }

    private async Task<TicketCall<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await auth.SendAsync(method, url, body, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            return new(TicketCallStatus.Unreachable, default);
        }
        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                var status = res.StatusCode == HttpStatusCode.Accepted ? TicketCallStatus.Queued : TicketCallStatus.Ok;
                if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength is 0) return new(status, default);
                try { return new(status, await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct)); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or HttpRequestException or IOException)
                {
                    return new(TicketCallStatus.Failed, default);
                }
            }
            var err = await ReadErrorAsync(res, ct);
            return new(Classify(res.StatusCode, err?.Code), default, err);
        }
    }

    internal static TicketCallStatus Classify(HttpStatusCode code, string? errorCode) => (code, errorCode) switch
    {
        (HttpStatusCode.BadRequest, "rejected") => TicketCallStatus.Rejected,
        (HttpStatusCode.BadRequest, _) => TicketCallStatus.Invalid,
        (HttpStatusCode.UnprocessableEntity, _) => TicketCallStatus.NotRegistered,
        (HttpStatusCode.Conflict, _) => TicketCallStatus.NotPending,
        (HttpStatusCode.NotFound, _) => TicketCallStatus.NotFound,
        (HttpStatusCode.ServiceUnavailable, "not_configured") => TicketCallStatus.NotConfigured,
        (HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout, _) => TicketCallStatus.Unreachable,
        _ => TicketCallStatus.Failed,
    };

    private static async Task<TicketApiError?> ReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadFromJsonAsync<TicketApiError>(cancellationToken: ct); }
        catch { return null; } // proxy HTML page or empty body
    }
}
