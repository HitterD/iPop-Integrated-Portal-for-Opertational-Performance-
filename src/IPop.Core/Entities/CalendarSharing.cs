namespace IPop.Core.Entities;

/// Per event: Normal (stored "busy") follows each viewer's access level; Private is a Busy block for
/// everyone except the owner's "Act on my behalf" assistant. Invited people always see the full meeting.
/// Everyone at the company sees when anyone is busy; titles and details only by the owner's grant.
public static class CalendarVisibility
{
    /// Legacy "visible to everyone" value; treated as Normal since the access rework.
    public const string Public = "public";
    /// Normal: what a viewer sees depends on the access the owner gave them.
    public const string Busy = "busy";
    /// A "Busy" block even for people the owner shares with, except an Act-on-my-behalf assistant.
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
    /// Owner gets a notification when someone asks to see more of their calendar.
    public bool NotifyShareRequests { get; set; } = true;
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

/// The access ladder the owner gives someone. Everyone (no grant) sees when the owner is busy.
public static class DelegateLevel
{
    /// "See event titles": title and location of Normal events.
    public const string Titles = "titles";
    /// "See all details": also description, attendees and attachments (Private stays a block).
    public const string View = "view";
    /// "Edit my events": details + create, edit and cancel events and meetings on the owner's behalf.
    public const string Edit = "edit";
    /// "Act on my behalf": edit + the owner's meeting invites and answering them; sees Private events too.
    public const string Full = "full";

    public static bool IsValid(string? v) => v is Titles or View or Edit or Full;
    /// Departments and chat groups can only be given read levels.
    public static bool IsGroupLevel(string? v) => v is Titles or View;
    public static bool CanEdit(string? v) => v is Edit or Full;
    public static bool SeesDetails(string? v) => Rank(v) >= Rank(View);
    public static int Rank(string? v) => v switch { Full => 4, Edit => 3, View => 2, Titles => 1, _ => 0 };
    public static string? Max(string? a, string? b) => Rank(a) >= Rank(b) ? a : b;
}

/// The owner gives <see cref="DelegateId"/> one level of access (see <see cref="DelegateLevel"/>); the top two
/// make them an assistant who can change the calendar. Granted by the owner in Calendar settings or by an
/// admin; both people are notified.
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

/// Access for everyone in a department or a chat group (membership is checked live, so people who join get
/// it and people who leave lose it). Only read levels: <see cref="DelegateLevel.Titles"/> or <see cref="DelegateLevel.View"/>.
public class CalendarGroupShare
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? RoomId { get; set; }
    public string Level { get; set; } = DelegateLevel.Titles;
    public Guid? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }

    public User Owner { get; set; } = default!;
    public Department? Department { get; set; }
    public ChatRoom? Room { get; set; }
}
