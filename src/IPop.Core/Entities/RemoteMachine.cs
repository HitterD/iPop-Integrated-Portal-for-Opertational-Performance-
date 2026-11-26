namespace IPop.Core.Entities;

/// <summary>
/// One physical PC for remote help, whoever is signed in to Windows on it. Every iPop Notifier on the PC (one
/// <see cref="NotifierDevice"/> per person) points here, so the PC has a single 9-digit remote ID like AnyDesk.
/// Found by <see cref="MachineKey"/>: Windows' MachineGuid (HKLM\SOFTWARE\Microsoft\Cryptography) plus the computer
/// name, so cloned images with the same GUID still get their own ID; apps without the GUID report the name only.
/// </summary>
public class RemoteMachine
{
    /// Locks after this many wrong unattended passwords in a row…
    public const int MaxFailedPasswords = 5;
    /// …for this long.
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 10;

    public Guid Id { get; set; }
    /// "{machineguid}|{COMPUTERNAME}" (lower-case GUID, upper-case name), or "|{COMPUTERNAME}" without a GUID.
    public required string MachineKey { get; set; }
    public required string ComputerName { get; set; }
    /// 9 random digits, unique, never sequential; shown as "482 913 576".
    public required string ConnectId { get; set; }
    /// Admin switch "Connect without asking the user". Active only together with a password.
    public bool Unattended { get; set; }
    /// Argon2id hash of the unattended password (never the password; never sent to any client).
    public string? PasswordHash { get; set; }
    public DateTime? PasswordUpdatedAt { get; set; }
    public Guid? SetById { get; set; }
    /// Wrong passwords since the last right one; reaching <see cref="MaxFailedPasswords"/> locks the PC.
    public int FailedCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }

    /// Unattended access is on only with the Admin switch and a password.
    public bool UnattendedActive => Unattended && PasswordHash is not null;
}
