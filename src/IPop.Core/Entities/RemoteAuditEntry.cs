namespace IPop.Core.Entities;

/// An Administrator changed remote-help access to a PC (password, unattended switch, permissions, exclusion,
/// alias, group, unlock). Shown in Session history and in the PC's Session log; kept as long as sessions.
public class RemoteAuditEntry
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public Guid? ActorId { get; set; }
    public DateTime At { get; set; }
    /// What happened, for people: "Unattended password changed".
    public required string What { get; set; }
}
