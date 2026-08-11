using IPop.Web.Models;

namespace IPop.Web.Services;

/// Mirrors the API's DirectoryEmployee / DirectoryDepartment (camelCase JSON both ways).
/// Roles arrives on /api/directory/me and stays empty on the employees list.
public record DirectoryEmployeeDto(
    string Nik, string Name, string? Position, string? Department, string? DepartmentCode,
    string? Site, string? SiteCode, string? Manager, string Email, string? Extension,
    string? Location, string Status, string[]? Roles = null, string? PhotoPath = null);

public record DirectoryDepartmentDto(string Code, string Name, string? ParentCode, int DirectMembers);
public record DirectorySiteDto(string Code, string Name);
public record DirectoryPageDto(int Total, int Page, int PageSize, DirectoryEmployeeDto[] Items);

/// Mirrors the API's DirGroupDto / DirGroupMemberDto (camelCase JSON both ways).
public record DirGroupDto(Guid Id, string Name, Guid? ParentId, int SortOrder, bool IsActive, DirGroupMemberDto[] Members);
public record DirGroupMemberDto(string Nik, string Name, string? Position, string? Department, string? Site, string Status);

/// Mirrors the API's OrgNodeDto / OrgNodeMemberDto.
public record OrgNodeDto(Guid Id, string Name, string? Title, Guid? ParentId, Guid? DepartmentId, int SortOrder,
    bool IsActive, OrgNodeMemberDto[] Members);
public record OrgNodeMemberDto(string Nik, string Name, string? Position, string? Department,
    string? Site, string Status);

/// Mirrors the API's member-search rows (admin picker).
public record MemberSearchRow(string Nik, string Name, string? Position, string? Department)
{
    public string Initials =>
        string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(w => w[0]).Take(2)).ToUpperInvariant();
    public string PositionLabel => string.IsNullOrWhiteSpace(Position)
        ? (Department ?? "Directory")
        : Position;
    public string AvatarColor => AvatarPalette.For(Nik);
}

/// Mirrors the API's DirectoryStructure / DirectorySiteNode / DirectoryDepartmentNode /
/// DirectoryDivisionNode (camelCase JSON both ways). Suffixed Dto*Node to avoid colliding with
/// the existing flat DirectoryDepartmentDto / DirectorySiteDto.
public record DirectoryStructureNodeDto(DirectorySiteNodeDto[] Sites, int NoDivisionCount);
public record DirectorySiteNodeDto(string Code, string Name, int MemberCount, DirectoryDepartmentNodeDto[] Departments);
public record DirectoryDepartmentNodeDto(string Code, string Name, int MemberCount, DirectoryDivisionNodeDto[] Divisions);
public record DirectoryDivisionNodeDto(string Code, string Name, int MemberCount);
