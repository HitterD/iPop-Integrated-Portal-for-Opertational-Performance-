namespace IPop.Application.Projects;

public interface IProjectService
{
    // Projects
    Task<ProjectResultDto> CreateProjectAsync(Guid actorId, CreateProjectRequest req, CancellationToken ct);
    Task<ProjectDto?> GetProjectAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<PagedResult<ProjectListDto>> GetProjectsAsync(Guid userId, string? status, int? year, int page, int pageSize, CancellationToken ct);
    Task<ProjectResultDto> UpdateProjectAsync(Guid actorId, Guid projectId, UpdateProjectRequest req, CancellationToken ct);
    Task<MemberResultDto> DeleteProjectAsync(Guid actorId, Guid projectId, CancellationToken ct);

    // Groups
    Task<IReadOnlyList<ProjectGroupDto>?> GetGroupsAsync(Guid projectId, Guid userId, CancellationToken ct, DateTime? localToday = null);
    Task<GroupResultDto> CreateGroupAsync(Guid actorId, Guid projectId, CreateProjectGroupRequest req, CancellationToken ct);
    Task<GroupResultDto> UpdateGroupAsync(Guid actorId, Guid groupId, UpdateProjectGroupRequest req, CancellationToken ct);
    Task<MemberResultDto> ReorderGroupsAsync(Guid actorId, Guid projectId, ReorderProjectGroupsRequest req, CancellationToken ct);
    Task<DeleteGroupResultDto> DeleteGroupAsync(Guid actorId, Guid groupId, CancellationToken ct);
    Task<GroupResultDto> RestoreGroupAsync(Guid actorId, Guid projectId, DeletedProjectGroupDto snapshot, CancellationToken ct);
    Task<TaskResultDto> MoveTaskToGroupAsync(Guid actorId, Guid taskId, MoveTaskToGroupRequest req, CancellationToken ct);

