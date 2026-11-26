using IPop.Application.Auth;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// ICT → "Connect" (BIGIS #8): find the PC, then either ask the user (Allow prompt) or — when an Administrator set
/// unattended access with a password — check the password here on the server. Every unattended attempt is a row in
/// the session log: started sessions as usual, a wrong password as "denied", a locked PC as "locked".
public static class RemoteStart
{
    public sealed record Request(Guid? DeviceId, string? Target, string? Mode, string? Password, bool AskUser, string? FromMachine);

    /// Status 200 with <see cref="Session"/> set, or an error status with <see cref="Body"/> for the viewer.
    public sealed record Result(int Status, object Body, RemoteSession? Session = null, NotifierDevice? Device = null, bool Joining = false);

    public static async Task<Result> StartAsync(IPopDbContext db, IPasswordHasher hasher, Guid helperId, Request req, DateTime now, CancellationToken ct)
    {
        var (device, problem) = await RemoteConnect.ResolveAsync(db, req.DeviceId, req.Target, now, ct);
        if (problem is not null) return new Result(problem.Status, problem.Body);
        if (device!.UserId == helperId) return new Result(400, new { code = "own", message = "That's your own computer." });
        var machine = device.Machine;
        var remoteId = machine?.ConnectId;

        await IPop.Api.Endpoints.RemoteEndpoints.CloseStaleAsync(db, device.Id, now, ct);
        // Someone is already helping this PC: the newcomer joins that room to watch (view only, still asks the user).
        var running = await db.RemoteSessions.AsNoTracking()
            .Where(x => x.DeviceId == device.Id && x.Status == "active" && (x.AliveAt ?? x.StartedAt) > now - IPop.Api.Endpoints.RemoteEndpoints.AliveWindow)
            .OrderByDescending(x => x.StartedAt).Select(x => x.Room).FirstOrDefaultAsync(ct);
        var mode = running is not null || req.Mode == "view" ? "view" : "control";
        var unattended = machine is { UnattendedActive: true } && running is null && !req.AskUser;

        if (unattended)
        {
            if (string.IsNullOrEmpty(req.Password) && !(machine!.LockedUntil > now))
                return new Result(428, new { code = "password", message = "Enter the access password.", machine = device.MachineName, user = device.User.Name, remoteId,
                    triesLeft = RemoteAccess.TriesLeft(machine) });
            var check = RemoteAccess.Verify(machine!, req.Password, hasher, now);
            if (check != RemoteAccess.Check.Ok)
            {
                var locked = check == RemoteAccess.Check.Locked;
                db.RemoteSessions.Add(Row(helperId, device, mode, unattended: true, now, locked ? "locked" : "denied"));
                await db.SaveChangesAsync(ct);
                return new Result(locked ? 423 : 403, new
                {
                    code = locked ? "locked" : "wrong", machine = device.MachineName, user = device.User.Name, remoteId,
                    triesLeft = RemoteAccess.TriesLeft(machine!), lockedUntil = machine!.LockedUntil,
                    message = locked ? $"Access to {device.MachineName} is locked" : $"{device.MachineName} did not accept that password.",
                });
            }
        }

        var id = Guid.NewGuid();
        var session = Row(helperId, device, mode, unattended, now, unattended ? "active" : "asking", id, running ?? IPop.Api.Endpoints.RemoteEndpoints.RoomName(id));
        if (unattended) { session.StartedAt = now; session.AliveAt = now; session.Permissions = RemoteAccess.AllPermissions; }
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new Result(200, new { sessionId = session.Id, status = session.Status, machine = device.MachineName, user = device.User.Name, remoteId,
            unattended, permissions = session.Permissions }, session, device, running is not null);
    }

    private static RemoteSession Row(Guid helperId, NotifierDevice device, string mode, bool unattended, DateTime now, string status, Guid? id = null, string? room = null) => new()
    {
        Id = id ?? Guid.NewGuid(), HelperId = helperId, TargetUserId = device.UserId, DeviceId = device.Id,
        Room = room ?? "-", Mode = mode, Unattended = unattended, RequestedAt = now, Status = status,
        EndedAt = status is "denied" or "locked" ? now : null, EndedBy = status is "denied" or "locked" ? "password" : null,
    };
}
