using System.Net.Http.Json;

namespace IPop.Web.Services;

/// Client mirrors of the /api/invite DTOs (meeting scheduling v3 §4). Key = NIK as an opaque key: photos and
/// AttendeeNiks only, never printed. PhotoUrl is always null; PersonAvatar resolves the photo by Key.
public record InvitePersonDto(string Key, string Name, string? Department, string? PhotoUrl = null);
/// Kind: "chat" | "department". Color: the chat room's avatar colour (chat only).
public record InviteGroupDto(string Kind, Guid Id, string Name, int Members, string? Color);
public record InviteSuggestionsDto(IReadOnlyList<InvitePersonDto> Frequent, IReadOnlyList<InvitePersonDto> Department,
    IReadOnlyList<InviteGroupDto> ChatGroups, IReadOnlyList<InviteGroupDto> Departments);
public record InviteSearchDto(IReadOnlyList<InvitePersonDto> People, IReadOnlyList<InviteGroupDto> Groups);
/// State: "free" | "busy" | "unknown". Blocks only: there is no title.
public record PersonBusyDto(string Key, string State, DateTime? BusyFrom, DateTime? BusyTo);

/// Thin client for the invite picker endpoints. Never throws for HTTP or network failures: null = could not load.
/// Suggestions are cached for the session (one scoped instance per tab).
public sealed class InviteApi(AuthState auth)
{
    public const int BusyBatch = 200;
    private InviteSuggestionsDto? suggestions;

    public async Task<InviteSuggestionsDto?> SuggestionsAsync(CancellationToken ct = default)
    {
        if (suggestions is not null) return suggestions;
        var r = await auth.GetResultAsync<InviteSuggestionsDto>("/api/invite/suggestions", ct);
        return r.Status == GetStatus.Ok ? suggestions = r.Value : null;
    }

    public async Task<InviteSearchDto?> SearchAsync(string q, CancellationToken ct = default)
    {
        var r = await auth.GetResultAsync<InviteSearchDto>("/api/invite/search?q=" + Uri.EscapeDataString(q.Trim()), ct);
        return r.Status == GetStatus.Ok ? r.Value : null;
    }

    /// The active members of a chat group (I'm in) or a department, without me. Null = not allowed / failed.
    public async Task<List<InvitePersonDto>?> ExpandAsync(string kind, Guid id, CancellationToken ct = default)
    {
        var r = await auth.GetResultAsync<List<InvitePersonDto>>($"/api/invite/expand?kind={Uri.EscapeDataString(kind)}&id={id}", ct);
        return r.Status is GetStatus.Ok or GetStatus.Empty ? r.Value ?? [] : null;
    }

    /// Free/busy for up to 200 keys at a time (more are sent in further batches). Null = could not load.
    public async Task<List<PersonBusyDto>?> BusyAsync(IReadOnlyCollection<string> keys, DateTime fromUtc, DateTime toUtc, Guid? exceptMeeting = null,
        CancellationToken ct = default)
    {
        var all = new List<PersonBusyDto>();
        foreach (var chunk in keys.Distinct().Chunk(BusyBatch))
        {
            try
            {
                using var res = await auth.SendAsync(HttpMethod.Post, "/api/invite/busy",
                    new { keys = chunk, from = fromUtc, to = toUtc, exceptMeeting }, ct);
                if (!res.IsSuccessStatusCode) return null;
                all.AddRange(await res.Content.ReadFromJsonAsync<List<PersonBusyDto>>(cancellationToken: ct) ?? []);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
            {
                return null;
            }
        }
        return all;
    }
}
