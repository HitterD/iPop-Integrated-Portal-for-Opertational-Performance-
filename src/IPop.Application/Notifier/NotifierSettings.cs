using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPop.Application.Notifier;

/// What the iPop Notifier desktop app does with each notification (mockups docs/mockups/notifier, approved
/// 2026-10-18). Saved per user on the server (ui-prefs key "notifier") so it follows the person to another PC.
public sealed record NotifierSettings
{
    /// "windows" = one window per notification grouped on the taskbar (mockup v2, default);
    /// "stack" = glass popups stacked bottom-right (mockup v1).
    public string Style { get; init; } = "windows";
    /// "on" | "off" | "until" (off until <see cref="OffUntil"/>, the app clears it when it passes).
    public string Popups { get; init; } = "on";
    public DateTime? OffUntil { get; init; }
    /// Full-screen apps, Windows Focus or iPop status Busy: "hold" (show after) or "show".
    public string DuringMeetings { get; init; } = "hold";
    public bool UrgentBreaksThrough { get; init; } = true;
    /// "bottom-right" | "top-right".
    public string Position { get; init; } = "bottom-right";
    /// No longer used (1.4.2: no limit; every notification gets its own visible window, the stack shows as many
    /// cards as fit the screen). Kept so settings saved by older apps still parse; always normalised to 0 = unlimited.
    public int MaxVisible { get; init; }
    /// Module → "popup" | "toast" | "off". Chat is always "toast" or "off".
    public Dictionary<string, string> Modules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Sound { get; init; } = true;
    public int Volume { get; init; } = 80;
    public bool StartWithWindows { get; init; } = true;
    /// "normal" | "large": type and button size in every Notifier window (easier to read for some people).
    public string TextSize { get; init; } = "normal";
    /// "system" (follows Windows light/dark, default) | "light" | "dark": every Notifier window (Settings → General → Appearance, 1.4.0).
    public string Theme { get; init; } = "system";
    /// 1.5.0 priority inbox: kind (<see cref="KindKeys"/>) → "act" | "msg" | "info". Empty in settings saved by an
    /// older app; <see cref="Normalized"/> then derives it from <see cref="Modules"/> (module "off" → its kinds info).
    public Dictionary<string, string> Tiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// People whose notifications always need action (user ids, at most <see cref="MaxVips"/>).
    public List<Guid> Vips { get; init; } = [];
    /// The Messages tier shows no toast at all (Inbox + count only).
    public bool HideMessageToasts { get; init; }

    /// "ticketchat" = messages in an iDesk ticket's chat (toast with a reply box by default); "tickets" = the other
    /// iDesk ticket updates (assigned, status, resolved…).
    public static readonly string[] ModuleKeys = ["mail", "tickets", "ticketchat", "calendar", "chat", "projects", "news", "notes", "calls", "system"];
    private static readonly string[] Styles = ["windows", "stack"];
    private static readonly string[] PopupModes = ["on", "off", "until"];
    private static readonly string[] MeetingModes = ["hold", "show"];
    private static readonly string[] Positions = ["bottom-right", "top-right"];
    private static readonly string[] Delivery = ["popup", "toast", "off"];
    private static readonly string[] TextSizes = ["normal", "large"];
    private static readonly string[] Themes = ["system", "light", "dark"];

    public const int MaxVips = 50;
    public const string Act = "act", Msg = "msg", Info = "info";
    private static readonly string[] TierValues = [Act, Msg, Info];

    /// Settings › Categories rows, in display order (docs/notifier/PRIORITY-150.md "Kinds").
    public static readonly string[] KindKeys =
    [
        "approval", "chat", "mail", "mailmass", "ticketassigned", "ticketchat", "tickets", "meetingsoon", "calendar",
        "projects", "projectwatch", "notes", "calls", "news", "system",
    ];

    /// The old per-module setting each kind migrates from ("approval" had no module: always its default).
    private static string? LegacyModuleOf(string kind) => kind switch
    {
        "chat" => "chat",
        "mail" or "mailmass" => "mail",
        "ticketassigned" or "tickets" => "tickets",
        "ticketchat" => "ticketchat",
        "meetingsoon" or "calendar" => "calendar",
        "projects" or "projectwatch" => "projects",
        "notes" => "notes",
        "calls" => "calls",
        "news" => "news",
        "system" => "system",
        _ => null,
    };

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Effective delivery for a module (defaults: chat and iDesk chat → toast, everything else → popup).
    public string DeliveryFor(string module)
    {
        var fallback = module is "chat" or "ticketchat" ? "toast" : "popup";
        if (!Modules.TryGetValue(module, out var v) || !Delivery.Contains(v)) return fallback;
        return module == "chat" && v == "popup" ? "toast" : v;
    }

