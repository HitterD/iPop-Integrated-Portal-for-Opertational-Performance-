using IPop.Application.Rbac;

namespace IPop.Web.Components.Admin;

/// The Admin › People role picker. A person holds one preset; any other stored role (a custom
/// RBAC role) is kept when the preset changes.
public static class AdminRoles
{
    public static readonly string[] Picker =
        [RoleNames.Employee, RoleNames.HrNews, RoleNames.HcApproval, RoleNames.HcApprovalDirectory, RoleNames.HcFull, RoleNames.Admin];

    // Strongest first: it decides the chip shown in the list.
    private static readonly string[] Rank =
        [RoleNames.Admin, RoleNames.HcFull, RoleNames.HcApprovalDirectory, RoleNames.HcApproval, RoleNames.HrNews];

    public static string Label(string role) => RoleNames.Label(role);
    public static string Hint(string role) => RoleNames.Describe(role);

    public static string Primary(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet();
        return Rank.FirstOrDefault(set.Contains) ?? RoleNames.Employee;
    }

    /// Employee is the implicit base role, so it is only stored when nothing else is.
    public static string[] With(IEnumerable<string> current, string role)
    {
        var kept = current.Where(r => !Picker.Contains(r)).ToList();
        if (role != RoleNames.Employee || kept.Count == 0) kept.Add(role);
        return kept.Distinct().ToArray();
    }

    public static string Css(string role) => role switch
    {
        RoleNames.Admin => "admin",
        RoleNames.HrNews => "hr",
        RoleNames.HcApproval or RoleNames.HcApprovalDirectory or RoleNames.HcFull => "hc",
        _ => "emp",
    };
}
