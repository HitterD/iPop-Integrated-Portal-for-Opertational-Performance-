namespace IPop.Core.Entities;

/// <summary>
/// A PC running the iPop Notifier desktop app for one user. Reported by the app at sign-in and on every
/// heartbeat; the ICT/Admin Devices list reads it (computer name, IP, Windows user) to find a PC for help.
/// One row per (user, device key) — the app generates the key once per Windows profile.
/// </summary>
public class NotifierDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// Random id the app keeps in its local settings (not a secret; identifies "this PC for this user").
    public required string DeviceKey { get; set; }
    public required string MachineName { get; set; }
    public string? WindowsUser { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }
    /// IPs the PC reports for itself (comma separated) and the address the server saw.
    public string? LocalIps { get; set; }
    public string? RemoteIp { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public User User { get; set; } = default!;
}
