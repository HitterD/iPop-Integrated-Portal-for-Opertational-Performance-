using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Phase 3 of the work-management spec: per-task timers and time entries.
/// One running timer per user (partial unique index); starting another stops the first.
/// ProjectTask.ActualHours is no longer typed — it is the sum of the task's finished entries.
/// Everyone in the project reads entries; members track their own; owner/manager edit anyone's.
public sealed class ProjectTimeService(IPopDbContext db) : IProjectTimeService
{
    public const int MaxTimerMinutes = 10 * 60;
    private const int MaxManualMinutes = 24 * 60;
    private const int MaxNoteLength = 200;
    private static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(5);

    public async Task<RunningTimerDto?> GetRunningAsync(Guid actorId, CancellationToken ct) =>
        await db.TaskTimeEntries.AsNoTracking()
            .Where(e => e.UserId == actorId && e.EndedAt == null)
            .Select(e => new RunningTimerDto(e.Id, e.TaskId, e.Task.Title, e.Task.ProjectId, e.Task.Project.Name, e.StartedAt))
            .FirstOrDefaultAsync(ct);

    public async Task<TimeResult<RunningTimerDto>> StartAsync(Guid actorId, Guid taskId, CancellationToken ct, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var task = await db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId)
            .Select(t => new { t.Id, t.Title, t.ProjectId, ProjectName = t.Project.Name }).FirstOrDefaultAsync(ct);
        if (task is null) return Fail<RunningTimerDto>("task_not_found");
        if (!await CanTrackAsync(actorId, task.ProjectId, ct)) return Fail<RunningTimerDto>("forbidden");

