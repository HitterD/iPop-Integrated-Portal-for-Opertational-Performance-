using IPop.Application.Rbac;

namespace IPop.Web.Components.Admin;

/// Administration sections and who may open each. Admin sees all; other people see the sections
/// their permissions unlock (HC presets: Approval, and Organization with directory.manage).
public static class AdminSections
{
    public sealed record Section(string Group, string Slug, string Label, string Hint, string Icon, string Tint, string? Permission);

    public static readonly IReadOnlyList<Section> All =
    [
        new("People", "users", "Users", "Accounts, roles and access", "users", "blue", null),
        new("Structure", "organization", "Organization", "Plants, divisions and departments", "building-2", "green", PermissionCodes.DirectoryManage),
        new("Structure", "approval", "Approvers", "Who approves for whom, from HC's approver workbook", "git-fork", "violet", PermissionCodes.ApprovalRead),
        new("Security", "security", "Security", "Sessions and audit log", "shield-check", "red", null),
        new("Communication", "meeting-rooms", "Meeting rooms", "Video per room and server recordings", "door-open", "green", null),
        new("Communication", "announcements", "Announcements", "Launch videos that pop up after login", "clapperboard", "orange", null),
        new("Communication", "calendar", "Holidays", "National and company holidays on everyone's calendar", "party-popper", "red", null),
        new("Integrations", "hris", "HRIS sync", "Employee data from HRIS", "refresh-cw", "violet", null),
        new("Integrations", "notifier", "iPop Notifier", "Released versions and PCs per version", "bell-ring", "blue", null),
        new("Integrations", "email", "Email", "The mailbox iPop sends password resets from", "mail", "blue", null),
    ];

    /// Sections with no permission are Admin-only.
    public static IReadOnlyList<Section> Visible(bool isAdmin, Func<string, bool> can) =>
        All.Where(s => isAdmin || (s.Permission is { } p && can(p))).ToList();
}
