using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;

namespace IPop.Api.Notifier;

/// "Who changed remote access to this PC" (BIGIS #8 v3): every Administrator change is a row, shown in Session
/// history and the PC's Session log. Saved with the caller's own SaveChanges.
public static class RemoteAudit
{
    public static void Add(IPopDbContext db, Guid machineId, Guid? actorId, string what, DateTime now) =>
        db.Set<RemoteAuditEntry>().Add(new RemoteAuditEntry { Id = Guid.NewGuid(), MachineId = machineId, ActorId = actorId, At = now, What = what.Length > 200 ? what[..200] : what });
}
