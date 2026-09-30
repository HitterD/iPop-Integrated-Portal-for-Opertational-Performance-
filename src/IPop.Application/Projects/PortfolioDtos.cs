namespace IPop.Application.Projects;

/// <summary>Portfolio aggregates are limited to active projects (not completed/cancelled).</summary>
public record PortfolioDto(string Scope, bool IsSuperAdmin, int ActiveProjects, int AtRiskProjects,
    int AverageProgress, int MyDueWeek, int OverdueTasks,
    PagedResult<PortfolioProjectDto> Projects, IReadOnlyList<PortfolioPersonDto> OverdueByPerson,
    IReadOnlyList<PortfolioPersonDto> WorkloadByPerson, IReadOnlyList<PortfolioDeadlineDto> Deadlines);

public record PortfolioProjectDto(Guid ProjectId, string Name, string Status, int Progress,
    DateTime? Deadline, int TaskCount, int DoneCount, int OverdueCount, string Health);

public record PortfolioPersonDto(Guid UserId, string Name, int OverdueCount, int OpenTaskCount,
    decimal EstimatedHours, int CapacityTasks, bool OverCapacity);

public record PortfolioDeadlineDto(Guid ProjectId, string Name, DateTime Deadline, int Progress, string Health);
public record PortfolioAssigneeDto(Guid UserId, string Name);
public record PortfolioTaskDto(Guid TaskId, Guid ProjectId, string ProjectName, string Title,
    string Status, DateTime? DueDate, decimal? EstimatedHours, IReadOnlyList<PortfolioAssigneeDto> Assignees);
