using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IPop.Tests;

/// SQLite in-memory — a real relational engine, so ExecuteUpdateAsync works
/// (the InMemory provider would throw on it). The connection must stay open
/// for the database to live.
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<IPopDbContext> _options;
    public IPopDbContext Db { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<IPopDbContext>().UseSqlite(_conn).Options;
        Db = new IPopDbContext(_options);
        Db.Database.EnsureCreated();   // applies the HasData seed for HrisSyncState (Id=1)
    }

    /// ExecuteUpdateAsync writes behind the change tracker's back, so any
    /// read after one must use a fresh context or it sees stale tracked state.
    /// Production gets this for free via one scoped context per request.
    public IPopDbContext Verify() => new(_options);

    public User AddUser(string nik, bool active = true, string? passwordHash = null, bool mustChange = true)
    {
        var u = new User
        {
            Id = Guid.NewGuid(), Nik = nik, Name = $"User {nik}", Email = $"{nik}@t.co",
            PasswordHash = passwordHash, MustChangePassword = mustChange, IsActive = active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.Users.Add(u);
        Db.SaveChanges();
        return u;
    }

    // ── Helpers per design Testing Strategy ──

    public ChatRoom AddRoom(string type = "dm", string? name = null, string? description = null)
    {
        var r = new ChatRoom
        {
            Id = Guid.NewGuid(), Type = type, Name = name, Description = description,
            IsActive = true, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow,
        };
        Db.ChatRooms.Add(r);
        Db.SaveChanges();
        return r;
    }

    public ChatRoomMember AddMember(Guid roomId, Guid userId, string role = "member", DateTime? joinedAt = null, DateTime? leftAt = null)
    {
        var m = new ChatRoomMember
        {
            Id = Guid.NewGuid(), RoomId = roomId, UserId = userId, Role = role,
            JoinedAt = joinedAt ?? DateTime.UtcNow, LeftAt = leftAt,
        };
        Db.ChatRoomMembers.Add(m);
        Db.SaveChanges();
        return m;
    }

    public ChatMessage AddMessage(Guid roomId, Guid senderId, string content = "test", string type = "text", DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var msg = new ChatMessage
        {
            Id = Guid.NewGuid(), RoomId = roomId, SenderId = senderId,
            Type = type, Content = content, IdempotencyKey = Guid.NewGuid(), CreatedAt = now,
        };
        Db.ChatMessages.Add(msg);
        var room = Db.ChatRooms.Find(roomId);
        if (room != null) room.LastMessageAt = now;
        Db.SaveChanges();
        return msg;
    }

    public ChatRoomUserSettings AddSettings(Guid userId, Guid roomId, Action<ChatRoomUserSettings>? configure = null)
    {
        var s = new ChatRoomUserSettings { UserId = userId, RoomId = roomId };
        configure?.Invoke(s);
        Db.ChatRoomUserSettings.Add(s);
        Db.SaveChanges();
        return s;
    }

    public ChatService NewChatService(INotificationGateway? notifications = null)
        => new(Verify());

    // ── Notification Gateway helpers ──
    public NotificationGatewayClient AddNotificationClient(
        string clientId, string secretHash, string? allowedIps = null,
        string? allowedEventTypes = null, int rateLimit = 0, int maxRecipients = 1000, bool active = true)
    {
        var c = new NotificationGatewayClient
        {
            Id = Guid.NewGuid(), ClientId = clientId, SecretHash = secretHash,
            AllowedIps = allowedIps, AllowedEventTypes = allowedEventTypes,
            RateLimitPerSecond = rateLimit, MaxRecipients = maxRecipients, IsActive = active,
            CreatedAt = DateTime.UtcNow,
        };
        Db.NotificationGatewayClients.Add(c);
        Db.SaveChanges();
        return c;
    }

    // ── News helpers ──
    public Department EnsureDepartment(string code, string name)
    {
        var d = Db.Departments.FirstOrDefault(x => x.Code == code);
        if (d is not null) return d;
        d = new Department { Id = Guid.NewGuid(), Code = code, Name = name, IsActive = true };
        Db.Departments.Add(d); Db.SaveChanges(); return d;
    }
    public void AssignDepartment(Guid userId, Guid deptId)
    {
        var u = Db.Users.Find(userId); if (u is null) return;
        u.DepartmentId = deptId; Db.SaveChanges();
    }
    public Role EnsureRole(string name, Guid? parentId = null)
    {
        var r = Db.Roles.FirstOrDefault(x => x.Name == name);
        if (r is not null) return r;
        r = new Role { Id = Guid.NewGuid(), Name = name, ParentRoleId = parentId };
        Db.Roles.Add(r); Db.SaveChanges(); return r;
    }
    public void AssignRole(Guid userId, string roleName)
    {
        var role = EnsureRole(roleName);
        if (Db.UserRoles.Any(x => x.UserId == userId && x.RoleId == role.Id)) return;
        Db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id }); Db.SaveChanges();
    }
    public User AddAdmin(string nik)
    {
        var u = AddUser(nik); AssignRole(u.Id, "Admin"); return u;
    }
    /// HR as seeded in production: the HR role carries news.publish (the news manager gate).
    public User AddHr(string nik)
    {
        var u = AddUser(nik); AssignRole(u.Id, "HR");
        var role = EnsureRole("HR");
        var perm = Db.Permissions.FirstOrDefault(p => p.Code == "news.publish");
        if (perm is null)
        {
            perm = new Permission { Id = Guid.NewGuid(), Code = "news.publish", Description = "Publish news" };
            Db.Permissions.Add(perm);
        }
        if (!Db.RolePermissions.Any(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id))
            Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        Db.SaveChanges();
        return u;
    }

    // ── Project helpers (ipop-23 P2-1: News project targeting) ──
    public Project AddProject(string name, Guid ownerId, Guid? creatorId = null)
    {
        var p = new Project
        {
            Id = Guid.NewGuid(), Name = name, Year = DateTime.UtcNow.Year, OwnerId = ownerId,
            Status = "active", Progress = 0, IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        Db.Projects.Add(p);
        Db.ProjectGroups.Add(new ProjectGroup
        {
            Id = Guid.NewGuid(), ProjectId = p.Id, Name = "Tasks", SortOrder = 0
        });
        var actor = creatorId ?? ownerId;
        Db.ProjectMembers.Add(new ProjectMember { ProjectId = p.Id, UserId = actor, Role = "manager", JoinedAt = DateTime.UtcNow });
        if (ownerId != actor)
            Db.ProjectMembers.Add(new ProjectMember { ProjectId = p.Id, UserId = ownerId, Role = "owner", JoinedAt = DateTime.UtcNow });
        Db.SaveChanges();
        return p;
    }

    public void AddProjectMember(Guid projectId, Guid userId, string role = "member")
    {
        Db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = userId, Role = role, JoinedAt = DateTime.UtcNow });
        Db.SaveChanges();
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }

    // ── Internal helpers ──
    internal static ChatMessage Msg(Guid roomId, Guid senderId, string type, string content)
        => new() { Id = Guid.NewGuid(), RoomId = roomId, SenderId = senderId, Type = type, Content = content, IdempotencyKey = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
}
