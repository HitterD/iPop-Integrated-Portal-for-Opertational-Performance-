using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

public sealed class ProjectService(IPopDbContext db, INotificationGatewayService? notificationGateway = null) : IProjectService
{
    // ──────────────────────────────────────────────────────────────────────────
    // Projects
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ProjectResultDto> CreateProjectAsync(Guid actorId, CreateProjectRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
            return new ProjectResultDto(false, null, "invalid_name");
        if (req.Year < 2000 || req.Year > 2100)
            return new ProjectResultDto(false, null, "invalid_year");

        var ownerId = req.OwnerId == Guid.Empty ? actorId : req.OwnerId;
        if (!string.IsNullOrWhiteSpace(req.OwnerNik))
        {
            var owner = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Nik == req.OwnerNik.Trim() && u.IsActive, ct);
            if (owner is null) return new ProjectResultDto(false, null, "invalid_owner");
            ownerId = owner.Id;
        }
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == ownerId && u.IsActive, ct))
            return new ProjectResultDto(false, null, "invalid_owner");

        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            Description = req.Description?.Trim(),
            Year = req.Year,
            OwnerId = ownerId,
            StartDate = req.StartDate,
            Deadline = req.Deadline,
            Status = "planning",
            Progress = 0,
            Risk = req.Risk?.Trim(),
            Obstacle = req.Obstacle?.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Projects.Add(project);

        var creatorRole = (ownerId == actorId) ? "owner" : "manager";
        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = project.Id,
            UserId = actorId,
            Role = creatorRole,
            JoinedAt = now
        });

        if (ownerId != actorId)
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = project.Id,
                UserId = ownerId,
                Role = "owner",
                JoinedAt = now
            });
        }

        // ipop-21 PERFECTION: dedicated chat room per project (interconnect).
        // Room is created atomically with the project so members can talk immediately.
        CreateProjectRoom(project, actorId, ownerId, now);

        await db.SaveChangesAsync(ct);

        if (notificationGateway != null && ownerId != actorId)
        {
            try
            {
                await notificationGateway.PublishInternalAsync(
                    clientSlug: "projects",
                    eventType: "project.member_added",
                    title: $"New Project: {project.Name}",
                    message: $"You have been assigned as the owner of project {project.Name}.",
                    target: new NotificationTarget(UserId: ownerId),
                    priority: "normal",
                    targetUrl: $"/projects/{project.Id}",
                    ct: ct);
            }
            catch
            {
                // Non-blocking notification delivery
            }
        }

        return new ProjectResultDto(true, await MapToProjectDtoAsync(project.Id, ct), null);
    }

    public async Task<ProjectDto?> GetProjectAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;

        return await MapToProjectDtoAsync(projectId, ct);
    }

    public async Task<PagedResult<ProjectListDto>> GetProjectsAsync(Guid userId, string? status, int? year, int page, int pageSize, CancellationToken ct)
    {
        var memberProjectIds = db.ProjectMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.ProjectId);

        // Members see their projects; admins see every active project.
        var isAdmin = await IsAdminAsync(userId, ct);
        var query = db.Projects
            .AsNoTracking()
            .Where(p => (isAdmin || memberProjectIds.Contains(p.Id)) && p.IsActive);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(p => p.Status == status);
        if (year.HasValue)
            query = query.Where(p => p.Year == year.Value);

        var totalCount = await query.CountAsync(ct);

        var projects = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(p => p.Owner)
            .Include(p => p.Members)
            .Include(p => p.Tasks)
            .ToListAsync(ct);

        var dtos = projects.Select(p =>
        {
            return new ProjectListDto(
                p.Id, p.Name, p.Year, p.Owner.Name, p.Status, p.Progress, p.Deadline,
                p.Members.Count, p.Tasks.Count, p.IsActive);
        }).ToList();

        return new PagedResult<ProjectListDto>(dtos, totalCount, page, pageSize);
    }

    public async Task<ProjectResultDto> UpdateProjectAsync(Guid actorId, Guid projectId, UpdateProjectRequest req, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new ProjectResultDto(false, null, "project_not_found");

        var isOwnerOrManager = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"), ct);
        if (!isOwnerOrManager)
            return new ProjectResultDto(false, null, "forbidden");

        if (req.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
                return new ProjectResultDto(false, null, "invalid_name");
            project.Name = req.Name.Trim();
        }
        if (req.Description is not null) project.Description = req.Description.Trim();
        if (req.Year.HasValue)
        {
            if (req.Year.Value < 2000 || req.Year.Value > 2100)
                return new ProjectResultDto(false, null, "invalid_year");
            project.Year = req.Year.Value;
        }
        if (!string.IsNullOrWhiteSpace(req.OwnerNik))
        {
            var owner = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Nik == req.OwnerNik.Trim() && u.IsActive, ct);
            if (owner is null) return new ProjectResultDto(false, null, "invalid_owner");
            project.OwnerId = owner.Id;
        }
        else if (req.OwnerId.HasValue)
        {
            if (req.OwnerId.Value == Guid.Empty || !await db.Users.AsNoTracking().AnyAsync(u => u.Id == req.OwnerId.Value && u.IsActive, ct))
                return new ProjectResultDto(false, null, "invalid_owner");
            project.OwnerId = req.OwnerId.Value;
        }
        if (req.StartDate.HasValue) project.StartDate = req.StartDate;
        if (req.Deadline.HasValue) project.Deadline = req.Deadline;
        if (req.Status is not null)
        {
            var valid = new[] { "planning", "active", "on_hold", "completed", "cancelled" };
            if (!valid.Contains(req.Status)) return new ProjectResultDto(false, null, "invalid_status");
            project.Status = req.Status;
        }
        if (req.Risk is not null) project.Risk = req.Risk.Trim();
        if (req.Obstacle is not null) project.Obstacle = req.Obstacle.Trim();

        project.UpdatedAt = DateTime.UtcNow;

        // ipop-21 PERFECTION: sync room name when project is renamed
        if (req.Name is not null)
        {
            var pr = await db.ProjectRooms.FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);
            if (pr is not null)
            {
                var room = await db.ChatRooms.FirstOrDefaultAsync(r => r.Id == pr.RoomId, ct);
                if (room is not null) room.Name = project.Name;
            }
        }

        await db.SaveChangesAsync(ct);

        return new ProjectResultDto(true, await MapToProjectDtoAsync(projectId, ct), null);
    }

    public async Task<MemberResultDto> DeleteProjectAsync(Guid actorId, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return new MemberResultDto(false, "project_not_found");

        var isOwner = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && m.Role == "owner", ct);
        if (!isOwner)
            return new MemberResultDto(false, "forbidden");

        project.IsActive = false;
        project.UpdatedAt = DateTime.UtcNow;

        // ipop-21 PERFECTION: deactivate the linked chat room too
        var pr = await db.ProjectRooms.FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);
        if (pr is not null)
        {
            var room = await db.ChatRooms.FirstOrDefaultAsync(r => r.Id == pr.RoomId, ct);
            if (room is not null) room.IsActive = false;
        }

        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Members
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<MemberResultDto> AddMemberAsync(Guid actorId, Guid projectId, AddProjectMemberRequest req, CancellationToken ct)
    {
        var isOwnerOrManager = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"), ct);
        if (!isOwnerOrManager)
            return new MemberResultDto(false, "forbidden");

        var targetUserId = req.UserId;
        if (targetUserId == Guid.Empty && !string.IsNullOrWhiteSpace(req.Nik))
        {
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Nik == req.Nik.Trim() && x.IsActive, ct);
            if (u is not null) targetUserId = u.Id;
        }

        if (targetUserId == Guid.Empty || !await db.Users.AsNoTracking().AnyAsync(u => u.Id == targetUserId, ct))
            return new MemberResultDto(false, "invalid_user");

        var exists = await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == targetUserId, ct);
        if (exists) return new MemberResultDto(true, null);

        var validRoles = new[] { "owner", "manager", "member", "viewer" };
        var role = string.IsNullOrWhiteSpace(req.Role) ? "member" : req.Role.Trim();
        if (!validRoles.Contains(role)) return new MemberResultDto(false, "invalid_role");

        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = projectId,
            UserId = targetUserId,
            Role = role,
            JoinedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);

        // ipop-21 PERFECTION: sync chat room membership
        await SyncProjectMemberToRoomAsync(projectId, targetUserId, role, ct);

        if (notificationGateway != null && targetUserId != actorId)
        {
            try
            {
                var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
                var projectName = project?.Name ?? "Project";

                await notificationGateway.PublishInternalAsync(
                    clientSlug: "projects",
                    eventType: "project.member_added",
                    title: $"Joined Project: {projectName}",
                    message: $"You have been added as {role} to project {projectName}.",
                    target: new NotificationTarget(UserId: targetUserId),
                    priority: "normal",
                    targetUrl: $"/projects/{projectId}",
                    ct: ct);
            }
            catch
            {
                // Non-blocking notification delivery
            }
        }

        return new MemberResultDto(true, null);
    }

    public async Task<MemberResultDto> RemoveMemberAsync(Guid actorId, Guid projectId, Guid userId, CancellationToken ct)
    {
        var isOwnerOrManager = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"), ct);
        if (!isOwnerOrManager)
            return new MemberResultDto(false, "forbidden");

        var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (member is null) return new MemberResultDto(false, "member_not_found");

        if (member.Role == "owner")
        {
            var ownerCount = await db.ProjectMembers.CountAsync(m => m.ProjectId == projectId && m.Role == "owner", ct);
            if (ownerCount <= 1) return new MemberResultDto(false, "cannot_remove_last_owner");
        }

        db.ProjectMembers.Remove(member);
        await db.SaveChangesAsync(ct);

        // ipop-21 PERFECTION: sync chat room membership (remove user from room)
        await RemoveMemberFromProjectRoomAsync(projectId, userId, ct);

        return new MemberResultDto(true, null);
    }

    public async Task<IReadOnlyList<ProjectMemberDto>> GetMembersAsync(Guid projectId, CancellationToken ct)
    {
        return await db.ProjectMembers
            .AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .Include(m => m.User)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new ProjectMemberDto(m.UserId, m.User.Nik, m.User.Name, m.Role, m.JoinedAt))
            .ToListAsync(ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tasks
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<TaskResultDto> CreateTaskAsync(Guid actorId, CreateTaskRequest req, CancellationToken ct)
    {
        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == req.ProjectId && m.UserId == actorId, ct);
        if (!isMember) return new TaskResultDto(false, null, "forbidden");

        if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 300)
            return new TaskResultDto(false, null, "invalid_title");

        var validStatus = new[] { "todo", "doing", "review", "done" };
        var status = string.IsNullOrWhiteSpace(req.Status) ? "todo" : req.Status.Trim();
        if (!validStatus.Contains(status)) return new TaskResultDto(false, null, "invalid_status");

        if (req.ParentTaskId.HasValue)
        {
            var parentOk = await db.ProjectTasks.AnyAsync(t => t.Id == req.ParentTaskId.Value && t.ProjectId == req.ProjectId, ct);
            if (!parentOk) return new TaskResultDto(false, null, "parent_not_found");
        }

        var now = DateTime.UtcNow;
        var maxSort = await db.ProjectTasks
            .Where(t => t.ProjectId == req.ProjectId && t.Status == status && t.ParentTaskId == req.ParentTaskId)
            .Select(t => (int?)t.SortOrder)
            .MaxAsync(ct) ?? 0;

        var task = new ProjectTask
        {
            Id = Guid.NewGuid(),
            ProjectId = req.ProjectId,
            ParentTaskId = req.ParentTaskId,
            Title = req.Title.Trim(),
            Description = req.Description?.Trim(),
            Status = status,
            Priority = req.Priority?.Trim(),
            StartDate = req.StartDate,
            DueDate = req.DueDate,
            EstimatedHours = req.EstimatedHours,
            SortOrder = maxSort + 100,
            Dependencies = req.Dependencies?.Trim(),
            Tags = req.Tags?.Trim(),
            CreatedById = actorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.ProjectTasks.Add(task);
        await db.SaveChangesAsync(ct);

        await RecalculateProgressAsync(req.ProjectId, ct);

        return new TaskResultDto(true, await MapToTaskDtoAsync(task.Id, ct), null);
    }

    public async Task<ProjectTaskDto?> GetTaskAsync(Guid taskId, Guid userId, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        if (!await CanViewAsync(task.ProjectId, userId, ct)) return null;

        return await MapToTaskDtoAsync(taskId, ct);
    }

    public async Task<IReadOnlyList<ProjectTaskDto>> GetTasksAsync(Guid projectId, Guid? parentTaskId, CancellationToken ct)
    {
        var tasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.ParentTaskId == parentTaskId)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreatedAt)
            .Include(t => t.CreatedBy)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .Include(t => t.Subtasks)
            .ToListAsync(ct);

        var result = new List<ProjectTaskDto>();
        // Agregasi penanda per halaman (bukan per baris) — 3 query untuk seluruh daftar.
        var taskIds = tasks.Select(t => t.Id).ToList();
        var commentCounts = await db.ProjectTaskComments.Where(c => taskIds.Contains(c.TaskId) && !c.IsDeleted)
            .GroupBy(c => c.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var attachmentCounts = await db.ProjectTaskAttachments.Where(a => taskIds.Contains(a.TaskId) && !a.IsDeleted)
            .GroupBy(a => a.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var reminderTaskIds = (await db.ProjectTaskReminders.Where(r => taskIds.Contains(r.TaskId) && r.SentAt == null)
            .Select(r => r.TaskId).Distinct().ToListAsync(ct)).ToHashSet();
        foreach (var t in tasks)
        {
            var assignees = t.Assignees.Select(a => new ProjectTaskAssigneeDto(a.UserId, a.User.Nik, a.User.Name, a.Role, a.AssignedAt)).ToList();
            var subCount = t.Subtasks.Count;
            var subDone = t.Subtasks.Count(s => s.Status == "done");
            result.Add(new ProjectTaskDto(
                t.Id, t.ProjectId, t.ParentTaskId, t.Title, t.Description, t.Status, t.Priority,
                t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours, t.SortOrder,
                t.Dependencies, t.IsRecurring, t.RecurrenceRule, t.IsTemplate,
                t.Tags,
                t.CreatedById, t.CreatedBy?.Name ?? "Unknown",
                assignees, subCount, subDone, t.CreatedAt, t.UpdatedAt,
                commentCounts.GetValueOrDefault(t.Id), attachmentCounts.GetValueOrDefault(t.Id), reminderTaskIds.Contains(t.Id)));
        }
        return result;
    }

    public async Task<TaskResultDto> UpdateTaskAsync(Guid actorId, Guid taskId, UpdateTaskRequest req, CancellationToken ct)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new TaskResultDto(false, null, "task_not_found");

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == actorId, ct);
        if (!isMember) return new TaskResultDto(false, null, "forbidden");

        // Diskusi Tugas (Fase 2): catat perubahan penting ke riwayat aktivitas.
        var activityRows = new List<ProjectTaskActivity>();
        void Track(string action, string? oldVal, string? newVal)
            => activityRows.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId,
                Action = action, OldValue = oldVal, NewValue = newVal, CreatedAt = DateTime.UtcNow
            });

        if (req.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 300)
                return new TaskResultDto(false, null, "invalid_title");
            if (task.Title != req.Title.Trim()) Track("title_changed", task.Title, req.Title.Trim());
            task.Title = req.Title.Trim();
        }
        if (req.Description is not null) task.Description = req.Description.Trim();
        if (req.Priority is not null)
        {
            if (task.Priority != req.Priority.Trim()) Track("priority_changed", task.Priority, req.Priority.Trim());
            task.Priority = req.Priority.Trim();
        }
        if (req.StartDate.HasValue) task.StartDate = req.StartDate;
        if (req.DueDate.HasValue)
        {
            if (task.DueDate != req.DueDate) Track("due_changed", task.DueDate?.ToString("yyyy-MM-dd"), req.DueDate.Value.ToString("yyyy-MM-dd"));
            task.DueDate = req.DueDate;
        }
        if (req.EstimatedHours.HasValue) task.EstimatedHours = req.EstimatedHours;
        if (req.ActualHours.HasValue) task.ActualHours = req.ActualHours;
        if (req.SortOrder.HasValue) task.SortOrder = req.SortOrder.Value;
        if (req.Dependencies is not null) task.Dependencies = req.Dependencies.Trim();
        if (req.IsRecurring.HasValue) task.IsRecurring = req.IsRecurring.Value;
        if (req.RecurrenceRule is not null) task.RecurrenceRule = req.RecurrenceRule.Trim();
        if (req.IsTemplate.HasValue) task.IsTemplate = req.IsTemplate.Value;
        if (req.Tags is not null) task.Tags = req.Tags.Trim();

        if (req.Status is not null)
        {
            var valid = new[] { "todo", "doing", "review", "done" };
            if (!valid.Contains(req.Status)) return new TaskResultDto(false, null, "invalid_status");

            if (req.Status != task.Status)
            {
                Track("status_changed", task.Status, req.Status);
                var maxSort = await db.ProjectTasks
                    .Where(t => t.ProjectId == task.ProjectId && t.Status == req.Status && t.ParentTaskId == task.ParentTaskId)
                    .Select(t => (int?)t.SortOrder)
                    .MaxAsync(ct) ?? 0;
                task.SortOrder = maxSort + 100;
            }
            task.Status = req.Status;
        }

        task.UpdatedAt = DateTime.UtcNow;

        if (activityRows.Count > 0) db.ProjectTaskActivities.AddRange(activityRows);
        await db.SaveChangesAsync(ct);
        await RecalculateProgressAsync(task.ProjectId, ct);

        return new TaskResultDto(true, await MapToTaskDtoAsync(taskId, ct), null);
    }

    public async Task<MemberResultDto> DeleteTaskAsync(Guid actorId, Guid taskId, CancellationToken ct)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new MemberResultDto(false, "task_not_found");

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == actorId, ct);
        if (!isMember) return new MemberResultDto(false, "forbidden");

        db.ProjectTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        await RecalculateProgressAsync(task.ProjectId, ct);

        return new MemberResultDto(true, null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Assignees
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<MemberResultDto> AssignUserAsync(Guid actorId, Guid taskId, Guid userId, string? role, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new MemberResultDto(false, "task_not_found");

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == actorId, ct);
        if (!isMember) return new MemberResultDto(false, "forbidden");

        var already = await db.ProjectTaskAssignees.AnyAsync(a => a.TaskId == taskId && a.UserId == userId, ct);
        if (already) return new MemberResultDto(true, null);

        db.ProjectTaskAssignees.Add(new ProjectTaskAssignee
        {
            TaskId = taskId,
            UserId = userId,
            Role = role?.Trim(),
            AssignedAt = DateTime.UtcNow
        });
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId,
            Action = "assignee_added", NewValue = (await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct)) ?? userId.ToString(),
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);

        if (notificationGateway != null && userId != actorId)
        {
            try
            {
                var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == task.ProjectId, ct);
                var projectName = project?.Name ?? "Project";

                await notificationGateway.PublishInternalAsync(
                    clientSlug: "projects",
                    eventType: "project.task_assigned",
                    title: $"Task Assignment: {task.Title}",
                    message: $"You have been assigned to task \"{task.Title}\" in {projectName}.",
                    target: new NotificationTarget(UserId: userId),
                    priority: "normal",
                    targetUrl: $"/projects/{task.ProjectId}?task={task.Id}",
                    ct: ct);
            }
            catch
            {
                // Non-blocking notification delivery
            }
        }

        return new MemberResultDto(true, null);
    }

    public async Task<MemberResultDto> UnassignUserAsync(Guid actorId, Guid taskId, Guid userId, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new MemberResultDto(false, "task_not_found");

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == actorId, ct);
        if (!isMember) return new MemberResultDto(false, "forbidden");

        var link = await db.ProjectTaskAssignees.FirstOrDefaultAsync(a => a.TaskId == taskId && a.UserId == userId, ct);
        if (link is null) return new MemberResultDto(true, null);

        db.ProjectTaskAssignees.Remove(link);
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId,
            Action = "assignee_removed", OldValue = (await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct)) ?? userId.ToString(),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Kanban + List + Timeline
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<KanbanBoardDto?> GetKanbanAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;

        var all = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.ParentTaskId == null)
            .Include(t => t.CreatedBy)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .Include(t => t.Subtasks)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt)
            .ToListAsync(ct);

        // Penanda diskusi per papan (3 query untuk semua kartu).
        var boardTaskIds = all.Select(t => t.Id).ToList();
        var boardCommentCounts = await db.ProjectTaskComments.Where(c => boardTaskIds.Contains(c.TaskId) && !c.IsDeleted)
            .GroupBy(c => c.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var boardAttachmentCounts = await db.ProjectTaskAttachments.Where(a => boardTaskIds.Contains(a.TaskId) && !a.IsDeleted)
            .GroupBy(a => a.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var boardReminderTaskIds = (await db.ProjectTaskReminders.Where(r => boardTaskIds.Contains(r.TaskId) && r.SentAt == null)
            .Select(r => r.TaskId).Distinct().ToListAsync(ct)).ToHashSet();

        ProjectTaskDto Map(ProjectTask t)
        {
            var ass = t.Assignees.Select(a => new ProjectTaskAssigneeDto(a.UserId, a.User.Nik, a.User.Name, a.Role, a.AssignedAt)).ToList();
            var subCount = t.Subtasks.Count;
            var subDone = t.Subtasks.Count(s => s.Status == "done");
            return new ProjectTaskDto(t.Id, t.ProjectId, t.ParentTaskId, t.Title, t.Description, t.Status, t.Priority,
                t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours, t.SortOrder,
                t.Dependencies, t.IsRecurring, t.RecurrenceRule, t.IsTemplate,
                t.Tags,
                t.CreatedById, t.CreatedBy?.Name ?? "Unknown", ass, subCount, subDone, t.CreatedAt, t.UpdatedAt,
                boardCommentCounts.GetValueOrDefault(t.Id), boardAttachmentCounts.GetValueOrDefault(t.Id), boardReminderTaskIds.Contains(t.Id));
        }

        return new KanbanBoardDto(
            projectId,
            all.Where(t => t.Status == "todo").Select(Map).ToList(),
            all.Where(t => t.Status == "doing").Select(Map).ToList(),
            all.Where(t => t.Status == "done").Select(Map).ToList(),
            all.Where(t => t.Status == "review").Select(Map).ToList()
        );
    }

    public async Task<TaskResultDto> MoveTaskAsync(Guid actorId, Guid taskId, TaskMoveRequest req, CancellationToken ct)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new TaskResultDto(false, null, "task_not_found");

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == actorId, ct);
        if (!isMember) return new TaskResultDto(false, null, "forbidden");

        var valid = new[] { "todo", "doing", "review", "done" };
        if (!valid.Contains(req.Status)) return new TaskResultDto(false, null, "invalid_status");

        // DETAIL MAX: dependency guard — cannot move to "done" unless all listed dependencies are "done"
        if (req.Status == "done" && task.Status != "done")
        {
            var (canComplete, unmet) = await ValidateTaskDependenciesAsync(taskId, actorId, ct);
            if (!canComplete)
            {
                return new TaskResultDto(false, null, "unmet_dependencies");
            }
        }

        if (req.Status != task.Status)
        {
            var maxSort = await db.ProjectTasks
                .Where(t => t.ProjectId == task.ProjectId && t.Status == req.Status && t.ParentTaskId == task.ParentTaskId)
                .Select(t => (int?)t.SortOrder)
                .MaxAsync(ct) ?? 0;
            task.SortOrder = req.SortOrder ?? (maxSort + 100);
            task.Status = req.Status;
        }
        else if (req.SortOrder.HasValue)
        {
            task.SortOrder = req.SortOrder.Value;
        }

        task.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await RecalculateProgressAsync(task.ProjectId, ct);

        return new TaskResultDto(true, await MapToTaskDtoAsync(taskId, ct), null);
    }

    public async Task<PagedResult<ProjectTaskDto>> GetTaskListAsync(Guid projectId, Guid userId, string? status, int page, int pageSize, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return new PagedResult<ProjectTaskDto>([], 0, page, pageSize);

        var q = db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(t => t.Status == status);

        var total = await q.CountAsync(ct);

        var tasks = await q
            .OrderByDescending(t => t.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(t => t.CreatedBy)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .Include(t => t.Subtasks)
            .ToListAsync(ct);

        var dtos = tasks.Select(t =>
        {
            var ass = t.Assignees.Select(a => new ProjectTaskAssigneeDto(a.UserId, a.User.Nik, a.User.Name, a.Role, a.AssignedAt)).ToList();
            var subCount = t.Subtasks.Count;
            var subDone = t.Subtasks.Count(s => s.Status == "done");
            return new ProjectTaskDto(t.Id, t.ProjectId, t.ParentTaskId, t.Title, t.Description, t.Status, t.Priority,
                t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours, t.SortOrder,
                t.Dependencies, t.IsRecurring, t.RecurrenceRule, t.IsTemplate,
                t.Tags,
                t.CreatedById, t.CreatedBy?.Name ?? "Unknown", ass, subCount, subDone, t.CreatedAt, t.UpdatedAt);
        }).ToList();

        return new PagedResult<ProjectTaskDto>(dtos, total, page, pageSize);
    }

    public async Task<ProjectTimelineDto?> GetTimelineAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;

        var tasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Include(t => t.Assignees)
            .Include(t => t.Subtasks)
            .OrderBy(t => t.StartDate).ThenBy(t => t.DueDate).ThenBy(t => t.SortOrder)
            .ToListAsync(ct);

        var dtos = tasks.Select(t => new TimelineTaskDto(
            t.Id, t.Title, t.ParentTaskId, t.Status, t.Priority,
            t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours,
            t.Assignees.Select(a => a.UserId).ToList(),
            t.Subtasks.Count, t.Subtasks.Count(s => s.Status == "done"),
            t.Tags
        )).ToList();

        return new ProjectTimelineDto(project.Id, project.Name, project.StartDate, project.Deadline, dtos);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DETAIL MAX: Workload, Export, Recurring Preview, Dependency Validation
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ProjectWorkloadDto?> GetProjectWorkloadAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;

        var tasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            return new ProjectWorkloadDto(0, 0, 0, 0, 0, 0, 0, 0, []);
        }

        int todo = tasks.Count(t => t.Status == "todo");
        int doing = tasks.Count(t => t.Status == "doing");
        int review = tasks.Count(t => t.Status == "review");
        int done = tasks.Count(t => t.Status == "done");

        decimal totalEst = tasks.Sum(t => t.EstimatedHours ?? 0);
        decimal todoEst = tasks.Where(t => t.Status == "todo").Sum(t => t.EstimatedHours ?? 0);
        decimal doingEst = tasks.Where(t => t.Status == "doing").Sum(t => t.EstimatedHours ?? 0);
        decimal reviewEst = tasks.Where(t => t.Status == "review").Sum(t => t.EstimatedHours ?? 0);
        decimal doneEst = tasks.Where(t => t.Status == "done").Sum(t => t.EstimatedHours ?? 0);

        var byAssignee = tasks
            .SelectMany(t => t.Assignees.Select(a => new { a.UserId, a.User.Name, t.EstimatedHours }))
            .GroupBy(x => new { x.UserId, x.Name })
            .Select(g => new AssigneeWorkloadDto(g.Key.UserId, g.Key.Name, g.Count(), g.Sum(x => x.EstimatedHours ?? 0)))
            .OrderByDescending(x => x.EstimatedHours)
            .ToList();

        return new ProjectWorkloadDto(
            tasks.Count, todo, doing, done,
            totalEst, todoEst, doingEst, doneEst,
            byAssignee, review, reviewEst);
    }

    public async Task<ProjectExportDto?> GetProjectExportDataAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var projectDto = await GetProjectAsync(projectId, userId, ct);
        if (projectDto is null) return null;

        var tasks = await GetTasksAsync(projectId, null, ct); // top-level; callers can also request full tree via other calls
        // For export, also gather all tasks (flat) to avoid missing subtasks
        var allTasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Include(t => t.CreatedBy)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .Include(t => t.Subtasks)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct);

        var taskDtos = allTasks.Select(t =>
        {
            var ass = t.Assignees.Select(a => new ProjectTaskAssigneeDto(a.UserId, a.User.Nik, a.User.Name, a.Role, a.AssignedAt)).ToList();
            var subCount = t.Subtasks.Count;
            var subDone = t.Subtasks.Count(s => s.Status == "done");
            return new ProjectTaskDto(
                t.Id, t.ProjectId, t.ParentTaskId, t.Title, t.Description, t.Status, t.Priority,
                t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours, t.SortOrder,
                t.Dependencies, t.IsRecurring, t.RecurrenceRule, t.IsTemplate,
                t.Tags,
                t.CreatedById, t.CreatedBy?.Name ?? "Unknown",
                ass, subCount, subDone, t.CreatedAt, t.UpdatedAt);
        }).ToList();

        var members = await GetMembersAsync(projectId, ct);

        return new ProjectExportDto(projectDto, taskDtos, members);
    }

    public async Task<RecurringPreviewDto?> GetRecurringPreviewAsync(Guid taskId, Guid userId, int count, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == userId, ct);
        if (!isMember) return null;

        if (!task.IsRecurring || string.IsNullOrWhiteSpace(task.RecurrenceRule))
        {
            // Fallback: if not configured, preview based on DueDate (weekly x count as a stub)
            if (task.DueDate is null)
                return new RecurringPreviewDto(task.Id, task.Title, null, []);

            var upcoming = new List<DateTime>();
            var baseDue = task.DueDate.Value.Date;
            for (int i = 1; i <= Math.Max(1, Math.Min(count, 52)); i++)
            {
                upcoming.Add(baseDue.AddDays(7 * i));
            }
            return new RecurringPreviewDto(task.Id, task.Title, baseDue, upcoming);
        }

        // Very small stub parser for common patterns like "FREQ=WEEKLY;INTERVAL=1" or "FREQ=MONTHLY"
        var rule = task.RecurrenceRule!.Trim().ToUpperInvariant();
        var baseDate = task.DueDate?.Date ?? task.StartDate?.Date ?? DateTime.UtcNow.Date;

        var upcomingDates = new List<DateTime>();
        int n = Math.Max(1, Math.Min(count, 104));

        if (rule.Contains("WEEKLY"))
        {
            int interval = 1;
            var m = System.Text.RegularExpressions.Regex.Match(rule, @"INTERVAL=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var iv)) interval = Math.Max(1, iv);
            for (int i = 1; i <= n; i++)
                upcomingDates.Add(baseDate.AddDays(7 * interval * i));
        }
        else if (rule.Contains("MONTHLY"))
        {
            int interval = 1;
            var m = System.Text.RegularExpressions.Regex.Match(rule, @"INTERVAL=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var iv)) interval = Math.Max(1, iv);
            for (int i = 1; i <= n; i++)
                upcomingDates.Add(baseDate.AddMonths(interval * i));
        }
        else if (rule.Contains("DAILY"))
        {
            int interval = 1;
            var m = System.Text.RegularExpressions.Regex.Match(rule, @"INTERVAL=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var iv)) interval = Math.Max(1, iv);
            for (int i = 1; i <= n; i++)
                upcomingDates.Add(baseDate.AddDays(interval * i));
        }
        else
        {
            // Unknown rule -> weekly fallback
            for (int i = 1; i <= n; i++)
                upcomingDates.Add(baseDate.AddDays(7 * i));
        }

        return new RecurringPreviewDto(task.Id, task.Title, task.DueDate, upcomingDates);
    }

    public async Task<IReadOnlyList<RecurringInstanceDto>?> GetRecurringInstancesPreviewAsync(Guid taskId, Guid userId, int count, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return null;

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == userId, ct);
        if (!isMember) return null;

        var dates = GenerateUpcomingDates(
            baseDate: task.DueDate?.Date ?? task.StartDate?.Date ?? DateTime.UtcNow.Date,
            rule: task.IsRecurring ? task.RecurrenceRule : null,
            count: Math.Max(1, Math.Min(count, 104)));

        if (dates.Count == 0 && task.DueDate is null)
        {
            // no usable base -> empty
            return [];
        }

        // If not recurring and no rule, still generate from DueDate (weekly fallback)
        if (!task.IsRecurring && string.IsNullOrWhiteSpace(task.RecurrenceRule) && task.DueDate is not null && dates.Count == 0)
        {
            var baseDue = task.DueDate.Value.Date;
            for (int i = 1; i <= Math.Max(1, Math.Min(count, 52)); i++)
                dates.Add(baseDue.AddDays(7 * i));
        }

        var instances = dates.Select(d => new RecurringInstanceDto(
            TemplateTaskId: task.Id,
            Title: task.Title,
            Description: task.Description,
            Priority: task.Priority,
            EstimatedHours: task.EstimatedHours,
            DueDate: d,
            Tags: task.Tags,
            Status: "todo"
        )).ToList();

        return instances;
    }

    // Shared generator for recurring previews/instances
    private static List<DateTime> GenerateUpcomingDates(DateTime baseDate, string? rule, int count)
    {
        var list = new List<DateTime>();
        if (string.IsNullOrWhiteSpace(rule))
            return list;

        var r = rule.Trim().ToUpperInvariant();
        int n = Math.Max(1, Math.Min(count, 104));

        if (r.Contains("WEEKLY"))
        {
            int interval = 1;
            var m = System.Text.RegularExpressions.Regex.Match(r, @"INTERVAL=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var iv)) interval = Math.Max(1, iv);
            for (int i = 1; i <= n; i++)
                list.Add(baseDate.AddDays(7 * interval * i));
        }
        else if (r.Contains("MONTHLY"))
        {
            int interval = 1;
            var m = System.Text.RegularExpressions.Regex.Match(r, @"INTERVAL=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var iv)) interval = Math.Max(1, iv);
            for (int i = 1; i <= n; i++)
                list.Add(baseDate.AddMonths(interval * i));
        }
        else if (r.Contains("DAILY"))
        {
            int interval = 1;
            var m = System.Text.RegularExpressions.Regex.Match(r, @"INTERVAL=(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var iv)) interval = Math.Max(1, iv);
            for (int i = 1; i <= n; i++)
                list.Add(baseDate.AddDays(interval * i));
        }
        else
        {
            for (int i = 1; i <= n; i++)
                list.Add(baseDate.AddDays(7 * i));
        }

        return list;
    }

    public async Task<(bool CanComplete, IReadOnlyList<Guid> UnmetDependencyIds)> ValidateTaskDependenciesAsync(Guid taskId, Guid userId, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return (false, []);

        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == task.ProjectId && m.UserId == userId, ct);
        if (!isMember) return (false, []);

        if (string.IsNullOrWhiteSpace(task.Dependencies))
            return (true, []);

        // Parse comma-separated GUIDs (tolerant of spaces)
        var ids = task.Dependencies!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return (true, []);

        var depTasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(ct);

        var unmet = depTasks
            .Where(t => t.Status != "done")
            .Select(t => t.Id)
            .ToList();

        // Also surface any dependency IDs that don't exist (treated as unmet)
        var found = depTasks.Select(t => t.Id).ToHashSet();
        var missing = ids.Where(id => !found.Contains(id)).ToList();
        unmet.AddRange(missing);

        return (unmet.Count == 0, unmet);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Progress
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<int> RecalculateProgressAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return 0;

        var tasks = await db.ProjectTasks
            .Where(t => t.ProjectId == projectId && t.ParentTaskId == null)
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            project.Progress = 0;
            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var done = tasks.Count(t => t.Status == "done");
        var progress = (int)Math.Round(done * 100.0 / tasks.Count);

        project.Progress = Math.Clamp(progress, 0, 100);
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return project.Progress;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Mapping
    // ──────────────────────────────────────────────────────────────────────────

    private async Task<ProjectDto?> MapToProjectDtoAsync(Guid projectId, CancellationToken ct)
    {
        var p = await db.Projects
            .AsNoTracking()
            .Include(x => x.Owner)
            .Include(x => x.Members)
            .Include(x => x.Tasks)
            .FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return null;

        var todo = p.Tasks.Count(t => t.Status == "todo");
        var doing = p.Tasks.Count(t => t.Status == "doing");
        var review = p.Tasks.Count(t => t.Status == "review");
        var done = p.Tasks.Count(t => t.Status == "done");

        return new ProjectDto(
            p.Id, p.Name, p.Description, p.Year, p.OwnerId,
            p.Owner?.Name ?? "Unknown", p.Owner?.Nik ?? "Unknown",
            p.StartDate, p.Deadline, p.Status, p.Progress, p.Risk, p.Obstacle, p.IsActive,
            p.Members.Count, p.Tasks.Count, todo, doing, review, done,
            p.CreatedAt, p.UpdatedAt);
    }

    private async Task<ProjectTaskDto?> MapToTaskDtoAsync(Guid taskId, CancellationToken ct)
    {
        var t = await db.ProjectTasks
            .AsNoTracking()
            .Include(x => x.CreatedBy)
            .Include(x => x.Assignees).ThenInclude(a => a.User)
            .Include(x => x.Subtasks)
            .FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (t is null) return null;

        var ass = t.Assignees.Select(a => new ProjectTaskAssigneeDto(a.UserId, a.User.Nik, a.User.Name, a.Role, a.AssignedAt)).ToList();
        var subCount = t.Subtasks.Count;
        var subDone = t.Subtasks.Count(s => s.Status == "done");

        // Fase 2 — Diskusi Tugas: penanda jumlah komentar, lampiran, pengingat aktif.
        var commentCount = await db.ProjectTaskComments.CountAsync(c => c.TaskId == taskId && !c.IsDeleted, ct);
        var attachmentCount = await db.ProjectTaskAttachments.CountAsync(a => a.TaskId == taskId && !a.IsDeleted, ct);
        var hasReminder = await db.ProjectTaskReminders.AnyAsync(r => r.TaskId == taskId && r.SentAt == null, ct);

        return new ProjectTaskDto(
            t.Id, t.ProjectId, t.ParentTaskId, t.Title, t.Description, t.Status, t.Priority,
            t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours, t.SortOrder,
            t.Dependencies, t.IsRecurring, t.RecurrenceRule, t.IsTemplate,
            t.Tags,
            t.CreatedById, t.CreatedBy?.Name ?? "Unknown",
            ass, subCount, subDone, t.CreatedAt, t.UpdatedAt,
            commentCount, attachmentCount, hasReminder);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: Chat room per project (interconnect)
    // ──────────────────────────────────────────────────────────────────────────

    /// Adds the ChatRoom + ProjectRoom + initial members in the same transaction as project creation.
    private void CreateProjectRoom(Project project, Guid actorId, Guid ownerId, DateTime now)
    {
        var room = new ChatRoom
        {
            Id = Guid.NewGuid(),
            Type = "group",
            Name = project.Name,
            Description = $"Project room: {project.Name}",
            IsActive = true,
            CreatedAt = now,
            LastMessageAt = now
        };

        db.ChatRooms.Add(room);
        db.ProjectRooms.Add(new ProjectRoom
        {
            ProjectId = project.Id,
            RoomId = room.Id,
            CreatedAt = now
        });

        // Creator is owner of the chat room; project owner joins as admin when different
        db.ChatRoomMembers.Add(new ChatRoomMember
        {
            Id = Guid.NewGuid(), RoomId = room.Id, UserId = actorId, Role = "owner", JoinedAt = now
        });
        if (ownerId != actorId)
        {
            db.ChatRoomMembers.Add(new ChatRoomMember
            {
                Id = Guid.NewGuid(), RoomId = room.Id, UserId = ownerId, Role = "admin", JoinedAt = now
            });
        }
    }

    /// Syncs a newly added project member into the linked chat room (no-op if room missing).
    private async Task SyncProjectMemberToRoomAsync(Guid projectId, Guid userId, string projectRole, CancellationToken ct)
    {
        var pr = await db.ProjectRooms.FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);
        if (pr is null) return;

        var exists = await db.ChatRoomMembers.AnyAsync(m => m.RoomId == pr.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (exists) return;

        var chatRole = projectRole switch
        {
            "owner" => "admin",
            "manager" => "admin",
            _ => "member"
        };

        db.ChatRoomMembers.Add(new ChatRoomMember
        {
            Id = Guid.NewGuid(), RoomId = pr.RoomId, UserId = userId, Role = chatRole, JoinedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// Removes a member from the linked chat room (soft-leave, no-op if room missing).
    private async Task RemoveMemberFromProjectRoomAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var pr = await db.ProjectRooms.FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);
        if (pr is null) return;

        var link = await db.ChatRoomMembers
            .FirstOrDefaultAsync(m => m.RoomId == pr.RoomId && m.UserId == userId && m.LeftAt == null, ct);
        if (link is null) return;

        link.LeftAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProjectChatRoomDto?> GetProjectChatRoomAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (!isMember) return null;

        // Backward compat: pre-existing projects (created before ipop-21) have no linked room.
        // Lazily create one on first access so every project gains its interconnect room.
        var pr = await db.ProjectRooms.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectId == projectId, ct);
        if (pr is null)
        {
            pr = await EnsureProjectRoomAsync(projectId, ct);
            if (pr is null) return null;
        }

        var room = await db.ChatRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == pr.RoomId, ct);
        if (room is null) return null;

        var memberCount = await db.ChatRoomMembers.CountAsync(m => m.RoomId == room.Id && m.LeftAt == null, ct);

        return new ProjectChatRoomDto(room.Id, room.Name ?? room.Id.ToString(), memberCount, room.IsActive);
    }

    /// Creates the linked chat room for a project if missing (idempotent), seeding all current members.
    private async Task<ProjectRoom?> EnsureProjectRoomAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct);
        if (project is null) return null;

        var members = await db.ProjectMembers.Where(m => m.ProjectId == projectId).ToListAsync(ct);
        if (members.Count == 0) return null;

        var now = DateTime.UtcNow;
        var room = new ChatRoom
        {
            Id = Guid.NewGuid(),
            Type = "group",
            Name = project.Name,
            Description = $"Project room: {project.Name}",
            IsActive = true,
            CreatedAt = now,
            LastMessageAt = now
        };
        db.ChatRooms.Add(room);

        var link = new ProjectRoom { ProjectId = projectId, RoomId = room.Id, CreatedAt = now };
        db.ProjectRooms.Add(link);

        foreach (var m in members)
        {
            var chatRole = m.Role switch
            {
                "owner" => "admin",
                "manager" => "admin",
                _ => "member"
            };
            db.ChatRoomMembers.Add(new ChatRoomMember
            {
                Id = Guid.NewGuid(), RoomId = room.Id, UserId = m.UserId, Role = chatRole, JoinedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        return link;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: Gantt date-mapping + critical path
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ProjectGanttDto?> GetGanttAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return null;

        var tasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .Include(t => t.Assignees)
            .Include(t => t.Subtasks)
            .ToListAsync(ct);

        if (tasks.Count == 0)
        {
            return new ProjectGanttDto(project.Id, project.Name, project.StartDate, project.Deadline,
                0, [], []);
        }

        // Chart window = min start .. max end across project and dated tasks
        var starts = tasks.Where(t => t.StartDate.HasValue).Select(t => t.StartDate!.Value).ToList();
        var ends = tasks.Where(t => t.DueDate.HasValue).Select(t => t.DueDate!.Value).ToList();
        if (project.StartDate.HasValue) starts.Add(project.StartDate.Value);
        if (project.Deadline.HasValue) ends.Add(project.Deadline.Value);

        if (starts.Count == 0 || ends.Count == 0)
        {
            // No dates anywhere: return empty chart window with the tasks but no positions
            var undated = tasks.Select(t => new GanttTaskDto(
                t.Id, t.Title, t.ParentTaskId, t.Status, t.Priority,
                t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours,
                t.Assignees.Select(a => a.UserId).ToList(),
                t.Subtasks.Count, t.Subtasks.Count(s => s.Status == "done"), t.Tags,
                null, null, false)).ToList();
            return new ProjectGanttDto(project.Id, project.Name, null, null, 0, undated, []);
        }

        var windowStart = starts.Min().Date;
        var windowEnd = ends.Max().Date;
        var totalDays = Math.Max(1, (int)(windowEnd - windowStart).TotalDays + 1);

        // Day-index positions for dated tasks
        var dayIndexes = new Dictionary<Guid, (int Start, int Duration)>();
        foreach (var t in tasks.Where(t => t.StartDate.HasValue && t.DueDate.HasValue))
        {
            var s = Math.Max(0, (int)(t.StartDate!.Value.Date - windowStart).TotalDays);
            var e = Math.Max(s, (int)(t.DueDate!.Value.Date - windowStart).TotalDays);
            dayIndexes[t.Id] = (s, e - s + 1);
        }

        // Critical path via dependency graph (forward + backward pass)
        var critical = ComputeCriticalPath(tasks, dayIndexes);

        var ganttTasks = tasks.Select(t =>
        {
            var hasPos = dayIndexes.TryGetValue(t.Id, out var pos);
            return new GanttTaskDto(
                t.Id, t.Title, t.ParentTaskId, t.Status, t.Priority,
                t.StartDate, t.DueDate, t.EstimatedHours, t.ActualHours,
                t.Assignees.Select(a => a.UserId).ToList(),
                t.Subtasks.Count, t.Subtasks.Count(s => s.Status == "done"), t.Tags,
                hasPos ? pos.Start : (int?)null,
                hasPos ? pos.Duration : (int?)null,
                critical.Contains(t.Id));
        }).ToList();

        return new ProjectGanttDto(project.Id, project.Name, windowStart, windowEnd, totalDays, ganttTasks, critical);
    }

    /// Forward/backward pass over the dependency graph. Duration = day-index span when dated, else 1.
    /// Returns the set of task IDs on the critical path (zero slack), ordered by earliest start.
    private static IReadOnlyList<Guid> ComputeCriticalPath(
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyDictionary<Guid, (int Start, int Duration)> dayIndexes)
    {
        var ids = tasks.Select(t => t.Id).ToHashSet();

        // predecessor -> successor edges from Dependencies ("GUID,GUID")
        var successors = new Dictionary<Guid, List<Guid>>();
        foreach (var t in tasks)
        {
            foreach (var dep in ParseDependencyIds(t.Dependencies))
            {
                if (!ids.Contains(dep)) continue; // dangling dependency: ignore for CP
                if (!successors.TryGetValue(dep, out var list)) successors[dep] = list = [];
                list.Add(t.Id);
            }
        }

        int Duration(Guid id)
        {
            if (dayIndexes.TryGetValue(id, out var p)) return Math.Max(1, p.Duration);
            return 1;
        }

        // Kahn topological order
        var indegree = tasks.ToDictionary(t => t.Id, _ => 0);
        foreach (var t in tasks)
            foreach (var dep in ParseDependencyIds(t.Dependencies))
                if (ids.Contains(dep)) indegree[t.Id]++;

        var queue = new Queue<Guid>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var topo = new List<Guid>();
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            topo.Add(n);
            if (!successors.TryGetValue(n, out var succ)) continue;
            foreach (var s in succ)
            {
                indegree[s]--;
                if (indegree[s] == 0) queue.Enqueue(s);
            }
        }

        // Forward pass: earliest finish (EF) = ES + duration, ES = max EF of predecessors
        var ef = new Dictionary<Guid, int>();
        foreach (var n in topo)
        {
            var es = 0;
            foreach (var (pred, succ) in successors.SelectMany(kv => kv.Value.Select(v => (Pred: kv.Key, Succ: v))))
            {
                if (succ == n && ef.TryGetValue(pred, out var pe)) es = Math.Max(es, pe);
            }
            ef[n] = es + Duration(n);
        }

        var projectFinish = topo.Count > 0 ? ef.Values.Max() : 0;

        // Backward pass: latest finish (LF) = min LS over successors; LF = projectFinish for sinks.
        // In reversed topo order successors are already resolved.
        var lf = new Dictionary<Guid, int>();
        foreach (var n in topo.AsEnumerable().Reverse())
        {
            if (!successors.TryGetValue(n, out var succ) || succ.Count == 0)
                lf[n] = projectFinish;
            else
                lf[n] = succ.Select(s => lf[s] - Duration(s)).Min();
        }

        // Slack = LS - ES = (LF - duration) - (EF - duration) = LF - EF
        return topo.Where(n => lf[n] - ef[n] <= 0).ToList();
    }

    private static IEnumerable<Guid> ParseDependencyIds(string? dependencies)
    {
        if (string.IsNullOrWhiteSpace(dependencies)) yield break;
        foreach (var part in dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var g)) yield return g;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: WorkCalendar
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ProjectWorkCalendarDto?> GetWorkCalendarAsync(Guid projectId, Guid userId, DateTime from, DateTime to, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;

        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        if ((to - from).TotalDays > 366) to = from.AddDays(366);

        var entries = await db.WorkCalendarEntries
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId && e.Date >= from && e.Date <= to)
            .Include(e => e.CreatedBy)
            .OrderBy(e => e.Date)
            .Select(e => new CalendarEntryDto(
                e.Id, e.Date, e.Kind, e.Title, e.Note,
                e.CreatedById, e.CreatedBy.Name, e.CreatedAt, e.UpdatedAt))
            .ToListAsync(ct);

        // Tasks that fall on each day within the range
        var tasks = await db.ProjectTasks
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId &&
                        ((t.StartDate.HasValue && t.StartDate.Value.Date <= to) ||
                         (t.DueDate.HasValue && t.DueDate.Value.Date >= from)))
            .Include(t => t.Assignees)
            .ToListAsync(ct);

        var dayTasks = new List<CalendarDayTaskDto>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var onDay = tasks.Where(t =>
                (!t.StartDate.HasValue || t.StartDate.Value.Date <= d) &&
                (!t.DueDate.HasValue || t.DueDate.Value.Date >= d)).ToList();

            if (onDay.Count == 0) continue;
            dayTasks.Add(new CalendarDayTaskDto(
                d, onDay.Count,
                onDay.Count(t => t.Status == "todo"),
                onDay.Count(t => t.Status == "doing"),
                onDay.Count(t => t.Status == "done"),
                onDay.Sum(t => t.EstimatedHours ?? 0),
                onDay.Sum(t => t.ActualHours ?? 0),
                onDay.Select(t => t.Id).ToList(),
                onDay.Count(t => t.Status == "review")));
        }

        return new ProjectWorkCalendarDto(projectId, entries.Count, entries, dayTasks);
    }

    public async Task<MemberResultDto> UpsertCalendarEntryAsync(Guid actorId, Guid projectId, UpsertCalendarEntryRequest req, CancellationToken ct)
    {
        var isOwnerOrManager = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"), ct);
        if (!isOwnerOrManager) return new MemberResultDto(false, "forbidden");

        var validKinds = new[] { "workday", "holiday", "offday" };
        if (!validKinds.Contains(req.Kind)) return new MemberResultDto(false, "invalid_kind");

        var date = req.Date.Date;
        var now = DateTime.UtcNow;
        var existing = await db.WorkCalendarEntries.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Date == date, ct);

        if (existing is null)
        {
            db.WorkCalendarEntries.Add(new WorkCalendarEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Date = date,
                Kind = req.Kind,
                Title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim(),
                Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
                CreatedById = actorId,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existing.Kind = req.Kind;
            existing.Title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim();
            existing.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }

    public async Task<MemberResultDto> DeleteCalendarEntryAsync(Guid actorId, Guid projectId, DateTime date, CancellationToken ct)
    {
        var isOwnerOrManager = await db.ProjectMembers
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && (m.Role == "owner" || m.Role == "manager"), ct);
        if (!isOwnerOrManager) return new MemberResultDto(false, "forbidden");

        var entry = await db.WorkCalendarEntries.FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Date == date.Date, ct);
        if (entry is null) return new MemberResultDto(true, null); // idempotent

        db.WorkCalendarEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return new MemberResultDto(true, null);
    }
    private Task<bool> IsAdminAsync(Guid userId, CancellationToken ct) =>
        db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

    /// Read access: project members, or any admin (decision: admins see all projects).
    private async Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct)
        || await IsAdminAsync(userId, ct);
}
