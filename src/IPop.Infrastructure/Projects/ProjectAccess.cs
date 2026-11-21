using IPop.Application.Projects;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Shared project access rules for the stage 4 services (same rules as ProjectService / ProjectStatusService).
internal static class ProjectAccess
{
    public static async Task<bool> CanViewAsync(IPopDbContext db, Guid projectId, Guid userId, CancellationToken ct) =>
        await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct)
        || await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

    /// Owner or manager.
    public static Task<bool> CanManageAsync(IPopDbContext db, Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId && (m.Role == "owner" || m.Role == "manager"), ct);

    /// Owner, manager or member (not viewer): who may edit tasks and toggle assignees.
    public static Task<bool> CanMutateAsync(IPopDbContext db, Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId
            && (m.Role == "owner" || m.Role == "manager" || m.Role == "member"), ct);

    public static Task<bool> ProjectExistsAsync(IPopDbContext db, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive, ct);

    /// Monday of the week containing the date.
    public static DateTime WeekStart(DateTime d)
    {
        var day = DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        var back = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-back);
    }

    public static ProjectOpResult<T> Fail<T>(string code) => new(default, code);
    public static ProjectOpResult<T> Ok<T>(T value) => new(value, null);
}
