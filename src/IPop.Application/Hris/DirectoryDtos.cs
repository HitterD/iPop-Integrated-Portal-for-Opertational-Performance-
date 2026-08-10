namespace IPop.Application.Hris;

/// Flat employee row for bounded Directory queries (spec §6).
public record DirectoryEmployee(
    string Nik, string Name, string? Position, string? Department, string? DepartmentCode,
    string? Site, string? SiteCode, string? Manager, string Email, string? Extension,
    string? Location, string Status, string[]? Roles = null, string? PhotoPath = null);

public record DirectoryDepartment(string Code, string Name, string? ParentCode, int DirectMembers);
public record DirectorySite(string Code, string Name);
public record DirectoryQuery(string? Q, string? DepartmentCode, string? SiteCode,
    string? Presence, string Sort = "name", bool Desc = false, int Page = 1, int PageSize = 120);
public record DirectoryPage(int Total, int Page, int PageSize, DirectoryEmployee[] Items);

/// Custom directory-group tree (admin-owned folders). Read for everyone via /api/directory/dir-groups.
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive,
    DirGroupMemberDto[] Members);
public record DirGroupMemberDto(string Nik, string Name, string? Position, string? Department,
    string? Site, string Status);

/// Org-chart node tree (admin-owned positions). Read for everyone via /api/directory/org-tree.
public record OrgNodeDto(Guid Id, string Name, string? Title, Guid? ParentId, Guid? DepartmentId, int SortOrder,
    bool IsActive, OrgNodeMemberDto[] Members);
public record OrgNodeMemberDto(string Nik, string Name, string? Position, string? Department,
    string? Site, string Status);
