using IPop.Web.Services;

namespace IPop.Web.Components.Admin;

/// Turns raw admin data (audit codes, device fingerprints, timestamps) into plain English.
/// Pure functions, so Users, Security and HRIS all word things the same way.
public static class AdminHumanize
{
    public sealed record Device(string Label, string Icon, bool Automated);

    /// ipop.js fingerprint(): "brands|platform|mobile-or-desktop|language|WxH".
    /// Browsers without userAgentData send the first 40 chars of the UA as "brands".
    public static Device ParseDevice(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return new("Unknown device", "monitor", false);
        var parts = fingerprint.Split('|');
        var brands = parts[0];
        var platform = parts.Length > 1 ? parts[1] : "";
        var mobile = parts.Length > 2 && parts[2] == "mobile";
        var automated = brands.Contains("Headless", StringComparison.OrdinalIgnoreCase);

        var browser = automated ? "Headless Chrome"
            : Has(brands, "Edge") ? "Edge"
            : Has(brands, "Opera") ? "Opera"
            : Has(brands, "Brave") ? "Brave"
            : Has(brands, "Google Chrome") ? "Chrome"
            : Has(brands, "Chromium") ? "Chrome"
            : Has(brands, "Firefox") ? "Firefox"
            : Has(brands, "Safari") ? "Safari"
            : "Browser";
        var os = Os(platform + " " + brands);
        var label = os is null ? browser : $"{browser} on {os}";
        var icon = automated ? "bot" : mobile ? "smartphone" : "laptop";
        return new(label, icon, automated);
    }

    private static bool Has(string s, string token) => s.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string? Os(string s) =>
        Has(s, "Android") ? "Android"
        : Has(s, "iPhone") || Has(s, "iOS") ? "iPhone"
        : Has(s, "iPad") ? "iPad"
        : Has(s, "Win") ? "Windows"
        : Has(s, "Mac") ? "macOS"
        : Has(s, "CrOS") || Has(s, "Chrome OS") ? "ChromeOS"
        : Has(s, "Linux") ? "Linux"
        : null;

    public static string Ago(DateTime? at)
    {
        if (at is null) return "Never";
        var d = DateTime.UtcNow - DateTime.SpecifyKind(at.Value, DateTimeKind.Utc);
        if (d.TotalMinutes < 1) return "Just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "Yesterday";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
        return Local(at).ToString("d MMM yyyy");
    }

    public static DateTime Local(DateTime? utc) =>
        DateTime.SpecifyKind(utc ?? DateTime.UtcNow, DateTimeKind.Utc).ToLocalTime();

    /// "Today", "Yesterday", or a date — used as day headers in activity lists.
    public static string Day(DateTime utc)
    {
        var d = Local(utc).Date;
        var today = DateTime.Now.Date;
        return d == today ? "Today" : d == today.AddDays(-1) ? "Yesterday" : d.ToString("dddd, d MMMM yyyy");
    }

    public static string Plural(int n, string one, string? many = null) =>
        $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";

    // ---- audit ----

    public sealed record Kind(string Key, string Icon);

    public static Kind KindOf(string action) =>
        action.StartsWith("auth") || action.StartsWith("admin.session") || action.StartsWith("security")
            ? new("security", "shield")
        : action.StartsWith("admin.user") ? new("user", "user")
        : action.StartsWith("hris") || action.StartsWith("sync") ? new("sync", "refresh-cw")
        : action.StartsWith("admin.org") || action.StartsWith("admin.orgnode") || action.StartsWith("admin.dirgroup")
            ? new("org", "building-2")
        : action.StartsWith("settings") ? new("settings", "settings")
        : action.Contains("export") ? new("export", "download")
        : new("other", "activity");

    public static string Actor(AuditRowDto r) =>
        r.ActorName ?? (string.IsNullOrEmpty(r.ActorNik) || r.ActorNik == "system" ? "System" : r.ActorNik);

    private static string Target(AuditRowDto r) => r.EntityName ?? r.EntityId ?? "a user";

    private static int? Count(string? entityId) =>
        entityId?.Split(';').Select(p => p.Split('=')).FirstOrDefault(kv => kv.Length == 2 && kv[0] is "revoked" or "n") is { } kv
        && int.TryParse(kv[1], out var n) ? n : null;

    private static readonly Dictionary<string, string> SettingNames = new()
    {
        ["hris.autoSync.cron"] = "the automatic sync schedule",
        ["hris.protectManualEdits"] = "manual-edit protection",
        ["hris.notifyOnFailure"] = "sync failure alerts",
    };

