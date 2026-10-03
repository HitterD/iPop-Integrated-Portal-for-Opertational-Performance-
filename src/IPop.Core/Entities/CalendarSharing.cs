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

/// How much a delegate may do on the owner's calendar.
public static class DelegateLevel
{
    /// See every detail of Public and Busy only events (Private stays a block).
    public const string View = "view";
    /// View + create, edit and cancel events and meetings on the owner's behalf.
    public const string Edit = "edit";
    /// Edit + receive the owner's meeting invites and answer them on the owner's behalf.
    public const string Full = "full";

    public static bool IsValid(string? v) => v is View or Edit or Full;
    public static bool CanEdit(string? v) => v is Edit or Full;
    public static int Rank(string? v) => v switch { Full => 3, Edit => 2, View => 1, _ => 0 };
}

/// The owner lets <see cref="DelegateId"/> manage their calendar (manager → assistant).
/// Granted by the owner in Calendar settings or by an admin; both people are notified.
public class CalendarDelegate
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid DelegateId { get; set; }
    /// <see cref="DelegateLevel"/>: view | edit | full
    public string Level { get; set; } = DelegateLevel.View;
    public Guid? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = default!;
    public User Delegate { get; set; } = default!;
}
