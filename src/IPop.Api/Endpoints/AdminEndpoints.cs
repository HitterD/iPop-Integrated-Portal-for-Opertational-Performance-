using IPop.Application.Auth;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Admin-only user management. HRIS sync keeps profile facts fresh; everything here
/// (create, edit, activate, roles, password resets) is portal business the feed never touches.
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/users").RequireAuthorization("AdminOnly");
        g.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (context.HttpContext.Request.Method is "POST" or "PUT" or "PATCH"
                && result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
            {
                await context.HttpContext.RequestServices.GetRequiredService<IDirectoryChangeNotifier>()
                    .NotifyAsync(context.HttpContext.RequestAborted);
                await context.HttpContext.RequestServices.GetRequiredService<IAuditChangeNotifier>()
                    .NotifyAsync(context.HttpContext.RequestAborted);
            }
            return result;
        });

        g.MapGet("", async (string? q, bool? active, string? role, string? departmentCode,
            string? siteCode, bool? manuallyEdited, string? sort, bool? desc, int? page,
            int? pageSize, IPopDbContext db, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var query = db.Users.Include(u => u.Department).Include(u => u.Site).AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                var pat = $"%{term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")}%";
                query = query.Where(u => EF.Functions.ILike(u.Name, pat, @"\") || EF.Functions.ILike(u.Nik, pat, @"\"));
            }
            if (active is not null) query = query.Where(u => u.IsActive == active);
            if (manuallyEdited is not null) query = query.Where(u => u.IsManuallyEdited == manuallyEdited);
            if (!string.IsNullOrWhiteSpace(departmentCode))
                query = query.Where(u => u.Department != null && u.Department.Code == departmentCode.Trim());
            if (!string.IsNullOrWhiteSpace(siteCode))
                query = query.Where(u => u.Site != null && u.Site.Code == siteCode.Trim());
            if (!string.IsNullOrWhiteSpace(role))
                query = query.Where(u => u.UserRoles.Any(ur => ur.Role.Name == role.Trim()));

            var total = await query.CountAsync(ct);
            var ordered = (sort?.ToLowerInvariant()) switch
            {
                "nik" => desc == true
                    ? query.OrderByDescending(u => u.Nik).ThenBy(u => u.Name)
                    : query.OrderBy(u => u.Nik).ThenBy(u => u.Name),
                "updated" => desc == true
                    ? query.OrderByDescending(u => u.UpdatedAt).ThenBy(u => u.Nik)
                    : query.OrderBy(u => u.UpdatedAt).ThenBy(u => u.Nik),
                _ => desc == true
                    ? query.OrderByDescending(u => u.Name).ThenBy(u => u.Nik)
                    : query.OrderBy(u => u.Name).ThenBy(u => u.Nik),
            };
            var users = await ordered.Skip((p - 1) * size).Take(size).ToListAsync(ct);
            var ids = users.Select(u => u.Id).ToList();
            var rolesByUser = (await db.UserRoles.Where(ur => ids.Contains(ur.UserId))
                    .Select(ur => new { ur.UserId, ur.Role.Name }).ToListAsync(ct))
                .GroupBy(r => r.UserId)
                .ToDictionary(gr => gr.Key, gr => gr.Select(x => x.Name).ToArray());
            var now = DateTime.UtcNow;
            var items = users.Select(u => new AdminUserRow(
                u.Nik, u.Name, u.Email, u.Position, u.Location, u.Department?.Name, u.Department?.Code,
                u.Site?.Name, u.Site?.Code, u.IsActive, u.IsManuallyEdited,
                rolesByUser.GetValueOrDefault(u.Id, []),
                PresenceStatus.ComputeStatus(u.PresenceLastSeenAt, u.PresenceManualStatus, now), u.UpdatedAt));
            return Results.Ok(new { total, page = p, pageSize = size, items });
        });

        g.MapGet("/stats", async (IPopDbContext db, CancellationToken ct) =>
        {
            var total = await db.Users.CountAsync(ct);
            var active = await db.Users.CountAsync(u => u.IsActive, ct);
            var adminRoleId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstOrDefaultAsync(ct);
            var admins = adminRoleId != default
                ? await db.UserRoles.CountAsync(ur => ur.RoleId == adminRoleId, ct)
                : 0;
            var now = DateTime.UtcNow;
            var activeCutoff = now.AddMinutes(-5);
            var online = await db.Users.CountAsync(u => u.IsActive && u.PresenceLastSeenAt >= activeCutoff, ct);
            return Results.Ok(new AdminUserStats(total, active, admins, online));
        });

        g.MapGet("/sessions", async (string? refreshToken, ITokenService tokens, IPopDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var currentHash = !string.IsNullOrWhiteSpace(refreshToken) ? tokens.HashRefreshToken(refreshToken) : null;
            var items = await db.AuthSessions.AsNoTracking()
                .Where(s => s.RevokedAt == null && s.ExpiresAt > now)
                .OrderByDescending(s => s.CreatedAt).Take(200)
                .Select(s => new AdminSessionRow(s.Id, s.User.Nik, s.User.Name,
                    s.DeviceFingerprint, s.IpAddress, s.CreatedAt, s.ExpiresAt,
                    currentHash != null && s.RefreshTokenHash == currentHash))
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        g.MapPost("/sessions/revoke-others", async (RevokeOthersRequest? req, ITokenService tokens,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var callerNik = CallerNik(http);
            var currentHash = !string.IsNullOrWhiteSpace(req?.CurrentRefreshToken)
                ? tokens.HashRefreshToken(req.CurrentRefreshToken)
                : null;

            var query = db.AuthSessions.Where(s => s.RevokedAt == null && s.ExpiresAt > now);
            if (req?.CurrentSessionId is not null)
                query = query.Where(s => s.Id != req.CurrentSessionId.Value);
            else if (currentHash is not null)
                query = query.Where(s => s.RefreshTokenHash != currentHash);

            var count = await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
            Audit(db, callerNik, "admin.session.revoke-others", "revoked=" + count, now);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { count });
        });

        g.MapPost("/sessions/{id:guid}/revoke", async (Guid id, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var changed = await db.AuthSessions
                .Where(s => s.Id == id && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
            if (changed == 0) return Results.NotFound();
            Audit(db, CallerNik(http), "admin.session.revoke", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        g.MapPost("/{nik}/sessions/revoke-all", async (string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            await RevokeSessions(db, user.Id, DateTime.UtcNow, ct);
            Audit(db, CallerNik(http), "admin.session.revoke-all", nik, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // Create lands directly on the default-password flow — no auth code written.
        g.MapPost("", async Task<IResult> (CreateUserRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var nik = req.Nik.Trim();
            if (nik.Length == 0 || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email))
                return Results.BadRequest(new { code = "invalid_request", message = "NIK, name, and email are required." });
            if (await db.Users.AnyAsync(u => u.Nik == nik, ct))
                return Results.Conflict(new { code = "duplicate_nik", message = "A user with this NIK already exists." });

            var now = DateTime.UtcNow;
            var user = new User
            {
                Id = Guid.NewGuid(), Nik = nik, Name = req.Name.Trim(), Email = req.Email.Trim(),
                Position = req.Position?.Trim(), Location = req.Location?.Trim(),
                PasswordHash = null, MustChangePassword = true, IsActive = true,
                CreatedAt = now, UpdatedAt = now,
            };
            var placementError = await ApplyPlacement(user, req.DepartmentCode, req.SiteCode, db, ct);
            if (placementError is not null) return placementError;
            db.Users.Add(user);
            Audit(db, CallerNik(http), "admin.user.create", nik, now);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/users/{nik}", new { nik });
        });

        // Edit sets IsManuallyEdited so the next HRIS sync cannot revert the correction.
        g.MapPatch("/{nik}", async Task<IResult> (string nik, EditUserRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            if (req.Name is not null) user.Name = req.Name.Trim();
            if (req.Email is not null) user.Email = req.Email.Trim();
            if (req.Position is not null) user.Position = req.Position.Trim();
            if (req.Location is not null) user.Location = req.Location.Trim();
            var placementError = await ApplyPlacement(user, req.DepartmentCode, req.SiteCode, db, ct);
            if (placementError is not null) return placementError;
            user.IsManuallyEdited = true;
            user.UpdatedAt = DateTime.UtcNow;
            Audit(db, CallerNik(http), "admin.user.edit", nik, user.UpdatedAt);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { nik });
        });

        // Deactivate cuts live sessions too — a signed-out leaver stays out.
        g.MapPost("/{nik}/deactivate", async Task<IResult> (string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            if (CallerNik(http) == nik)
                return Results.BadRequest(new { code = "self_lockout", message = "You cannot deactivate your own account." });
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            var now = DateTime.UtcNow;
            user.IsActive = false;
            user.UpdatedAt = now;
            await RevokeSessions(db, user.Id, now, ct);
            Audit(db, CallerNik(http), "admin.user.deactivate", nik, now);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        g.MapPost("/{nik}/reactivate", async Task<IResult> (string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            user.IsActive = true;
            user.UpdatedAt = DateTime.UtcNow;
            Audit(db, CallerNik(http), "admin.user.reactivate", nik, user.UpdatedAt);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // Reset returns the account to the default flow and forces re-login everywhere.
        g.MapPost("/{nik}/reset-password", async Task<IResult> (string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            var now = DateTime.UtcNow;
            user.PasswordHash = null;
            user.MustChangePassword = true;
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            user.UpdatedAt = now;
            await RevokeSessions(db, user.Id, now, ct);
            Audit(db, CallerNik(http), "admin.user.reset-password", nik, now);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // Roles are a full replace. Two roles exist today, so load-all + match in memory.
        g.MapPut("/{nik}/roles", async Task<IResult> (string nik, SetRolesRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            var wanted = req.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var known = await db.Roles.ToListAsync(ct);
            var roles = wanted.Select(w => known.FirstOrDefault(r => r.Name.Equals(w, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (roles.Any(r => r is null))
                return Results.BadRequest(new { code = "unknown_role", message = "One or more roles do not exist." });
            if (CallerNik(http) == nik && !wanted.Contains("Admin", StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { code = "self_lockout", message = "You cannot remove your own Admin role." });
            db.UserRoles.RemoveRange(user.UserRoles);
            foreach (var r in roles) db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = r!.Id });
            Audit(db, CallerNik(http), "admin.user.roles", nik, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { roles = wanted });
        });

        var org = app.MapGroup("/api/admin/org").RequireAuthorization("AdminOnly");
        org.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (context.HttpContext.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE"
                && result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                await context.HttpContext.RequestServices.GetRequiredService<IDirectoryChangeNotifier>()
                    .NotifyAsync(context.HttpContext.RequestAborted);
            return result;
        });

        org.MapGet("", async (IPopDbContext db, CancellationToken ct) =>
        {
            var depts = await db.Departments.Include(d => d.Parent).AsNoTracking().ToListAsync(ct);
            var sites = await db.Sites.AsNoTracking().ToListAsync(ct);
            var deptCounts = await db.Users.GroupBy(u => u.DepartmentId)
                .Select(g => new { Id = g.Key, Total = g.Count(), Active = g.Count(u => u.IsActive) }).ToListAsync(ct);
            var siteCounts = await db.Users.GroupBy(u => u.SiteId)
                .Select(g => new { Id = g.Key, Total = g.Count(), Active = g.Count(u => u.IsActive) }).ToListAsync(ct);
            return Results.Ok(new
            {
                departments = depts.Select(d =>
                {
                    var c = deptCounts.FirstOrDefault(x => x.Id == d.Id);
                    return new OrgDepartmentRow(d.Id, d.Code, d.Name, d.Parent?.Code, d.IsActive,
                        c?.Total ?? 0, c?.Active ?? 0);
                }),
                sites = sites.Select(s =>
                {
                    var c = siteCounts.FirstOrDefault(x => x.Id == s.Id);
                    return new OrgSiteRow(s.Id, s.Code, s.Name, s.IsActive,
                        c?.Total ?? 0, c?.Active ?? 0);
                }),
            });
        });

        org.MapPost("/departments", async (UpsertDepartmentRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var code = req.Code.Trim();
            if (code.Length == 0 || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { code = "invalid_request", message = "Code and name are required." });
            if (await db.Departments.AnyAsync(d => d.Code == code, ct))
                return Results.Conflict(new { code = "duplicate_code", message = "A department with this code already exists." });
            var dept = new Department { Id = Guid.NewGuid(), Code = code, Name = req.Name.Trim(), IsActive = true };
            var parentError = await ApplyParent(dept, req.ParentCode, db, ct);
            if (parentError is not null) return parentError;
            db.Departments.Add(dept);
            Audit(db, CallerNik(http), "admin.org.dept.create", code, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/org/departments/{code}", new { code });
        });

        org.MapPatch("/departments/{code}", async (string code, UpsertDepartmentRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var dept = await db.Departments.FirstOrDefaultAsync(d => d.Code == code, ct);
            if (dept is null) return Results.NotFound();
            var deactivating = req.IsActive is false && dept.IsActive;
            if (req.Name?.Trim() is { Length: > 0 } name) dept.Name = name;
            if (req.IsActive is { } active) dept.IsActive = active;
            var parentError = await ApplyParent(dept, req.ParentCode, db, ct);
            if (parentError is not null) return parentError;
            if (deactivating)
                await db.Users.Where(u => u.Division != null && u.Division.DepartmentId == dept.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null), ct);
            Audit(db, CallerNik(http), "admin.org.dept.edit", code, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { code });
        });

        org.MapPost("/sites", async (UpsertSiteRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var code = req.Code.Trim();
            if (code.Length == 0 || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { code = "invalid_request", message = "Code and name are required." });
            if (await db.Sites.AnyAsync(s => s.Code == code, ct))
                return Results.Conflict(new { code = "duplicate_code", message = "A site with this code already exists." });
            db.Sites.Add(new Site { Id = Guid.NewGuid(), Code = code, Name = req.Name.Trim(), IsActive = true });
            Audit(db, CallerNik(http), "admin.org.site.create", code, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/org/sites/{code}", new { code });
        });

        org.MapPatch("/sites/{code}", async (string code, UpsertSiteRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var site = await db.Sites.FirstOrDefaultAsync(s => s.Code == code, ct);
            if (site is null) return Results.NotFound();
            var deactivating = req.IsActive is false && site.IsActive;
            if (req.Name?.Trim() is { Length: > 0 } name) site.Name = name;
            if (req.IsActive is { } active) site.IsActive = active;
            if (deactivating)
                await db.Users.Where(u => u.Division != null && u.Division.SiteId == site.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null), ct);
            Audit(db, CallerNik(http), "admin.org.site.edit", code, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { code });
        });

        // One-shot cleanup of the HRIS-era departments. Sync no longer writes them, so these are stale.
        org.MapPost("/departments/purge", async (IPopDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var unassigned = await db.Users.Where(u => u.DepartmentId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (Guid?)null), ct);
            var removed = await db.Departments.ExecuteDeleteAsync(ct);
            db.AuditLogs.Add(new AuditLog
            {
                ActorNik = CallerNik(http), Action = "admin.org.dept.purge", Entity = "Department",
                EntityId = "*", DetailJson = $"{{\"unassigned\":{unassigned},\"removed\":{removed}}}",
                CreatedAt = now
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { unassigned, removed });
        });

        org.MapPut("/users/{nik}/placement", async (string nik, SetPlacementRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            var error = await ApplyPlacement(user, req.DepartmentCode, req.SiteCode, db, ct);
            if (error is not null) return error;
            user.UpdatedAt = DateTime.UtcNow;
            Audit(db, CallerNik(http), "admin.user.placement", nik, user.UpdatedAt);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // Divisions — admin-owned bundles of one Department + one Site backing the Directory tree.
        org.MapGet("/divisions", async (IPopDbContext db, CancellationToken ct) =>
        {
            var divs = await db.Divisions
                .Include(d => d.Department).Include(d => d.Site).AsNoTracking()
                .OrderBy(d => d.Code).ToListAsync(ct);
            var counts = await db.Users.Where(u => u.DivisionId != null)
                .GroupBy(u => u.DivisionId)
                .Select(g => new { Id = g.Key, Total = g.Count(), Active = g.Count(u => u.IsActive) })
                .ToListAsync(ct);
            return Results.Ok(divs.Select(d =>
            {
                var c = counts.FirstOrDefault(x => x.Id == d.Id);
                return new AdminDivisionRow(d.Id, d.Code, d.Name, d.Department?.Code, d.Department?.Name,
                    d.Site?.Code, d.Site?.Name, d.IsActive, c?.Total ?? 0, c?.Active ?? 0);
            }));
        });

        org.MapPost("/divisions", async (UpsertDivisionRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var code = req.Code.Trim();
            if (code.Length == 0 || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { code = "invalid_request", message = "Code and name are required." });
            if (await db.Divisions.AnyAsync(d => d.Code == code, ct))
                return Results.Conflict(new { code = "duplicate_code", message = "A division with this code already exists." });
            var div = new Division { Id = Guid.NewGuid(), Code = code, Name = req.Name.Trim(), IsActive = true };
            var parentError = await ApplyDivisionParents(div, req.DepartmentCode, req.SiteCode, db, ct);
            if (parentError is not null) return parentError;
            db.Divisions.Add(div);
            Audit(db, CallerNik(http), "admin.org.division.create", code, DateTime.UtcNow, "Division");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/org/divisions/{code}", new { code });
        });

        org.MapPatch("/divisions/{code}", async (string code, UpsertDivisionRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var div = await db.Divisions.FirstOrDefaultAsync(d => d.Code == code, ct);
            if (div is null) return Results.NotFound();
            if (req.Name?.Trim() is { Length: > 0 } name) div.Name = name;
            var deactivating = req.IsActive is false && div.IsActive;
            if (req.IsActive is { } active) div.IsActive = active;
            var parentError = await ApplyDivisionParents(div, req.DepartmentCode, req.SiteCode, db, ct);
            if (parentError is not null) return parentError;
            // Deactivating a Division must clear DivisionId on its members, same as delete —
            // otherwise they'd keep a non-null DivisionId pointing at a now-inactive Division and
            // silently vanish from both the /structure tree and NoDivisionCount.
            if (deactivating)
                await db.Users.Where(u => u.DivisionId == div.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null), ct);
            Audit(db, CallerNik(http), "admin.org.division.edit", code, DateTime.UtcNow, "Division");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { code });
        });

        // Delete sets member DivisionId to null (no cascade) and preserves Site/Department.
        org.MapDelete("/divisions/{code}", async (string code, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var div = await db.Divisions.FirstOrDefaultAsync(d => d.Code == code, ct);
            if (div is null) return Results.NotFound();
            await db.Users.Where(u => u.DivisionId == div.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DivisionId, (Guid?)null), ct);
            db.Divisions.Remove(div);
            Audit(db, CallerNik(http), "admin.org.division.delete", code, DateTime.UtcNow, "Division");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { code });
        });

        // Assign a user to a Division — syncs Site/Department from the Division's parents.
        org.MapPut("/users/{nik}/division", async (string nik, SetUserDivisionRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            var div = await db.Divisions.Include(d => d.Department).Include(d => d.Site)
                .SingleOrDefaultAsync(d => d.Code == req.DivisionCode.Trim() && d.IsActive, ct);
            if (div is null)
                return Results.BadRequest(new { code = "unknown_division", message = "Division does not exist or is inactive." });
            user.DivisionId = div.Id;
            if (div.DepartmentId is not null) user.DepartmentId = div.DepartmentId;
            if (div.SiteId is not null) user.SiteId = div.SiteId;
            user.UpdatedAt = DateTime.UtcNow;
            Audit(db, CallerNik(http), "admin.user.division.assign", nik, user.UpdatedAt);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { nik, divisionCode = div.Code });
        });

        // Unassign — clears only DivisionId; Site/Department are preserved.
        org.MapDelete("/users/{nik}/division", async (string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            user.DivisionId = null;
            user.UpdatedAt = DateTime.UtcNow;
            Audit(db, CallerNik(http), "admin.user.division.unassign", nik, user.UpdatedAt);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        var dir = app.MapGroup("/api/admin/dir-groups").RequireAuthorization("AdminOnly");
        dir.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (context.HttpContext.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE"
                && result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                await context.HttpContext.RequestServices.GetRequiredService<IDirectoryChangeNotifier>()
                    .NotifyAsync(context.HttpContext.RequestAborted);
            return result;
        });

        dir.MapGet("", async (IPopDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var groups = await db.DirGroups
                .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
                .Include(g => g.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
                .OrderBy(g => g.SortOrder).ToListAsync(ct);
            return groups.Select(g => new AdminDirGroupRow(
                g.Id, g.Name, g.ParentId, g.SortOrder, g.IsActive,
                g.Members.Select(m => new AdminDirGroupMember(
                    m.User.Nik, m.User.Name, m.User.Position,
                    m.User.Department?.Name, m.User.Site?.Name,
                    PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
                )).ToArray()
            )).ToArray();
        });

        dir.MapPost("", async (UpsertDirGroupRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { code = "invalid_request", message = "Name is required." });
            var group = new DirGroup { Id = Guid.NewGuid(), Name = name, SortOrder = req.SortOrder ?? 0 };
            var parentError = await ApplyDirGroupParent(group, req.ParentId, db, ct);
            if (parentError is not null) return parentError;
            db.DirGroups.Add(group);
            Audit(db, CallerNik(http), "admin.dirgroup.create", group.Id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/dir-groups/{group.Id}", new { id = group.Id });
        });

        dir.MapPatch("/{id:guid}", async (Guid id, UpsertDirGroupRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var group = await db.DirGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            if (req.Name?.Trim() is { Length: > 0 } name) group.Name = name;
            if (req.IsActive is { } active) group.IsActive = active;
            if (req.SortOrder is { } sort) group.SortOrder = sort;
            var parentError = await ApplyDirGroupParent(group, req.ParentId, db, ct);
            if (parentError is not null) return parentError;
            Audit(db, CallerNik(http), "admin.dirgroup.edit", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        dir.MapDelete("/{id:guid}", async (Guid id, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var group = await db.DirGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            // Promote children to the deleted group's parent, then delete.
            await db.DirGroups.Where(g => g.ParentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ParentId, group.ParentId), ct);
            db.DirGroups.Remove(group);
            Audit(db, CallerNik(http), "admin.dirgroup.delete", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        dir.MapPost("/{id:guid}/members", async (Guid id, DirGroupMemberRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var group = await db.DirGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == req.Nik.Trim(), ct);
            if (user is null) return Results.NotFound(new { code = "unknown_user", message = "User does not exist." });
            if (await db.UserDirGroups.AnyAsync(x => x.DirGroupId == id && x.UserId == user.Id, ct))
                return Results.Ok(new { id }); // already a member — no-op
            db.UserDirGroups.Add(new UserDirGroup { UserId = user.Id, DirGroupId = id });
            Audit(db, CallerNik(http), "admin.dirgroup.member.add", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        dir.MapDelete("/{id:guid}/members/{nik}", async (Guid id, string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            await db.UserDirGroups.Where(x => x.DirGroupId == id && x.UserId == user.Id)
                .ExecuteDeleteAsync(ct);
            Audit(db, CallerNik(http), "admin.dirgroup.member.remove", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        // Add by display name — picker resolves the exact user, so NIK is not required.
        dir.MapPost("/{id:guid}/members/by-name", async (Guid id, DirGroupMemberRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var group = await db.DirGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Name == req.Nik.Trim(), ct);
            if (user is null)
                return Results.NotFound(new { code = "unknown_user", message = "User does not exist." });
            if (await db.UserDirGroups.AnyAsync(x => x.DirGroupId == id && x.UserId == user.Id, ct))
                return Results.Ok(new { id }); // already a member — no-op
            db.UserDirGroups.Add(new UserDirGroup { UserId = user.Id, DirGroupId = id });
            Audit(db, CallerNik(http), "admin.dirgroup.member.add", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        // Full member-list replace (members are managed as a single set from the editor).
        dir.MapPatch("/{id:guid}/members", async (Guid id, DirGroupMembersRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var group = await db.DirGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
            if (group is null) return Results.NotFound();
            var wanted = req.Niks.Distinct().ToArray();
            var users = await db.Users.AsNoTracking()
                .Where(u => wanted.Contains(u.Nik)).Select(u => u.Id).ToListAsync(ct);
            if (users.Count != wanted.Length)
                return Results.BadRequest(new { code = "unknown_user", message = "One or more NIKs do not exist." });
            await db.UserDirGroups.Where(x => x.DirGroupId == id).ExecuteDeleteAsync(ct);
            foreach (var userId in users) db.UserDirGroups.Add(new UserDirGroup { UserId = userId, DirGroupId = id });
            Audit(db, CallerNik(http), "admin.dirgroup.members.replace", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        var orgNodes = app.MapGroup("/api/admin/org-nodes").RequireAuthorization("AdminOnly");
        orgNodes.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (context.HttpContext.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE"
                && result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                await context.HttpContext.RequestServices.GetRequiredService<IDirectoryChangeNotifier>()
                    .NotifyAsync(context.HttpContext.RequestAborted);
            return result;
        });

        orgNodes.MapGet("", async (IPopDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var nodes = await db.OrgNodes
                .Include(n => n.Members).ThenInclude(m => m.User).ThenInclude(u => u.Department)
                .Include(n => n.Members).ThenInclude(m => m.User).ThenInclude(u => u.Site)
                .OrderBy(n => n.SortOrder).ToListAsync(ct);
            return nodes.Select(n => new AdminOrgNodeRow(
                n.Id, n.Name, n.Title, n.ParentId, n.DepartmentId, n.SortOrder, n.IsActive,
                n.Members.Select(m => new AdminOrgNodeMember(
                    m.User.Nik, m.User.Name, m.User.Position,
                    m.User.Department?.Name, m.User.Site?.Name,
                    PresenceStatus.ComputeStatus(m.User.PresenceLastSeenAt, m.User.PresenceManualStatus, now)
                )).ToArray()
            )).ToArray();
        });

        orgNodes.MapPost("", async (UpsertOrgNodeRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var name = req.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { code = "invalid_request", message = "Name is required." });
            var node = new OrgNode { Id = Guid.NewGuid(), Name = name, Title = req.Title?.Trim(), SortOrder = req.SortOrder ?? 0 };
            var parentError = await ApplyOrgNodeParent(node, req.ParentId, db, ct);
            if (parentError is not null) return parentError;
            if (req.DepartmentId is { } deptId)
            {
                if (!await db.Departments.AnyAsync(d => d.Id == deptId && d.IsActive, ct))
                    return Results.BadRequest(new { code = "unknown_department", message = "Department does not exist or is inactive." });
                node.DepartmentId = deptId;
            }
            db.OrgNodes.Add(node);
            Audit(db, CallerNik(http), "admin.orgnode.create", node.Id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/admin/org-nodes/{node.Id}", new { id = node.Id });
        });

        orgNodes.MapPatch("/{id:guid}", async (Guid id, UpsertOrgNodeRequest req, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var node = await db.OrgNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (node is null) return Results.NotFound();
            if (req.Name?.Trim() is { Length: > 0 } name) node.Name = name;
            if (req.Title is not null) node.Title = req.Title.Trim();
            if (req.IsActive is { } active) node.IsActive = active;
            if (req.SortOrder is { } sort) node.SortOrder = sort;
            var parentError = await ApplyOrgNodeParent(node, req.ParentId, db, ct);
            if (parentError is not null) return parentError;
            if (req.DepartmentId is { } deptId)
            {
                if (!await db.Departments.AnyAsync(d => d.Id == deptId && d.IsActive, ct))
                    return Results.BadRequest(new { code = "unknown_department", message = "Department does not exist or is inactive." });
                node.DepartmentId = deptId;
            }
            if (req.DepartmentId == null) node.DepartmentId = null;
            Audit(db, CallerNik(http), "admin.orgnode.edit", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        orgNodes.MapDelete("/{id:guid}", async (Guid id, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var node = await db.OrgNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (node is null) return Results.NotFound();
            // Promote children to the deleted node's parent, then delete.
            await db.OrgNodes.Where(n => n.ParentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ParentId, node.ParentId), ct);
            db.OrgNodes.Remove(node);
            Audit(db, CallerNik(http), "admin.orgnode.delete", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        orgNodes.MapPost("/{id:guid}/members", async (Guid id, DirGroupMemberRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var node = await db.OrgNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (node is null) return Results.NotFound();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == req.Nik.Trim(), ct);
            if (user is null) return Results.NotFound(new { code = "unknown_user", message = "User does not exist." });
            if (await db.UserOrgNodes.AnyAsync(x => x.OrgNodeId == id && x.UserId == user.Id, ct))
                return Results.Ok(new { id }); // already a member — no-op
            db.UserOrgNodes.Add(new UserOrgNode { UserId = user.Id, OrgNodeId = id });
            Audit(db, CallerNik(http), "admin.orgnode.member.add", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        orgNodes.MapDelete("/{id:guid}/members/{nik}", async (Guid id, string nik, IPopDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik, ct);
            if (user is null) return Results.NotFound();
            await db.UserOrgNodes.Where(x => x.OrgNodeId == id && x.UserId == user.Id)
                .ExecuteDeleteAsync(ct);
            Audit(db, CallerNik(http), "admin.orgnode.member.remove", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        orgNodes.MapPost("/{id:guid}/members/by-name", async (Guid id, DirGroupMemberRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var node = await db.OrgNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (node is null) return Results.NotFound();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Name == req.Nik.Trim(), ct);
            if (user is null)
                return Results.NotFound(new { code = "unknown_user", message = "User does not exist." });
            if (await db.UserOrgNodes.AnyAsync(x => x.OrgNodeId == id && x.UserId == user.Id, ct))
                return Results.Ok(new { id }); // already a member — no-op
            db.UserOrgNodes.Add(new UserOrgNode { UserId = user.Id, OrgNodeId = id });
            Audit(db, CallerNik(http), "admin.orgnode.member.add", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        orgNodes.MapPatch("/{id:guid}/members", async (Guid id, DirGroupMembersRequest req,
            IPopDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var node = await db.OrgNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (node is null) return Results.NotFound();
            var wanted = req.Niks.Distinct().ToArray();
            var users = await db.Users.AsNoTracking()
                .Where(u => wanted.Contains(u.Nik)).Select(u => u.Id).ToListAsync(ct);
            if (users.Count != wanted.Length)
                return Results.BadRequest(new { code = "unknown_user", message = "One or more NIKs do not exist." });
            await db.UserOrgNodes.Where(x => x.OrgNodeId == id).ExecuteDeleteAsync(ct);
            foreach (var userId in users) db.UserOrgNodes.Add(new UserOrgNode { UserId = userId, OrgNodeId = id });
            Audit(db, CallerNik(http), "admin.orgnode.members.replace", id.ToString(), DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id });
        });

        return app;
    }

    private static async Task<IResult?> ApplyPlacement(User user, string? departmentCode,
        string? siteCode, IPopDbContext db, CancellationToken ct)
    {
        Guid? departmentId = null;
        Guid? siteId = null;
        if (!string.IsNullOrWhiteSpace(departmentCode))
        {
            var dept = await db.Departments.SingleOrDefaultAsync(
                d => d.Code == departmentCode.Trim() && d.IsActive, ct);
            if (dept is null)
                return Results.BadRequest(new { code = "unknown_department", message = "Department does not exist or is inactive." });
            departmentId = dept.Id;
        }
        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            var site = await db.Sites.SingleOrDefaultAsync(
                s => s.Code == siteCode.Trim() && s.IsActive, ct);
            if (site is null)
                return Results.BadRequest(new { code = "unknown_site", message = "Site does not exist or is inactive." });
            siteId = site.Id;
        }
        user.DepartmentId = departmentId;
        user.SiteId = siteId;
        return null;
    }

    private static async Task<IResult?> ApplyParent(Department dept, string? parentCode,
        IPopDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentCode))
        {
            dept.ParentId = null;
            return null;
        }
        var parent = await db.Departments.SingleOrDefaultAsync(
            d => d.Code == parentCode.Trim() && d.IsActive, ct);
        if (parent is null || parent.Id == dept.Id)
            return Results.BadRequest(new { code = "invalid_parent" });
        var seen = new HashSet<Guid> { dept.Id };
        var current = parent;
        while (true)
        {
            if (!seen.Add(current.Id)) return Results.BadRequest(new { code = "department_cycle" });
            if (current.ParentId is not Guid parentId) break;
            current = await db.Departments.SingleAsync(d => d.Id == parentId, ct);
        }
        dept.ParentId = parent.Id;
        return null;
    }

    // Resolves the Division's parent Department/Site, rejecting inactive parents.
    internal static async Task<IResult?> ApplyDivisionParents(Division div, string? departmentCode,
        string? siteCode, IPopDbContext db, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(departmentCode))
        {
            var dept = await db.Departments.SingleOrDefaultAsync(
                d => d.Code == departmentCode.Trim() && d.IsActive, ct);
            if (dept is null)
                return Results.BadRequest(new { code = "unknown_department", message = "Department does not exist or is inactive." });
            div.DepartmentId = dept.Id;
        }
        else
        {
            div.DepartmentId = null;
        }
        if (!string.IsNullOrWhiteSpace(siteCode))
        {
            var site = await db.Sites.SingleOrDefaultAsync(
                s => s.Code == siteCode.Trim() && s.IsActive, ct);
            if (site is null)
                return Results.BadRequest(new { code = "unknown_site", message = "Site does not exist or is inactive." });
            div.SiteId = site.Id;
        }
        else
        {
            div.SiteId = null;
        }
        return null;
    }

    private static async Task<IResult?> ApplyDirGroupParent(DirGroup group, Guid? parentId,
        IPopDbContext db, CancellationToken ct)
    {
        if (parentId is null)
        {
            group.ParentId = null;
            return null;
        }
        var parent = await db.DirGroups.SingleOrDefaultAsync(g => g.Id == parentId && g.IsActive, ct);
        if (parent is null || parent.Id == group.Id)
            return Results.BadRequest(new { code = "invalid_parent" });
        var seen = new HashSet<Guid> { group.Id };
        var current = parent;
        while (true)
        {
            if (!seen.Add(current.Id)) return Results.BadRequest(new { code = "dirgroup_cycle" });
            if (current.ParentId is not Guid parentGuid) break;
            current = await db.DirGroups.SingleAsync(g => g.Id == parentGuid, ct);
        }
        group.ParentId = parent.Id;
        return null;
    }

    private static async Task<IResult?> ApplyOrgNodeParent(OrgNode node, Guid? parentId,
        IPopDbContext db, CancellationToken ct)
    {
        if (parentId is null)
        {
            node.ParentId = null;
            return null;
        }
        var parent = await db.OrgNodes.SingleOrDefaultAsync(n => n.Id == parentId && n.IsActive, ct);
        if (parent is null || parent.Id == node.Id)
            return Results.BadRequest(new { code = "invalid_parent" });
        var seen = new HashSet<Guid> { node.Id };
        var current = parent;
        while (true)
        {
            if (!seen.Add(current.Id)) return Results.BadRequest(new { code = "orgnode_cycle" });
            if (current.ParentId is not Guid parentGuid) break;
            current = await db.OrgNodes.SingleAsync(n => n.Id == parentGuid, ct);
        }
        node.ParentId = parent.Id;
        return null;
    }

    private static string? CallerNik(HttpContext http) => http.User.FindFirst("nik")?.Value;

    private static Task RevokeSessions(IPopDbContext db, Guid userId, DateTime now,
        CancellationToken ct) => db.AuthSessions
        .Where(s => s.UserId == userId && s.RevokedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);

    internal static void Audit(IPopDbContext db, string? actor, string action,
        string entityId, DateTime now, string entity = "User") => db.AuditLogs.Add(new AuditLog
        {
            ActorNik = actor, Action = action, Entity = entity, EntityId = entityId,
            DetailJson = "{}", CreatedAt = now
        });

    public record AdminUserRow(string Nik, string Name, string Email, string? Position,
        string? Location, string? Department, string? DepartmentCode, string? Site,
        string? SiteCode, bool IsActive, bool IsManuallyEdited, string[] Roles,
        string Status, DateTime UpdatedAt);
    public record AdminUserStats(int Total, int Active, int Admins, int Online);
    public record AdminSessionRow(Guid Id, string Nik, string Name, string? DeviceFingerprint,
        string? IpAddress, DateTime CreatedAt, DateTime ExpiresAt, bool IsCurrent = false);
    public record RevokeOthersRequest(string? CurrentRefreshToken = null, Guid? CurrentSessionId = null);
    public record CreateUserRequest(string Nik, string Name, string Email, string? Position,
        string? Location, string? DepartmentCode = null, string? SiteCode = null);
    public record EditUserRequest(string? Name, string? Email, string? Position, string? Location,
        string? DepartmentCode = null, string? SiteCode = null);
    public record SetRolesRequest(string[] Roles);
    public record OrgDepartmentRow(Guid Id, string Code, string Name, string? ParentCode,
        bool IsActive, int MemberCount, int ActiveMemberCount);
    public record OrgSiteRow(Guid Id, string Code, string Name, bool IsActive,
        int MemberCount, int ActiveMemberCount);
    public record UpsertDepartmentRequest(string Code, string? Name, bool? IsActive,
        string? ParentCode = null);
    public record UpsertSiteRequest(string Code, string? Name, bool? IsActive);
    public record SetPlacementRequest(string? DepartmentCode, string? SiteCode);
    public record AdminDivisionRow(Guid Id, string Code, string Name, string? DepartmentCode,
        string? DepartmentName, string? SiteCode, string? SiteName, bool IsActive,
        int MemberCount, int ActiveMemberCount);
    public record UpsertDivisionRequest(string Code, string? Name, string? DepartmentCode,
        string? SiteCode, bool? IsActive);
    public record SetUserDivisionRequest(string DivisionCode);
    public record UpsertDirGroupRequest(string? Name, Guid? ParentId, int? SortOrder, bool? IsActive);
    public record DirGroupMemberRequest(string Nik);
    public record DirGroupMembersRequest(string[] Niks);
    public record AdminDirGroupRow(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive,
        AdminDirGroupMember[] Members);
    public record AdminDirGroupMember(string Nik, string Name, string? Position, string? Department,
        string? Site, string Status);
    public record UpsertOrgNodeRequest(string? Name, string? Title, Guid? ParentId, Guid? DepartmentId, int? SortOrder, bool? IsActive);
    public record AdminOrgNodeRow(Guid Id, string Name, string? Title, Guid? ParentId, Guid? DepartmentId, int SortOrder,
        bool IsActive, AdminOrgNodeMember[] Members);
    public record AdminOrgNodeMember(string Nik, string Name, string? Position, string? Department,
        string? Site, string Status);
}
