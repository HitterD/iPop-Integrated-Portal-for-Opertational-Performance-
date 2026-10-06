using IPop.Application.Files;
using IPop.Application.Notifications;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Files;

/// Files land on the app server only when the NAS was unreachable at upload time (chat and mail
/// attachments, stickers). Every few minutes this moves them to the NAS — same shared folders the
/// uploads use — repoints the row, and deletes the local copy, so the server is only ever a
/// transit point. Admins hear about it when a file has been waiting for more than an hour.
public sealed class LocalToNasWorker(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<LocalToNasWorker> log, string? contentRoot = null) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlertAfter = TimeSpan.FromHours(1);
    private DateTime _lastAlert = DateTime.MinValue;

    /// One local file waiting for the NAS: which row, where it is now, where it goes.
    public sealed record Pending(string Kind, Guid Id, string LocalRelative, string NasDir, DateTime CreatedAt);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogWarning(ex, "Local → NAS move failed; will retry"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// Moves what it can; returns (moved, stillWaiting). Public for tests and the admin "retry now".
    public async Task<(int Moved, int Waiting)> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var root = contentRoot ?? scope.ServiceProvider.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
        if (!files.HasServiceAccount || files.ServiceServer is not { } server) return (0, 0);

        var pending = await FindAsync(db, ct);
        var moved = 0;
        foreach (var p in pending)
        {
            var full = Path.GetFullPath(Path.Combine(root, p.LocalRelative));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
            var name = Path.GetFileName(full);
            try
            {
                await using (var fs = File.OpenRead(full))
                    await files.UploadAsServiceAsync(p.NasDir, name, fs, ct);
                await RepointAsync(db, p, server, $"{p.NasDir}/{name}", root, files, ct);
                File.Delete(full);
                moved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // NAS still down: stop this round, the rest waits for the next one.
                log.LogInformation("NAS not reachable yet ({Error}); {Count} file(s) still on the server.", ex.Message, pending.Count - moved);
                break;
            }
        }
        if (moved > 0) log.LogInformation("Moved {Count} file(s) from the server to the NAS.", moved);

        var waiting = pending.Count - moved;
        var oldest = pending.Skip(moved).Select(p => p.CreatedAt).DefaultIfEmpty(DateTime.UtcNow).Min();
        if (waiting > 0 && DateTime.UtcNow - oldest > AlertAfter && DateTime.UtcNow - _lastAlert > TimeSpan.FromHours(24))
        {
            _lastAlert = DateTime.UtcNow;
            try
            {
                var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
                await notify.PublishInternalAsync("system", "system.ops", "Files waiting for the NAS",
                    $"{waiting} uploaded file(s) are still only on the iPop server because the NAS could not be reached. They move automatically once it is back.",
                    new NotificationTarget(Role: "Admin"), "high", "/admin", ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "Could not alert admins about files waiting for the NAS"); }
        }
        return (moved, waiting);
    }

    private async Task<List<Pending>> FindAsync(IPopDbContext db, CancellationToken ct)
    {
        string Folder(string key, string fallback) => (cfg[key]?.Trim().TrimEnd('/') is { Length: > 0 } f ? f : fallback);
        var chatRoot = Folder("Synology:ChatFolder", "/home/chat");
        var mailRoot = Folder("Synology:MailFolder", "/sja/SJA SPJ/ICT/mail");
        var list = new List<Pending>();

        // Local chat files live at chat-files/{roomId:N}/{file}; the NAS folder is {ChatFolder}/{roomId:D}.
        list.AddRange((await db.ChatAttachments.AsNoTracking().Where(a => a.SynologyServer == "local")
                .Select(a => new { a.Id, a.SynologyPath, a.CreatedAt }).ToListAsync(ct))
            .Select(a => (a, room: Guid.TryParse(a.SynologyPath.Replace('\\', '/').Split('/').ElementAtOrDefault(1), out var r) ? r : (Guid?)null))
            .Where(x => x.room is not null)
            .Select(x => new Pending("chat", x.a.Id, x.a.SynologyPath, $"{chatRoot}/{x.room}", x.a.CreatedAt)));
        list.AddRange((await db.MailAttachments.AsNoTracking().Where(a => a.SynologyServer == "local")
                .Select(a => new { a.Id, a.SynologyPath, a.CreatedAt, a.UploaderId }).ToListAsync(ct))
            .Select(a => new Pending("mail", a.Id, a.SynologyPath, $"{mailRoot}/drafts/{a.UploaderId:N}", a.CreatedAt)));
        list.AddRange((await db.ChatStickers.AsNoTracking().Where(s => s.StorageServer == "local")
                .Select(s => new { s.Id, s.StoragePath, s.CreatedAt }).ToListAsync(ct))
            .Select(s => new Pending("sticker", s.Id, s.StoragePath, $"{chatRoot}/stickers", s.CreatedAt)));
        return list.OrderBy(p => p.CreatedAt).ToList();
    }

    private static async Task RepointAsync(IPopDbContext db, Pending p, string server, string nasPath, string root,
        IFileAccessService files, CancellationToken ct)
    {
        switch (p.Kind)
        {
            case "chat":
            {
                var a = await db.ChatAttachments.FirstAsync(x => x.Id == p.Id, ct);
                // The thumbnail sits next to the file locally; move it too, or point at the original.
                string? thumb = null;
                if (a.ThumbnailPath is { } t && t != a.SynologyPath)
                {
                    var tFull = Path.GetFullPath(Path.Combine(root, t));
                    if (File.Exists(tFull))
                    {
                        await using (var ts = File.OpenRead(tFull))
                            await files.UploadAsServiceAsync(p.NasDir, Path.GetFileName(tFull), ts, ct);
                        File.Delete(tFull);
                        thumb = $"{p.NasDir}/{Path.GetFileName(tFull)}";
                    }
                }
                a.ThumbnailPath = thumb ?? (a.ThumbnailPath is null ? null : nasPath);
                a.SynologyServer = server; a.SynologyPath = nasPath;
                break;
            }
            case "mail":
            {
                var a = await db.MailAttachments.FirstAsync(x => x.Id == p.Id, ct);
                if (a.ThumbnailPath == a.SynologyPath) a.ThumbnailPath = nasPath;
                a.SynologyServer = server; a.SynologyPath = nasPath;
                break;
            }
            case "sticker":
            {
                var s = await db.ChatStickers.FirstAsync(x => x.Id == p.Id, ct);
                s.StorageServer = server; s.StoragePath = nasPath;
                break;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
