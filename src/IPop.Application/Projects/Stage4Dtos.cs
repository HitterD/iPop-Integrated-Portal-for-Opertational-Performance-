using System.Text.Json;

namespace IPop.Application.Projects;

// Projects v2 stage 4: My work, request form, close project, automations, overview, weekly workload.

public record ProjectOpResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

// ── My work ──
public record MyWorkCountsDto(int Late, int DueToday, int ThisWeek, int Later, int DoneThisWeek);
/// Status is the custom status label shown; Stage the four-stage key. IsLate = due before the caller's today.
public record MyWorkItemDto(Guid TaskId, Guid ProjectId, string ProjectName, string ProjectColor, string Title,
    string Status, string StatusColor, string Stage, DateTime? DueDate, bool IsLate, string GroupName);
/// Kind: "blocked" | "request" ("mention" is not produced: comments carry no structured mentions). FromNik is only the avatar key.
public record MyWorkWaitingDto(string Kind, string Text, Guid TaskId, Guid ProjectId, string ProjectName,
    string FromName, string FromNik, DateTime At);
public record MyWorkDto(MyWorkCountsDto Counts, IReadOnlyList<MyWorkItemDto> Items, IReadOnlyList<MyWorkWaitingDto> WaitingOnMe);

public interface IMyWorkService
{
    Task<MyWorkDto> GetAsync(Guid userId, DateTime localToday, CancellationToken ct);
}

// ── Request form ──
/// Type: text | longtext | choice | date. MapsTo: title | description | dueDate | field:{fieldId} | none.
public record FormQuestionDto(string Key, string Label, string Type, bool Required, IReadOnlyList<string>? Options, string MapsTo);
/// AudienceScope: project | department | everyone (signed-in iPop users only; there is no anonymous access).
public record ProjectFormDto(Guid ProjectId, bool IsOpen, string Title, string Intro, string AudienceScope,
    Guid? AssigneeId, string? AssigneeName, Guid? IntakeGroupId, IReadOnlyList<FormQuestionDto> Questions, int SubmissionCount);
public record SaveProjectFormRequest(bool IsOpen, string Title, string Intro, string AudienceScope, Guid? AssigneeId,
    IReadOnlyList<FormQuestionDto> Questions);
public record PublicFormDto(Guid ProjectId, string ProjectName, string ProjectColor, string Title, string Intro,
    IReadOnlyList<FormQuestionDto> Questions);
public record SubmitFormRequest(Dictionary<string, string>? Answers);
public record SubmitFormResultDto(Guid TaskId, string Title);

public interface IProjectFormService
{
    Task<ProjectOpResult<ProjectFormDto>> GetAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<ProjectOpResult<ProjectFormDto>> SaveAsync(Guid projectId, Guid userId, SaveProjectFormRequest request, CancellationToken ct);
    Task<ProjectOpResult<PublicFormDto>> GetPublicAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<ProjectOpResult<SubmitFormResultDto>> SubmitAsync(Guid projectId, Guid userId, SubmitFormRequest request, CancellationToken ct);
}

// ── Close project ──
public record LongestDelayDto(Guid TaskId, string Title, int Days);
/// OnTimePercent: done tasks finished on/before their due date / done tasks that have a due date (null when none).
/// FinishedDaysVsPlan: last completion vs the project deadline (or last planned due date); negative = early.
public record CloseSummaryDto(int Total, int Done, int? OnTimePercent, int LateCount, int? FinishedDaysVsPlan,
    LongestDelayDto? LongestDelay, int OpenCommentsCount, int OpenTasks, bool FilesOnNas);
public record CloseProjectRequest(string? Lessons, bool SaveAsTemplate, string? TemplateName, bool PostSummaryInChat);
/// OpenTasksWarning is a flag, not an error: a project may be closed with open work.
/// TemplateError: the project closed but "Save as template" failed ("no_department", "invalid_name", "template_failed", ...).
public record CloseProjectResultDto(CloseSummaryDto Summary, bool OpenTasksWarning, Guid? TemplateId, bool SummaryPosted, string? TemplateError = null);

public interface IProjectCloseService
{
    Task<ProjectOpResult<CloseSummaryDto>> GetSummaryAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<ProjectOpResult<CloseProjectResultDto>> CloseAsync(Guid projectId, Guid userId, CloseProjectRequest request, CancellationToken ct);
    Task<ProjectOpResult<bool>> ReopenAsync(Guid projectId, Guid userId, CancellationToken ct);
}

// ── Automations ──
public record AutomationDto(string Kind, string Title, string Description, bool IsEnabled, int RunCount,
    DateTime? LastRunAt, JsonElement Config);
/// DependentsFollow mirrors the "dependents_follow" rule: the Gantt drag moves successors too by default.
public record AutomationListDto(IReadOnlyList<AutomationDto> Rules, bool DependentsFollow);
public record SaveAutomationRequest(bool IsEnabled, JsonElement? Config = null);

