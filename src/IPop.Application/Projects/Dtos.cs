namespace IPop.Application.Projects;

// Project DTOs
public record CreateProjectRequest(string Name, string? Description, int Year, Guid OwnerId,
    DateTime? StartDate, DateTime? Deadline, string? Risk, string? Obstacle, string? OwnerNik = null, string? Color = null);

public record UpdateProjectRequest(string? Name, string? Description, int? Year, Guid? OwnerId,
    DateTime? StartDate, DateTime? Deadline, string? Status, string? Risk, string? Obstacle, string? OwnerNik = null, string? Color = null);

public record ProjectDto(Guid Id, string Name, string? Description, int Year, Guid OwnerId,
    string OwnerName, string OwnerNik, DateTime? StartDate, DateTime? Deadline,
    string Status, int Progress, string? Risk, string? Obstacle, bool IsActive,
    int MemberCount, int TaskCount, int TodoCount, int DoingCount, int ReviewCount, int DoneCount,
    DateTime CreatedAt, DateTime UpdatedAt, string? Color = null);

public record ProjectListDto(Guid Id, string Name, int Year, string OwnerName, string Status,
    int Progress, DateTime? Deadline, int MemberCount, int TaskCount, bool IsActive, string? Color = null);

public record ProjectGroupDto(Guid Id, Guid ProjectId, string Name, string Color, int SortOrder,
    int TaskCount, int Progress, int OverdueCount);
public record CreateProjectGroupRequest(string Name, string? Color = null);
public record UpdateProjectGroupRequest(string Name, string? Color = null);
public record ReorderProjectGroupsRequest(IReadOnlyList<Guid> GroupIds);
public record MoveTaskToGroupRequest(Guid GroupId);
public record GroupResultDto(bool Success, ProjectGroupDto? Group, string? Error);
/// Snapshot returned by a group delete so the client can offer Undo. Restoring only moves back
/// tasks that are still in <see cref="MovedToGroupId"/>, so later edits are never overwritten.
public record DeletedProjectGroupDto(Guid GroupId, Guid ProjectId, string Name, string Color, int SortOrder, Guid MovedToGroupId, IReadOnlyList<Guid> TaskIds);
public record DeleteGroupResultDto(bool Success, DeletedProjectGroupDto? Deleted, string? Error);

// Project Member DTOs
public record AddProjectMemberRequest(Guid UserId, string Role, string? Nik = null);
public record ProjectMemberDto(Guid UserId, string Nik, string Name, string Role, DateTime JoinedAt);

// Task DTOs
public record CreateTaskRequest(Guid ProjectId, Guid? ParentTaskId, string Title, string? Description,
    string Status, string? Priority, DateTime? StartDate, DateTime? DueDate,
    decimal? EstimatedHours, string? Dependencies, string? Tags, Guid? GroupId = null);

public record UpdateTaskRequest(string? Title, string? Description, string? Status, string? Priority,
    DateTime? StartDate, DateTime? DueDate, decimal? EstimatedHours, decimal? ActualHours,
    int? SortOrder, string? Dependencies, bool? IsRecurring, string? RecurrenceRule, bool? IsTemplate,
    string? Tags);

public record ProjectTaskDto(Guid Id, Guid ProjectId, Guid? ParentTaskId, string Title, string? Description,
    string Status, string? Priority, DateTime? StartDate, DateTime? DueDate,
    decimal? EstimatedHours, decimal? ActualHours, int SortOrder,
    string? Dependencies, bool IsRecurring, string? RecurrenceRule, bool IsTemplate,
    string? Tags,
    Guid CreatedById, string CreatedByName,
    IReadOnlyList<ProjectTaskAssigneeDto> Assignees,
    int SubtaskCount, int SubtaskDoneCount,
    DateTime CreatedAt, DateTime UpdatedAt,
    /// Fase 2 — Diskusi Tugas: jumlah komentar, lampiran, dan status pengingat.
    int CommentCount = 0, int AttachmentCount = 0, bool HasReminder = false, Guid GroupId = default,
    /// Phase 2: tasks this one waits on; blocked while any of them is not done.
    IReadOnlyList<DependencyRefDto>? DependsOn = null, bool IsBlocked = false,
    /// Set only on the response that completed a recurring task: the next instance it created.
    Guid? NextRecurringTaskId = null);

