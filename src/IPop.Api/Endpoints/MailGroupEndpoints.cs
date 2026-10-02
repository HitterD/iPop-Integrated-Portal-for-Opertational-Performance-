using IPop.Api.Rbac;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Address-book groups for mail: the caller's personal groups (CRUD, owner only) and a light list
/// of shared groups (admin DirGroups) with member counts — members load per group on expand.
public static class MailGroupEndpoints
{
    public const int MaxGroupsPerUser = 50;
    public const int MaxMembers = 1000;

    public record MailGroupSummaryDto(Guid Id, string Kind, string Name, int Count);
    public record MailGroupMemberDto(string Nik, string Name, string? Department, string? Site, bool IsActive);
    public record MailGroupDetailDto(Guid Id, string Kind, string Name, int Count, IReadOnlyList<MailGroupMemberDto> Members);
    public record SaveMailGroupRequest(string? Name, IReadOnlyList<string>? Niks);

    public static IEndpointRouteBuilder MapMailGroups(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/mail/groups").RequireAuthorization().RequirePermission("mail.read");

        // GET /api/mail/groups — shared + personal, counts of active members only.
        g.MapGet("", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var shared = await db.DirGroups.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
                .Select(x => new MailGroupSummaryDto(x.Id, "shared", x.Name, x.Members.Count(m => m.User.IsActive)))
                .ToListAsync(ct);
            var personal = await db.MailGroups.AsNoTracking().Where(x => x.OwnerId == userId).OrderBy(x => x.Name)
                .Select(x => new MailGroupSummaryDto(x.Id, "personal", x.Name, x.Members.Count(m => m.User.IsActive)))
                .ToListAsync(ct);
            return Results.Ok(new { shared, personal });
        });

        // GET /api/mail/groups/{kind}/{id} — one group with its members (for expanding a chip).
        g.MapGet("/{kind}/{id:guid}", async (string kind, Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var dto = await LoadAsync(db, kind, id, userId.Value, ct);
            return dto is null ? NotFound() : Results.Ok(dto);
        });

        // POST /api/mail/groups — create a personal group from the address-book selection.
        g.MapPost("", async (SaveMailGroupRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 120) return Error("name_invalid", "Give the group a name (up to 120 characters).");
            if (await db.MailGroups.CountAsync(x => x.OwnerId == userId, ct) >= MaxGroupsPerUser)
                return Error("too_many_groups", $"You can keep up to {MaxGroupsPerUser} personal groups.");
            if (await db.MailGroups.AnyAsync(x => x.OwnerId == userId && x.Name.ToLower() == name.ToLower(), ct))
                return Error("name_taken", "You already have a group with that name.");
            var members = await ResolveMembersAsync(db, req.Niks, ct);
            if (members is null) return Error("too_many_members", $"A group can have up to {MaxMembers:N0} people.");
            if (members.Count == 0) return Error("no_members", "Pick at least one person.");

            var now = DateTime.UtcNow;
            var group = new MailGroup { Id = Guid.NewGuid(), OwnerId = userId.Value, Name = name, CreatedAt = now, UpdatedAt = now };
            db.MailGroups.Add(group);
            foreach (var uid in members) db.MailGroupMembers.Add(new MailGroupMember { GroupId = group.Id, UserId = uid });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new MailGroupSummaryDto(group.Id, "personal", name, members.Count));
        }).RequirePermission("mail.send");

        // PUT /api/mail/groups/{id} — rename and/or replace members (owner only).
        g.MapPut("/{id:guid}", async (Guid id, SaveMailGroupRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var group = await db.MailGroups.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == userId, ct);
            if (group is null) return NotFound();
            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (name.Length is 0 or > 120) return Error("name_invalid", "Give the group a name (up to 120 characters).");
                if (await db.MailGroups.AnyAsync(x => x.OwnerId == userId && x.Id != id && x.Name.ToLower() == name.ToLower(), ct))
                    return Error("name_taken", "You already have a group with that name.");
                group.Name = name;
            }
            if (req.Niks is not null)
            {
                var members = await ResolveMembersAsync(db, req.Niks, ct);
                if (members is null) return Error("too_many_members", $"A group can have up to {MaxMembers:N0} people.");
                if (members.Count == 0) return Error("no_members", "Pick at least one person.");
                db.MailGroupMembers.RemoveRange(await db.MailGroupMembers.Where(m => m.GroupId == id).ToListAsync(ct));
                foreach (var uid in members) db.MailGroupMembers.Add(new MailGroupMember { GroupId = id, UserId = uid });
            }
            group.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await LoadAsync(db, "personal", id, userId.Value, ct));
        }).RequirePermission("mail.send");

        // DELETE /api/mail/groups/{id} — owner only. Mails already sent keep the group name.
        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await UserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var group = await db.MailGroups.FirstOrDefaultAsync(x => x.Id == id && x.OwnerId == userId, ct);
            if (group is null) return NotFound();
            db.MailGroups.Remove(group);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true });
        }).RequirePermission("mail.send");

        return app;
    }

    private static async Task<MailGroupDetailDto?> LoadAsync(IPopDbContext db, string kind, Guid id, Guid userId, CancellationToken ct)
    {
        if (string.Equals(kind, "personal", StringComparison.OrdinalIgnoreCase))
        {
            var p = await db.MailGroups.AsNoTracking().Where(x => x.Id == id && x.OwnerId == userId)
                .Select(x => new { x.Name, Members = x.Members.Select(m => new MailGroupMemberDto(m.User.Nik, m.User.Name,
                    m.User.Department != null ? m.User.Department.Name : null, m.User.Site != null ? m.User.Site.Name : null, m.User.IsActive)).ToList() })
                .FirstOrDefaultAsync(ct);
            return p is null ? null : Detail(id, "personal", p.Name, p.Members);
        }
        var s = await db.DirGroups.AsNoTracking().Where(x => x.Id == id && x.IsActive)
            .Select(x => new { x.Name, Members = x.Members.Select(m => new MailGroupMemberDto(m.User.Nik, m.User.Name,
                m.User.Department != null ? m.User.Department.Name : null, m.User.Site != null ? m.User.Site.Name : null, m.User.IsActive)).ToList() })
            .FirstOrDefaultAsync(ct);
        return s is null ? null : Detail(id, "shared", s.Name, s.Members);
    }

    private static MailGroupDetailDto Detail(Guid id, string kind, string name, List<MailGroupMemberDto> members)
        => new(id, kind, name, members.Count(m => m.IsActive),
            members.OrderBy(m => !m.IsActive).ThenBy(m => m.Name).ToList());

    /// Active users for the given NIKs; null when over the size limit.
    private static async Task<List<Guid>?> ResolveMembersAsync(IPopDbContext db, IReadOnlyList<string>? niks, CancellationToken ct)
    {
        var list = (niks ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().ToList();
        if (list.Count > MaxMembers) return null;
        if (list.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Nik) && u.IsActive).Select(u => u.Id).ToListAsync(ct);
    }

    private static async Task<Guid?> UserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    private static IResult NotFound() => Results.Json(new { code = "group_not_found", message = "Group not found." }, statusCode: 404);
    private static IResult Error(string code, string message) => Results.Json(new { code, message }, statusCode: 422);
}
