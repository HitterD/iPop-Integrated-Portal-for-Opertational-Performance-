using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Meeting scheduling v3 §4: data for the one invite picker (Calendar form, booking sheet, Scheduled).
/// People travel with an opaque <c>Key</c> (the NIK, the key the meeting APIs already take in AttendeeNiks); it is never
/// rendered and no DTO here carries a position. Free/busy follows calendar access v2: everyone sees busy times, never
/// titles, so <see cref="PersonBusyDto"/> has no title field at all.
public static class InvitePickerEndpoints
{
    public const int FrequentMax = 8, DepartmentMax = 12, ChatGroupMax = 8, SearchMax = 20, BusyMaxKeys = 200;
    public static readonly TimeSpan FrequentWindow = TimeSpan.FromDays(90), BusyMaxRange = TimeSpan.FromHours(24);

    /// Key = NIK, opaque (never shown). PhotoUrl is null: the client resolves photos by key (AvatarPhotos / PersonAvatar).
    public record InvitePersonDto(string Key, string Name, string? Department, string? PhotoUrl);
    /// Kind: "chat" | "department".
    public record InviteGroupDto(string Kind, Guid Id, string Name, int Members, string? Color);
    public record InviteSuggestionsDto(IReadOnlyList<InvitePersonDto> Frequent, IReadOnlyList<InvitePersonDto> Department,
        IReadOnlyList<InviteGroupDto> ChatGroups, IReadOnlyList<InviteGroupDto> Departments);
    public record InviteSearchDto(IReadOnlyList<InvitePersonDto> People, IReadOnlyList<InviteGroupDto> Groups);
    /// State: "free" | "busy" | "unknown". BusyFrom/BusyTo = the first busy stretch overlapping the range (touching blocks merged).
    public record PersonBusyDto(string Key, string State, DateTime? BusyFrom, DateTime? BusyTo);
    /// ExceptMeeting = the meeting being edited, so it does not block its own people.
    public record InviteBusyRequest(IReadOnlyList<string>? Keys, DateTime? From, DateTime? To, Guid? ExceptMeeting = null);

    public enum ExpandError { InvalidKind, NotFound, Forbidden }
    public enum BusyError { TooManyKeys, InvalidRange }

    public const string ChatKind = "chat", DepartmentKind = "department";
    public const string Free = "free", Busy = "busy", Unknown = "unknown";

    public static IEndpointRouteBuilder MapInvitePicker(this IEndpointRouteBuilder app)
    {
        // Same gate as the Calendar sharing / free-busy routes (/api/calendar): any signed-in employee.
        var g = app.MapGroup("/api/invite").RequireAuthorization();

        g.MapGet("/suggestions", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await SuggestionsAsync(db, me, DateTime.UtcNow, ct)) : Results.Unauthorized());

