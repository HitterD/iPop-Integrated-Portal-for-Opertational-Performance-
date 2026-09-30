namespace IPop.Application.Projects;

public interface IPortfolioService
{
    Task<PortfolioDto> GetPortfolioAsync(Guid userId, CancellationToken ct, DateTime? today = null,
        int page = 1, int pageSize = 20);
    Task<PagedResult<PortfolioTaskDto>> GetTasksAsync(Guid userId, string filter, Guid? projectId,
        Guid? assigneeId, int page, int pageSize, CancellationToken ct, DateTime? today = null);
}
