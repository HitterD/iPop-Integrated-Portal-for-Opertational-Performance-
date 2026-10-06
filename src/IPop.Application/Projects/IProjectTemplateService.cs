namespace IPop.Application.Projects;

/// A template row in New project → Template. Only templates the caller may use are listed:
/// built-in, "everyone", the caller's own, and "department" ones of the caller's department.
public record ProjectTemplateDto(Guid Id, string Name, string? Description, int TaskCount, Guid? SourceProjectId,
    int GroupCount = 0, int MilestoneCount = 0, int UsedCount = 0, bool IsBuiltIn = false,
    string Visibility = "everyone", string? CreatedByName = null, string? DepartmentName = null,
    string? Icon = null, string? Color = null, bool CanDelete = false, string? SourceProjectName = null);

public record ProjectTemplateTaskDto(Guid Id, string Title, string? Description, string? Priority, int? DueOffsetDays, int SortOrder,
    Guid? GroupId = null, IReadOnlyList<string>? AssigneeNiks = null);
public record ProjectTemplateGroupDto(Guid Id, string Name, string Color, int SortOrder);
public record ProjectTemplateMilestoneDto(Guid Id, string Title, int DueOffsetDays, IReadOnlyList<Guid> TaskIds);
public record ProjectTemplateDetailDto(Guid Id, string Name, string? Description, IReadOnlyList<ProjectTemplateTaskDto> Tasks,
    IReadOnlyList<ProjectTemplateGroupDto>? Groups = null, IReadOnlyList<ProjectTemplateMilestoneDto>? Milestones = null);

/// Save as template. Tasks (top-level) are always copied when SourceProjectId is set; the
/// Include* flags add sections, milestones (dates relative to the project start) and assignees
/// (stored by NIK). Comments and files are never copied. Visibility: private | department | everyone.
public record CreateProjectTemplateRequest(string? Name, string? Description, Guid? SourceProjectId,
    bool IncludeSections = true, bool IncludeMilestones = true, bool IncludeAssignees = false,
    string? Visibility = null, string? Icon = null, string? Color = null);

public record CreateProjectFromTemplateRequest(Guid TemplateId, string? Name, string? Description, int? Year, Guid? OwnerId,
    DateTime? StartDate, DateTime? Deadline, string? OwnerNik = null, string? Color = null);

/// The caller's department, for the "Who can use it" segment.
public record ProjectTemplateScopeDto(Guid? DepartmentId, string? DepartmentName);

/// Error codes: "not_found", "forbidden", "invalid_name", "invalid_visibility", "no_department",
/// "built_in", plus the project-creation codes from IProjectService.
public record TemplateResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

public interface IProjectTemplateService
{
    Task<IReadOnlyList<ProjectTemplateDto>> ListAsync(Guid actorId, bool isAdmin, CancellationToken ct);
    Task<TemplateResult<ProjectTemplateDetailDto>> GetAsync(Guid actorId, Guid templateId, CancellationToken ct);
    Task<ProjectTemplateScopeDto> ScopeAsync(Guid actorId, CancellationToken ct);
    Task<TemplateResult<ProjectTemplateDto>> CreateAsync(Guid actorId, CreateProjectTemplateRequest req, CancellationToken ct);
    /// Creates the project, its sections, tasks (due = start + offset), milestones and
    /// assignees (existing users only), then bumps UsedCount.
    Task<TemplateResult<ProjectDto>> CreateProjectAsync(Guid actorId, CreateProjectFromTemplateRequest req, CancellationToken ct);
    /// Soft delete (IsActive = false). Creator or Admin only; built-ins never.
    Task<TemplateResult<bool>> DeleteAsync(Guid actorId, Guid templateId, bool isAdmin, CancellationToken ct);
}
