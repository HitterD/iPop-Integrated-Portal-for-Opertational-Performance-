namespace IPop.Web.Services;

/// Mirrors the API's admin endpoints (camelCase JSON both ways).

public record AdminUserRowDto(string Nik, string Name, string Email, string? Position, string? Location,
    string? Department, string? DepartmentCode, string? Site, string? SiteCode,
    bool IsActive, bool IsManuallyEdited, string[] Roles, string Status, DateTime UpdatedAt);

public record AdminUserPageDto(int Total, int Page, int PageSize, AdminUserRowDto[] Items);

public record OrgDepartmentRowDto(Guid Id, string Code, string Name, string? ParentCode, bool IsActive,
    int MemberCount, int ActiveMemberCount);
public record OrgSiteRowDto(Guid Id, string Code, string Name, bool IsActive,
    int MemberCount, int ActiveMemberCount);
public record OrgViewDto(OrgDepartmentRowDto[] Departments, OrgSiteRowDto[] Sites);
public record AdminDivisionRowDto(Guid Id, string Code, string Name, string? DepartmentCode,
    string? DepartmentName, string? SiteCode, string? SiteName, bool IsActive,
    int MemberCount, int ActiveMemberCount);
public record UpsertDivisionRequest(string Code, string? Name, string? DepartmentCode,
    string? SiteCode, bool? IsActive);
public record AdminSessionRowDto(Guid Id, string Nik, string Name, string? DeviceFingerprint,
    string? IpAddress, DateTime CreatedAt, DateTime ExpiresAt);

public record HrisSyncStatusDto(DateTime? LastSyncedAt, int EmployeeCount);
public record AuditRowDto(Guid Id, string? ActorNik, string Action, string? Entity,
    string? EntityId, string? DetailJson, string? IpAddress, DateTime CreatedAt);
public record AuditPageDto(int Total, int Page, int PageSize, AuditRowDto[] Items);

// Dir-group admin DTOs — mirror AdminEndpoints.cs
public record AdminDirGroupRowDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive,
    AdminDirGroupMemberDto[] Members);
public record AdminDirGroupMemberDto(string Nik, string Name, string? Position, string? Department, string? Site);
public record UpsertDirGroupRequest(string? Name, Guid? ParentId, int? SortOrder, bool? IsActive);
public record DirGroupMemberRequest(string Nik);

// Org-node admin DTOs — mirror AdminEndpoints.cs
public record AdminOrgNodeRowDto(Guid Id, string Name, string? Title, Guid? ParentId, Guid? DepartmentId, int SortOrder,
    bool IsActive, AdminOrgNodeMemberDto[] Members);
public record AdminOrgNodeMemberDto(string Nik, string Name, string? Position, string? Department, string? Site);
public record UpsertOrgNodeRequest(string? Name, string? Title, Guid? ParentId, Guid? DepartmentId, int? SortOrder, bool? IsActive);