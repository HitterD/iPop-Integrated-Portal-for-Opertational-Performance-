using System.Globalization;
using System.Net;
using IPop.Application.Tickets;
using Microsoft.AspNetCore.Components;

namespace IPop.Web.Services;

/// Labels, colours and wording for the Tickets module (docs/mockups/ipop-apple/tickets.html).
/// Pure functions so the list, the detail, the form and the Dashboard widget say the same thing.
public static class TicketUi
{
    public sealed record StatusMeta(string Short, string Long, string Css, string Dot, string? Icon, bool Spin);

    private static readonly Dictionary<string, StatusMeta> Statuses = new()
    {
        [TicketStatus.Sending] = new("Sending…", "Sending…", "s-send", "var(--ink-3)", null, true),
        [TicketStatus.Failed] = new("Couldn't send", "Couldn't send", "s-fail", "var(--danger)", "alert-triangle", false),
        [TicketStatus.Todo] = new("Open", "Open", "s-open", "var(--ink-3)", null, false),
        [TicketStatus.InProgress] = new("In progress", "In progress", "s-prog", "var(--info)", null, false),
        [TicketStatus.WaitingVendor] = new("Waiting on vendor", "Waiting on vendor", "s-vend", "var(--tk-violet)", null, false),
        [TicketStatus.Resolved] = new("Please confirm", "Resolved — please confirm", "s-res", "var(--warn)", "hand", false),
        [TicketStatus.Closed] = new("Closed", "Closed", "s-closed", "var(--accent)", "check", false),
        [TicketStatus.Cancelled] = new("Cancelled", "Cancelled", "s-cancel", "var(--ink-3)", null, false),
    };

    public static StatusMeta Status(string? status)
        => status is not null && Statuses.TryGetValue(status, out var m) ? m : new(Humanize(status), Humanize(status), "s-open", "var(--ink-3)", null, false);

    /// Dashboard widget pills are shorter than the list's.
    public static string WidgetLabel(string status) => status switch
    {
        TicketStatus.Resolved => "Confirm",
        TicketStatus.WaitingVendor => "Vendor",
        _ => Status(status).Short,
    };

    public static bool IsPending(string status) => status is TicketStatus.Sending or TicketStatus.Failed;

    /// List sections in order; Closed only shows under "All".
    public static readonly (string Name, string[] Statuses)[] Sections =
    [
        ("Not sent yet", [TicketStatus.Sending, TicketStatus.Failed]),
        ("Needs you", [TicketStatus.Resolved]),
        ("Open", [TicketStatus.Todo, TicketStatus.InProgress, TicketStatus.WaitingVendor]),
        ("Closed", [TicketStatus.Closed, TicketStatus.Cancelled]),
    ];

    // ── Priority ──
    public static readonly string[] Priorities = ["LOW", "MEDIUM", "HIGH", "CRITICAL"];
    public static string PriorityLabel(string? p) => Humanize(p);
    public static string PriorityColor(string? p) => p switch
    {
        "LOW" => "var(--ink-3)",
        "MEDIUM" => "var(--warn)",
        "HIGH" => "#e8743b",
        "CRITICAL" => "var(--danger)",
        _ => "var(--ink-3)",
    };
    public static bool IsHot(string? p) => p is "HIGH" or "CRITICAL";

    // ── Queues and categories ──
    public sealed record ModuleLook(string Icon, string Color, string Blurb);

    public static ModuleLook Module(string slug) => slug switch
    {
        "it-support" => new("life-buoy", "#0a84ff", "PC, network, printer, apps"),
        "oracle-k2" => new("database", "#c2442e", "Access & requests"),
        "web-developer" => new("globe", "#7c4dd6", "Websites & web apps"),
        "mobile-developer" => new("smartphone", "#2e9d6b", "Mobile apps"),
        _ => new("ticket", "#8f8c85", ""),
    };

    /// iDesk's module names carry "Tickets"/"Request" suffixes; the tiles read better without them.
    public static string ModuleName(TicketModuleDto m) => m.Slug switch
    {
        "it-support" => "IT Support",
        "oracle-k2" => "Oracle K2",
        "web-developer" => "Web Developer",
        "mobile-developer" => "Mobile Developer",
        _ => m.Name,
    };

    public const string HardwareInstallation = "HARDWARE_INSTALLATION";

    public static string CategoryLabel(string? code) => code switch
    {
        "WEB_DEVELOPER" => "Web development",
        "MOBILE_DEVELOPER" => "Mobile development",
        _ => Humanize(code),
    };

