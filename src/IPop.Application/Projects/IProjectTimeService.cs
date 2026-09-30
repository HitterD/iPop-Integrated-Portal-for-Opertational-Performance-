namespace IPop.Application.Projects;

// Phase 3: time tracking. Entries roll up into ProjectTask.ActualHours; the typed value is gone.

public record RunningTimerDto(Guid EntryId, Guid TaskId, string TaskTitle, Guid ProjectId, string ProjectName, DateTime StartedAt);

public record TimeEntryDto(Guid Id, Guid TaskId, Guid UserId, string UserName, DateTime StartedAt, DateTime? EndedAt,
    int Minutes, string? Note, string Source, bool AutoStopped, bool CanEdit, string? UserNik = null);

public record TaskTimeDto(Guid TaskId, int TotalMinutes, IReadOnlyList<TimeEntryDto> Entries, bool CanTrack);

/// StartedAt is an instant (UTC); the client turns "date picked + now's time" into one.
public record AddTimeEntryRequest(int Minutes, DateTime StartedAt, string? Note);
public record UpdateTimeEntryRequest(int? Minutes, DateTime? StartedAt, string? Note);

/// One person's week: minutes per day (index 0 = the first day of the week in the viewer's zone).
public record TimesheetRowDto(Guid UserId, string Name, IReadOnlyList<int> DayMinutes, int TotalMinutes, string? Nik = null);
public record TimesheetDto(DateTime WeekStart, IReadOnlyList<TimesheetRowDto> Rows, IReadOnlyList<int> DayTotals, int TotalMinutes);

/// A timer the 10-hour cap stopped; the worker turns each into a notification.
public record AutoStoppedTimerDto(Guid EntryId, Guid UserId, Guid TaskId, string TaskTitle, Guid ProjectId);

public record TimeResult<T>(T? Data, string? Error)
{
    public bool Success => Error is null;
}

public interface IProjectTimeService
{
    Task<RunningTimerDto?> GetRunningAsync(Guid actorId, CancellationToken ct);
    /// Starts a timer on the task; a timer already running elsewhere is stopped first.
    Task<TimeResult<RunningTimerDto>> StartAsync(Guid actorId, Guid taskId, CancellationToken ct, DateTime? now = null);
    /// Stops the actor's running timer. Data is null when nothing was running.
    Task<TimeResult<TimeEntryDto?>> StopAsync(Guid actorId, CancellationToken ct, DateTime? now = null);
    Task<TimeResult<TaskTimeDto>> GetTaskTimeAsync(Guid actorId, Guid taskId, CancellationToken ct);
    Task<TimeResult<TimeEntryDto>> AddManualAsync(Guid actorId, Guid taskId, AddTimeEntryRequest req, CancellationToken ct);
    Task<TimeResult<TimeEntryDto>> UpdateEntryAsync(Guid actorId, Guid entryId, UpdateTimeEntryRequest req, CancellationToken ct);
    Task<TimeResult<Guid>> DeleteEntryAsync(Guid actorId, Guid entryId, CancellationToken ct);
    /// weekStart: the UTC instant of the viewer's local Monday 00:00; days are 24h slices from it.
    Task<TimeResult<TimesheetDto>> GetProjectTimesheetAsync(Guid actorId, Guid projectId, DateTime weekStart, CancellationToken ct);
    /// Admin/HR see every project; everyone else the projects they are a member of.
    Task<TimesheetDto> GetPortfolioTimesheetAsync(Guid actorId, DateTime weekStart, CancellationToken ct);
    /// Caps timers running longer than the limit and returns them.
    Task<IReadOnlyList<AutoStoppedTimerDto>> AutoStopAsync(DateTime now, CancellationToken ct);
    /// The project a task (or entry's task) belongs to, for realtime pushes.
    Task<Guid?> ProjectOfTaskAsync(Guid taskId, CancellationToken ct);
}
