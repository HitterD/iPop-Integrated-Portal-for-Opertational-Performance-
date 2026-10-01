namespace IPop.Application.Hris;

/// Flat employee row for bounded Directory queries (spec §6).
public record DirectoryEmployee(
    string Nik, string Name, string? Position, string? Department, string? DepartmentCode,
    string? Site, string? SiteCode, string? Manager, string Email, string? Extension,
    string? Location, string Status, string[]? Roles = null, string? PhotoPath = null,
    /// NIK pegawai yang berkorespondensi dengan pegawai ini adalah favorit pemanggil.
    bool IsFavorite = false,
    /// NIK atasan (untuk relasi bawahan di kartu profil).
    string? ManagerNik = null,
    /// Opaque key for deep links (/chat?user=…, /directory/person/…) so URLs never carry the NIK.
    Guid? UserId = null,
    /// Division name ("Section"), shown as "Department › Section" in the Directory list.
    string? Section = null);

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

/// Site ▸ Department ▸ Division browse tree for /api/directory/structure, plus the
/// "No Division" safety node (active users with DivisionId == null).
/// PresenceCounts carries directory-wide totals so the header tabs stay correct even
/// though the list itself is paged (counting the loaded page would show 0 for anyone
/// whose peers happen to sit on a later page).
public record DirectoryStructure(DirectorySiteNode[] Sites, int NoDivisionCount,
    DirectoryPresenceCounts? PresenceCounts = null);
public record DirectoryPresenceCounts(int All, int Online, int Busy, int Away, int Offline);
public record DirectorySiteNode(string Code, string Name, int MemberCount, DirectoryDepartmentNode[] Departments);
/// Children are sub-departments (Department.ParentId) — the tree nests to any depth.
public record DirectoryDepartmentNode(string Code, string Name, int MemberCount, DirectoryDivisionNode[] Divisions,
    DirectoryDepartmentNode[]? Children = null);
public record DirectoryDivisionNode(string Code, string Name, int MemberCount);

/// A division matching a /api/directory/divisions?q= search, with its per-division match count.
public record DirectoryDivisionMatch(string Code, string Name, int MatchCount);
