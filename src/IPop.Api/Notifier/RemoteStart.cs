using IPop.Application.Auth;
using IPop.Application.Notifier;
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
    public sealed record Result(int Status, object Body, RemoteSession? Session = null, NotifierDevice? Device = null, bool Joining = false)
    {
        /// This attempt is the one that locked the PC (Administrators get an Inbox message once).
        public bool JustLocked { get; init; }
    }

    public static async Task<Result> StartAsync(IPopDbContext db, IPasswordHasher hasher, Guid helperId, Request req, DateTime now, CancellationToken ct,
        RemotePolicy? policy = null, bool joinViewOnly = false)
    {
        var p = policy ?? new RemotePolicy();
        var (device, problem) = await RemoteConnect.ResolveAsync(db, req.DeviceId, req.Target, now, ct);
        if (problem is not null) return new Result(problem.Status, problem.Body);
        if (device!.UserId == helperId) return new Result(400, new { code = "own", message = "That's your own computer." });
        var machine = device.Machine;
        var remoteId = machine?.ConnectId;
        var name = machine?.Alias ?? device.MachineName;
        // Company policy first (server-enforced): Remote on, this person allowed, PC not excluded, hourly limit.
        if (await RemotePolicyStore.PreflightAsync(db, p, helperId, machine, device.MachineName, now, ct) is { } no)
            return new Result(no.Code == "rate" ? 429 : 403, new { code = no.Code, title = no.Title, message = no.Sub, machine = name, user = device.User.Name, remoteId });

        await IPop.Api.Endpoints.RemoteEndpoints.CloseStaleAsync(db, device.Id, now, ct);
        // Someone is already helping this PC: never cut them off. The newcomer is told who, and may join view-only
        // (the user is still asked) only when they chose to (joinViewOnly / mode "view").
        var running = await db.RemoteSessions.AsNoTracking().Include(x => x.Helper)
            .Where(x => x.DeviceId == device.Id && x.Status == "active" && (x.AliveAt ?? x.StartedAt) > now - IPop.Api.Endpoints.RemoteEndpoints.AliveWindow)
            .OrderByDescending(x => x.StartedAt).Select(x => new { x.Room, x.HelperId, Name = x.Helper.Name }).FirstOrDefaultAsync(ct);
        if (running is not null && running.HelperId == helperId)
            return new Result(409, new { code = "own-session", message = $"You are already connected to {name}.", machine = name, remoteId });
        if (running is not null && !joinViewOnly)
            return new Result(409, new { code = "busy", busyBy = running.Name, machine = name, user = device.User.Name, remoteId,
                message = $"{name} is being helped by {running.Name}" });
        var mode = running is not null || req.Mode == "view" ? "view" : "control";
        var unattended = p.AllowUnattended && machine is { UnattendedActive: true } && running is null && !req.AskUser;

        if (unattended)
        {
            if (string.IsNullOrEmpty(req.Password) && !(machine!.LockedUntil > now))
                return new Result(428, new { code = "password", message = "Enter the access password.", machine = name, user = device.User.Name, remoteId,
                    triesLeft = RemoteAccess.TriesLeft(machine, p.LockAttempts), lockMinutes = p.LockMinutes });
            var check = await RemoteAccess.VerifyAsync(db, machine!.Id, req.Password, hasher, now, ct, p.LockAttempts, TimeSpan.FromMinutes(p.LockMinutes));
            if (check.Check != RemoteAccess.Check.Ok)
            {
                var locked = check.Check == RemoteAccess.Check.Locked;
                var justLocked = check.JustLocked;
                db.RemoteSessions.Add(Row(helperId, device, mode, unattended: true, now, locked ? "locked" : "denied"));
                await db.SaveChangesAsync(ct);
                return new Result(locked ? 423 : 403, new
                {
                    code = locked ? "locked" : "wrong", machine = name, user = device.User.Name, remoteId,
                    triesLeft = check.TriesLeft, lockedUntil = check.LockedUntil, lockMinutes = p.LockMinutes,
                    message = locked ? $"Access to {name} is locked" : $"{name} did not accept that password.",
                }, Device: device) { JustLocked = justLocked };
            }
        }

        var max = RemotePolicyStore.PcMax(machine);
        // Unattended skips the prompt unless the PC says "Always ask"; prompt-less connects use the company default ticks.
        var prompt = !unattended || machine!.AlwaysAsk;
        var noPrompt = !prompt || (!p.RequirePermission && running is null);
        var id = Guid.NewGuid();
        var session = Row(helperId, device, mode, unattended, now, noPrompt ? "active" : "asking", id, running?.Room ?? IPop.Api.Endpoints.RemoteEndpoints.RoomName(id));
        session.AskSeconds = p.AskSeconds;
        if (noPrompt)
        {
            var perms = unattended ? RemotePolicyStore.Unattended(machine) : p.DefaultPerms.And(max);
            session.StartedAt = now; session.AliveAt = now; session.Permissions = RemoteAccess.Permissions(perms.List.Split(',', StringSplitOptions.RemoveEmptyEntries), mode);
        }
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new Result(200, new { sessionId = session.Id, status = session.Status, machine = name, user = device.User.Name, remoteId,
            unattended, permissions = session.Permissions, askSeconds = p.AskSeconds }, session, device, running is not null);
    }

    private static RemoteSession Row(Guid helperId, NotifierDevice device, string mode, bool unattended, DateTime now, string status, Guid? id = null, string? room = null) => new()
    {
        Id = id ?? Guid.NewGuid(), HelperId = helperId, TargetUserId = device.UserId, DeviceId = device.Id,
        Room = room ?? "-", Mode = mode, Unattended = unattended, RequestedAt = now, Status = status,
        EndedAt = status is "denied" or "locked" ? now : null, EndedBy = status is "denied" or "locked" ? "password" : null,
    };
}
