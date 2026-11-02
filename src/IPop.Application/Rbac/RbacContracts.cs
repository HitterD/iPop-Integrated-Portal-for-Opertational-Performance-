namespace IPop.Application.Rbac;

public interface IPermissionEvaluator
{
    Task<bool> IsAllowedAsync(Guid userId, string permissionCode,
        string? departmentCode, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AllowedCodesAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
