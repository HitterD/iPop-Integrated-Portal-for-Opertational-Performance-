using System.Text.Json;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Sharing my calendar with a whole department or chat group (read levels only: See event titles /
/// See all details). Membership is checked when someone looks, so people who join get it and people who
/// leave lose it. Also the one-time "let my followers see titles" step after the access rework.
public static class CalendarGroupShareEndpoints
{
    public const int MaxGroups = 30;

    public static IEndpointRouteBuilder MapCalendarGroupShares(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/calendar").RequireAuthorization();

        g.MapGet("/group-shares", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            return me is null ? Results.Unauthorized() : Results.Ok(await ListAsync(db, me.Value, ct));
        });

        // PUT /api/calendar/group-shares { departmentId | roomId, level } — add or change.
        g.MapPut("/group-shares", async (SetGroupShareRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            return await SetAsync(db, me.Value, req, ct) switch
            {
                SetResult.Ok => Results.Ok(await ListAsync(db, me.Value, ct)),
                SetResult.NotFound => Results.Json(new { code = "not_found", message = "That department or chat group was not found." }, statusCode: 404),
                SetResult.NotMember => Results.Json(new { code = "not_member", message = "You can only share with chat groups you're in." }, statusCode: 403),
                SetResult.TooMany => Results.Json(new { code = "too_many", message = $"You can share with up to {MaxGroups} groups." }, statusCode: 422),
                _ => Results.Json(new { code = "invalid_level", message = "Groups can see event titles or all details." }, statusCode: 422),
            };
        });

        g.MapDelete("/group-shares/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var row = await db.CalendarGroupShares.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == me, ct);
            if (row is not null)
            {
                db.CalendarGroupShares.Remove(row);
                await AuditAsync(db, me.Value, "calendar.share.group.revoke", row, ct);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(await ListAsync(db, me.Value, ct));
        });

        // GET /api/calendar/share-targets?q= — departments (and my chat groups) to share with, by name.
        g.MapGet("/share-targets", async (string? q, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            if (me is null) return Results.Unauthorized();
            var term = (q ?? "").Trim().ToLower();
            var depts = await db.Departments.AsNoTracking()
                .Where(d => d.IsActive && (term == "" || d.Name.ToLower().Contains(term)))
                .OrderBy(d => d.Name).Take(8)
                .Select(d => new ShareTargetDto("department", d.Id, d.Name, db.Users.Count(u => u.DepartmentId == d.Id && u.IsActive)))
                .ToListAsync(ct);
            var rooms = await db.ChatRoomMembers.AsNoTracking()
                .Where(m => m.UserId == me && m.LeftAt == null && m.Room.Type == "group" && (term == "" || (m.Room.Name ?? "").ToLower().Contains(term)))
                .OrderBy(m => m.Room.Name).Take(8)
                .Select(m => new ShareTargetDto("room", m.RoomId, m.Room.Name ?? "Chat group", db.ChatRoomMembers.Count(x => x.RoomId == m.RoomId && x.LeftAt == null)))
                .ToListAsync(ct);
            return Results.Ok(depts.Concat(rooms).Where(x => x.Members > 0).ToList());
        });

        // GET /api/calendar/followers — people who follow my calendar but get no titles from me.
        g.MapGet("/followers", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await GetUserIdAsync(http, db, ct);
            return me is null ? Results.Unauthorized() : Results.Ok(await FollowersWithoutAccessAsync(db, me.Value, ct));
        });

        return app;
    }

    public record GroupShareDto(Guid Id, string Kind, Guid TargetId, string Name, int Members, string Level);
    public record SetGroupShareRequest(Guid? DepartmentId, Guid? RoomId, string? Level);
    public record FollowerDto(string Nik, string Name, string? Department);
    public record ShareTargetDto(string Kind, Guid Id, string Name, int Members);
    public enum SetResult { Ok, NotFound, NotMember, TooMany, InvalidLevel }

    public static async Task<List<GroupShareDto>> ListAsync(IPopDbContext db, Guid ownerId, CancellationToken ct)
    {
        var rows = await db.CalendarGroupShares.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id, x.DepartmentId, x.RoomId, x.Level,
                DeptName = x.Department != null ? x.Department.Name : null,
                DeptMembers = x.DepartmentId != null ? db.Users.Count(u => u.DepartmentId == x.DepartmentId && u.IsActive) : 0,
                RoomName = x.Room != null ? x.Room.Name : null,
                RoomMembers = x.RoomId != null ? db.ChatRoomMembers.Count(m => m.RoomId == x.RoomId && m.LeftAt == null) : 0,
            })
            .ToListAsync(ct);
        return rows.Select(r => r.DepartmentId is { } d
                ? new GroupShareDto(r.Id, "department", d, r.DeptName ?? "Department", r.DeptMembers, r.Level)
                : new GroupShareDto(r.Id, "room", r.RoomId!.Value, r.RoomName ?? "Chat group", r.RoomMembers, r.Level))
            .ToList();
    }

    public static async Task<SetResult> SetAsync(IPopDbContext db, Guid ownerId, SetGroupShareRequest req, CancellationToken ct)
    {
        if (!DelegateLevel.IsGroupLevel(req.Level)) return SetResult.InvalidLevel;
        if ((req.DepartmentId is null) == (req.RoomId is null)) return SetResult.NotFound;
        if (req.DepartmentId is { } dept && !await db.Departments.AnyAsync(d => d.Id == dept && d.IsActive, ct)) return SetResult.NotFound;
        if (req.RoomId is { } room)
        {
            if (!await db.ChatRooms.AnyAsync(r => r.Id == room, ct)) return SetResult.NotFound;
            if (!await db.ChatRoomMembers.AnyAsync(m => m.RoomId == room && m.UserId == ownerId && m.LeftAt == null, ct)) return SetResult.NotMember;
        }
        var row = await db.CalendarGroupShares.FirstOrDefaultAsync(x => x.OwnerId == ownerId && x.DepartmentId == req.DepartmentId && x.RoomId == req.RoomId, ct);
        if (row is null)
        {
            if (await db.CalendarGroupShares.CountAsync(x => x.OwnerId == ownerId, ct) >= MaxGroups) return SetResult.TooMany;
            row = new CalendarGroupShare
            {
                Id = Guid.NewGuid(), OwnerId = ownerId, DepartmentId = req.DepartmentId, RoomId = req.RoomId,
                Level = req.Level!, CreatedById = ownerId, CreatedAt = DateTime.UtcNow,
            };
            db.CalendarGroupShares.Add(row);
            await AuditAsync(db, ownerId, "calendar.share.group.grant", row, ct);
        }
        else if (row.Level != req.Level)
        {
            row.Level = req.Level!;
            await AuditAsync(db, ownerId, "calendar.share.group.change", row, ct);
        }
        await db.SaveChangesAsync(ct);
        return SetResult.Ok;
    }

    /// People who follow my calendar and see only busy times — what the one-time card offers to share titles with.
    public static async Task<List<FollowerDto>> FollowersWithoutAccessAsync(IPopDbContext db, Guid ownerId, CancellationToken ct)
    {
        var followers = await db.CalendarSubscriptions.AsNoTracking()
            .Where(s => s.TargetUserId == ownerId && s.User.IsActive)
            .OrderBy(s => s.User.Name)
            .Select(s => new { s.UserId, s.User.Nik, s.User.Name, Dept = s.User.Department != null ? s.User.Department.Name : null })
            .ToListAsync(ct);
        var result = new List<FollowerDto>();
        foreach (var f in followers)
            if (await CalendarAccess.LevelAsync(db, f.UserId, ownerId, ct) is null) result.Add(new FollowerDto(f.Nik, f.Name, f.Dept));
        return result;
    }

    private static async Task AuditAsync(IPopDbContext db, Guid actorId, string action, CalendarGroupShare row, CancellationToken ct)
    {
        var nik = await db.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.Nik).FirstOrDefaultAsync(ct);
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = nik, Action = action, Entity = "CalendarGroupShare", EntityId = nik,
            DetailJson = JsonSerializer.Serialize(new { department = row.DepartmentId, room = row.RoomId, level = row.Level }),
            CreatedAt = DateTime.UtcNow,
        });
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        // The access token already carries the user id (sub); only tokens without it fall back to the NIK lookup.
        if (IPop.Api.Auth.CurrentUser.IdFromToken(http.User) is { } fromToken) return fromToken;
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
