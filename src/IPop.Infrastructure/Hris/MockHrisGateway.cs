using IPop.Application.Hris;

namespace IPop.Infrastructure.Hris;

/// In-memory adapter for tests (spec §4).
public class MockHrisGateway(IReadOnlyList<HrisEmployee> data) : IHrisGateway
{
    public Task<IReadOnlyList<HrisEmployee>> SyncEmployeesAsync(DateTime? updatedSince, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HrisEmployee>>(
            updatedSince is null ? [.. data] : [.. data.Where(e => e.UpdatedAt > updatedSince)]);
}
