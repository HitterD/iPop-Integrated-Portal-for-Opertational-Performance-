namespace IPop.Application.Projects;

public record ProjectFieldDto(Guid Id, Guid ProjectId, string Name, string Type, IReadOnlyList<string> Options, int SortOrder);
public record ProjectFieldValueDto(Guid FieldId, string Value);
public record ProjectTaskFieldValuesDto(Guid TaskId, IReadOnlyList<ProjectFieldValueDto> Values);
public record CreateProjectFieldRequest(string? Name, string? Type, IReadOnlyList<string>? Options);
public record UpdateProjectFieldRequest(string? Name, IReadOnlyList<string>? Options, int? SortOrder);
public record SetProjectFieldValueRequest(string? Value);
public record ProjectFieldResult<T>(T? Data, string? Error)
{
    public bool Success => Error is null;
}

public interface IProjectFieldService
{
    Task<ProjectFieldResult<IReadOnlyList<ProjectFieldDto>>> ListAsync(Guid actorId, Guid projectId, CancellationToken ct);
    Task<ProjectFieldResult<ProjectFieldDto>> CreateAsync(Guid actorId, Guid projectId, CreateProjectFieldRequest request, CancellationToken ct);
    Task<ProjectFieldResult<ProjectFieldDto>> UpdateAsync(Guid actorId, Guid projectId, Guid fieldId, UpdateProjectFieldRequest request, CancellationToken ct);
    Task<ProjectFieldResult<bool>> DeleteAsync(Guid actorId, Guid projectId, Guid fieldId, CancellationToken ct);
    Task<ProjectFieldResult<IReadOnlyList<ProjectFieldValueDto>>> GetTaskValuesAsync(Guid actorId, Guid projectId, Guid taskId, CancellationToken ct);
    Task<ProjectFieldResult<IReadOnlyList<ProjectTaskFieldValuesDto>>> GetValuesForTasksAsync(Guid actorId, Guid projectId, IReadOnlyList<Guid> taskIds, CancellationToken ct);
    Task<ProjectFieldResult<ProjectFieldValueDto?>> SetTaskValueAsync(Guid actorId, Guid projectId, Guid taskId, Guid fieldId, string? value, CancellationToken ct);
}
