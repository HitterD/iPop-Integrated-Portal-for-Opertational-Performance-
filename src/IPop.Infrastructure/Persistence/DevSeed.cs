using IPop.Application.Auth;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Persistence;

/// Dev-only seed: roles + the admin account. The old one-user default-password seed below
/// stays for exercising /api/auth/login before HRIS sync exists, but the admin account is
/// seeded independently — it must appear even when HRIS has already populated Users.
public static class DevSeed
{
    public static async Task EnsureSeededAsync(IPopDbContext db, IPasswordHasher hasher, CancellationToken ct = default)
    {
        // RBAC seed: Admin + Employee always exist; the CTO NIK gets Admin so /api/admin/*
        // is reachable out of the box. New synced users land role-less → portal Employee
        // baseline is implicit (no claim needed for non-admin routes).
        var admin = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Admin", ct);
        if (admin is null)
        {
            admin = new Role { Id = Guid.NewGuid(), Name = "Admin" };
            db.Roles.Add(admin);
            db.Roles.Add(new Role { Id = Guid.NewGuid(), Name = "Employee" });
            await db.SaveChangesAsync(ct);
        }

        var permissionCodes = new (string Code, string Description)[]
        {
            ("directory.read", "View the employee directory"),
            ("files.read", "Browse and access files on the NAS"),
            ("chat.read", "View chat conversations and message history"),
            ("chat.send", "Send messages, reactions, and attachments in chat"),
            ("chat.admin", "Manage group settings, pin messages, send emergency broadcasts"),
            ("calls.use", "Start, accept, and manage calls"),
            ("mail.read", "View and read internal mail"),
            ("mail.send", "Compose and send internal mail"),
            ("news.read", "Browse and read corporate news, announcements, and memos"),
            ("news.write", "Create and edit news/announcement/memo posts"),
            ("news.approve", "Approve or reject news submissions and publish/unpublish"),
            ("projects.read", "Browse and view projects and tasks"),
            ("projects.write", "Create and update projects and tasks"),
            ("projects.admin", "Administer and delete projects"),
            ("admin.users", "Manage user accounts"),
            ("admin.organization", "Manage departments and sites"),
            ("admin.audit", "View audit records"),
            ("admin.sessions", "Manage active sessions"),
            ("admin.rbac", "Manage roles and permissions"),
        };
        var permissions = await db.Permissions.ToListAsync(ct);
        foreach (var (code, description) in permissionCodes.Where(x => permissions.All(p => p.Code != x.Code)))
        {
            var permission = new Permission { Id = Guid.NewGuid(), Code = code, Description = description };
            db.Permissions.Add(permission);
            if (code is "directory.read" or "files.read" or "chat.read" or "chat.send" or "calls.use" or "mail.read" or "mail.send" or "news.read" or "news.write" or "projects.read")
                db.RolePermissions.Add(new RolePermission { RoleId = (await db.Roles.SingleAsync(r => r.Name == "Employee", ct)).Id, PermissionId = permission.Id });
            db.RolePermissions.Add(new RolePermission { RoleId = admin.Id, PermissionId = permission.Id });
        }
        await db.SaveChangesAsync(ct);
        var cto = await db.Users.FirstOrDefaultAsync(u => u.Nik == "20080115", ct);
        if (cto is not null && !await db.UserRoles.AnyAsync(ur => ur.UserId == cto.Id && ur.RoleId == admin.Id, ct))
        {
            db.UserRoles.Add(new UserRole { UserId = cto.Id, RoleId = admin.Id });
            await db.SaveChangesAsync(ct);
        }

        // The admin account stands alone from the HRIS feed: a fixed NIK outside the HRIS
        // range keeps it collision-proof, and it is seeded regardless of how many users exist.
        // Reset to Admin123! for convenience in this environment. MustChangePassword is off
        // so you can log in directly without being forced to change it.
        var adminUser = await db.Users.FirstOrDefaultAsync(u => u.Nik == "ipopadmin", ct);
        var desiredAdminPassword = "Admin123!";
        if (adminUser is null)
        {
            var now = DateTime.UtcNow;
            adminUser = new User
            {
                Id = Guid.NewGuid(),
                Nik = "ipopadmin",
                Name = "IPop Administrator",
                Email = "ipopadmin@local",
                PasswordHash = hasher.Hash(desiredAdminPassword),
                MustChangePassword = false,
                IsManuallyEdited = true,   // never let a future sync touch this account
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(adminUser);
            db.UserRoles.Add(new UserRole { UserId = adminUser.Id, RoleId = admin.Id });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // Ensure existing seeded admin has the reset password, no forced change, is not locked,
            // and is active. This rescues accounts that got locked by bad login attempts during dev.
            adminUser.PasswordHash = hasher.Hash(desiredAdminPassword);
            adminUser.MustChangePassword = false;
            adminUser.IsActive = true;
            adminUser.FailedLoginCount = 0;
            adminUser.LockedUntil = null;
            adminUser.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (await db.Users.AnyAsync(ct)) return;

        var dept = new Department { Id = Guid.NewGuid(), Code = "IT-INFRA", Name = "Infrastructure" };
        db.Departments.Add(dept);
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Nik = "20140312",
            Name = "Bagas Arif",
            Email = "bagas.arif@company.com",
            Position = "Infrastructure Engineer",
            DepartmentId = dept.Id,
            PasswordHash = null,               // spec §3.3 — null + MustChangePassword = default-password flow
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
