namespace IPop.Application.Projects;

// Project DTOs
public record CreateProjectRequest(string Name, string? Description, int Year, Guid OwnerId,
    DateTime? StartDate, DateTime? Deadline, string? Risk, string? Obstacle, string? OwnerNik = null);

public record UpdateProjectRequest(string? Name, string? Description, int? Year, Guid? OwnerId,
    DateTime? StartDate, DateTime? Deadline, string? Status, string? Risk, string? Obstacle, string? OwnerNik = null);

public record ProjectDto(Guid Id, string Name, string? Description, int Year, Guid OwnerId,
    string OwnerName, string OwnerNik, DateTime? StartDate, DateTime? Deadline,
    string Status, int Progress, string? Risk, string? Obstacle, bool IsActive,
    int MemberCount, int TaskCount, int TodoCount, int DoingCount, int DoneCount,
    DateTime CreatedAt, DateTime UpdatedAt);

public record ProjectListDto(Guid Id, string Name, int Year, string OwnerName, string Status,
    int Progress, DateTime? Deadline, int MemberCount, int TaskCount, bool IsActive);

// Project Member DTOs
public record AddProjectMemberRequest(Guid UserId, string Role, string? Nik = null);
public record ProjectMemberDto(Guid UserId, string Nik, string Name, string Role, DateTime JoinedAt);

// Task DTOs
public record CreateTaskRequest(Guid ProjectId, Guid? ParentTaskId, string Title, string? Description,
    string Status, string? Priority, DateTime? StartDate, DateTime? DueDate,
    decimal? EstimatedHours, string? Dependencies, string? Tags);

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
    int CommentCount = 0, int AttachmentCount = 0, bool HasReminder = false);

public record ProjectTaskAssigneeDto(Guid UserId, string Nik, string Name, string? Role, DateTime AssignedAt);

// Kanban / List / Timeline DTOs
public record KanbanBoardDto(Guid ProjectId, IReadOnlyList<ProjectTaskDto> Todo, IReadOnlyList<ProjectTaskDto> Doing, IReadOnlyList<ProjectTaskDto> Done);

public record TaskMoveRequest(string Status, int? SortOrder);

public record TimelineTaskDto(Guid Id, string Title, Guid? ParentTaskId, string Status, string? Priority,
    DateTime? StartDate, DateTime? DueDate, decimal? EstimatedHours, decimal? ActualHours,
    IReadOnlyList<Guid> AssigneeUserIds, int SubtaskCount, int SubtaskDoneCount,
    string? Tags);

public record ProjectTimelineDto(Guid ProjectId, string ProjectName, DateTime? ProjectStart, DateTime? ProjectDeadline,
    IReadOnlyList<TimelineTaskDto> Tasks);

// Workload & Export (DETAIL MAX stubs)
public record AssigneeWorkloadDto(Guid UserId, string Name, int TaskCount, decimal EstimatedHours);

public record ProjectWorkloadDto(
    int TotalTasks,
    int TodoTasks,
    int DoingTasks,
    int DoneTasks,
    decimal TotalEstimatedHours,
    decimal TodoEstimatedHours,
    decimal DoingEstimatedHours,
    decimal DoneEstimatedHours,
    IReadOnlyList<AssigneeWorkloadDto> ByAssignee);

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
    bool IsCritical               // true if task is on the critical path (slack = 0)
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
    IReadOnlyList<Guid> TaskIds
);

// ──────────────────────────────────────────────────────────────────────────
// ipop-21 PERFECTION: Chat room per project
// ──────────────────────────────────────────────────────────────────────────

/// DTO returned by GET /api/projects/{id}/chat-room.
/// Contains the linked chat room identity + member count for ORION to embed.
public record ProjectChatRoomDto(
    Guid RoomId, string RoomName, int MemberCount, bool IsActive
);

// Result wrappers
public record ProjectResultDto(bool Success, ProjectDto? Project, string? Error);
public record TaskResultDto(bool Success, ProjectTaskDto? Task, string? Error);
public record MemberResultDto(bool Success, string? Error);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
