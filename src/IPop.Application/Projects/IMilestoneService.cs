namespace IPop.Application.Projects;

/// Milestone statuses, computed on read (never stored).
public static class MilestoneStatus
{
    public const string Achieved = "achieved";
    public const string Overdue = "overdue";
    public const string DueSoon = "due_soon";
    public const string Planned = "planned";

    /// Days ahead (inclusive) that count as "due soon".
    public const int DueSoonDays = 7;

    /// Achieved when marked, or automatically when it has linked tasks and every one is done;
    /// otherwise overdue (due date before today), due soon (up to 7 days ahead) or planned.
    public static string Of(DateTime dueDate, DateTime? achievedAt, int linkedCount, int doneCount, DateTime today)
    {
        if (achievedAt is not null || (linkedCount > 0 && doneCount >= linkedCount)) return Achieved;
        var days = (dueDate.Date - today.Date).Days;
        if (days < 0) return Overdue;
        return days <= DueSoonDays ? DueSoon : Planned;
    }
}

/// A project checkpoint. AchievedAt is the manual mark or, when auto-achieved because every
/// linked task is done, the completion time of the last linked task. AchievedManually tells
/// the two apart (only a manual mark can be undone).
public record MilestoneDto(Guid Id, string Title, DateTime DueDate, DateTime? AchievedAt,
    IReadOnlyList<Guid> TaskIds, int LinkedCount, int DoneCount, string Status, bool AchievedManually);

/// TaskId is the legacy single-task link from older clients; it is merged into TaskIds.
public record CreateMilestoneRequest(string? Title, DateTime DueDate, IReadOnlyList<Guid>? TaskIds = null, Guid? TaskId = null);

/// Null fields are left alone; TaskIds replaces the whole link set (empty list = unlink all).
/// TaskId / ClearTask / Achieved are the legacy PUT fields.
public record UpdateMilestoneRequest(string? Title = null, DateTime? DueDate = null, IReadOnlyList<Guid>? TaskIds = null,
    Guid? TaskId = null, bool? ClearTask = null, bool? Achieved = null);

/// Error codes: "forbidden", "project_not_found", "milestone_not_found", "task_not_found", "invalid_title".
public record MilestoneResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

public interface IMilestoneService
{
    /// Members only; ordered by due date. `today` is the caller's calendar date.
    Task<MilestoneResult<IReadOnlyList<MilestoneDto>>> ListAsync(Guid actorId, Guid projectId, DateTime today, CancellationToken ct);
    Task<MilestoneResult<MilestoneDto>> CreateAsync(Guid actorId, Guid projectId, CreateMilestoneRequest req, DateTime today, CancellationToken ct);
    Task<MilestoneResult<MilestoneDto>> UpdateAsync(Guid actorId, Guid projectId, Guid milestoneId, UpdateMilestoneRequest req, DateTime today, CancellationToken ct);
    /// Mark achieved (keeps an existing mark's time) or undo the mark.
    Task<MilestoneResult<MilestoneDto>> SetAchievedAsync(Guid actorId, Guid projectId, Guid milestoneId, bool achieved, DateTime today, CancellationToken ct);
    Task<MilestoneResult<bool>> DeleteAsync(Guid actorId, Guid projectId, Guid milestoneId, CancellationToken ct);
}
