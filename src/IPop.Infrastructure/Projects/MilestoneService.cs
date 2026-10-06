using IPop.Application;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Project milestones: a dated checkpoint that groups several tasks (ProjectMilestoneTask).
/// Progress = linked tasks done / linked; status is computed on read (MilestoneStatus.Of).
/// Project members only, as for every other project read/write.
public sealed class MilestoneService(IPopDbContext db) : IMilestoneService
{
    public const int MaxTitle = 300;
    public const int MaxLinkedTasks = 500;

    public async Task<MilestoneResult<IReadOnlyList<MilestoneDto>>> ListAsync(Guid actorId, Guid projectId, DateTime today, CancellationToken ct)
    {
        if (await AccessErrorAsync(actorId, projectId, ct) is { } err) return Fail<IReadOnlyList<MilestoneDto>>(err);
        var ids = await db.ProjectMilestones.AsNoTracking()
            .Where(m => m.ProjectId == projectId).OrderBy(m => m.DueDate).ThenBy(m => m.CreatedAt)
            .Select(m => m.Id).ToListAsync(ct);
        return new(await ProjectAsync(ids, today, ct), null);
    }

    public async Task<MilestoneResult<MilestoneDto>> CreateAsync(Guid actorId, Guid projectId, CreateMilestoneRequest req, DateTime today, CancellationToken ct)
    {
        if (await AccessErrorAsync(actorId, projectId, ct) is { } err) return Fail<MilestoneDto>(err);
        var title = req.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > MaxTitle) return Fail<MilestoneDto>("invalid_title");

        var taskIds = Merge(req.TaskIds, req.TaskId);
        if (!await TasksBelongAsync(projectId, taskIds, ct)) return Fail<MilestoneDto>("task_not_found");
        var ownerId = req.OwnerId is { } o && o != Guid.Empty ? o : (Guid?)null;
        if (ownerId is { } oid && !await IsMemberAsync(projectId, oid, ct)) return Fail<MilestoneDto>("invalid_owner");

