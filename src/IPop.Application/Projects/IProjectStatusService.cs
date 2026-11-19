namespace IPop.Application.Projects;

public record ProjectStatusOptionDto(Guid Id, string Label, string Color, string Stage, int SortOrder, int TaskCount);

/// One row of a full-replace save. Id null = new option; an existing Id keeps its tasks.
public record SaveStatusOptionRequest(Guid? Id, string Label, string Color, string Stage);

/// Options in display order. Reassign maps a removed option id → a kept option id; required for
/// every removed option that still has tasks.
public record SaveStatusOptionsRequest(IReadOnlyList<SaveStatusOptionRequest> Options, IReadOnlyDictionary<Guid, Guid>? Reassign = null);

public record SaveProjectTabsRequest(IReadOnlyList<string> Tabs);

public record ProjectStatusResult<T>(T? Data, string? Error)
{
    public bool Success => Error is null;
}

/// Per-project custom statuses (each mapped to a stage: todo | doing | review | done) and the tab layout.
public interface IProjectStatusService
{
    /// Any project member (viewer included) or admin. Materializes the defaults on first read.
    Task<ProjectStatusResult<IReadOnlyList<ProjectStatusOptionDto>>> GetAsync(Guid projectId, Guid userId, CancellationToken ct);
    /// Owner/manager only. Full replace in the given order.
    Task<ProjectStatusResult<IReadOnlyList<ProjectStatusOptionDto>>> SaveAsync(Guid projectId, Guid userId, SaveStatusOptionsRequest request, CancellationToken ct);
    /// Writes the default status set (and points existing tasks at it) the first time; no-op afterwards.
    Task EnsureStatusesAsync(Guid projectId, CancellationToken ct);
    /// Owner/manager only. 1–16 unique known view keys.
    Task<ProjectStatusResult<IReadOnlyList<string>>> SaveTabsAsync(Guid projectId, Guid userId, SaveProjectTabsRequest request, CancellationToken ct);
}
