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
    };

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
}
