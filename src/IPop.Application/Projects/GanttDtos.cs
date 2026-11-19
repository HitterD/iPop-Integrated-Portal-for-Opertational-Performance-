namespace IPop.Application.Projects;

// Projects v2 stage 3: Gantt engine (links, slack, baselines, reschedule, MS Project XML).

public record GanttResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

// ── Baselines ──
public record CreateBaselineRequest(string? Name = null);
public record BaselineDto(Guid Id, string Name, DateTime CreatedAt, string CreatedByName, int TaskCount, bool IsActive);

// ── Gantt v2 read model ──
public record GanttV2ProjectDto(Guid Id, string Name, DateTime? Start, DateTime? Deadline);
public record GanttWindowDto(DateTime Start, DateTime End);
public record GanttGroupDto(Guid Id, string Name, string Color, int SortOrder);
/// Nik is only the avatar key; never display it.
public record GanttAssigneeDto(Guid UserId, string Name, string Nik);
public record GanttLinkDto(Guid TaskId, string LinkType, int LagDays);

/// One task row. Status is the label shown (custom status name when set, else the stage name),
/// StageStatus is the stage (todo | doing | review | done). DurationDays is in working days (0 = undated).
public record GanttRowDto(
    Guid Id, Guid GroupId, Guid? ParentTaskId, string WbsPath, string Title, string Status, Guid? StatusOptionId,
    string StageStatus, string? Priority, DateTime? StartDate, DateTime? DueDate, int DurationDays, int PercentDone,
    IReadOnlyList<GanttAssigneeDto> Assignees, bool IsCritical, int? SlackDays,
    DateTime? DeadlineDate, string? ConstraintType, DateTime? ConstraintDate,
    DateTime? BaselineStart, DateTime? BaselineDue, int? VarianceDays,
    IReadOnlyList<GanttLinkDto> DependsOn, bool IsMilestoneRow = false);

public record GanttMilestoneDto(Guid Id, string Title, DateTime Date, DateTime? AchievedAt,
    IReadOnlyList<Guid> LinkedTaskIds, bool IsCritical);
public record GanttHolidayDto(DateTime Date, string? Title);
public record GanttConflictDto(Guid FromTaskId, Guid ToTaskId);

public record GanttV2Dto(
    GanttV2ProjectDto Project, GanttWindowDto Window, IReadOnlyList<GanttGroupDto> Groups,
    IReadOnlyList<GanttRowDto> Rows, IReadOnlyList<GanttMilestoneDto> Milestones,
    IReadOnlyList<GanttHolidayDto> Holidays, IReadOnlyList<GanttConflictDto> Conflicts,
    IReadOnlyList<Guid> CriticalPathTaskIds, DateTime? ProjectFinish, IReadOnlyList<BaselineDto> Baselines,
    Guid? BaselineId = null, IReadOnlyList<Guid>? ConstraintViolationTaskIds = null);

// ── Reschedule ──
/// Mode: "single" | "cascade". NewDue (optional) resizes the task.
public record RescheduleRequest(Guid TaskId, DateTime NewStart, string Mode = "single", DateTime? NewDue = null);
public record RescheduleChangeDto(Guid TaskId, string Title, DateTime? OldStart, DateTime? OldDue, DateTime NewStart, DateTime NewDue);
public record ReschedulePreviewDto(IReadOnlyList<RescheduleChangeDto> Changes, IReadOnlyList<GanttConflictDto> Conflicts,
    DateTime? NewProjectFinish, DateTime? OldProjectFinish, IReadOnlyList<Guid>? ConstraintViolationTaskIds = null);
public record RevertChangeDto(Guid TaskId, DateTime? Start, DateTime? Due);
public record RescheduleRevertRequest(IReadOnlyList<RevertChangeDto> Changes);
public record RescheduleRevertResultDto(int Reverted);

// ── MS Project XML ──
public record MsProjectImportResultDto(int GroupCount, int TaskCount, int LinkCount, int MilestoneCount,
    IReadOnlyList<string> Warnings, bool DryRun = false);
public record MsProjectExportDto(string FileName, string Xml);

public interface IGanttService
{
    Task<GanttResult<GanttV2Dto>> GetGanttV2Async(Guid actorId, Guid projectId, Guid? baselineId, Guid? groupId, CancellationToken ct);
    /// Adds baseline fields to a v1 gantt (BaselineStart/BaselineDue/VarianceDays).
    Task<ProjectGanttDto> WithBaselineAsync(ProjectGanttDto gantt, Guid? baselineId, CancellationToken ct);

    Task<GanttResult<BaselineDto>> CreateBaselineAsync(Guid actorId, Guid projectId, CreateBaselineRequest req, CancellationToken ct);
    Task<GanttResult<IReadOnlyList<BaselineDto>>> ListBaselinesAsync(Guid actorId, Guid projectId, CancellationToken ct);
    Task<GanttResult<BaselineDto>> ActivateBaselineAsync(Guid actorId, Guid projectId, Guid baselineId, CancellationToken ct);
    Task<GanttResult<bool>> DeleteBaselineAsync(Guid actorId, Guid projectId, Guid baselineId, CancellationToken ct);

    Task<GanttResult<ReschedulePreviewDto>> PreviewRescheduleAsync(Guid actorId, Guid projectId, RescheduleRequest req, CancellationToken ct);
    Task<GanttResult<ReschedulePreviewDto>> ApplyRescheduleAsync(Guid actorId, Guid projectId, RescheduleRequest req, CancellationToken ct);
    Task<GanttResult<RescheduleRevertResultDto>> RevertRescheduleAsync(Guid actorId, Guid projectId, RescheduleRevertRequest req, CancellationToken ct);

    Task<GanttResult<MsProjectExportDto>> ExportMsProjectAsync(Guid actorId, Guid projectId, CancellationToken ct);
    Task<GanttResult<MsProjectImportResultDto>> ImportMsProjectAsync(Guid actorId, Guid projectId, byte[] content, string? fileName, bool dryRun, CancellationToken ct);
}
