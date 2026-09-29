using System.Net.Http.Json;
using IPop.Web.Services;

namespace IPop.Web.Components.Admin;

/// One read of the org lookups, shared by the Organization, Directory tree and Org chart tabs.
/// UI speaks the mockup's words while the tables keep their names:
///   Plant = Site, Division = Department entity, Department = Division entity.
public sealed class AdminOrgModel
{
    public OrgSiteRowDto[] Plants { get; private init; } = [];
    public OrgDepartmentRowDto[] Divisions { get; private init; } = [];
    public AdminDivisionRowDto[] Departments { get; private init; } = [];

    public IEnumerable<OrgSiteRowDto> ActivePlants => Plants.Where(p => p.IsActive).OrderBy(p => p.Name);
    public IEnumerable<OrgDepartmentRowDto> ActiveDivisions => Divisions.Where(d => d.IsActive).OrderBy(d => d.Name);
    public IEnumerable<AdminDivisionRowDto> ActiveDepartments => Departments.Where(d => d.IsActive).OrderBy(d => d.Name);

    public int AssignedEmployees => ActiveDepartments.Sum(d => d.ActiveMemberCount);

    public static async Task<AdminOrgModel?> LoadAsync(AuthState auth)
    {
        var view = await auth.GetAsync<OrgViewDto>("/api/admin/org");
        var depts = await auth.GetAsync<AdminDivisionRowDto[]>("/api/admin/org/divisions");
        if (view is null || depts is null) return null;
        return new AdminOrgModel { Plants = view.Sites, Divisions = view.Departments, Departments = depts };
    }

    /// Codes are required and unique server-side but meaningless to admins, so derive one from the name.
    public static string NewCode(string name, IEnumerable<string> taken)
    {
        var baseCode = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(10).ToArray());
        if (baseCode.Length == 0) baseCode = "UNIT";
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(baseCode)) return baseCode;
        for (var i = 2; ; i++)
            if (!set.Contains($"{baseCode}{i}")) return $"{baseCode}{i}";
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name.Trim()[..Math.Min(2, name.Trim().Length)]).ToUpperInvariant();
    }

    public static async Task<string> ErrorText(HttpResponseMessage res, string fallback)
    {
        try
        {
            var body = await res.Content.ReadFromJsonAsync<ApiError>();
            if (!string.IsNullOrWhiteSpace(body?.Message)) return body.Message;
        }
        catch { /* non-JSON body */ }
        return fallback;
    }

    private sealed record ApiError(string? Code, string? Message);
}