    public static string CategoryIcon(string? code) => code switch
    {
        "GENERAL" => "circle-help",
        "HARDWARE" => "monitor",
        "SOFTWARE" => "app-window",
        "NETWORK" => "wifi",
        HardwareInstallation => "package-plus",
        "ORACLE_REQUEST" => "database",
        "WEB_DEVELOPER" => "globe",
        "MOBILE_DEVELOPER" => "smartphone",
        _ => "tag",
    };

    /// Same names as iDesk's scheduling settings (PC, IP-Phone, Printer), sent verbatim; iDesk accepts custom values too.
    public static readonly (string Name, string Icon)[] HardwareTypes =
        [("PC", "monitor"), ("IP-Phone", "phone"), ("Printer", "printer"), ("Other", "more-horizontal")];

    /// One-tap starters copied from iDesk's own create page (BentoCreateTicketPage.tsx) so a ticket
    /// filed from iPop reads like one filed in iDesk. Category is only set for IT Support.
    public sealed record QuickTemplate(string Label, string? Category, string Priority, string Subject, string Description);

    public static IReadOnlyList<QuickTemplate> Templates(string? module) => module switch
    {
        "it-support" =>
        [
            new("Email Issue", "SOFTWARE", "HIGH", "Email tidak bisa diakses", "Tidak dapat mengirim atau menerima email. Error message: "),
            new("Printer Fault", "HARDWARE", "MEDIUM", "Printer rusak / tidak merespon", "Printer tidak merespon saat print. Sudah direstart. Model printer: "),
            new("Slow System", "HARDWARE", "LOW", "Komputer lemot", "PC/Laptop sangat lambat saat membuka aplikasi. Area timbul keluhan: "),
            new("No Network", "NETWORK", "HIGH", "Koneksi internet bermasalah", "Tidak ada koneksi WiFi maupun LAN. Area/Lantai: "),
            new("Software Error", "SOFTWARE", "CRITICAL", "Error sistem saat input data", "Proses berhenti karena error. Nama sistem: "),
            new("Login Issue", "GENERAL", "MEDIUM", "Password terkunci", "Gagal login berkali-kali. Mohon dibantu reset. Username: "),
        ],
        "oracle-k2" =>
        [
            new("Login Issue", null, "MEDIUM", "Lupa Password / Gagal Login Oracle", "Gagal login ke portal Oracle. Error message: "),
            new("Role Update", null, "MEDIUM", "Penambahan Role K2", "Mohon tambahkan role [Nama Role] untuk user [Nama User] di K2."),
            new("System Error", null, "HIGH", "Error Transaksi Oracle", "Terdapat error saat proses transaksi modul [Nama Modul]. Error detail: "),
        ],
        "web-developer" =>
        [
            new("UI Bug", null, "HIGH", "Bug Tampilan / Antarmuka Web", "Terdapat kendala tampilan pada halaman [Nama Halaman]. Detail isu: "),
            new("API Error", null, "HIGH", "Error Integrasi API / Backend", "Gagal memuat data dari endpoint API [Nama Endpoint]. Pesan error: "),
            new("Fitur Baru", null, "MEDIUM", "Permintaan Fitur Baru Web", "Kebutuhan fitur baru pada modul [Nama Modul]. Rincian kebutuhan: "),
            new("Slow Web", null, "MEDIUM", "Loading Website Lambat / Timeout", "Akses ke web portal mengalami lag/timeout pada bagian [Bagian Web]."),
        ],
        "mobile-developer" =>
        [
            new("App Crash", null, "CRITICAL", "Aplikasi Mobile Force Close / Crash", "Aplikasi mobile mengalami crash saat membuka [Menu/Fitur]. Perangkat & OS: "),
            new("Release Issue", null, "HIGH", "Kendala Rilis / Update APK", "Gagal melakukan instalasi atau update APK versi [Nomor Versi]."),
            new("Sync Error", null, "HIGH", "Gagal Sinkronisasi Data Mobile", "Data pada aplikasi mobile tidak tersinkron ke server pada modul [Nama Modul]."),
            new("Push Notif", null, "MEDIUM", "Push Notification Tidak Masuk", "Notifikasi tidak muncul pada device pengguna saat trigger [Nama Event]."),
        ],
        _ => [],
    };

    /// Suggestions for the optional Device / Software fields: iDesk's DEVICE and SOFTWARE ticket
    /// attributes. Free text is still allowed (iDesk takes any value up to 100 chars).
    public static readonly string[] DeviceOptions = ["Laptop", "Desktop", "Mobile"];
    public static readonly string[] SoftwareOptions = ["Windows", "MacOS", "Office 365"];