public interface IProjectAutomationService
{
    Task<ProjectOpResult<AutomationListDto>> GetAsync(Guid projectId, Guid userId, CancellationToken ct);
    Task<ProjectOpResult<AutomationDto>> SaveAsync(Guid projectId, Guid userId, string kind, SaveAutomationRequest request, CancellationToken ct);
}

// ── Overview ──
public record OverviewChecklistItemDto(string Key, string Label, bool Done);
public record OverviewStatusCountDto(string Label, string Color, string Stage, int Count);
/// Done holds null after the current week (the line stops at today).
public record OverviewBurnUpDto(IReadOnlyList<string> Weeks, IReadOnlyList<int> Plan, IReadOnlyList<int?> Done);
/// State: achieved | at_risk | upcoming.
public record OverviewMilestoneDto(Guid Id, string Title, DateTime Date, string State, string? Note);
/// Reason: late | stuck | overloaded. OwnerNik is only the avatar key.
public record OverviewAttentionDto(Guid TaskId, string Title, string Status, string StatusColor, string Reason, string? OwnerName, string? OwnerNik);
public record OverviewUpdateDto(string ActorName, string ActorNik, string Text, DateTime At);
public record ProjectOverviewDto(string? Goal, DateTime? StartDate, DateTime? Deadline, DateTime? ProjectFinish, string LeadName,
    string? NasPath, IReadOnlyList<OverviewChecklistItemDto> Checklist, IReadOnlyList<OverviewStatusCountDto> StatusCounts,
    OverviewBurnUpDto BurnUp, IReadOnlyList<OverviewMilestoneDto> Milestones, IReadOnlyList<OverviewAttentionDto> NeedsAttention,
    IReadOnlyList<OverviewUpdateDto> LatestUpdates);

// ── Weekly workload (hours) ──
public record WorkloadWeekDto(DateTime Start, string Label);
public record WorkloadCellDto(decimal Hours, int Tasks, int Percent, bool Off);
public record WorkloadPersonDto(Guid UserId, string Name, string Nik, string Role, IReadOnlyList<WorkloadCellDto> Weeks);
public record WeeklyWorkloadDto(IReadOnlyList<WorkloadWeekDto> Weeks, IReadOnlyList<WorkloadPersonDto> People, int CapacityHoursPerWeek);
public record WorkloadSuggestRequest(Guid UserId, DateTime WeekStart);
public record WorkloadSuggestionDto(Guid TaskId, string Title, decimal Hours, Guid ToUserId, string ToName, decimal ToFreeHours);
public record WorkloadSuggestionsDto(IReadOnlyList<WorkloadSuggestionDto> Suggestions);
public record WorkloadReassignRequest(Guid TaskId, Guid FromUserId, Guid ToUserId);

public interface IProjectOverviewService
{
    Task<ProjectOpResult<ProjectOverviewDto>> GetOverviewAsync(Guid projectId, Guid userId, DateTime localToday, CancellationToken ct);
    Task<ProjectOpResult<WeeklyWorkloadDto>> GetWeeklyWorkloadAsync(Guid projectId, Guid userId, DateTime? from, DateTime localToday, CancellationToken ct);
    Task<ProjectOpResult<WorkloadSuggestionsDto>> SuggestAsync(Guid projectId, Guid userId, WorkloadSuggestRequest request, CancellationToken ct);
    Task<ProjectOpResult<bool>> ReassignAsync(Guid projectId, Guid userId, WorkloadReassignRequest request, CancellationToken ct);
}

/// The fixed automation catalogue (no free-form scripting).
public static class ProjectAutomationCatalogue
{
    public const string StuckNotifyLead = "stuck_notify_lead";
    public const string DueTomorrowRemind = "due_tomorrow_remind";
    public const string SubtasksDoneReview = "subtasks_done_review";
    public const string FormToIntake = "form_to_intake";
    public const string WeeklyRecurringTask = "weekly_recurring_task";
    public const string DependentsFollow = "dependents_follow";

    public record Entry(string Kind, string Title, string Description, string DefaultConfigJson, bool DefaultOnForNewProjects);

    public static readonly IReadOnlyList<Entry> All =
    [
        new(StuckNotifyLead, "Tell the lead when a task is stuck", "When a task moves to the Stuck status, the project owner is notified (once per task per day).", "{\"statusLabel\":\"Stuck\"}", true),
        new(DueTomorrowRemind, "Remind people the day before", "Every morning at 08:00, each assignee is reminded of open tasks due tomorrow.", "{}", true),
        new(SubtasksDoneReview, "Send to review when all subtasks are done", "When the last subtask of a task is done, the task moves to its review status.", "{}", true),
        new(FormToIntake, "Requests land in Intake", "Answers from the request form become tasks in the Intake section. Switching this off closes the form.", "{}", false),
        new(WeeklyRecurringTask, "Create a weekly task", "Creates one task every week on the chosen weekday at 07:30.", "{\"title\":\"\",\"weekday\":1}", false),
        new(DependentsFollow, "Dependents follow", "On the Gantt, moving a task also moves the tasks that wait for it.", "{}", true),
    ];

    public static Entry? Find(string kind) => All.FirstOrDefault(e => e.Kind == kind);
}
