using IPop.Application.Realtime;
using IPop.Api.Rbac;
using IPop.Application.Chat;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").RequireAuthorization().RequirePermission("projects.read");

        // ──────────────────────────────────────────────────────────────────────
        // Projects
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects?status=&year=&page=1&pageSize=20
        g.MapGet("", async (string? status, int? year, int? page, int? pageSize,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);

            var result = await svc.GetProjectsAsync(userId.Value, status, year, p, size, ct);
            return Results.Ok(result);
        });

        // GET /api/projects/{id}
        g.MapGet("/{id:guid}", async (Guid id,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var project = await svc.GetProjectAsync(id, userId.Value, ct);
            return project is not null
                ? Results.Ok(project)
                : Results.Json(new { code = "project_not_found", message = "Project does not exist or you are not a member." }, statusCode: 404);
        });

        // POST /api/projects — create
        g.MapPost("", async (CreateProjectRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, IChatNotifier chat, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.CreateProjectAsync(userId.Value, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "invalid_name" or "invalid_year" or "invalid_owner" => 422,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.create", result.Project!.Id.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.Project!.Id);
            await SyncProjectRoomLiveAsync(db, chat, result.Project!.Id, added: null, removed: null);
            return Results.Ok(result.Project);
        }); // any signed-in user may create a project (decision 2026-09-23)

        // PUT /api/projects/{id}
        g.MapPut("/{id:guid}", async (Guid id, UpdateProjectRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.UpdateProjectAsync(userId.Value, id, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "project_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.update", id.ToString(), ct);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result.Project);
        }).RequirePermission("projects.write");

        // DELETE /api/projects/{id} (soft deactivate)
        g.MapDelete("/{id:guid}", async (Guid id,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.DeleteProjectAsync(userId.Value, id, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "project_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.delete", id.ToString(), ct);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result);
        }).RequirePermission("projects.admin");

        // ──────────────────────────────────────────────────────────────────────
        // Groups
        // ──────────────────────────────────────────────────────────────────────

        g.MapGet("/{id:guid}/groups", async (Guid id, DateTime? localToday, HttpContext http,
            IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var groups = await svc.GetGroupsAsync(id, actor.Value, ct, localToday);
            return groups is null ? Results.NotFound() : Results.Ok(groups);
        });

        g.MapPost("/{id:guid}/groups", async (Guid id, CreateProjectGroupRequest req, HttpContext http,
            IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.CreateGroupAsync(actor.Value, id, req, ct);
            if (!result.Success) return GroupError(result.Error);
            await Audit(http, db, "projects.group.create", result.Group!.Id.ToString(), ct);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result.Group);
        }).RequirePermission("projects.write");

        g.MapPut("/groups/{groupId:guid}", async (Guid groupId, UpdateProjectGroupRequest req, HttpContext http,
            IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.UpdateGroupAsync(actor.Value, groupId, req, ct);
            if (!result.Success) return GroupError(result.Error);
            await Audit(http, db, "projects.group.update", groupId.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.Group!.ProjectId);
            return Results.Ok(result.Group);
        }).RequirePermission("projects.write");

        g.MapPut("/{id:guid}/groups/order", async (Guid id, ReorderProjectGroupsRequest req, HttpContext http,
            IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.ReorderGroupsAsync(actor.Value, id, req, ct);
            if (!result.Success) return GroupError(result.Error);
            await Audit(http, db, "projects.group.reorder", id.ToString(), ct);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        g.MapDelete("/groups/{groupId:guid}", async (Guid groupId, HttpContext http,
            IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var projectId = await db.ProjectGroups.Where(g => g.Id == groupId).Select(g => (Guid?)g.ProjectId).FirstOrDefaultAsync(ct);
            var result = await svc.DeleteGroupAsync(actor.Value, groupId, ct);
            if (!result.Success) return GroupError(result.Error);
            await Audit(http, db, "projects.group.delete", groupId.ToString(), ct);
            await PushProjectChangedAsync(realtime, projectId);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // POST /api/projects/{id}/groups/restore — Undo for a group delete (body = the delete snapshot)
        g.MapPost("/{id:guid}/groups/restore", async (Guid id, DeletedProjectGroupDto snapshot, HttpContext http,
            IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.RestoreGroupAsync(actor.Value, id, snapshot, ct);
            if (!result.Success) return GroupError(result.Error);
            await Audit(http, db, "projects.group.restore", snapshot.GroupId.ToString(), ct);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result.Group);
        }).RequirePermission("projects.write");

        g.MapPut("/tasks/{taskId:guid}/group", async (Guid taskId, MoveTaskToGroupRequest req, HttpContext http,
            IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actor = await GetUserIdAsync(http, db);
            if (actor is null) return Results.Unauthorized();
            var result = await svc.MoveTaskToGroupAsync(actor.Value, taskId, req, ct);
            if (!result.Success) return GroupError(result.Error);
            await Audit(http, db, "projects.task.group", taskId.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.Task!.ProjectId);
            return Results.Ok(result.Task);
        }).RequirePermission("projects.write");

        // ──────────────────────────────────────────────────────────────────────
        // Members
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/members
        g.MapGet("/{id:guid}/members", async (Guid id,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            // Verify caller can see the project
            var proj = await svc.GetProjectAsync(id, userId.Value, ct);
            if (proj is null) return Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);

            var members = await svc.GetMembersAsync(id, ct);
            return Results.Ok(members);
        });

        // POST /api/projects/{id}/members — { userId, role }
        g.MapPost("/{id:guid}/members", async (Guid id, AddProjectMemberRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, IChatNotifier chat, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.AddMemberAsync(userId.Value, id, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.member.add", $"{id}:{req.UserId}", ct);
            await PushProjectChangedAsync(realtime, id);
            var addedId = req.UserId != Guid.Empty ? req.UserId
                : await db.Users.AsNoTracking().Where(u => u.Nik == (req.Nik ?? "").Trim()).Select(u => u.Id).FirstOrDefaultAsync(ct);
            await SyncProjectRoomLiveAsync(db, chat, id, added: addedId, removed: null);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // DELETE /api/projects/{id}/members/{userId}
        g.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, IChatNotifier chat, CancellationToken ct) =>
        {
            var actorId = await GetUserIdAsync(http, db);
            if (actorId is null) return Results.Unauthorized();

            var result = await svc.RemoveMemberAsync(actorId.Value, id, userId, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" or "cannot_remove_last_owner" => 403,
                    "member_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.member.remove", $"{id}:{userId}", ct);
            await PushProjectChangedAsync(realtime, id);
            await SyncProjectRoomLiveAsync(db, chat, id, added: null, removed: userId);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // ──────────────────────────────────────────────────────────────────────
        // Tasks
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{projectId}/tasks?parentId=&status=
        g.MapGet("/{projectId:guid}/tasks", async (Guid projectId, Guid? parentId, string? status,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var proj = await svc.GetProjectAsync(projectId, userId.Value, ct);
            if (proj is null) return Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);

            // If status provided, use list; else use tree for parent (top-level when null)
            if (!string.IsNullOrWhiteSpace(status))
            {
                var list = await svc.GetTaskListAsync(projectId, userId.Value, status, 1, 500, ct);
                return Results.Ok(list);
            }

            var tasks = await svc.GetTasksAsync(projectId, parentId, ct);
            return Results.Ok(tasks);
        });

        // GET /api/projects/tasks/{taskId}
        g.MapGet("/tasks/{taskId:guid}", async (Guid taskId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var task = await svc.GetTaskAsync(taskId, userId.Value, ct);
            return task is not null
                ? Results.Ok(task)
                : Results.Json(new { code = "task_not_found", message = "Task not found or access denied." }, statusCode: 404);
        });

        // POST /api/projects/{projectId}/tasks — create task (optionally with parentId for subtask)
        g.MapPost("/{projectId:guid}/tasks", async (Guid projectId, CreateTaskRequest body,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            // Ensure projectId matches body
            var req = new CreateTaskRequest(projectId, body.ParentTaskId, body.Title, body.Description,
                body.Status, body.Priority, body.StartDate, body.DueDate, body.EstimatedHours, body.Dependencies, body.Tags, body.GroupId);

            var result = await svc.CreateTaskAsync(userId.Value, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "parent_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.task.create", result.Task!.Id.ToString(), ct);
            await PushProjectChangedAsync(realtime, projectId);
            return Results.Ok(result.Task);
        }).RequirePermission("projects.write");

        // PUT /api/projects/tasks/{taskId}
        g.MapPut("/tasks/{taskId:guid}", async (Guid taskId, UpdateTaskRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.UpdateTaskAsync(userId.Value, taskId, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "task_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.task.update", taskId.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.Task!.ProjectId);
            return Results.Ok(result.Task);
        }).RequirePermission("projects.write");

        // DELETE /api/projects/tasks/{taskId}
        g.MapDelete("/tasks/{taskId:guid}", async (Guid taskId,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
            var result = await svc.DeleteTaskAsync(userId.Value, taskId, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "task_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.task.delete", taskId.ToString(), ct);
            await PushProjectChangedAsync(realtime, task?.ProjectId);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // Assignees
        g.MapPost("/tasks/{taskId:guid}/assignees", async (Guid taskId, AssignRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.AssignUserAsync(userId.Value, taskId, req.UserId, req.Role, ct);
            if (!result.Success)
            {
                var statusCode = result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.task.assign", $"{taskId}:{req.UserId}", ct);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        g.MapDelete("/tasks/{taskId:guid}/assignees/{userId:guid}", async (Guid taskId, Guid userId,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var actorId = await GetUserIdAsync(http, db);
            if (actorId is null) return Results.Unauthorized();

            var result = await svc.UnassignUserAsync(actorId.Value, taskId, userId, ct);
            if (!result.Success)
            {
                var statusCode = result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.task.unassign", $"{taskId}:{userId}", ct);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // ──────────────────────────────────────────────────────────────────────
        // Kanban
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/kanban
        g.MapGet("/{id:guid}/kanban", async (Guid id, Guid? groupId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var board = await svc.GetKanbanAsync(id, userId.Value, ct, groupId);
            return board is not null
                ? Results.Ok(board)
                : Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);
        });

        // POST /api/projects/tasks/{taskId}/move — { status, sortOrder? }
        g.MapPost("/tasks/{taskId:guid}/move", async (Guid taskId, TaskMoveRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.MoveTaskAsync(userId.Value, taskId, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "task_not_found" => 404,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }

            await Audit(http, db, "projects.task.move", taskId.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.Task?.ProjectId ?? await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(result.Task);
        }).RequirePermission("projects.write");

        // ──────────────────────────────────────────────────────────────────────
        // List (paged) + Timeline (Gantt stub)
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/tasks/list?status=&assigneeId=&unassigned=&page=1&pageSize=50
        g.MapGet("/{id:guid}/tasks/list", async (Guid id, string? status, Guid? assigneeId, bool? unassigned, Guid? groupId, int? page, int? pageSize,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var proj = await svc.GetProjectAsync(id, userId.Value, ct);
            if (proj is null) return Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);

            var result = await svc.GetTaskListAsync(id, userId.Value, status, p, size, ct, assigneeId, unassigned == true, groupId);
            return Results.Ok(result);
        });

        // GET /api/projects/{id}/timeline — returns flat list of tasks with dates for Gantt (no critical path calc in v1)
        g.MapGet("/{id:guid}/timeline", async (Guid id, Guid? groupId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var timeline = await svc.GetTimelineAsync(id, userId.Value, ct, groupId);
            return timeline is not null
                ? Results.Ok(timeline)
                : Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);
        });

        // ──────────────────────────────────────────────────────────────────────
        // DETAIL MAX: Workload, Export, Recurring, Dependency Validation
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/workload?localToday=2026-09-27 — local calendar cutoff for overdue counts
        g.MapGet("/{id:guid}/workload", async (Guid id, DateTime? localToday, Guid? groupId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var wl = await svc.GetProjectWorkloadAsync(id, userId.Value, ct, localToday, groupId);
            return wl is not null
                ? Results.Ok(wl)
                : Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);
        });

        // GET /api/projects/{id}/export — snapshot for export (project + tasks flat + members)
        g.MapGet("/{id:guid}/export", async (Guid id,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var exp = await svc.GetProjectExportDataAsync(id, userId.Value, ct);
            return exp is not null
                ? Results.Ok(exp)
                : Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);
        }).RequirePermission("projects.read");

        // GET /api/projects/{id}/export.csv — real CSV file download
        g.MapGet("/{id:guid}/export.csv", async (Guid id,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var exp = await svc.GetProjectExportDataAsync(id, userId.Value, ct);
            if (exp is null)
                return Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("ID;Title;Status;Priority;Assignees;Start;Due;EstimatedHours;ActualHours;Tags");
            foreach (var t in exp.Tasks)
            {
                var assignees = string.Join(", ", t.Assignees.Select(a => a.Name));
                var tags = string.Join(", ", t.Tags);
                sb.AppendLine(string.Join(';',
                    t.Id,
                    DirectoryEndpoints.CsvField(t.Title),
                    DirectoryEndpoints.CsvField(t.Status),
                    DirectoryEndpoints.CsvField(t.Priority),
                    DirectoryEndpoints.CsvField(assignees),
                    t.StartDate?.ToString("yyyy-MM-dd") ?? "",
                    t.DueDate?.ToString("yyyy-MM-dd") ?? "",
                    t.EstimatedHours,
                    t.ActualHours,
                    DirectoryEndpoints.CsvField(tags)));
            }

            var safeName = string.Concat(exp.Project.Name.Where(c => !System.IO.Path.GetInvalidFileNameChars().Contains(c)));
            var filename = $"{safeName}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", filename);
        }).RequirePermission("projects.read");

        // GET /api/projects/tasks/{taskId}/recurring-preview?count=5
        g.MapGet("/tasks/{taskId:guid}/recurring-preview", async (Guid taskId, int? count,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var n = Math.Clamp(count ?? 5, 1, 104);
            var preview = await svc.GetRecurringPreviewAsync(taskId, userId.Value, n, ct);
            return preview is not null
                ? Results.Ok(preview)
                : Results.Json(new { code = "task_not_found", message = "Task not found or access denied." }, statusCode: 404);
        });

        // GET /api/projects/tasks/{taskId}/recurring-instances?count=N — richer projected instances (virtual)
        g.MapGet("/tasks/{taskId:guid}/recurring-instances", async (Guid taskId, int? count,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var n = Math.Clamp(count ?? 5, 1, 104);
            var instances = await svc.GetRecurringInstancesPreviewAsync(taskId, userId.Value, n, ct);
            return instances is not null
                ? Results.Ok(instances)
                : Results.Json(new { code = "task_not_found", message = "Task not found or access denied." }, statusCode: 404);
        });

        // GET /api/projects/tasks/{taskId}/dependencies/validate — returns unmet dependency ids if any
        g.MapGet("/tasks/{taskId:guid}/dependencies/validate", async (Guid taskId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var (canComplete, unmet) = await svc.ValidateTaskDependenciesAsync(taskId, userId.Value, ct);
            return Results.Ok(new { canComplete, unmetDependencyIds = unmet });
        });

        // ──────────────────────────────────────────────────────────────────────
        // ipop-21 PERFECTION: Gantt date-mapping
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/gantt — date-mapped chart data with critical path
        g.MapGet("/{id:guid}/gantt", async (Guid id, Guid? groupId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var gantt = await svc.GetGanttAsync(id, userId.Value, ct, groupId);
            return gantt is not null
                ? Results.Ok(gantt)
                : Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);
        });

        // ──────────────────────────────────────────────────────────────────────
        // ipop-21 PERFECTION: WorkCalendar
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/work-calendar?from=2026-01-01&to=2026-12-31
        g.MapGet("/{id:guid}/work-calendar", async (Guid id, DateTime? from, DateTime? to,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var f = from ?? DateTime.UtcNow.AddMonths(-1);
            var t = to ?? DateTime.UtcNow.AddMonths(1);
            var cal = await svc.GetWorkCalendarAsync(id, userId.Value, f, t, ct);
            return cal is not null
                ? Results.Ok(cal)
                : Results.Json(new { code = "project_not_found", message = "Not a member of this project." }, statusCode: 404);
        });

        // PUT /api/projects/{id}/work-calendar/entries — { date, kind, title?, note? }
        g.MapPut("/{id:guid}/work-calendar/entries", async (Guid id, UpsertCalendarEntryRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.UpsertCalendarEntryAsync(userId.Value, id, req, ct);
            if (!result.Success)
            {
                var statusCode = result.Error switch
                {
                    "forbidden" => 403,
                    "invalid_kind" => 422,
                    _ => 422
                };
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // DELETE /api/projects/{id}/work-calendar/entries?date=2026-06-01
        g.MapDelete("/{id:guid}/work-calendar/entries", async (Guid id, DateTime date,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var result = await svc.DeleteCalendarEntryAsync(userId.Value, id, date, ct);
            if (!result.Success)
            {
                var statusCode = result.Error == "forbidden" ? 403 : 422;
                return Results.Json(new { code = result.Error, message = result.Error }, statusCode: statusCode);
            }
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // ──────────────────────────────────────────────────────────────────────
        // ipop-21 PERFECTION: Chat room per project (interconnect)
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/{id}/chat-room — returns the linked chat room for ORION to embed
        g.MapGet("/{id:guid}/chat-room", async (Guid id,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();

            var room = await svc.GetProjectChatRoomAsync(id, userId.Value, ct);
            return room is not null
                ? Results.Ok(room)
                : Results.Json(new { code = "room_not_found", message = "Project or chat room not found." }, statusCode: 404);
        });

        // ──────────────────────────────────────────────────────────────────────
        // Diskusi Tugas (Fase 2): komentar, aktivitas, lampiran, pengingat
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/tasks/{taskId}/comments — komentar + balasan satu tingkat.
        g.MapGet("/tasks/{taskId:guid}/comments", async (Guid taskId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskMember(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var rows = await db.ProjectTaskComments.AsNoTracking()
                .Where(c => c.TaskId == taskId && !c.IsDeleted)
                .Include(c => c.Author)
                .OrderBy(c => c.CreatedAt)
                .Select(c => new TaskCommentDto(c.Id, c.Author.Name, c.Author.Nik, c.Content, c.ParentCommentId, c.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/projects/tasks/{taskId}/comments — komentar baru / balasan.
        g.MapPost("/tasks/{taskId:guid}/comments", async (Guid taskId, AddTaskCommentRequest req,
            HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskWriter(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.Content) || req.Content.Trim().Length > 4000)
                return Results.Json(new { code = "content_invalid", message = "Comment is required (max 4000 characters)." }, statusCode: 422);
            if (req.ParentCommentId is { } pid)
            {
                var parentOk = await db.ProjectTaskComments.AnyAsync(c => c.Id == pid && c.TaskId == taskId && !c.IsDeleted && c.ParentCommentId == null, ct);
                if (!parentOk)
                    return Results.Json(new { code = "parent_not_found", message = "Parent comment not found (replies are single-level only)." }, statusCode: 404);
            }

            var now = DateTime.UtcNow;
            var comment = new ProjectTaskComment
            {
                Id = Guid.NewGuid(), TaskId = taskId, AuthorId = userId.Value,
                Content = req.Content.Trim(), ParentCommentId = req.ParentCommentId, CreatedAt = now
            };
            db.ProjectTaskComments.Add(comment);
            db.ProjectTaskActivities.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = taskId, ActorId = userId.Value,
                Action = req.ParentCommentId is null ? "comment_added" : "comment_replied", CreatedAt = now
            });
            await db.SaveChangesAsync(ct);

            var author = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(new TaskCommentDto(comment.Id, author?.Name ?? "Unknown", author?.Nik ?? "", comment.Content, comment.ParentCommentId, comment.CreatedAt));
        }).RequirePermission("projects.write");

        // DELETE /api/projects/tasks/{taskId}/comments/{id} — hapus (penulis atau pemilik proyek).
        g.MapDelete("/tasks/{taskId:guid}/comments/{commentId:guid}", async (Guid taskId, Guid commentId,
            HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskWriter(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Read-only project access." }, statusCode: 403);
            var comment = await db.ProjectTaskComments.FirstOrDefaultAsync(c => c.Id == commentId && c.TaskId == taskId, ct);
            if (comment is null || comment.IsDeleted)
                return Results.Json(new { code = "not_found", message = "Comment not found." }, statusCode: 404);

            var taskProjectId = await db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.ProjectId).FirstAsync(ct);
            var isAuthor = comment.AuthorId == userId.Value;
            var isOwner = await db.ProjectMembers.AnyAsync(m =>
                m.ProjectId == taskProjectId && m.UserId == userId.Value && m.Role == "owner", ct);
            if (!isAuthor && !isOwner)
                return Results.Json(new { code = "forbidden", message = "Only the author or project owner." }, statusCode: 403);

            comment.IsDeleted = true;
            db.ProjectTaskActivities.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = taskId, ActorId = userId.Value,
                Action = "comment_deleted", CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await PushProjectChangedAsync(realtime, taskProjectId);
            return Results.Ok(new { success = true });
        }).RequirePermission("projects.write");

        // GET /api/projects/tasks/{taskId}/activity — riwayat aktivitas kronologis.
        g.MapGet("/tasks/{taskId:guid}/activity", async (Guid taskId, int? page, int? pageSize,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskMember(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 50, 1, 100);
            var q = db.ProjectTaskActivities.AsNoTracking()
                .Where(a => a.TaskId == taskId)
                .OrderByDescending(a => a.CreatedAt);
            var total = await q.CountAsync(ct);
            var rows = await q.Skip((p - 1) * size).Take(size)
                .Include(a => a.Actor)
                .Select(a => new TaskActivityDto(a.Id, a.Actor.Name, a.Action, a.OldValue, a.NewValue, a.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(new PagedActivities(rows, total, p, size));
        });

        // GET /api/projects/tasks/{taskId}/attachments — daftar lampiran NAS tugas.
        g.MapGet("/tasks/{taskId:guid}/attachments", async (Guid taskId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskMember(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var rows = await db.ProjectTaskAttachments.AsNoTracking()
                .Where(a => a.TaskId == taskId && !a.IsDeleted)
                .OrderByDescending(a => a.CreatedAt)
                .Include(a => a.UploadedBy)
                .Select(a => new TaskAttachmentDto(a.Id, a.FileName, a.FileSize, a.UploadedBy.Name, a.SynologyServer, a.SynologyPath, a.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/projects/tasks/{taskId}/attachments — tautkan berkas NAS (referensi).
        g.MapPost("/tasks/{taskId:guid}/attachments", async (Guid taskId, AddTaskAttachmentRequest req,
            HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskWriter(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);
            if (string.IsNullOrWhiteSpace(req.FileName) || string.IsNullOrWhiteSpace(req.Server) || string.IsNullOrWhiteSpace(req.Path))
                return Results.Json(new { code = "invalid_attachment", message = "Name, server, and path are required." }, statusCode: 422);
            if (!req.Path.StartsWith('/'))
                return Results.Json(new { code = "invalid_path", message = "Invalid NAS path." }, statusCode: 422);

            var now = DateTime.UtcNow;
            var att = new ProjectTaskAttachment
            {
                Id = Guid.NewGuid(), TaskId = taskId, UploadedById = userId.Value,
                FileName = req.FileName.Trim(), FileSize = req.Size,
                SynologyServer = req.Server.Trim(), SynologyPath = req.Path.Trim(), CreatedAt = now
            };
            db.ProjectTaskAttachments.Add(att);
            db.ProjectTaskActivities.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = taskId, ActorId = userId.Value,
                Action = "attachment_added", NewValue = att.FileName, CreatedAt = now
            });
            await db.SaveChangesAsync(ct);

            var uploader = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(new TaskAttachmentDto(att.Id, att.FileName, att.FileSize, uploader?.Name ?? "Unknown", att.SynologyServer, att.SynologyPath, att.CreatedAt));
        }).RequirePermission("projects.write");

        // GET /api/projects/tasks/{taskId}/attachments/{id}/download — unduh via proxy NAS.
        g.MapGet("/tasks/{taskId:guid}/attachments/{attachmentId:guid}/download", async (Guid taskId, Guid attachmentId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskMember(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var att = await db.ProjectTaskAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TaskId == taskId && !a.IsDeleted, ct);
            if (att is null)
                return Results.Json(new { code = "not_found", message = "Attachment not found." }, statusCode: 404);

            // Alihkan ke endpoint unduh NAS yang sudah ada (membawa kredensial layanan).
            return Results.Redirect($"/api/files/download?server={Uri.EscapeDataString(att.SynologyServer)}&path={Uri.EscapeDataString(att.SynologyPath)}&inline=false");
        });

        // DELETE /api/projects/tasks/{taskId}/attachments/{id} — hapus tautan (soft delete).
        g.MapDelete("/tasks/{taskId:guid}/attachments/{attachmentId:guid}", async (Guid taskId, Guid attachmentId,
            HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskWriter(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Read-only project access." }, statusCode: 403);
            var att = await db.ProjectTaskAttachments
                .FirstOrDefaultAsync(a => a.Id == attachmentId && a.TaskId == taskId && !a.IsDeleted, ct);
            if (att is null)
                return Results.Json(new { code = "not_found", message = "Attachment not found." }, statusCode: 404);
            if (att.UploadedById != userId.Value && !await IsTaskOwner(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Only the uploader or project owner." }, statusCode: 403);

            att.IsDeleted = true;
            db.ProjectTaskActivities.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = taskId, ActorId = userId.Value,
                Action = "attachment_removed", OldValue = att.FileName, CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(new { success = true });
        }).RequirePermission("projects.write");

        // ── Task files on the NAS (shared attachment store, area "projects") ──
        // Field photos / deliverables / attachments every member reads from the same place;
        // download is GET /api/attachments/{attachmentId} gated by ProjectsAttachmentPolicy.

        // POST /api/projects/tasks/{taskId}/files?kind=field|deliverable|attachment — multipart "file".
        g.MapPost("/tasks/{taskId:guid}/files", async Task<IResult> (Guid taskId, string? kind, IFormFile file,
            HttpContext http, IPopDbContext db, ITaskFileService files, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            try
            {
                await using var stream = file.OpenReadStream();
                var result = await files.UploadAsync(userId.Value, taskId, kind ?? ProjectTaskFile.KindAttachment,
                    file.FileName, file.ContentType, stream, ct);
                if (!result.Success) return TaskFileError(result.Error);
                await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
                return Results.Ok(result.Value);
            }
            catch (IPop.Application.Attachments.AttachmentRejectedException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message }, statusCode: 422);
            }
            catch (IPop.Application.Files.SynoException ex)
            {
                return Results.Json(new { code = "synology_error", message = ex.Message }, statusCode: 502);
            }
        })
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxTaskFileRequestBytes))
        .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxTaskFileRequestBytes })
        .DisableAntiforgery()
        .RequirePermission("projects.write");

        // GET /api/projects/tasks/{taskId}/files — all kinds, newest first.
        g.MapGet("/tasks/{taskId:guid}/files", async (Guid taskId,
            HttpContext http, IPopDbContext db, ITaskFileService files, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await files.ListAsync(userId.Value, taskId, ct);
            return result.Success ? Results.Ok(result.Value) : TaskFileError(result.Error);
        });

        // DELETE /api/projects/tasks/{taskId}/files/{fileId} — soft delete (uploader or project owner).
        g.MapDelete("/tasks/{taskId:guid}/files/{fileId:guid}", async (Guid taskId, Guid fileId,
            HttpContext http, IPopDbContext db, ITaskFileService files, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await files.DeleteAsync(userId.Value, taskId, fileId, ct);
            if (!result.Success) return TaskFileError(result.Error);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(new { success = true });
        }).RequirePermission("projects.write");

        // GET /api/projects/{id}/files/recent?take=12 — newest field photos + deliverables across the project.
        g.MapGet("/{id:guid}/files/recent", async (Guid id, int? take,
            HttpContext http, IPopDbContext db, ITaskFileService files, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await files.RecentAsync(userId.Value, id, take ?? 12, ct);
            return result.Success ? Results.Ok(result.Value) : TaskFileError(result.Error);
        });

        // GET /api/projects/tasks/{taskId}/reminders — preferensi pengingat tenggat tugas.
        g.MapGet("/tasks/{taskId:guid}/reminders", async (Guid taskId,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskMember(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var rows = await db.ProjectTaskReminders.AsNoTracking()
                .Where(r => r.TaskId == taskId)
                .OrderByDescending(r => r.OffsetHours)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new TaskReminderDto(r.Id, r.OffsetHours, r.SentAt)));
        });

        // PUT /api/projects/tasks/{taskId}/reminders — ganti himpunan pengingat (offset jam).
        g.MapPut("/tasks/{taskId:guid}/reminders", async (Guid taskId, SetTaskRemindersRequest req,
            HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (!await IsTaskWriter(db, taskId, userId.Value, ct))
                return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var offsets = (req.OffsetHours ?? []).Where(h => h > 0 && h <= 24 * 90).Distinct().ToList();
            if (offsets.Count > 8)
                return Results.Json(new { code = "too_many_reminders", message = "Maximum 8 reminders per task." }, statusCode: 422);

            // Ganti himpunan: hapus yang belum terkirim, pertahankan riwayat yang sudah terkirim.
            var existing = await db.ProjectTaskReminders.Where(r => r.TaskId == taskId).ToListAsync(ct);
            foreach (var r in existing.Where(r => r.SentAt is null && !offsets.Contains(r.OffsetHours)))
                db.ProjectTaskReminders.Remove(r);
            var now = DateTime.UtcNow;
            foreach (var h in offsets.Where(h => existing.All(e => e.OffsetHours != h)))
                db.ProjectTaskReminders.Add(new ProjectTaskReminder { Id = Guid.NewGuid(), TaskId = taskId, OffsetHours = h, CreatedAt = now });
            if (offsets.Count > 0)
                db.ProjectTaskActivities.Add(new ProjectTaskActivity
                {
                    Id = Guid.NewGuid(), TaskId = taskId, ActorId = userId.Value,
                    Action = "reminders_updated", NewValue = string.Join(",", offsets), CreatedAt = now
                });
            await db.SaveChangesAsync(ct);

            var rows = await db.ProjectTaskReminders.AsNoTracking().Where(r => r.TaskId == taskId).OrderByDescending(r => r.OffsetHours).ToListAsync(ct);
            await PushProjectChangedAsync(realtime, await TaskProjectIdAsync(db, taskId, ct));
            return Results.Ok(rows.Select(r => new TaskReminderDto(r.Id, r.OffsetHours, r.SentAt)));
        }).RequirePermission("projects.write");

        // ──────────────────────────────────────────────────────────────────────
        // Rencana & Laporan (Fase 4) — template proyek, milestone
        // ──────────────────────────────────────────────────────────────────────

        // GET /api/projects/templates — templates the caller may use (built-in, everyone, own,
        // own department), with section/task/milestone counts and how often each was used.
        g.MapGet("/templates", async (HttpContext http, IPopDbContext db, IProjectTemplateService templates, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await templates.ListAsync(userId.Value, http.User.IsInRole("Admin"), ct));
        });

        // GET /api/projects/templates/scope — the caller's department for "Who can use it".
        g.MapGet("/templates/scope", async (HttpContext http, IPopDbContext db, IProjectTemplateService templates, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await templates.ScopeAsync(userId.Value, ct));
        });

        // GET /api/projects/templates/{id} — detail: sections, tasks, milestones.
        g.MapGet("/templates/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, IProjectTemplateService templates, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await templates.GetAsync(userId.Value, id, ct);
            return r.Success ? Results.Ok(r.Value) : TemplateError(r.Error);
        });

        // POST /api/projects/templates — Save as template { name, description, sourceProjectId,
        // includeSections, includeMilestones, includeAssignees, visibility }.
        g.MapPost("/templates", async (CreateProjectTemplateRequest req, HttpContext http, IPopDbContext db, IProjectTemplateService templates, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await templates.CreateAsync(userId.Value, req, ct);
            return r.Success ? Results.Ok(r.Value) : TemplateError(r.Error);
        }).RequirePermission("projects.write");

        // DELETE /api/projects/templates/{id} — soft delete; creator or Admin, never a built-in.
        g.MapDelete("/templates/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, IProjectTemplateService templates, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await templates.DeleteAsync(userId.Value, id, http.User.IsInRole("Admin"), ct);
            return r.Success ? Results.Ok(new { success = true }) : TemplateError(r.Error);
        }).RequirePermission("projects.write");

        // POST /api/projects/from-template — new project from a template; dates shift to startDate.
        g.MapPost("/from-template", async (CreateProjectFromTemplateRequest req,
            HttpContext http, IProjectTemplateService templates, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await templates.CreateProjectAsync(userId.Value, req, ct);
            if (!r.Success || r.Value is null)
                return r.Error == "not_found"
                    ? Results.Json(new { code = "template_not_found", message = "Template not found." }, statusCode: 404)
                    : Results.Json(new { code = r.Error ?? "create_failed", message = r.Error }, statusCode: 422);
            await PushProjectChangedAsync(realtime, r.Value.Id);
            return Results.Ok(r.Value);
        }).RequirePermission("projects.write");

        // ── Milestone (Fase 4) ──

        // GET /api/projects/{id}/activity — aktivitas terbaru seluruh tugas proyek.
        // The v6 Overview shows a project-wide "Recent activity" feed; the per-task
        // endpoint above is too narrow for it.
        g.MapGet("/{id:guid}/activity", async (Guid id, int? limit, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.UserId == userId.Value, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var take = Math.Clamp(limit ?? 20, 1, 100);
            var rows = await db.ProjectTaskActivities.AsNoTracking()
                .Where(a => a.Task.ProjectId == id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(take)
                .Select(a => new ProjectActivityDto(a.Id, a.TaskId, a.Task.Title, a.Actor.Name,
                    a.Action, a.OldValue, a.NewValue, a.CreatedAt, a.Actor.Nik))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // ── Milestones: a dated checkpoint grouping several tasks (IMilestoneService) ──
        // Status is computed per request: achieved | overdue | due_soon (≤ 7 days) | planned.
        // `localToday` (yyyy-MM-dd) is the caller's calendar date; defaults to UTC today.

        // GET /api/projects/{id}/milestones
        g.MapGet("/{id:guid}/milestones", async (Guid id, DateTime? localToday, HttpContext http, IPopDbContext db, IMilestoneService ms, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await ms.ListAsync(userId.Value, id, MilestoneToday(localToday), ct);
            return r.Success ? Results.Ok(r.Value) : MilestoneError(r.Error);
        });

        // POST /api/projects/{id}/milestones  { title, dueDate, taskIds[] }
        g.MapPost("/{id:guid}/milestones", async (Guid id, DateTime? localToday, CreateMilestoneRequest req, HttpContext http, IPopDbContext db, IMilestoneService ms, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await ms.CreateAsync(userId.Value, id, req, MilestoneToday(localToday), ct);
            if (!r.Success) return MilestoneError(r.Error);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(r.Value);
        }).RequirePermission("projects.write");

        // PATCH /api/projects/{id}/milestones/{milestoneId}  { title?, dueDate?, taskIds? }
        // PUT is the legacy verb for the same update (older clients send taskId/clearTask/achieved).
        foreach (var verb in new[] { "PATCH", "PUT" })
        {
            g.MapMethods("/{id:guid}/milestones/{milestoneId:guid}", [verb], async (Guid id, Guid milestoneId, DateTime? localToday, UpdateMilestoneRequest req,
                HttpContext http, IPopDbContext db, IMilestoneService ms, IRealtime realtime, CancellationToken ct) =>
            {
                var userId = await GetUserIdAsync(http, db);
                if (userId is null) return Results.Unauthorized();
                var r = await ms.UpdateAsync(userId.Value, id, milestoneId, req, MilestoneToday(localToday), ct);
                if (!r.Success) return MilestoneError(r.Error);
                await PushProjectChangedAsync(realtime, id);
                return Results.Ok(r.Value);
            }).RequirePermission("projects.write");
        }

        // POST /api/projects/{id}/milestones/{milestoneId}/achieve | /unachieve (undo)
        foreach (var (path, achieved) in new[] { ("achieve", true), ("unachieve", false) })
        {
            g.MapPost($"/{{id:guid}}/milestones/{{milestoneId:guid}}/{path}", async (Guid id, Guid milestoneId, DateTime? localToday,
                HttpContext http, IPopDbContext db, IMilestoneService ms, IRealtime realtime, CancellationToken ct) =>
            {
                var userId = await GetUserIdAsync(http, db);
                if (userId is null) return Results.Unauthorized();
                var r = await ms.SetAchievedAsync(userId.Value, id, milestoneId, achieved, MilestoneToday(localToday), ct);
                if (!r.Success) return MilestoneError(r.Error);
                await PushProjectChangedAsync(realtime, id);
                return Results.Ok(r.Value);
            }).RequirePermission("projects.write");
        }

        // DELETE /api/projects/{id}/milestones/{milestoneId}
        g.MapDelete("/{id:guid}/milestones/{milestoneId:guid}", async (Guid id, Guid milestoneId, HttpContext http, IPopDbContext db, IMilestoneService ms, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var r = await ms.DeleteAsync(userId.Value, id, milestoneId, ct);
            if (!r.Success) return MilestoneError(r.Error);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(new { success = true });
        }).RequirePermission("projects.write");

        // GET /api/projects/{id}/reports/burndown?weeks=6&milestoneId=... — sisa tugas per minggu + garis ideal.
        g.MapGet("/{id:guid}/reports/burndown", async (Guid id, int? weeks, Guid? milestoneId, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.UserId == userId.Value, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var w = Math.Clamp(weeks ?? 6, 2, 26);
            var query = db.ProjectTasks.AsNoTracking()
                .Where(t => t.ProjectId == id && t.ParentTaskId == null);

            if (milestoneId.HasValue)
            {
                var linked = db.ProjectMilestoneTasks.AsNoTracking()
                    .Where(l => l.MilestoneId == milestoneId.Value && l.Milestone.ProjectId == id)
                    .Select(l => l.TaskId);
                query = query.Where(t => linked.Contains(t.Id));
            }

            var tasks = await query
                .Select(t => new { t.Status, t.DueDate, t.CreatedAt, t.UpdatedAt })
                .ToListAsync(ct);

            var total = tasks.Count;
            var points = new List<BurndownPointDto>();
            for (var i = w - 1; i >= 0; i--)
            {
                var weekEnd = DateTime.UtcNow.Date.AddDays(-7 * i);
                // Sisa pada titik lampau = belum selesai kini + yang selesai SETELAH titik
                // itu (UpdatedAt > weekEnd). Tugas overdue tetap terhitung sisa (bukan hilang).
                var remaining = tasks.Count(t => t.Status != "done" || t.UpdatedAt.Date > weekEnd);
                points.Add(new BurndownPointDto(weekEnd, remaining));
            }
            // Garis ideal: penurunan linier dari total ke 0 sepanjang rentang.
            var ideal = points.Select((p, i) => new BurndownPointDto(p.WeekEnd,
                (int)Math.Round(total * (1.0 - (double)i / Math.Max(1, points.Count - 1))))).ToList();

            return Results.Ok(new BurndownDto(points, ideal, total));
        });

        // GET /api/projects/{id}/reports/velocity?weeks=6 — tugas selesai per minggu.
        g.MapGet("/{id:guid}/reports/velocity", async (Guid id, int? weeks, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ProjectMembers.AnyAsync(m => m.ProjectId == id && m.UserId == userId.Value, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Not a project member." }, statusCode: 403);

            var w = Math.Clamp(weeks ?? 6, 2, 26);
            var tasks = await db.ProjectTasks.AsNoTracking()
                .Where(t => t.ProjectId == id && t.ParentTaskId == null && t.Status == "done")
                .Select(t => t.UpdatedAt)
                .ToListAsync(ct);

            var points = new List<VelocityPointDto>();
            for (var i = w - 1; i >= 0; i--)
            {
                var from = DateTime.UtcNow.Date.AddDays(-7 * (i + 1));
                var to = DateTime.UtcNow.Date.AddDays(-7 * i);
                points.Add(new VelocityPointDto(from, tasks.Count(u => u >= from && u < to)));
            }
            var avg = points.Count == 0 ? 0 : points.Average(p => p.Completed);
            return Results.Ok(new VelocityDto(points, avg));
        });

        // ──────────────────────────────────────────────────────────────────────
        // Phase 2: dependencies, soft-delete restore, bulk, saved views, calendar
        // ──────────────────────────────────────────────────────────────────────

        // PUT /api/projects/tasks/{taskId}/dependencies — replace the "waiting on" set
        g.MapPut("/tasks/{taskId:guid}/dependencies", async (Guid taskId, SetTaskDependenciesRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.SetTaskDependenciesAsync(userId.Value, taskId, req, ct);
            if (!result.Success) return Phase2Error(result.Error);
            await Audit(http, db, "projects.task.dependencies", taskId.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.Task!.ProjectId);
            return Results.Ok(result.Task);
        }).RequirePermission("projects.write");

        // POST /api/projects/{id}/tasks/restore — undo a (bulk) delete by batch id
        g.MapPost("/{id:guid}/tasks/restore", async (Guid id, RestoreTasksRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.RestoreTasksAsync(userId.Value, id, req.BatchId, ct);
            if (!result.Success) return Phase2Error(result.Error);
            await Audit(http, db, "projects.task.restore", req.BatchId.ToString(), ct);
            await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // POST /api/projects/{projectId}/tasks/bulk — status|assign|unassign|due|group|priority|delete
        g.MapPost("/{projectId:guid}/tasks/bulk", async (Guid projectId, BulkTaskRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.BulkUpdateTasksAsync(userId.Value, projectId, req, ct);
            if (!result.Success) return Phase2Error(result.Error);
            await Audit(http, db, $"projects.task.bulk.{req.Action}", projectId.ToString(), ct);
            await PushProjectChangedAsync(realtime, projectId);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // GET /api/projects/{id}/views — own + shared saved views
        g.MapGet("/{id:guid}/views", async (Guid id, HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var views = await svc.GetSavedViewsAsync(id, userId.Value, ct);
            return views is not null ? Results.Ok(views) : Phase2Error("project_not_found");
        });

        g.MapPost("/{id:guid}/views", async (Guid id, SaveProjectViewRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.CreateSavedViewAsync(userId.Value, id, req, ct);
            if (!result.Success) return Phase2Error(result.Error);
            await Audit(http, db, "projects.view.create", result.View!.Id.ToString(), ct);
            if (result.View.IsShared) await PushProjectChangedAsync(realtime, id);
            return Results.Ok(result.View);
        }).RequirePermission("projects.write");

        g.MapPut("/views/{viewId:guid}", async (Guid viewId, SaveProjectViewRequest req,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var result = await svc.UpdateSavedViewAsync(userId.Value, viewId, req, ct);
            if (!result.Success) return Phase2Error(result.Error);
            await Audit(http, db, "projects.view.update", viewId.ToString(), ct);
            await PushProjectChangedAsync(realtime, result.View!.ProjectId);
            return Results.Ok(result.View);
        }).RequirePermission("projects.write");

        g.MapDelete("/views/{viewId:guid}", async (Guid viewId,
            HttpContext http, IProjectService svc, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            var projectId = await svc.SavedViewProjectIdAsync(viewId, ct);
            var result = await svc.DeleteSavedViewAsync(userId.Value, viewId, ct);
            if (!result.Success) return Phase2Error(result.Error);
            await Audit(http, db, "projects.view.delete", viewId.ToString(), ct);
            await PushProjectChangedAsync(realtime, projectId);
            return Results.Ok(result);
        }).RequirePermission("projects.write");

        // GET /api/projects/{id}/calendar?from=2026-10-01&to=2026-11-01&groupId=
        g.MapGet("/{id:guid}/calendar", async (Guid id, DateTime from, DateTime to, Guid? groupId,
            HttpContext http, IProjectService svc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db);
            if (userId is null) return Results.Unauthorized();
            if (to < from || (to - from).TotalDays > 62) return Phase2Error("invalid_range");
            var cal = await svc.GetProjectCalendarAsync(id, userId.Value, from, to, groupId, ct);
            return cal is not null ? Results.Ok(cal) : Phase2Error("project_not_found");
        });

        return app;
    }

    private static IResult Phase2Error(string? code) => Results.Json(new { code, message = code },
        statusCode: code switch
        {
            "forbidden" => 403,
            not null when code.EndsWith("_not_found") => 404,
            _ => 422
        });

    /// One file (the store's 25 MB cap) plus multipart framing.
    private const long MaxTaskFileRequestBytes = 25L * 1024 * 1024 + 1024 * 1024;

    private static IResult MilestoneError(string? code) => Results.Json(new { code, message = code },
        statusCode: code switch
        {
            "forbidden" => 403,
            "project_not_found" or "milestone_not_found" or "task_not_found" => 404,
            _ => 422
        });

    private static IResult TemplateError(string? code) => Results.Json(new { code, message = code },
        statusCode: code switch
        {
            "forbidden" or "built_in" => 403,
            "not_found" => 404,
            _ => 422
        });

    private static DateTime MilestoneToday(DateTime? localToday) => (localToday ?? DateTime.UtcNow).Date;

    private static IResult TaskFileError(string? code) => Results.Json(new { code, message = code },
        statusCode: code switch
        {
            "forbidden" => 403,
            "task_not_found" or "project_not_found" or "file_not_found" => 404,
            _ => 422
        });

    private static IResult GroupError(string? code) => Results.Json(new { code, message = code },
        statusCode: code switch
        {
            "forbidden" => 403,
            "group_not_found" or "task_not_found" => 404,
            _ => 422
        });

    // Keanggotaan lewat tugas → proyek (anggota proyek boleh berdiskusi di tugasnya).
    private static async Task<bool> IsTaskMember(IPopDbContext db, Guid taskId, Guid userId, CancellationToken ct)
    {
        var projectId = await db.ProjectTasks.AsNoTracking()
            .Where(t => t.Id == taskId).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null) return false;
        return await db.ProjectMembers.AsNoTracking()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
    }

    private static async Task<bool> IsTaskWriter(IPopDbContext db, Guid taskId, Guid userId, CancellationToken ct)
    {
        var projectId = await db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);
        return projectId is not null && await db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == projectId && m.UserId == userId
            && (m.Role == "owner" || m.Role == "manager" || m.Role == "member"), ct);
    }

    private static async Task<bool> IsTaskOwner(IPopDbContext db, Guid taskId, Guid userId, CancellationToken ct)
    {
        var projectId = await db.ProjectTasks.AsNoTracking()
            .Where(t => t.Id == taskId).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId is null) return false;
        return await db.ProjectMembers.AsNoTracking()
            .AnyAsync(m => m.ProjectId == projectId && m.UserId == userId && m.Role == "owner", ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers (mirrors ChatEndpoints pattern)
    // ──────────────────────────────────────────────────────────────────────────

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Nik == nik);
        return user?.Id;
    }

    /// Live update for every open Projects page: the client filters by projectId and refetches
    /// through the membership-checked endpoints, so the payload leaks nothing but the id.
    /// Best effort: the mutation has already committed, so a failed push must not fail the
    /// request, and it is not tied to the request token (a caller hanging up should not
    /// silence everyone else's update).
    /// Project membership writes the linked chat room's members directly; the live hub groups
    /// must follow, or an added member gets nothing until they reconnect and a removed one keeps
    /// receiving the room. added/removed null = (re)announce the room to all current members.
    private static async Task SyncProjectRoomLiveAsync(IPopDbContext db, IChatNotifier chat, Guid projectId, Guid? added, Guid? removed)
    {
        try
        {
            var roomId = await db.ProjectRooms.AsNoTracking().Where(r => r.ProjectId == projectId)
                .Select(r => r.RoomId).FirstOrDefaultAsync();
            if (roomId == Guid.Empty) return;
            if (removed is { } gone) { await chat.NotifyRemovedAsync(gone, roomId, CancellationToken.None); return; }
            var ids = added is { } a && a != Guid.Empty ? [a]
                : await db.ChatRoomMembers.AsNoTracking().Where(m => m.RoomId == roomId && m.LeftAt == null)
                    .Select(m => m.UserId).ToListAsync();
            if (ids.Count > 0) await chat.NotifyAddedAsync(ids, roomId, CancellationToken.None);
        }
        catch { /* membership is saved; clients resync on reconnect */ }
    }

    private static async Task PushProjectChangedAsync(IRealtime realtime, Guid? projectId)
    {
        if (projectId is not { } id) return;
        try { await realtime.ToAllAsync(RealtimeEvents.ProjectChanged, id); }
        catch (Exception) { /* advisory; clients also reload on navigation and reconnect */ }
    }

    private static Task<Guid?> TaskProjectIdAsync(IPopDbContext db, Guid taskId, CancellationToken ct) =>
        db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);

    private static async Task Audit(HttpContext http, IPopDbContext db, string action, string entityId, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorNik = http.User.FindFirst("nik")?.Value,
            Action = action,
            Entity = "Project",
            EntityId = entityId,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    // Request DTOs local to endpoints (service owns its request records)
    public record AssignRequest(Guid UserId, string? Role);
    // Diskusi Tugas (Fase 2)
    public record AddTaskCommentRequest(string Content, Guid? ParentCommentId);
    public record TaskCommentDto(Guid Id, string AuthorName, string AuthorNik, string Content, Guid? ParentCommentId, DateTime CreatedAt);
    public record TaskActivityDto(Guid Id, string ActorName, string Action, string? OldValue, string? NewValue, DateTime CreatedAt);
    public record PagedActivities(IReadOnlyList<TaskActivityDto> Items, int TotalCount, int Page, int PageSize);

    /// Project-wide activity row: carries the task title so the Overview feed can render
    /// "<who> <what> on <task>" without an extra lookup per row.
    public record ProjectActivityDto(Guid Id, Guid TaskId, string TaskTitle, string ActorName,
        string Action, string? OldValue, string? NewValue, DateTime CreatedAt, string? ActorNik = null);
    // Lampiran berkas tugas (referensi NAS)
    public record AddTaskAttachmentRequest(string FileName, long Size, string Server, string Path);
    public record TaskAttachmentDto(Guid Id, string FileName, long FileSize, string UploadedByName, string Server, string Path, DateTime CreatedAt);
    // Pengingat tenggat
    public record SetTaskRemindersRequest(IReadOnlyList<int>? OffsetHours);
    public record TaskReminderDto(Guid Id, int OffsetHours, DateTime? SentAt);
    // Rencana & Laporan (Fase 4)
    // Template DTOs live in IPop.Application.Projects (IProjectTemplateService).
    // Milestone DTOs live in IPop.Application.Projects (IMilestoneService).
    // Laporan (Fase 4)
    public record BurndownPointDto(DateTime WeekEnd, int Remaining);
    public record BurndownDto(IReadOnlyList<BurndownPointDto> Actual, IReadOnlyList<BurndownPointDto> Ideal, int TotalTasks);
    public record VelocityPointDto(DateTime WeekStart, int Completed);
    public record VelocityDto(IReadOnlyList<VelocityPointDto> Points, double AveragePerWeek);
}