    /// One readable sentence per audit row; the raw code stays available in the detail view.
    public static string Sentence(AuditRowDto r)
    {
        var a = Actor(r);
        var t = Target(r);
        var self = r.ActorNik is not null && r.ActorNik == r.EntityId;
        var parts = r.Action.Split('.');
        if (r.Action.StartsWith("admin.org.") && parts.Length == 4)
        {
            // UI names: Site = plant, Department entity = division, Division entity = department.
            var unit = parts[2] switch { "site" => "plant", "dept" => "division", "division" => "department", _ => "unit" };
            var verb = parts[3] switch { "create" => "added", "edit" => "edited", "delete" => "deleted", "purge" => "purged", _ => parts[3] };
            return $"{a} {verb} {unit} {r.EntityId}";
        }
        return r.Action switch
        {
            "auth.login" => $"{a} signed in",
            "auth.locked" => $"{a}'s account locked after failed sign-ins",
            "auth.password_changed" => $"{a} changed their password",
            "auth.password_reset_requested" => $"{a} asked for a password reset link",
            "auth.password_reset" => $"{a} reset their password by email",
            "admin.user.create" => $"{a} added {t}",
            "admin.user.edit" => $"{a} edited {(self ? "their own" : t + "'s")} profile",
            "admin.user.deactivate" => $"{a} deactivated {t}",
            "admin.user.reactivate" => $"{a} activated {t}",
            "admin.user.lock" => $"{a} locked {t}'s account",
            "admin.user.unlock" => $"{a} unlocked {t}'s account",
            "admin.user.reset-password" => $"{a} reset {t}'s password",
            "admin.user.roles" => $"{a} changed {(self ? "their own" : t + "'s")} role",
            "admin.user.placement" => $"{a} moved {t} to another unit",
            "admin.user.division.assign" => $"{a} assigned {t} to a department",
            "admin.user.division.unassign" => $"{a} removed {t} from their department",
            "admin.session.revoke" => $"{a} ended a session",
            "admin.session.revoke-all" => $"{a} signed {(self ? "themselves" : t)} out everywhere",
            "admin.session.revoke-others" => Count(r.EntityId) is { } n
                ? $"{a} signed out {Plural(n, "other session")}" : $"{a} signed out all other sessions",
            "admin.session.revoke-automated" => Count(r.EntityId) is { } m
                ? $"{a} signed out {Plural(m, "automated session")}" : $"{a} signed out automated sessions",
            "hris.sync" => r.ActorNik is null or "system" ? "HRIS sync ran automatically" : $"{a} ran an HRIS sync",
            "settings.changed" => $"{a} changed {(r.EntityId is { } k && SettingNames.TryGetValue(k, out var s) ? s : "setting " + r.EntityId)}",
            "audit.export_csv" => $"{a} exported the audit log",
            "directory.export_csv" => $"{a} exported the directory",
            "news.delete" => $"{a} deleted a news post",
            "chat.pin" => $"{a} pinned a chat message",
            "chat.unpin" => $"{a} unpinned a chat message",
            "chat.save" => $"{a} saved a chat message",
            "chat.unsave" => $"{a} removed a saved chat message",
            "files.onlyoffice_save" => $"{a} saved a document in the editor",
            _ when r.Action.StartsWith("admin.orgnode") => $"{a} updated the org chart",
            _ when r.Action.StartsWith("admin.dirgroup") => $"{a} updated a directory group",
            _ => $"{a} · {r.Action}",
        };
    }

    public sealed record AuditGroup(AuditRowDto First, int Count, DateTime Oldest, List<AuditRowDto> Rows);

    /// Collapse runs of the same action by the same actor on the same target (rows arrive newest first).
    public static List<AuditGroup> Collapse(IEnumerable<AuditRowDto> rows)
    {
        var groups = new List<AuditGroup>();
        foreach (var r in rows)
        {
            var last = groups.Count > 0 ? groups[^1] : null;
            if (last is not null && last.First.Action == r.Action && last.First.ActorNik == r.ActorNik
                && last.First.EntityId == r.EntityId && Local(last.First.CreatedAt).Date == Local(r.CreatedAt).Date)
            {
                last.Rows.Add(r);
                groups[^1] = last with { Count = last.Count + 1, Oldest = r.CreatedAt };
            }
            else groups.Add(new(r, 1, r.CreatedAt, [r]));
        }
        return groups;
    }
}