public record DependencyRefDto(Guid Id, string Title, string Status);
public record SetTaskDependenciesRequest(IReadOnlyList<Guid> DependsOn);

public record ProjectTaskAssigneeDto(Guid UserId, string Nik, string Name, string? Role, DateTime AssignedAt);

// Kanban / List / Timeline DTOs
// Review is the fourth lane the v6 mockup shows between In Progress and Done.
// Defaulted so existing construction sites (and the 3-lane history) keep compiling.
public record KanbanBoardDto(Guid ProjectId, IReadOnlyList<ProjectTaskDto> Todo, IReadOnlyList<ProjectTaskDto> Doing,
    IReadOnlyList<ProjectTaskDto> Done, IReadOnlyList<ProjectTaskDto>? Review = null);

public record TaskMoveRequest(string Status, int? SortOrder);

public record TimelineTaskDto(Guid Id, string Title, Guid? ParentTaskId, string Status, string? Priority,
    DateTime? StartDate, DateTime? DueDate, decimal? EstimatedHours, decimal? ActualHours,
    IReadOnlyList<Guid> AssigneeUserIds, int SubtaskCount, int SubtaskDoneCount,
    string? Tags, Guid GroupId = default,
    IReadOnlyList<Guid>? DependsOn = null, bool IsBlocked = false);

public record ProjectTimelineDto(Guid ProjectId, string ProjectName, DateTime? ProjectStart, DateTime? ProjectDeadline,
    IReadOnlyList<TimelineTaskDto> Tasks);

// Workload & Export (DETAIL MAX stubs)
public record AssigneeWorkloadDto(Guid UserId, string Name, int TaskCount, decimal EstimatedHours,
    string? Position = null, int TodoCount = 0, int DoingCount = 0, int ReviewCount = 0,
    int DoneCount = 0, int OverdueCount = 0, bool IsUnassigned = false);

public record ProjectWorkloadDto(
    int TotalTasks,
    int TodoTasks,
    int DoingTasks,
    int DoneTasks,
    decimal TotalEstimatedHours,
    decimal TodoEstimatedHours,
    decimal DoingEstimatedHours,
    decimal DoneEstimatedHours,
    IReadOnlyList<AssigneeWorkloadDto> ByAssignee,
    int ReviewTasks = 0,
    decimal ReviewEstimatedHours = 0);

public record ProjectExportDto(
    ProjectDto Project,
    IReadOnlyList<ProjectTaskDto> Tasks,
    IReadOnlyList<ProjectMemberDto> Members);

// Recurring preview stub
public record RecurringPreviewDto(Guid TaskId, string Title, DateTime? BaseDue, IReadOnlyList<DateTime> UpcomingDates);

// Recurring more complete: projected instance preview (virtual, no DB rows)
public record RecurringInstanceDto(
    Guid TemplateTaskId,
    string Title,
    string? Description,
    string? Priority,
    decimal? EstimatedHours,
    DateTime? DueDate,
    string? Tags,
    string Status);

// ──────────────────────────────────────────────────────────────────────────
// ipop-21 PERFECTION: Gantt date-mapping
// ──────────────────────────────────────────────────────────────────────────

/// A single task in the Gantt chart with computed day-index positions and critical path.
public record GanttTaskDto(
    Guid Id, string Title, Guid? ParentTaskId, string Status, string? Priority,
    DateTime? StartDate, DateTime? DueDate,
    decimal? EstimatedHours, decimal? ActualHours,
    IReadOnlyList<Guid> AssigneeUserIds, int SubtaskCount, int SubtaskDoneCount, string? Tags,
    int? DayIndexStart,           // days from project start (0-based)
    int? DurationDays,            // end - start in days (1 for single-day tasks)
    bool IsCritical,              // true if task is on the critical path (slack = 0)
    IReadOnlyList<Guid>? DependsOn = null
);

