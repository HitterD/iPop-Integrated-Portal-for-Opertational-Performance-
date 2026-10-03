namespace IPop.Core.Entities;

/// Who sees what of an event on someone else's calendar. Invited people always see the full meeting.
public static class CalendarVisibility
{
    /// Title and location visible to every employee.
    public const string Public = "public";
    /// Others see a "Busy" block; delegates see the details.
    public const string Busy = "busy";
    /// A "Busy" block even for delegates.
    public const string Private = "private";
    /// <see cref="CalendarSettings.DefaultVisibility"/> only: every event shows as Busy to others.
    public const string Hidden = "hidden";

    public static bool IsEventLevel(string? v) => v is Public or Busy or Private;
    public static bool IsDefault(string? v) => v is Public or Busy or Hidden;
}

/// Per-user calendar sharing settings. No row = defaults (Busy only, notify on).
public class CalendarSettings
{
    public Guid UserId { get; set; }
    /// public | busy | hidden — preselected visibility for new events; hidden = details never shown to others.
    public string DefaultVisibility { get; set; } = CalendarVisibility.Busy;
    /// Owner gets a notification when a delegate changes their calendar.
    public bool NotifyDelegateChanges { get; set; } = true;
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = default!;
}

/// Someone else's calendar in my sidebar ("Other calendars"), saved per account.
public class CalendarSubscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TargetUserId { get; set; }
    /// Palette key (a..h) so the colour survives theme changes.
    public string Color { get; set; } = "a";
    public bool IsVisible { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
    public User TargetUser { get; set; } = default!;
}
