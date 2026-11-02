using IPop.Application.Files;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Files;

public class FileAccessService(
    IPopDbContext db,
    ISynologyGateway gateway,
    IDataProtectionProvider protector,
    IConfiguration cfg,
    ILogger<FileAccessService> log,
    SynologySessionCache? sessions = null) : IFileAccessService
{
    private const string Purpose = "synology-credential";
    private readonly IDataProtector _protector = protector.CreateProtector(Purpose);
    // Singleton in the app; tests that construct the service directly get a private one.
    private readonly SynologySessionCache _sessions = sessions ?? new SynologySessionCache();

    public async Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct)
    {
        server = NormalizeServer(server);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "Username and password are required.");

        string sid;
        try { sid = await gateway.AuthAsync(server, username.Trim(), password, ct); }
        catch (SynoException ex) when (ex.IsAuthFailure)
        {
            return (false, "Synology rejected the credentials.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Synology auth check failed for user {UserId} server {Server}: {Message}", userId, server, ex.Message);
            return (false, $"Could not reach the NAS ({ex.GetType().Name}: {ex.Message}).");
        }

        await StoreAsync(userId, server, username, password, sid, ct);
        return (true, null);
    }

    public static readonly string[] PlantServers = ["spj", "krw", "jtb", "smg"];

    public async Task<IReadOnlyList<SiteConnectResult>> ConnectAllAsync(Guid userId, string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return PlantServers.Select(s => new SiteConnectResult(s, "rejected", "Username and password are required.")).ToList();

        // The NAS logins run in parallel (one slow plant link must not hold up the others);
        // the DbContext writes afterwards stay sequential.
        var user = username.Trim();
        var attempts = await Task.WhenAll(PlantServers.Select(async server =>
        {
            try { return (server, sid: (string?)await gateway.AuthAsync(server, user, password, ct), status: "connected", error: (string?)null); }
            catch (SynoException ex) when (ex.IsAuthFailure) { return (server, sid: (string?)null, status: "rejected", error: (string?)"This NAS rejected the credentials."); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Synology connect-all: {Server} unreachable for user {UserId}", server, userId);
                return (server, sid: (string?)null, status: "unreachable", error: (string?)"Could not reach this NAS.");
            }
        }));

        foreach (var a in attempts.Where(a => a.sid is not null))
            await StoreAsync(userId, a.server, user, password, a.sid!, ct);

        return attempts.Select(a => new SiteConnectResult(a.server, a.status, a.error)).ToList();
    }

    private async Task StoreAsync(Guid userId, string server, string username, string password, string sid, CancellationToken ct)
    {
        _sessions.Set(userId, server, sid);
        var row = await db.SynologyCredentials.FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct);
        if (row is null)
        {
            row = new SynologyCredential { UserId = userId, Server = server, Username = username.Trim(), ProtectedPassword = _protector.Protect(password), IsInvalid = false, UpdatedAt = DateTime.UtcNow };
            db.SynologyCredentials.Add(row);
        }
        else
        {
            row.Username = username.Trim();
            row.ProtectedPassword = _protector.Protect(password);
            row.IsInvalid = false;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var row = await db.SynologyCredentials.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct);
        if (row is null) return (false, null);
        return (true, new CredentialDto(row.Server, true, row.IsInvalid, row.Username));
    }

    public async Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.SynologyCredentials.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct);
        return rows.Select(r => new CredentialDto(r.Server, true, r.IsInvalid, r.Username)).ToList();
    }

    public async Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var row = await db.SynologyCredentials.FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct);
        if (row is not null) db.SynologyCredentials.Remove(row);
        _sessions.Remove(userId, server);
        await db.SaveChangesAsync(ct);
    }

    public void DropSession(Guid userId, string server) => _sessions.Remove(userId, NormalizeServer(server));
    public void DropAllSessions(Guid userId) => _sessions.RemoveAll(userId);

    public Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.ListSharesAsync(server, sid, c), ct);

    public Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.ListFolderAsync(server, sid, path, c), ct);

    public Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.DownloadAsync(server, sid, path, c), ct);

    public Task RenameAsync(Guid userId, string server, string path, string name, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.RenameAsync(server, sid, path, name, c), ct);

    public Task CreateFolderAsync(Guid userId, string server, string folderPath, string name, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.CreateFolderAsync(server, sid, folderPath, name, c), ct);

    public Task DeleteAsync(Guid userId, string server, string path, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.DeleteAsync(server, sid, path, c), ct);

    public Task CopyMoveAsync(Guid userId, string server, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.CopyMoveAsync(server, sid, paths, destDir, removeSrc, overwrite, c), ct);

    public Task<SynoShareLink> CreateSharingLinkAsync(Guid userId, string server, string path, string? password, int? expiresInDays, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.CreateSharingLinkAsync(server, sid, path, password, expiresInDays, c), ct);

    public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(Guid userId, string server, string? path, CancellationToken ct)
        => WithSid(server, userId, async (sid, c) =>
        {
            var links = await gateway.ListSharingLinksAsync(server, sid, c);
            if (!string.IsNullOrWhiteSpace(path))
                return links.Where(l => l.Path == path).ToList();
            return links;
        }, ct);

    public Task DeleteSharingLinkAsync(Guid userId, string server, string id, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.DeleteSharingLinkAsync(server, sid, id, c), ct);

    public Task<SynoStorageInfo> GetStorageInfoAsync(Guid userId, string server, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.GetStorageInfoAsync(server, sid, c), ct);

    public Task<string> SearchStartAsync(Guid userId, string server, IReadOnlyList<string> folderPaths, string pattern, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.SearchStartAsync(server, sid, folderPaths, pattern, c), ct);

    public Task<SynoSearchPage> SearchListAsync(Guid userId, string server, string taskId, int offset, int limit, CancellationToken ct)
        => WithSid(server, userId, (sid, c) => gateway.SearchListAsync(server, sid, taskId, offset, limit, c), ct);

    public async Task SearchStopAsync(Guid userId, string server, string taskId, CancellationToken ct)
    {
        // Best effort: DSM drops finished or orphaned search tasks on its own.
        try { await WithSid(server, userId, (sid, c) => gateway.SearchStopAsync(server, sid, taskId, c), ct); }
        catch (SynoException) { }
    }

    public async Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct)
    {
        server = NormalizeServer(server);
        // A session-expired retry must resend the same bytes. The first attempt reads the stream to
        // the end, so a non-seekable source (OnlyOffice's HTTP download) is spooled to disk first —
        // retrying on the drained stream would overwrite the file with nothing.
        Stream? spool = null;
        if (!content.CanSeek)
        {
            spool = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await content.CopyToAsync(spool, ct);
            spool.Position = 0;
            content = spool;
        }
        try
        {
            var start = content.Position;
            var sid = await GetSidAsync(userId, server, ct);
            try { await gateway.UploadAsync(server, sid, dir, filename, content, ct); }
            catch (SynoException ex) when (ex.IsSessionExpired)
            {
                _sessions.Remove(userId, server);
                var fresh = await GetSidAsync(userId, server, ct);
                content.Position = start;
                await gateway.UploadAsync(server, fresh, dir, filename, content, ct);
            }
        }
        finally
        {
            if (spool is not null) await spool.DisposeAsync();
        }
    }

    private async Task<T> WithSid<T>(string server, Guid userId, Func<string, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        server = NormalizeServer(server);
        var sid = await GetSidAsync(userId, server, ct);
        try { return await action(sid, ct); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            _sessions.Remove(userId, server);
            var fresh = await GetSidAsync(userId, server, ct);
            return await action(fresh, ct);
        }
    }

    private Task WithSid(string server, Guid userId, Func<string, CancellationToken, Task> action, CancellationToken ct)
        => WithSid<bool>(server, userId, async (sid, c) => { await action(sid, c); return true; }, ct);

    private async Task<string> GetSidAsync(Guid userId, string server, CancellationToken ct)
    {
        server = NormalizeServer(server);
        if (_sessions.TryGet(userId, server, out var cached)) return cached;

        var gate = _sessions.LockFor(userId, server);
        await gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGet(userId, server, out var raced)) return raced;
            return await LoginAsync(userId, server, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<string> LoginAsync(Guid userId, string server, CancellationToken ct)
    {
        var row = await db.SynologyCredentials.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId && x.Server == server, ct)
            ?? throw new SynoException("No Synology credential stored", authFailure: true);
        if (row.IsInvalid)
            throw new SynoException("Stored credential is invalid — re-enter it", authFailure: true);

        string password;
        try { password = _protector.Unprotect(row.ProtectedPassword); }
        catch { throw new SynoException("Could not decrypt stored credential", authFailure: true); }

        try
        {
            var sid = await gateway.AuthAsync(server, row.Username, password, ct);
            _sessions.Set(userId, server, sid);
            return sid;
        }
        catch (SynoException ex) when (ex.IsAuthFailure)
        {
            await db.SynologyCredentials.Where(x => x.UserId == userId && x.Server == server)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsInvalid, true), ct);
            _sessions.Remove(userId, server);
            throw new SynoException("Stored credential rejected — re-enter it", authFailure: true);
        }
    }

    // ── Service account ──────────────────────────────────────────────────────

    private string? ServiceUser => cfg["Synology:ServiceAccount:Username"]?.Trim();
    private string? ServicePass => cfg["Synology:ServiceAccount:Password"];

    public string? ServiceServer
    {
        get
        {
            var s = cfg["Synology:ServiceAccount:Server"]?.Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s.ToLowerInvariant();
        }
    }

    public bool HasServiceAccount =>
        !string.IsNullOrWhiteSpace(ServiceUser) &&
        !string.IsNullOrWhiteSpace(ServicePass) &&
        !string.IsNullOrWhiteSpace(ServiceServer);

    public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct)
        => WithServiceSid((srv, sid, c) => gateway.DownloadAsync(srv, sid, path, c), ct);

    public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct)
    {
        // A session-expired retry must resend from the same place, not from the drained end.
        var start = content.CanSeek ? content.Position : -1;
        return WithServiceSid(async (srv, sid, c) =>
        {
            if (start >= 0) content.Position = start;
            await gateway.UploadAsync(srv, sid, dir, filename, content, c);
            return true;
        }, ct);
    }

    public Task DeleteAsServiceAsync(string path, CancellationToken ct)
        => WithServiceSid(async (srv, sid, c) =>
        {
            await gateway.DeleteAsync(srv, sid, path, c);
            return true;
        }, ct);

    private async Task<T> WithServiceSid<T>(Func<string, string, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var server = ServiceServer
            ?? throw new SynoException("No Synology service account configured", authFailure: true);

        var sid = await GetServiceSidAsync(server, ct);
        try { return await action(server, sid, ct); }
        catch (SynoException ex) when (ex.IsSessionExpired)
        {
            _sessions.ServiceSid = null;
            var fresh = await GetServiceSidAsync(server, ct);
            return await action(server, fresh, ct);
        }
    }

    private async Task<string> GetServiceSidAsync(string server, CancellationToken ct)
    {
        if (_sessions.ServiceSid is { } cached) return cached;

        // One login at a time: a burst of attachment reads on a cold service session would
        // otherwise open a Synology session per request.
        await _sessions.ServiceLock.WaitAsync(ct);
        try
        {
            if (_sessions.ServiceSid is { } raced) return raced;

            var user = ServiceUser;
            var pass = ServicePass;
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
                throw new SynoException("Synology service account is not configured", authFailure: true);

            try
            {
                var sid = await gateway.AuthAsync(server, user, pass, ct);
                _sessions.ServiceSid = sid;
                return sid;
            }
            catch (SynoException ex) when (ex.IsAuthFailure)
            {
                log.LogError("Synology service account '{User}' rejected by server '{Server}'. Chat attachments cannot be stored on the NAS until this is fixed.", user, server);
                throw;
            }
        }
        finally { _sessions.ServiceLock.Release(); }
    }

    private static string NormalizeServer(string s) => s.Trim().ToLowerInvariant() switch
    {
        "spj" or "krw" or "jtb" or "smg" or "ict" => s.Trim().ToLowerInvariant(),
        _ => throw new SynoException($"Unknown server '{s}' — expected spj/krw/jtb/smg/ict")
    };
}
