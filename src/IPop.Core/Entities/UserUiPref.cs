namespace IPop.Core.Entities;

/// Small per-user UI preference that follows the account across devices (phone tab order,
/// "notification card dismissed", dashboard order). Json is the raw value, at most 2 KB.
public class UserUiPref
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// Whitelisted key: "tabs", "notif-card", "dash-order", "notifier".
    public required string Key { get; set; }
    public required string Json { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = default!;
}