        g.MapGet("/search", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await SearchAsync(db, me, q, ct)) : Results.Unauthorized());

        g.MapGet("/expand", (string? kind, Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
            ExpandHttpAsync(kind, id, http, db, ct));

        g.MapPost("/busy", (InviteBusyRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
            BusyHttpAsync(req, http, db, ct));

        return app;
    }

    // ── Suggestions ──

    public static async Task<InviteSuggestionsDto> SuggestionsAsync(IPopDbContext db, Guid me, DateTime nowUtc, CancellationToken ct)
    {
        // Frequent: people who shared non-cancelled meetings with me (organised or attended, not declined) in the last 90 days.
        var since = nowUtc - FrequentWindow;
        var myMeetings = db.MeetingAttendees.AsNoTracking()
            .Where(a => a.UserId == me && a.RsvpStatus != "decline" && a.Meeting.Status != MeetingStatus.Cancelled && a.Meeting.StartAt >= since)
            .Select(a => a.MeetingId)
            .Union(db.Meetings.AsNoTracking()
                .Where(m => m.OrganizerId == me && m.Status != MeetingStatus.Cancelled && m.StartAt >= since).Select(m => m.Id));
        var coIds = await db.MeetingAttendees.AsNoTracking()
            .Where(a => myMeetings.Contains(a.MeetingId) && a.UserId != me)
            .Select(a => new { a.MeetingId, a.UserId })
            .ToListAsync(ct);
        var counts = coIds.Distinct().GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Count());
        var candidateIds = counts.Keys.ToList();
        var candidates = await People(db.Users.AsNoTracking().Where(u => candidateIds.Contains(u.Id) && u.IsActive))
            .ToListAsync(ct);
        var frequent = candidates
            .OrderByDescending(p => counts[p.Id]).ThenBy(p => p.Person.Name, StringComparer.OrdinalIgnoreCase)
            .Take(FrequentMax).Select(p => p.Person).ToList();

        var myDept = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        var department = myDept is null ? [] : (await People(db.Users.AsNoTracking()
                .Where(u => u.DepartmentId == myDept && u.IsActive && u.Id != me))
            .ToListAsync(ct))
            .Select(p => p.Person).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(DepartmentMax).ToList();

        var chatGroups = await ChatGroups(db, me).OrderByDescending(r => r.LastMessageAt).Take(ChatGroupMax)
            .Select(r => new InviteGroupDto(ChatKind, r.Id, r.Name ?? "Group", r.Members.Count(m => m.LeftAt == null && m.User.IsActive), r.AvatarColor))
            .ToListAsync(ct);

        var departments = (await Departments(db).ToListAsync(ct))
            .Where(d => d.Members > 0).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new InviteSuggestionsDto(frequent, department, chatGroups, departments);
    }

    // ── Search ──

    /// Name or department contains the query (case-insensitive, LIKE metacharacters literal), active people only, plus
    /// matching departments and chat groups I'm in. Fewer than 2 characters → nothing.
    public static async Task<InviteSearchDto> SearchAsync(IPopDbContext db, Guid me, string? q, CancellationToken ct)
    {
        var term = q?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(term) || term.Length < 2) return new([], []);
        var people = (await People(db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.Id != me
                    && (u.Name.ToLower().Contains(term) || (u.Department != null && u.Department.Name.ToLower().Contains(term)))))
            .ToListAsync(ct))
            .Select(p => p.Person).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(SearchMax).ToList();
        var depts = (await Departments(db, term).ToListAsync(ct))
            .Where(d => d.Members > 0).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var chats = (await ChatGroups(db, me).Where(r => r.Name != null && r.Name.ToLower().Contains(term))
                .Select(r => new InviteGroupDto(ChatKind, r.Id, r.Name!, r.Members.Count(m => m.LeftAt == null && m.User.IsActive), r.AvatarColor))
                .ToListAsync(ct))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase);
        return new(people, depts.Concat(chats).Take(SearchMax).ToList());
    }

    // ── Expand a group ──

    /// Active members of a chat group I'm in (403 otherwise) or of a department, never me, by name.
    public static async Task<(ExpandError? Error, IReadOnlyList<InvitePersonDto>? People)> ExpandAsync(IPopDbContext db, Guid me,
        string? kind, Guid id, CancellationToken ct)
    {
        IQueryable<User> users;
        if (kind == ChatKind)
        {
            var room = await db.ChatRooms.AsNoTracking().Where(r => r.Id == id && r.Type == "group" && r.IsActive)
                .Select(r => new { IAmIn = r.Members.Any(m => m.UserId == me && m.LeftAt == null) }).FirstOrDefaultAsync(ct);
            if (room is null) return (ExpandError.NotFound, null);
            if (!room.IAmIn) return (ExpandError.Forbidden, null);
            users = db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == id && m.LeftAt == null).Select(m => m.User);
        }
        else if (kind == DepartmentKind)
        {
            if (!await db.Departments.AsNoTracking().AnyAsync(d => d.Id == id && d.IsActive, ct)) return (ExpandError.NotFound, null);
            users = db.Users.Where(u => u.DepartmentId == id);
        }
        else return (ExpandError.InvalidKind, null);

        var people = (await People(users.Where(u => u.IsActive && u.Id != me)).ToListAsync(ct))
            .Select(p => p.Person).DistinctBy(p => p.Key).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (null, people);
    }

    public static async Task<IResult> ExpandHttpAsync(string? kind, Guid id, HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
        var (error, people) = await ExpandAsync(db, me, kind, id, ct);
        return error switch
        {
            ExpandError.InvalidKind => Results.Json(new { code = "invalid_kind", message = "Choose a chat group or a department." }, statusCode: 400),
            ExpandError.NotFound => Results.Json(new { code = "not_found", message = "That group no longer exists." }, statusCode: 404),
            ExpandError.Forbidden => Results.Json(new { code = "forbidden", message = "You can only add chat groups you're in." }, statusCode: 403),
            _ => Results.Ok(people),
        };
    }

    // ── Batch free/busy ──

    /// One row per requested key, in request order (duplicates dropped). Built on the Find a time lanes
    /// (<see cref="CalendarSharingEndpoints.BuildFreeBusyAsync"/>): blocks only, tentative counts as busy, all-day ignored.
    /// Unknown or inactive people → "unknown".
    public static async Task<(BusyError? Error, IReadOnlyList<PersonBusyDto>? Rows)> BusyAsync(IPopDbContext db, Guid me,
        InviteBusyRequest req, CancellationToken ct)
    {
        var keys = (req.Keys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct().ToList();
        if (keys.Count > BusyMaxKeys) return (BusyError.TooManyKeys, null);
        if (req.From is not { } f0 || req.To is not { } t0) return (BusyError.InvalidRange, null);
        var (from, to) = (AsUtc(f0), AsUtc(t0));
        if (to <= from || to - from > BusyMaxRange) return (BusyError.InvalidRange, null);
        if (keys.Count == 0) return (null, []);

        var ids = await db.Users.AsNoTracking().Where(u => keys.Contains(u.Nik) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var lanes = ids.Count == 0 ? [] : (await CalendarSharingEndpoints.BuildFreeBusyAsync(db, me, ids, from, to, 15,
            CalendarSharingEndpoints.ResolveTimeZone(null), DateTime.UtcNow, ct, req.ExceptMeeting)).Lanes;
        var byKey = lanes.ToDictionary(l => l.Nik);
        return (null, keys.Select(k => byKey.TryGetValue(k, out var lane) ? Row(k, lane.Blocks, from, to) : new PersonBusyDto(k, Unknown, null, null)).ToList());
    }

    private static PersonBusyDto Row(string key, IReadOnlyList<CalendarSharingEndpoints.FreeBusyBlock> blocks, DateTime from, DateTime to)
    {
        var sorted = blocks.OrderBy(b => b.StartAt).ToList();
        var first = sorted.FindIndex(b => b.StartAt < to && b.EndAt > from);
        if (first < 0) return new PersonBusyDto(key, Free, null, null);
        var (s, e) = (sorted[first].StartAt, sorted[first].EndAt);
        foreach (var b in sorted.Skip(first + 1))
        {
            if (b.StartAt > e) break;
            if (b.EndAt > e) e = b.EndAt;
        }
        return new PersonBusyDto(key, Busy, s, e);
    }

    public static async Task<IResult> BusyHttpAsync(InviteBusyRequest req, HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
        var (error, rows) = await BusyAsync(db, me, req, ct);
        return error switch
        {
            BusyError.TooManyKeys => Results.Json(new { code = "too_many", message = $"Check at most {BusyMaxKeys} people at once." }, statusCode: 400),
            BusyError.InvalidRange => Results.Json(new { code = "invalid_range", message = "End must be after start (at most one day)." }, statusCode: 400),
            _ => Results.Ok(rows),
        };
    }

    // ── Helpers ──

    private sealed record PersonRow(Guid Id, InvitePersonDto Person);

    private static IQueryable<PersonRow> People(IQueryable<User> users) =>
        users.Select(u => new PersonRow(u.Id, new InvitePersonDto(u.Nik, u.Name, u.Department != null ? u.Department.Name : null, null)));

    private static IQueryable<ChatRoom> ChatGroups(IPopDbContext db, Guid me) =>
        db.ChatRooms.AsNoTracking().Where(r => r.Type == "group" && r.IsActive && r.Members.Any(m => m.UserId == me && m.LeftAt == null));

    private static IQueryable<InviteGroupDto> Departments(IPopDbContext db, string? term = null) =>
        db.Departments.AsNoTracking().Where(d => d.IsActive && (term == null || d.Name.ToLower().Contains(term)))
            .Select(d => new InviteGroupDto(DepartmentKind, d.Id, d.Name, db.Users.Count(u => u.DepartmentId == d.Id && u.IsActive), null));

    private static DateTime AsUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    private static async Task<Guid?> UserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