        var m = new ProjectMilestone
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Title = title,
            DueDate = UtcDates.AsUtcDate(req.DueDate), TaskId = taskIds.Count > 0 ? taskIds[0] : null,
            OwnerId = ownerId, CreatedAt = DateTime.UtcNow
        };
        db.ProjectMilestones.Add(m);
        foreach (var tid in taskIds) db.ProjectMilestoneTasks.Add(new ProjectMilestoneTask { MilestoneId = m.Id, TaskId = tid });
        await db.SaveChangesAsync(ct);
        return new((await ProjectAsync([m.Id], today, ct))[0], null);
    }

    public async Task<MilestoneResult<MilestoneDto>> UpdateAsync(Guid actorId, Guid projectId, Guid milestoneId, UpdateMilestoneRequest req, DateTime today, CancellationToken ct)
    {
        if (await AccessErrorAsync(actorId, projectId, ct) is { } err) return Fail<MilestoneDto>(err);
        var m = await db.ProjectMilestones.FirstOrDefaultAsync(x => x.Id == milestoneId && x.ProjectId == projectId, ct);
        if (m is null) return Fail<MilestoneDto>("milestone_not_found");

        if (req.Title is not null)
        {
            var title = req.Title.Trim();
            if (title.Length == 0 || title.Length > MaxTitle) return Fail<MilestoneDto>("invalid_title");
            m.Title = title;
        }
        if (req.DueDate is { } dd) m.DueDate = UtcDates.AsUtcDate(dd);
        if (req.ClearOwner == true) m.OwnerId = null;
        else if (req.OwnerId is { } oid && oid != Guid.Empty)
        {
            if (!await IsMemberAsync(projectId, oid, ct)) return Fail<MilestoneDto>("invalid_owner");
            m.OwnerId = oid;
        }

        // TaskIds replaces the set; the legacy TaskId / ClearTask pair maps onto it.
        IReadOnlyList<Guid>? newSet = req.TaskIds is not null ? Merge(req.TaskIds, null)
            : req.TaskId is { } one ? [one]
            : req.ClearTask == true ? [] : null;
        if (newSet is not null)
        {
            if (!await TasksBelongAsync(projectId, newSet, ct)) return Fail<MilestoneDto>("task_not_found");
            var existing = await db.ProjectMilestoneTasks.IgnoreQueryFilters().Where(x => x.MilestoneId == m.Id).ToListAsync(ct);
            db.ProjectMilestoneTasks.RemoveRange(existing.Where(x => !newSet.Contains(x.TaskId)));
            foreach (var tid in newSet.Where(t => existing.All(x => x.TaskId != t)))
                db.ProjectMilestoneTasks.Add(new ProjectMilestoneTask { MilestoneId = m.Id, TaskId = tid });
            m.TaskId = newSet.Count > 0 ? newSet[0] : null;
        }
        if (req.Achieved == true) m.AchievedAt ??= DateTime.UtcNow;
        if (req.Achieved == false) m.AchievedAt = null;

        await db.SaveChangesAsync(ct);
        return new((await ProjectAsync([m.Id], today, ct))[0], null);
    }

    public Task<MilestoneResult<MilestoneDto>> SetAchievedAsync(Guid actorId, Guid projectId, Guid milestoneId, bool achieved, DateTime today, CancellationToken ct)
        => UpdateAsync(actorId, projectId, milestoneId, new UpdateMilestoneRequest(Achieved: achieved), today, ct);

    public async Task<MilestoneResult<bool>> DeleteAsync(Guid actorId, Guid projectId, Guid milestoneId, CancellationToken ct)
    {
        if (await AccessErrorAsync(actorId, projectId, ct) is { } err) return Fail<bool>(err);
        var m = await db.ProjectMilestones.FirstOrDefaultAsync(x => x.Id == milestoneId && x.ProjectId == projectId, ct);
        if (m is null) return Fail<bool>("milestone_not_found");
        var links = await db.ProjectMilestoneTasks.IgnoreQueryFilters().Where(x => x.MilestoneId == m.Id).ToListAsync(ct);
        db.ProjectMilestoneTasks.RemoveRange(links);
        db.ProjectMilestones.Remove(m);
        await db.SaveChangesAsync(ct);
        return new(true, null);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<MilestoneDto>> ProjectAsync(IReadOnlyList<Guid> ids, DateTime today, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var heads = await db.ProjectMilestones.AsNoTracking().Where(m => ids.Contains(m.Id))
            .Select(m => new { m.Id, m.Title, m.DueDate, m.AchievedAt, m.OwnerId,
                OwnerName = m.Owner != null ? m.Owner.Name : null, OwnerNik = m.Owner != null ? m.Owner.Nik : null })
            .ToListAsync(ct);
        // The query filter drops links to soft-deleted tasks.
        var links = await db.ProjectMilestoneTasks.AsNoTracking().Where(l => ids.Contains(l.MilestoneId))
            .Select(l => new { l.MilestoneId, l.TaskId, l.Task.Status, l.Task.CompletedAt, l.Task.SortOrder })
            .ToListAsync(ct);
        var byId = heads.ToDictionary(h => h.Id);
        return ids.Where(byId.ContainsKey).Select(id =>
        {
            var h = byId[id];
            var mine = links.Where(l => l.MilestoneId == id).OrderBy(l => l.SortOrder).ToList();
            var done = mine.Count(l => l.Status == "done");
            var status = MilestoneStatus.Of(h.DueDate, h.AchievedAt, mine.Count, done, today);
            var achievedAt = h.AchievedAt;
            if (achievedAt is null && status == MilestoneStatus.Achieved)
                achievedAt = mine.Select(l => l.CompletedAt).Max();
            return new MilestoneDto(h.Id, h.Title, Utc(h.DueDate), Utc(achievedAt), mine.Select(l => l.TaskId).ToList(),
                mine.Count, done, status, h.AchievedAt is not null, h.OwnerId, h.OwnerName, h.OwnerNik);
        }).ToList();
    }

    private async Task<string?> AccessErrorAsync(Guid actorId, Guid projectId, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct)) return "project_not_found";
        return await db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId, ct)
            ? null : "forbidden";
    }

    private Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);

    private async Task<bool> TasksBelongAsync(Guid projectId, IReadOnlyList<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return true;
        if (taskIds.Count > MaxLinkedTasks) return false;
        var found = await db.ProjectTasks.AsNoTracking().CountAsync(t => taskIds.Contains(t.Id) && t.ProjectId == projectId, ct);
        return found == taskIds.Count;
    }

    private static List<Guid> Merge(IReadOnlyList<Guid>? ids, Guid? one)
    {
        var list = (ids ?? []).Where(g => g != Guid.Empty).ToList();
        if (one is { } o && o != Guid.Empty) list.Insert(0, o);
        return list.Distinct().ToList();
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    private static DateTime? Utc(DateTime? d) => d is { } v ? Utc(v) : null;

    private static MilestoneResult<T> Fail<T>(string code) => new(default, code);
}