        var running = await db.TaskTimeEntries.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.UserId == actorId && e.EndedAt == null, ct);
        if (running?.TaskId == taskId)
            return new(new(running.Id, taskId, task.Title, task.ProjectId, task.ProjectName, running.StartedAt), null);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Guid? stoppedTask = null;
        if (running is not null)
        {
            if (Close(running, at)) stoppedTask = running.TaskId;
            else db.TaskTimeEntries.Remove(running);
            await db.SaveChangesAsync(ct);
        }
        var entry = new TaskTimeEntry
        {
            Id = Guid.NewGuid(), TaskId = taskId, UserId = actorId, StartedAt = at,
            Source = "timer", CreatedAt = at,
        };
        db.TaskTimeEntries.Add(entry);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another tab started a timer between our read and write.
            return Fail<RunningTimerDto>("timer_conflict");
        }
        if (stoppedTask is { } st) await RecomputeAsync(st, ct);
        await tx.CommitAsync(ct);
        return new(new(entry.Id, taskId, task.Title, task.ProjectId, task.ProjectName, at), null);
    }

    public async Task<TimeResult<TimeEntryDto?>> StopAsync(Guid actorId, CancellationToken ct, DateTime? now = null)
    {
        var running = await db.TaskTimeEntries.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.UserId == actorId && e.EndedAt == null, ct);
        if (running is null) return new(null, null);
        var projectId = await ProjectOfTaskAsync(running.TaskId, ct);
        if (projectId is null || !await CanTrackAsync(actorId, projectId.Value, ct)) return Fail<TimeEntryDto?>("forbidden");
        // Under a minute is a misclick, not work: drop it rather than bill 0 minutes.
        if (!Close(running, now ?? DateTime.UtcNow))
        {
            db.TaskTimeEntries.Remove(running);
            await db.SaveChangesAsync(ct);
            return new(null, null);
        }
        await db.SaveChangesAsync(ct);
        await RecomputeAsync(running.TaskId, ct);
        return new(await EntryDtoAsync(running.Id, ct), null);
    }

    public async Task<TimeResult<TaskTimeDto>> GetTaskTimeAsync(Guid actorId, Guid taskId, CancellationToken ct)
    {
        var projectId = await ProjectOfTaskAsync(taskId, ct);
        if (projectId is null) return Fail<TaskTimeDto>("task_not_found");
        if (!await CanReadAsync(actorId, projectId.Value, ct)) return Fail<TaskTimeDto>("forbidden");
        var manager = await IsManagerAsync(actorId, projectId.Value, ct);
        var canTrack = await CanTrackAsync(actorId, projectId.Value, ct);
        var entries = await db.TaskTimeEntries.AsNoTracking().Where(e => e.TaskId == taskId)
            .OrderByDescending(e => e.StartedAt).Take(200)
            .Select(e => new TimeEntryDto(e.Id, e.TaskId, e.UserId, e.User.Name, e.StartedAt, e.EndedAt,
                e.Minutes, e.Note, e.Source, e.AutoStopped, manager || (canTrack && e.UserId == actorId), e.User.Nik))
            .ToListAsync(ct);
        var total = await db.TaskTimeEntries.Where(e => e.TaskId == taskId && e.EndedAt != null).SumAsync(e => e.Minutes, ct);
        return new(new(taskId, total, entries, canTrack), null);
    }

    public async Task<TimeResult<TimeEntryDto>> AddManualAsync(Guid actorId, Guid taskId, AddTimeEntryRequest req, CancellationToken ct)
    {
        var projectId = await ProjectOfTaskAsync(taskId, ct);
        if (projectId is null) return Fail<TimeEntryDto>("task_not_found");
        if (!await CanTrackAsync(actorId, projectId.Value, ct)) return Fail<TimeEntryDto>("forbidden");
        if (req.Minutes < 1 || req.Minutes > MaxManualMinutes) return Fail<TimeEntryDto>("invalid_minutes");
        var start = AsInstant(req.StartedAt);
        if (start > DateTime.UtcNow + FutureSlack) return Fail<TimeEntryDto>("invalid_date");
        var note = CleanNote(req.Note, out var noteOk);
        if (!noteOk) return Fail<TimeEntryDto>("invalid_note");

        var entry = new TaskTimeEntry
        {
            Id = Guid.NewGuid(), TaskId = taskId, UserId = actorId, StartedAt = start,
            EndedAt = start.AddMinutes(req.Minutes), Minutes = req.Minutes, Note = note,
            Source = "manual", CreatedAt = DateTime.UtcNow,
        };
        db.TaskTimeEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        await RecomputeAsync(taskId, ct);
        return new(await EntryDtoAsync(entry.Id, ct), null);
    }

    public async Task<TimeResult<TimeEntryDto>> UpdateEntryAsync(Guid actorId, Guid entryId, UpdateTimeEntryRequest req, CancellationToken ct)
    {
        var entry = await db.TaskTimeEntries.Include(e => e.Task).FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null) return Fail<TimeEntryDto>("entry_not_found");
        if (!await CanEditAsync(actorId, entry, ct)) return Fail<TimeEntryDto>("forbidden");
        if (entry.EndedAt is null && (req.Minutes.HasValue || req.StartedAt.HasValue)) return Fail<TimeEntryDto>("timer_running");
        if (req.Minutes is { } m)
        {
            if (m < 1 || m > MaxManualMinutes) return Fail<TimeEntryDto>("invalid_minutes");
            entry.Minutes = m;
        }
        if (req.StartedAt is { } s)
        {
            var start = AsInstant(s);
            if (start > DateTime.UtcNow + FutureSlack) return Fail<TimeEntryDto>("invalid_date");
            entry.StartedAt = start;
        }
        if (req.Note is not null)
        {
            var note = CleanNote(req.Note, out var noteOk);
            if (!noteOk) return Fail<TimeEntryDto>("invalid_note");
            entry.Note = note;
        }
        if (entry.EndedAt is not null)
        {
            entry.EndedAt = entry.StartedAt.AddMinutes(entry.Minutes);
            // A corrected entry no longer needs the auto-stop warning.
            if (req.Minutes.HasValue) entry.AutoStopped = false;
        }
        await db.SaveChangesAsync(ct);
        await RecomputeAsync(entry.TaskId, ct);
        return new(await EntryDtoAsync(entry.Id, ct), null);
    }

    public async Task<TimeResult<Guid>> DeleteEntryAsync(Guid actorId, Guid entryId, CancellationToken ct)
    {
        var entry = await db.TaskTimeEntries.Include(e => e.Task).FirstOrDefaultAsync(e => e.Id == entryId, ct);
        if (entry is null) return Fail<Guid>("entry_not_found");
        if (!await CanEditAsync(actorId, entry, ct)) return Fail<Guid>("forbidden");
        db.TaskTimeEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        await RecomputeAsync(entry.TaskId, ct);
        return new(entry.Task.ProjectId, null);
    }

    public async Task<TimeResult<TimesheetDto>> GetProjectTimesheetAsync(Guid actorId, Guid projectId, DateTime weekStart, CancellationToken ct)
    {
        if (!await CanReadAsync(actorId, projectId, ct)) return Fail<TimesheetDto>("forbidden");
        var from = AsInstant(weekStart);
        var to = from.AddDays(7);
        var entries = await db.TaskTimeEntries.AsNoTracking()
            .Where(e => e.Task.ProjectId == projectId && e.EndedAt != null && e.StartedAt >= from && e.StartedAt < to)
            .Select(e => new { e.UserId, e.StartedAt, e.Minutes }).ToListAsync(ct);
        // Every member gets a row (an empty week is information too); past members only if they logged time.
        var memberIds = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId)
            .Select(m => m.UserId).ToListAsync(ct);
        var ids = memberIds.Concat(entries.Select(e => e.UserId)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Name, u.Nik }).ToListAsync(ct);
        return new(Build(from, ids, users.ToDictionary(u => u.Id, u => u.Name), entries.Select(e => (e.UserId, e.StartedAt, e.Minutes)),
            users.ToDictionary(u => u.Id, u => u.Nik)), null);
    }

    public async Task<TimesheetDto> GetPortfolioTimesheetAsync(Guid actorId, DateTime weekStart, CancellationToken ct)
    {
        var from = AsInstant(weekStart);
        var to = from.AddDays(7);
        var all = await db.UserRoles.AnyAsync(r => r.UserId == actorId &&
            (r.Role.Name == "Admin" || r.Role.Name == "HR" || r.Role.Name == "Super Admin" || r.Role.Name == "SuperAdmin"), ct);
        var q = db.TaskTimeEntries.AsNoTracking()
            .Where(e => e.EndedAt != null && e.StartedAt >= from && e.StartedAt < to && e.Task.Project.IsActive);
        if (!all) q = q.Where(e => e.Task.Project.Members.Any(m => m.UserId == actorId));
        var entries = await q.Select(e => new { e.UserId, e.StartedAt, e.Minutes }).ToListAsync(ct);
        var ids = entries.Select(e => e.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Name, u.Nik }).ToListAsync(ct);
        return Build(from, ids, users.ToDictionary(u => u.Id, u => u.Name), entries.Select(e => (e.UserId, e.StartedAt, e.Minutes)),
            users.ToDictionary(u => u.Id, u => u.Nik));
    }

    public async Task<IReadOnlyList<AutoStoppedTimerDto>> AutoStopAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddMinutes(-MaxTimerMinutes);
        var stale = await db.TaskTimeEntries.IgnoreQueryFilters().Include(e => e.Task)
            .Where(e => e.EndedAt == null && e.StartedAt <= cutoff).Take(500).ToListAsync(ct);
        if (stale.Count == 0) return [];
        foreach (var e in stale) Close(e, now);
        await db.SaveChangesAsync(ct);
        foreach (var taskId in stale.Select(e => e.TaskId).Distinct()) await RecomputeAsync(taskId, ct);
        return stale.Select(e => new AutoStoppedTimerDto(e.Id, e.UserId, e.TaskId, e.Task.Title, e.Task.ProjectId)).ToList();
    }

    public Task<Guid?> ProjectOfTaskAsync(Guid taskId, CancellationToken ct) =>
        db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);

    // ──────────────────────────────────────────────────────────────────────────

    /// Ends a running entry, capped at the 10-hour limit. False when it lasted under a minute.
    private static bool Close(TaskTimeEntry e, DateTime now)
    {
        var minutes = (int)Math.Round((now - e.StartedAt).TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes >= MaxTimerMinutes)
        {
            e.Minutes = MaxTimerMinutes;
            e.EndedAt = e.StartedAt.AddMinutes(MaxTimerMinutes);
            e.AutoStopped = true;
            return true;
        }
        e.Minutes = minutes;
        e.EndedAt = now;
        return minutes >= 1;
    }

    /// Actual hours = finished entries. Soft-deleted tasks are included so a restore stays correct.
    private async Task RecomputeAsync(Guid taskId, CancellationToken ct)
    {
        var minutes = await db.TaskTimeEntries.IgnoreQueryFilters()
            .Where(e => e.TaskId == taskId && e.EndedAt != null).SumAsync(e => e.Minutes, ct);
        var task = await db.ProjectTasks.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return;
        task.ActualHours = minutes == 0 ? null : Math.Round(minutes / 60m, 2);
        await db.SaveChangesAsync(ct);
    }

    /// Days are 24-hour slices from the viewer's local Monday midnight (sent as a UTC instant),
    /// so an entry lands on the day the viewer lived it.
    internal static TimesheetDto Build(DateTime from, IReadOnlyList<Guid> ids, IReadOnlyDictionary<Guid, string> names,
        IEnumerable<(Guid UserId, DateTime StartedAt, int Minutes)> entries, IReadOnlyDictionary<Guid, string>? niks = null)
    {
        var grid = ids.ToDictionary(id => id, _ => new int[7]);
        foreach (var (userId, startedAt, minutes) in entries)
        {
            var day = (int)Math.Floor((startedAt - from).TotalDays);
            if (day is >= 0 and < 7 && grid.TryGetValue(userId, out var row)) row[day] += minutes;
        }
        var rows = grid.Select(kv => new TimesheetRowDto(kv.Key, names.GetValueOrDefault(kv.Key) ?? "—", kv.Value, kv.Value.Sum(), niks?.GetValueOrDefault(kv.Key)))
            .OrderByDescending(r => r.TotalMinutes).ThenBy(r => r.Name).ToList();
        var totals = Enumerable.Range(0, 7).Select(d => rows.Sum(r => r.DayMinutes[d])).ToList();
        return new(from, rows, totals, totals.Sum());
    }

    private Task<TimeEntryDto> EntryDtoAsync(Guid entryId, CancellationToken ct) =>
        db.TaskTimeEntries.IgnoreQueryFilters().AsNoTracking().Where(e => e.Id == entryId)
            .Select(e => new TimeEntryDto(e.Id, e.TaskId, e.UserId, e.User.Name, e.StartedAt, e.EndedAt,
                e.Minutes, e.Note, e.Source, e.AutoStopped, true, e.User.Nik))
            .FirstAsync(ct);

    /// Instants arrive as UTC ("Z"), as local (model binding converts) or unspecified (treated as UTC).
    private static DateTime AsInstant(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    private static string? CleanNote(string? note, out bool ok)
    {
        var n = note?.Trim();
        ok = n is null || n.Length <= MaxNoteLength;
        return string.IsNullOrEmpty(n) ? null : n;
    }

    private Task<bool> CanReadAsync(Guid actorId, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId &&
            (p.OwnerId == actorId || p.Members.Any(m => m.UserId == actorId) ||
             db.UserRoles.Any(r => r.UserId == actorId && r.Role.Name == "Admin")), ct);

    private Task<bool> CanTrackAsync(Guid actorId, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId && p.IsActive &&
            (p.OwnerId == actorId || p.Members.Any(m => m.UserId == actorId && m.Role != "viewer")), ct);

    private Task<bool> IsManagerAsync(Guid actorId, Guid projectId, CancellationToken ct) =>
        db.Projects.AnyAsync(p => p.Id == projectId &&
            (p.OwnerId == actorId || p.Members.Any(m => m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"))), ct);

    private async Task<bool> CanEditAsync(Guid actorId, TaskTimeEntry e, CancellationToken ct) =>
        (e.UserId == actorId && await CanTrackAsync(actorId, e.Task.ProjectId, ct)) || await IsManagerAsync(actorId, e.Task.ProjectId, ct);

    private static TimeResult<T> Fail<T>(string error) => new(default, error);
}