    /// HARDWARE_INSTALLATION → "Hardware installation".
    public static string Humanize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var s = code.Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant();
        return char.ToUpperInvariant(s[0]) + s[1..];
    }

    // ── Attachments ──
    public static string FileSize(long bytes)
        => bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : Math.Max(1, (int)Math.Round(bytes / 1024.0)) + " KB";

    public static string FileIcon(string name, bool ok = true)
    {
        if (!ok) return "file-x";
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "webp" => "image",
            "xls" or "xlsx" or "csv" => "sheet",
            _ => "file-text",
        };
    }

    /// Why a file can't be attached, or null when it can.
    public static string? RejectReason(string name, long size)
    {
        if (!TicketAttachmentRules.Extensions.Contains(Path.GetExtension(name))) return "iDesk doesn't accept this file type";
        if (size > TicketAttachmentRules.MaxBytes) return "Larger than 10 MB";
        if (size == 0) return "This file is empty";
        return null;
    }

    // ── Time ──
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    public static DateTime Local(DateTime utc) => (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime();

    /// List column: "09:42" today, "Yesterday", "2 Oct", "2 Oct 2025".
    public static string ListTime(DateTime utc, DateTime? now = null)
    {
        var t = Local(utc); var n = now ?? DateTime.Now;
        if (t.Date == n.Date) return t.ToString("HH:mm", En);
        if (t.Date == n.Date.AddDays(-1)) return "Yesterday";
        return t.Year == n.Year ? t.ToString("d MMM", En) : t.ToString("d MMM yyyy", En);
    }

    /// Step and activity stamps: "Today, 09:42", "Yesterday, 10:15", "3 Oct, 08:12".
    public static string Stamp(DateTime utc, DateTime? now = null)
    {
        var t = Local(utc); var n = now ?? DateTime.Now;
        var hm = t.ToString("HH:mm", En);
        if (t.Date == n.Date) return $"Today, {hm}";
        if (t.Date == n.Date.AddDays(-1)) return $"Yesterday, {hm}";
        return t.Year == n.Year ? $"{t.ToString("d MMM", En)}, {hm}" : $"{t.ToString("d MMM yyyy", En)}, {hm}";
    }

    /// "Tue 7 Oct, 17:00" (or "Today, 14:00").
    public static string Due(DateTime utc, DateTime? now = null)
    {
        var t = Local(utc); var n = now ?? DateTime.Now;
        var hm = t.ToString("HH:mm", En);
        return t.Date == n.Date ? $"Today, {hm}" : $"{t.ToString("ddd d MMM", En)}, {hm}";
    }

    public static string ScheduleLabel(string? date, string? time)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return "";
        var s = d.ToString("ddd d MMM", En);
        return string.IsNullOrEmpty(time) ? s : $"{s}, {time}";
    }

    // ── SLA ──
    /// (css, label) for the Target row: Met / Missed / On track / "x h left" / Overdue.
    public static (string Css, string Label)? Sla(TicketDetailDto t, DateTime? nowUtc = null)
    {
        if (t.SlaTarget is not { } target) return null;
        var now = nowUtc ?? DateTime.UtcNow;
        if (t.Status is TicketStatus.Cancelled) return null;
        if (t.Status is TicketStatus.Resolved or TicketStatus.Closed)
        {
            var done = t.ResolvedAt ?? t.ClosedAt ?? t.UpdatedAt;
            return done <= target ? ("ok", "Met") : ("bad", "Missed");
        }
        var left = target - now;
        if (left <= TimeSpan.Zero) return ("bad", "Overdue");
        if (left < TimeSpan.FromHours(24))
            return ("warn", left < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)left.TotalMinutes)} min left" : $"{(int)Math.Floor(left.TotalHours)} h left");
        return ("ok", "On track");
    }

    // ── Progress steps ──
    /// Index of the current step (0 Submitted, 1 In progress / vendor, 2 Resolved, 4 = all done), or null when hidden.
    public static int? CurrentStep(string status) => status switch
    {
        TicketStatus.Todo => 0,
        TicketStatus.InProgress or TicketStatus.WaitingVendor => 1,
        TicketStatus.Resolved => 2,
        TicketStatus.Closed => 4,
        _ => null,
    };

    public static string FillWidth(int cur) => cur >= 4 ? "100%" : $"calc({cur * 25}% + 7px)";

    public static string[] StepLabels(string status)
        => ["Submitted", status == TicketStatus.WaitingVendor ? "Waiting on vendor" : "In progress", "Resolved", "Closed"];

    public static string[] StepStamps(TicketDetailDto t, DateTime? now = null)
    {
        var s = new string[4];
        s[0] = Stamp(t.CreatedAt, now);
        var work = t.Events.Where(e => e.Kind == "assigned" || e.ToStatus is TicketStatus.InProgress or TicketStatus.WaitingVendor)
            .Select(e => (DateTime?)e.OccurredAt).FirstOrDefault();
        if (work is { } w && CurrentStep(t.Status) >= 1) s[1] = Stamp(w, now);
        var resolved = t.ResolvedAt ?? t.Events.Where(e => e.ToStatus == TicketStatus.Resolved).Select(e => (DateTime?)e.OccurredAt).LastOrDefault();
        if (resolved is { } r && CurrentStep(t.Status) >= 2) s[2] = Stamp(r, now);
        if (t.ClosedAt is { } c && t.Status == TicketStatus.Closed) s[3] = Stamp(c, now);
        else if (t.Status == TicketStatus.Resolved) s[3] = "after you confirm";
        return s;
    }

    // ── Activity ──
    public sealed record ActivityRow(string Key, string Css, string Icon, MarkupString Text, string Sub);

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// Newest first, worded like the mockup.
    public static List<ActivityRow> Activity(TicketDetailDto t, DateTime? now = null)
    {
        var rows = new List<ActivityRow>();
        foreach (var e in t.Events.OrderByDescending(e => e.OccurredAt))
        {
            var when = Stamp(e.OccurredAt, now);
            var key = $"{e.Kind}|{e.ToStatus}|{e.OccurredAt.Ticks}";
            (string css, string icon, string html, string sub) = e.Kind switch
            {
                "created" when t.Origin == "IDESK" => ("ok", "send", "Filed in iDesk", when),
                "created" => ("ok", "send", "Submitted from iPop", WithRef(when, t.ExternalRef)),
                "sent" => ("ok", "send", "Sent to iDesk", WithRef(when, t.ExternalRef)),
                "send_failed" => ("hot", "alert-triangle", "iDesk unreachable — stopped retrying after 24 h", when),
                "assigned" when !string.IsNullOrWhiteSpace(e.Assignee) => ("inf", "user", $"Assigned to <b>{E(e.Assignee)}</b>", when),
                "assigned" => ("inf", "user", "Assigned", when),
                "resolved" => ("hot", "check", string.IsNullOrWhiteSpace(e.Assignee) ? "Marked <b>resolved</b>" : $"Marked <b>resolved</b> by {E(e.Assignee)}", when),
                "closed" => ("ok", "check-check", "<b>Closed</b>", when),
                _ => StatusChange(e.ToStatus, when),
            };
            rows.Add(new(key, css, icon, new MarkupString(html), sub));
        }
        if (rows.Count == 0 && t.Status == TicketStatus.Sending)
            rows.Add(new("waiting", "inf", "loader", new MarkupString("Waiting for iDesk to answer"), Stamp(t.CreatedAt, now)));
        return rows;
    }

    private static string WithRef(string when, string? r) => string.IsNullOrEmpty(r) ? when : $"{when} · {r}";

    private static (string, string, string, string) StatusChange(string? to, string when) => to switch
    {
        TicketStatus.InProgress => ("inf", "play", "Status changed to <b>In progress</b>", when),
        TicketStatus.WaitingVendor => ("inf", "truck", "Waiting on <b>vendor</b>", when),
        TicketStatus.Todo => ("inf", "rotate-ccw", "Status changed to <b>Open</b>", when),
        TicketStatus.Resolved => ("hot", "check", "Marked <b>resolved</b>", when),
        TicketStatus.Closed => ("ok", "check-check", "<b>Closed</b>", when),
        TicketStatus.Cancelled => ("inf", "x", "<b>Cancelled</b> in iDesk", when),
        _ => ("inf", "circle-dot", $"Status changed to <b>{E(Status(to).Short)}</b>", when),
    };

    // ── Search ──
    public static bool Matches(TicketListItemDto t, string q)
        => q.Length == 0 || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
           || (t.TicketNumber ?? t.ExternalRef ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);

    /// Wraps the first match of q in &lt;mark&gt;; everything else is encoded.
    public static MarkupString Highlight(string? text, string q)
    {
        text ??= "";
        var i = q.Length == 0 ? -1 : text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return new(E(text));
        return new($"{E(text[..i])}<mark>{E(text.Substring(i, q.Length))}</mark>{E(text[(i + q.Length)..])}");
    }

    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }

    public static string Number(TicketListItemDto t) => t.TicketNumber ?? t.ExternalRef ?? "";
    public static string Number(TicketDetailDto t) => t.TicketNumber ?? t.ExternalRef ?? "";
}
