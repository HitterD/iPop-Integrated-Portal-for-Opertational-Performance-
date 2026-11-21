using System.Net.Http.Json;
using IPop.Application.Hub;

namespace IPop.Web.Services;

/// What a Hub call came back with. Error bodies are { code, message }.
public readonly record struct HubCall<T>(bool Ok, T? Value, string? Code = null, string? Message = null)
{
    public static HubCall<T> Fail(string code, string message) => new(false, default, code, message);
}

/// HTTP client for /api/hub (docs/superpowers/specs/2026-11-21-the-hub-design.md). Never throws for HTTP or network
/// failures: every outcome is a HubCall with a message a person can read.
public sealed class HubApi(AuthState auth)
{
    public Task<HubCall<HubListDto>> ListAsync(string filter, string? q, CancellationToken ct = default) =>
        GetAsync<HubListDto>($"/api/hub/topics?filter={Uri.EscapeDataString(filter)}&q={Uri.EscapeDataString(q ?? "")}", ct);

    public Task<HubCall<HubTopicDto>> GetAsync(Guid id, bool peek = false, CancellationToken ct = default) =>
        GetAsync<HubTopicDto>($"/api/hub/topics/{id}{(peek ? "?peek=1" : "")}", ct);

    public Task<HubCall<HubTopicDto>> CreateAsync(CreateHubTopicRequest req, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, "/api/hub/topics", req, ct);

    public Task<HubCall<HubTopicDto>> PostAsync(Guid id, CreateHubPostRequest req, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, $"/api/hub/topics/{id}/posts", req, ct);

    public Task<HubCall<HubTopicDto>> EditPostAsync(Guid id, Guid postId, string body, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Put, $"/api/hub/topics/{id}/posts/{postId}", new EditHubPostRequest(body), ct);

    public Task<HubCall<HubTopicDto>> DeletePostAsync(Guid id, Guid postId, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Delete, $"/api/hub/topics/{id}/posts/{postId}", null, ct);

    public Task<HubCall<HubTopicDto>> NoteAsync(Guid id, Guid postId, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, $"/api/hub/topics/{id}/posts/{postId}/noted", null, ct);

    public Task<HubCall<HubTopicDto>> BestAsync(Guid id, Guid? postId, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, $"/api/hub/topics/{id}/best", new HubBestRequest(postId), ct);

    public Task<HubCall<HubTopicDto>> AddMembersAsync(Guid id, IReadOnlyList<Guid> people, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, $"/api/hub/topics/{id}/members", new AddHubMembersRequest(people), ct);

    public Task<HubCall<bool>> RemindAsync(Guid id, Guid askId, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Post, $"/api/hub/topics/{id}/asks/{askId}/remind", null, ct);

    public Task<HubCall<HubTopicDto>> SolveAsync(Guid id, SolveHubTopicRequest req, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, $"/api/hub/topics/{id}/solve", req, ct);

    public Task<HubCall<HubTopicDto>> ReopenAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<HubTopicDto>(HttpMethod.Post, $"/api/hub/topics/{id}/reopen", null, ct);

    public Task<HubCall<List<HubPersonDto>>> SearchPeopleAsync(string q, CancellationToken ct = default) =>
        GetAsync<List<HubPersonDto>>($"/api/hub/people?q={Uri.EscapeDataString(q)}", ct);

    public Task<HubCall<List<HubPersonDto>>> SuggestedPeopleAsync(CancellationToken ct = default) =>
        GetAsync<List<HubPersonDto>>("/api/hub/people/suggested", ct);

    public Task<HubCall<List<HubProjectOptionDto>>> ProjectsAsync(CancellationToken ct = default) =>
        GetAsync<List<HubProjectOptionDto>>("/api/hub/projects", ct);

    public async Task<HubCall<HubUploadDto>> UploadAsync(Guid id, string fileName, string contentType, Stream content, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var part = new StreamContent(content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            form.Add(part, "file", fileName);
            using var res = await auth.SendMultipartAsync($"/api/hub/topics/{id}/files", form, ct);
            return await ReadAsync<HubUploadDto>(res, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TimeoutException or TaskCanceledException or IOException)
        {
            return HubCall<HubUploadDto>.Fail("unreachable", "The upload didn't finish. Check the connection and try again.");
        }
    }

    private async Task<HubCall<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        var r = await auth.GetResultAsync<T>(url, ct);
        return r.Status switch
        {
            GetStatus.Ok => new(true, r.Value),
            GetStatus.Unreachable => HubCall<T>.Fail("unreachable", "iPop can't be reached right now. Try again in a moment."),
            GetStatus.NotFound or GetStatus.Forbidden => HubCall<T>.Fail("not_found", "This topic isn't shared with you."),
            _ => HubCall<T>.Fail("failed", "Something went wrong. Try again."),
        };
    }

    private async Task<HubCall<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        try
        {
            using var res = await auth.SendAsync(method, url, body, ct);
            return await ReadAsync<T>(res, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            return HubCall<T>.Fail("unreachable", "iPop can't be reached right now. Nothing was sent; try again.");
        }
    }

    private static async Task<HubCall<T>> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode)
        {
            if (res.Content.Headers.ContentLength is 0) return new(true, default);
            try { return new(true, await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct)); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or HttpRequestException or IOException)
            {
                return HubCall<T>.Fail("failed", "Saved, but the answer couldn't be read. Refresh the page.");
            }
        }
        try
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken: ct);
            return HubCall<T>.Fail(err?.Code ?? "failed", err?.Message ?? "Something went wrong. Try again.");
        }
        catch { return HubCall<T>.Fail("failed", "Something went wrong. Try again."); }
    }

    private sealed record ErrorBody(string? Code, string? Message);
}