    /// Accepts only known values; anything unknown falls back to the default for that field.
    public NotifierSettings Normalized() => this with
    {
        Style = Styles.Contains(Style) ? Style : "windows",
        Popups = PopupModes.Contains(Popups) ? Popups : "on",
        OffUntil = Popups == "until" ? OffUntil : null,
        DuringMeetings = MeetingModes.Contains(DuringMeetings) ? DuringMeetings : "hold",
        Position = Positions.Contains(Position) ? Position : "bottom-right",
        MaxVisible = 0,
        Volume = Math.Clamp(Volume, 0, 100),
        TextSize = TextSizes.Contains(TextSize) ? TextSize : "normal",
        Theme = Themes.Contains(Theme) ? Theme : "system",
        Modules = ModuleKeys.ToDictionary(k => k, DeliveryFor, StringComparer.OrdinalIgnoreCase),
        Tiers = KindKeys.ToDictionary(k => k, NormalizedTier, StringComparer.OrdinalIgnoreCase),
        Vips = (Vips ?? []).Where(v => v != Guid.Empty).Distinct().Take(MaxVips).ToList(),
    };

    private string NormalizedTier(string kind)
    {
        if (Tiers is { Count: > 0 })
            return Tiers.TryGetValue(kind, out var v) && v is not null && TierValues.Contains(v.ToLowerInvariant()) ? v.ToLowerInvariant() : DefaultTier(kind);
        // Saved by an app before 1.5.0 (no tiers): a module switched off keeps quiet, everything else gets the default.
        return LegacyModuleOf(kind) is { } module && DeliveryFor(module) == "off" ? Info : DefaultTier(kind);
    }

    public static NotifierSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new NotifierSettings().Normalized();
        try { return (JsonSerializer.Deserialize<NotifierSettings>(json, Json) ?? new()).Normalized(); }
        catch (JsonException) { return new NotifierSettings().Normalized(); }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalized(), Json);

    /// Delivery setting that applies to a notification: an iDesk chat message follows "ticketchat", the rest their module.
    public string DeliveryFor(string module, string? eventType) =>
        DeliveryFor(module == "tickets" && eventType == "ticket.message_created" ? "ticketchat" : module);

    /// Maps a notification event type ("mail.new", "ticket.message_created", "meeting.reminder"…) to a module key.
    public static string ModuleOf(string? eventType)
    {
        var head = (eventType ?? "").Split('.', 2)[0].ToLowerInvariant();
        return head switch
        {
            "mail" => "mail",
            "ticket" or "idesk" or "tickets" => "tickets",
            "meeting" or "calendar" => "calendar",
            "chat" => "chat",
            "project" or "task" => "projects",
            "news" => "news",
            "note" or "notes" => "notes",
            "call" or "calls" => "calls",
            _ => "system",
        };
    }

    /// Which Settings › Categories row a notification belongs to. <paramref name="targetUrl"/> is optional: it tells an
    /// iDesk technician's status notification ("/idesk/tickets/{id}#update": assigned to me, reopened, confirmed,
    /// visit moved — same event names as the requester's updates) from the requester's own ticket updates.
    public static string KindOf(string? eventType, string? priority, string? targetUrl = null)
    {
        var e = (eventType ?? "").Trim().ToLowerInvariant();
        var head = e.Split('.', 2)[0];
        var low = string.Equals(priority, "low", StringComparison.OrdinalIgnoreCase);
        switch (head)
        {
            case "approval": return "approval";
            case "chat": return "chat";
            case "mail": return e == "mail.new" && low ? "mailmass" : "mail";
            case "ticket" or "tickets" or "idesk":
                if (e == "ticket.message_created") return "ticketchat";
                if (e is "ticket.assigned" or "ticket.reopened") return "ticketassigned";
                if (targetUrl is not null && targetUrl.StartsWith("/idesk/tickets/", StringComparison.OrdinalIgnoreCase)
                    && targetUrl.EndsWith("#update", StringComparison.OrdinalIgnoreCase)) return "ticketassigned";
                return "tickets";
            case "meeting" or "calendar":
                return e is "meeting.reminder" or "calendar.reminder" ? "meetingsoon" : "calendar";
            case "project" or "task":
                return e is "project.task_assigned" or "project.task_due_reminder" or "project.task_due_tomorrow"
                       || e.Contains("comment") || e.Contains("mention") ? "projects" : "projectwatch";
            case "note" or "notes": return "notes";
            case "call" or "calls": return "calls";
            case "news" or "memo": return "news";
            default: return "system";
        }
    }

    /// Default tier of a kind when the person has not changed it.
    public static string DefaultTier(string kind) => kind switch
    {
        "approval" or "ticketassigned" or "meetingsoon" => Act,
        "chat" or "mail" or "ticketchat" or "tickets" or "calendar" or "projects" or "notes" or "calls" => Msg,
        _ => Info,   // mailmass, projectwatch, news, system and anything unknown
    };

    /// Needs action whatever the settings say: a leader or VIP, an emergency, a mandatory memo, priority high.
    public static bool AlwaysAct(string? eventType, string? priority, bool fromLeaderOrVip) =>
        fromLeaderOrVip
        || string.Equals(eventType, "chat.emergency", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventType, "memo.mandatory", StringComparison.OrdinalIgnoreCase)
        || string.Equals(priority, "high", StringComparison.OrdinalIgnoreCase);

    /// "act" | "msg" | "info" for a notification of <paramref name="kind"/> with these settings.
    public string TierFor(string kind, string? eventType, string? priority, bool fromLeaderOrVip)
    {
        if (AlwaysAct(eventType, priority, fromLeaderOrVip)) return Act;
        return Tiers.TryGetValue(kind, out var t) && TierValues.Contains(t) ? t : DefaultTier(kind);
    }
}
