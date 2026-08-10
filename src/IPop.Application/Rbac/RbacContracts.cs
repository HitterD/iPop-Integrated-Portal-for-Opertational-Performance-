namespace IPop.Application.Rbac;

public interface IPermissionEvaluator
{
    Task<bool> IsAllowedAsync(Guid userId, string permissionCode,
        string? departmentCode, CancellationToken ct = default);
}
