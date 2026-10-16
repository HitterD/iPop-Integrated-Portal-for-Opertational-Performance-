namespace IPop.Web.Components.Phone.Notifications;

/// One row of the phone notification list (NF1). Kind = module key for the icon colour
/// (mention/chat/mail/task/meeting/news/system); Group = "Today" / "Earlier …".
public sealed record PhoneNotifRow(Guid Id, string Kind, string Icon, string Title, string Body, string Time, string Group, bool IsRead, string? Snoozed);

/// One category switch in the phone preferences (NF2). On = notifications allowed.
public sealed record PhoneNotifCategory(string Id, string Label, string Icon, bool On);

/// One snooze choice in the phone sheet.
public sealed record PhoneSnoozeOption(string Label, string Icon, Func<Task>? Run);