/// Full Gantt chart data for a project, including date range and critical path.
public record ProjectGanttDto(
    Guid ProjectId, string ProjectName,
    DateTime? ProjectStart, DateTime? ProjectDeadline,
    int TotalDays,                                   // range of the chart window
    IReadOnlyList<GanttTaskDto> Tasks,
    IReadOnlyList<Guid> CriticalPathTaskIds           // ordered list of critical task IDs
);

// ──────────────────────────────────────────────────────────────────────────
// ipop-21 PERFECTION: WorkCalendar
// ──────────────────────────────────────────────────────────────────────────

/// Upsert request for a single work-calendar entry.
public record UpsertCalendarEntryRequest(DateTime Date, string Kind, string? Title, string? Note);

/// A single work-calendar entry DTO.
public record CalendarEntryDto(
    Guid Id, DateTime Date, string Kind, string? Title, string? Note,
    Guid CreatedById, string CreatedByName, DateTime CreatedAt, DateTime UpdatedAt
);

/// Work-calendar data for a date range, including tasks pinned to days.
public record ProjectWorkCalendarDto(
    Guid ProjectId, int TotalEntries,
    IReadOnlyList<CalendarEntryDto> Entries,
    IReadOnlyList<CalendarDayTaskDto> DayTasks
);

/// Tasks aggregated by day (for calendar overlays showing what's scheduled each day).
public record CalendarDayTaskDto(
    DateTime Date, int TaskCount, int TodoCount, int DoingCount, int DoneCount,
    decimal EstimatedHours, decimal ActualHours,
    IReadOnlyList<Guid> TaskIds,
    int ReviewCount = 0
);

// ──────────────────────────────────────────────────────────────────────────
// ipop-21 PERFECTION: Chat room per project
// ──────────────────────────────────────────────────────────────────────────

/// DTO returned by GET /api/projects/{id}/chat-room.
/// Contains the linked chat room identity + member count for ORION to embed.
public record ProjectChatRoomDto(
    Guid RoomId, string RoomName, int MemberCount, bool IsActive
);

// ──────────────────────────────────────────────────────────────────────────
// Phase 2: soft delete, bulk actions, saved views, calendar
// ──────────────────────────────────────────────────────────────────────────

/// Returned by a task delete; POST /{projectId}/tasks/restore with the batch id undoes it.
public record DeleteTaskResultDto(bool Success, Guid? BatchId, IReadOnlyList<Guid> TaskIds, string? Error);
public record RestoreTasksRequest(Guid BatchId);

/// action: status | assign | unassign | due | group | priority | delete.
/// Value carries the status/priority string, a user id, a group id, or a yyyy-MM-dd date ("" clears due).
/// Evidence is only used by status → doing/review/done: the shared evidence block written into each task.
public record BulkTaskRequest(IReadOnlyList<Guid> TaskIds, string Action, string? Value, string? Evidence = null);
public record BulkSkippedDto(Guid Id, string Reason);
public record BulkTaskResultDto(bool Success, int Updated, IReadOnlyList<BulkSkippedDto> Skipped, Guid? BatchId, string? Error);

public record ProjectSavedViewDto(Guid Id, Guid ProjectId, Guid OwnerId, string OwnerName, string Name,
    bool IsShared, string StateJson, int SortOrder, bool IsMine);
public record SaveProjectViewRequest(string Name, string StateJson, bool IsShared = false);
public record SavedViewResultDto(bool Success, ProjectSavedViewDto? View, string? Error);

public record CalendarTaskDto(Guid Id, Guid? ParentTaskId, string Title, string Status, string? Priority,
    DateTime? StartDate, DateTime? DueDate, Guid GroupId, IReadOnlyList<Guid> AssigneeUserIds, bool IsBlocked, bool IsRecurring);
public record ProjectCalendarDto(Guid ProjectId, DateTime From, DateTime To, IReadOnlyList<CalendarTaskDto> Tasks, bool Truncated);

// Result wrappers
public record ProjectResultDto(bool Success, ProjectDto? Project, string? Error);
public record TaskResultDto(bool Success, ProjectTaskDto? Task, string? Error);
public record MemberResultDto(bool Success, string? Error);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
