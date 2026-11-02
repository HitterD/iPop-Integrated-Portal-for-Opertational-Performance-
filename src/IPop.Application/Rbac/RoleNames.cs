namespace IPop.Application.Rbac;

/// Permission presets (ready-made roles). "HR" keeps its stored name — News, Portfolio and the
/// tutorials match on it — and is shown to people as "HR – News".
public static class RoleNames
{
    public const string Admin = "Admin";
    public const string Employee = "Employee";
    public const string HrNews = "HR";
    public const string HrNewsLabel = "HR – News";
    public const string HcApproval = "HC – Approval";
    public const string HcApprovalDirectory = "HC – Approval & Directory";
    public const string HcFull = "HC – Lengkap";

    public static readonly string[] Presets = [HrNews, HcApproval, HcApprovalDirectory, HcFull];

    public static string Label(string role) => role == HrNews ? HrNewsLabel : role;

    public static string Describe(string role) => role switch
    {
        Admin => "Full access, including Administration.",
        HrNews => "Publishes, pins and deletes News & Memos and tracks acknowledgements.",
        HcApproval => "Sees and edits everyone's approvers, imports and exports the approver workbook.",
        HcApprovalDirectory => "Approvers, plus the Directory tree: plants, divisions, departments and who sits where.",
        HcFull => "Approvers, the Directory tree, and News & Memos.",
        _ => "Reads news and memos. No publishing or admin rights.",
    };
}

public static class PermissionCodes
{
    public const string ApprovalRead = "approval.read";
    public const string ApprovalManage = "approval.manage";
    public const string DirectoryManage = "directory.manage";
}