    // Members
    Task<MemberResultDto> AddMemberAsync(Guid actorId, Guid projectId, AddProjectMemberRequest req, CancellationToken ct);
    Task<MemberResultDto> RemoveMemberAsync(Guid actorId, Guid projectId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<ProjectMemberDto>> GetMembersAsync(Guid projectId, CancellationToken ct);

    // Tasks
    Task<TaskResultDto> CreateTaskAsync(Guid actorId, CreateTaskRequest req, CancellationToken ct);
    Task<ProjectTaskDto?> GetTaskAsync(Guid taskId, Guid userId, CancellationToken ct);
    Task<IReadOnlyList<ProjectTaskDto>> GetTasksAsync(Guid projectId, Guid? parentTaskId, CancellationToken ct);
    Task<TaskResultDto> UpdateTaskAsync(Guid actorId, Guid taskId, UpdateTaskRequest req, CancellationToken ct);
    /// Soft delete (task + subtasks share one batch id); undo via RestoreTasksAsync.
    Task<DeleteTaskResultDto> DeleteTaskAsync(Guid actorId, Guid taskId, CancellationToken ct);
    Task<MemberResultDto> RestoreTasksAsync(Guid actorId, Guid projectId, Guid batchId, CancellationToken ct);
    Task<int> PurgeDeletedTasksAsync(DateTime olderThanUtc, CancellationToken ct);

    // Phase 2: dependencies, bulk actions, saved views, calendar
    Task<TaskResultDto> SetTaskDependenciesAsync(Guid actorId, Guid taskId, SetTaskDependenciesRequest req, CancellationToken ct);
    Task<BulkTaskResultDto> BulkUpdateTasksAsync(Guid actorId, Guid projectId, BulkTaskRequest req, CancellationToken ct);
    Task<IReadOnlyList<ProjectSavedViewDto>?> GetSavedViewsAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<SavedViewResultDto> CreateSavedViewAsync(Guid actorId, Guid projectId, SaveProjectViewRequest req, CancellationToken ct);
    Task<SavedViewResultDto> UpdateSavedViewAsync(Guid actorId, Guid viewId, SaveProjectViewRequest req, CancellationToken ct);
    Task<MemberResultDto> DeleteSavedViewAsync(Guid actorId, Guid viewId, CancellationToken ct);
    Task<Guid?> SavedViewProjectIdAsync(Guid viewId, CancellationToken ct);
    Task<ProjectCalendarDto?> GetProjectCalendarAsync(Guid projectId, Guid userId, DateTime from, DateTime to, Guid? groupId, CancellationToken ct);

    // Assignees
    Task<MemberResultDto> AssignUserAsync(Guid actorId, Guid taskId, Guid userId, string? role, CancellationToken ct);
    Task<MemberResultDto> UnassignUserAsync(Guid actorId, Guid taskId, Guid userId, CancellationToken ct);

    // Kanban
    Task<KanbanBoardDto?> GetKanbanAsync(Guid projectId, Guid userId, CancellationToken ct, Guid? groupId = null);
    Task<TaskResultDto> MoveTaskAsync(Guid actorId, Guid taskId, TaskMoveRequest req, CancellationToken ct);

    // List + Timeline (Gantt stub: returns data, no critical path calc)
    Task<PagedResult<ProjectTaskDto>> GetTaskListAsync(Guid projectId, Guid userId, string? status, int page, int pageSize, CancellationToken ct, Guid? assigneeId = null, bool unassigned = false, Guid? groupId = null);
    Task<ProjectTimelineDto?> GetTimelineAsync(Guid projectId, Guid userId, CancellationToken ct, Guid? groupId = null);

    // DETAIL MAX additions
    Task<ProjectWorkloadDto?> GetProjectWorkloadAsync(Guid projectId, Guid userId, CancellationToken ct, DateTime? localToday = null, Guid? groupId = null);
    Task<ProjectExportDto?> GetProjectExportDataAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<RecurringPreviewDto?> GetRecurringPreviewAsync(Guid taskId, Guid userId, int count, CancellationToken ct);

    // Recurring more complete: projected instances carrying task fields (virtual, no DB persistence)
    Task<IReadOnlyList<RecurringInstanceDto>?> GetRecurringInstancesPreviewAsync(Guid taskId, Guid userId, int count, CancellationToken ct);

    // Dependency validation (used internally by Move; exposed for UI hints)
    Task<(bool CanComplete, IReadOnlyList<Guid> UnmetDependencyIds)> ValidateTaskDependenciesAsync(Guid taskId, Guid userId, CancellationToken ct);

    // Progress recalculation (called internally after mutations; exposed for admin/tools)
    Task<int> RecalculateProgressAsync(Guid projectId, CancellationToken ct);

    // ──────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: Gantt date-mapping + critical path
    // ──────────────────────────────────────────────────────────────────────

    /// Returns date-mapped Gantt chart data with day-index positions and critical path computation.
    Task<ProjectGanttDto?> GetGanttAsync(Guid projectId, Guid userId, CancellationToken ct, Guid? groupId = null);

    // ──────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: WorkCalendar
    // ──────────────────────────────────────────────────────────────────────

    /// Returns work-calendar entries + per-day task aggregates for a date range.
    Task<ProjectWorkCalendarDto?> GetWorkCalendarAsync(Guid projectId, Guid userId, DateTime from, DateTime to, CancellationToken ct);

    /// Upserts a single work-calendar entry (owner/manager only).
    Task<MemberResultDto> UpsertCalendarEntryAsync(Guid actorId, Guid projectId, UpsertCalendarEntryRequest req, CancellationToken ct);

    /// Deletes a work-calendar entry (owner/manager only).
    Task<MemberResultDto> DeleteCalendarEntryAsync(Guid actorId, Guid projectId, DateTime date, CancellationToken ct);

    // ──────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: Chat room per project (interconnect)
    // ──────────────────────────────────────────────────────────────────────

    /// Returns the linked chat room for a project, or null if not found (caller not member).
    Task<ProjectChatRoomDto?> GetProjectChatRoomAsync(Guid projectId, Guid userId, CancellationToken ct);
}
