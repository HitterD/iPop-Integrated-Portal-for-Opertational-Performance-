using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Files;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Files;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class FileOperationsTests
{
    private sealed class MockRealtime : IRealtime
    {
        public List<(string Event, object? Payload)> Events { get; } = [];

        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default)
        {
            Events.Add((evt, payload));
            return Task.CompletedTask;
        }

        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default)
        {
            Events.Add((evt, payload));
            return Task.CompletedTask;
        }

        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default)
        {
            Events.Add((evt, payload));
            return Task.CompletedTask;
        }

        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default)
        {
            Events.Add((evt, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class MockGateway : ISynologyGateway
    {
        public List<string> Renames { get; } = [];
        public List<string> FoldersCreated { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<(string Server, string Dir, string Name)> Uploads { get; } = [];

        public Task<string> AuthAsync(string server, string username, string password, CancellationToken ct) => Task.FromResult("sid-" + username);
        public Task<IReadOnlyList<string>> ListSharesAsync(string server, string sid, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["/home", "/teams"]);
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(string server, string sid, string path, CancellationToken ct) => Task.FromResult<IReadOnlyList<SynoItem>>([]);
        public Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream("file-content"u8.ToArray()));
        public Task UploadAsync(string server, string sid, string dir, string filename, Stream content, CancellationToken ct)
        {
            Uploads.Add((server, dir, filename));
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string server, string sid, string path, CancellationToken ct)
        {
            Deletes.Add(path);
            return Task.CompletedTask;
        }
        public Task RenameAsync(string server, string sid, string path, string name, CancellationToken ct)
        {
            Renames.Add($"{path}->{name}");
            return Task.CompletedTask;
        }
        public Task CreateFolderAsync(string server, string sid, string folderPath, string name, CancellationToken ct)
        {
            FoldersCreated.Add($"{folderPath}/{name}");
            return Task.CompletedTask;
        }
        public Task CopyMoveAsync(string server, string sid, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoShareLink> CreateSharingLinkAsync(string server, string sid, string path, string? password, int? expiresInDays, CancellationToken ct)
            => Task.FromResult(new SynoShareLink("s1", "https://sharing/s1", path, null, false));
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SynoShareLink>>([new SynoShareLink("s1", "https://sharing/s1", "/home/test.txt", null, false)]);
        public Task DeleteSharingLinkAsync(string server, string sid, string id, CancellationToken ct) => Task.CompletedTask;
        public Task<SynoStorageInfo> GetStorageInfoAsync(string server, string sid, CancellationToken ct)
            => Task.FromResult(new SynoStorageInfo(server, 3000, 8000, 5000));
    }

    [Fact]
    public async Task FileService_Rename_CallsGateway()
    {
        using var t = new TestDb();
        var user = t.AddUser("USER1");
        var gw = new MockGateway();
        var protector = new EphemeralDataProtectionProvider();
        var cfg = new ConfigurationBuilder().Build();
        var svc = new FileAccessService(t.Db, gw, protector, cfg, NullLogger<FileAccessService>.Instance);

        await svc.SetCredentialAsync(user.Id, "spj", "alice", "secret", default);
        await svc.RenameAsync(user.Id, "spj", "/home/old.txt", "new.txt", default);

        Assert.Single(gw.Renames);
        Assert.Equal("/home/old.txt->new.txt", gw.Renames[0]);
    }

    [Fact]
    public async Task FileService_CreateFolder_CallsGateway()
    {
        using var t = new TestDb();
        var user = t.AddUser("USER1");
        var gw = new MockGateway();
        var protector = new EphemeralDataProtectionProvider();
        var cfg = new ConfigurationBuilder().Build();
        var svc = new FileAccessService(t.Db, gw, protector, cfg, NullLogger<FileAccessService>.Instance);

        await svc.SetCredentialAsync(user.Id, "spj", "alice", "secret", default);
        await svc.CreateFolderAsync(user.Id, "spj", "/home", "NewFolder", default);

        Assert.Single(gw.FoldersCreated);
        Assert.Equal("/home/NewFolder", gw.FoldersCreated[0]);
    }

    [Fact]
    public async Task FileOperationWorker_ProcessesCrossServerTransfer()
    {
        using var t = new TestDb();
        var user = t.AddUser("USER1");
        var gw = new MockGateway();
        var protector = new EphemeralDataProtectionProvider();
        var cfg = new ConfigurationBuilder().Build();
        var svc = new FileAccessService(t.Db, gw, protector, cfg, NullLogger<FileAccessService>.Instance);
        var realtime = new MockRealtime();

        await svc.SetCredentialAsync(user.Id, "spj", "alice", "secret", default);
        await svc.SetCredentialAsync(user.Id, "smg", "alice", "secret", default);

        var items = new List<FileTransferItem> { new("/home/report.pdf", "report.pdf", 1024) };
        var op = new FileOperation
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Kind = "move",
            SourceServer = "spj",
            DestServer = "smg",
            DestDir = "/teams",
            ItemsJson = JsonSerializer.Serialize(items),
            Status = "Queued",
            TotalFiles = 1,
            CompletedFiles = 0,
            FailedFiles = 0,
            BytesTransferred = 0,
            CreatedAt = DateTime.UtcNow
        };
        t.Db.FileOperations.Add(op);
        await t.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => t.Db);
        services.AddScoped<IFileAccessService>(_ => svc);
        services.AddScoped<IRealtime>(_ => realtime);
        var sp = services.BuildServiceProvider();

        var worker = new FileOperationWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<FileOperationWorker>.Instance);
        var processed = await worker.ProcessNextOperationAsync(default);

        Assert.True(processed);

        await using var v = t.Verify();
        var completedOp = await v.FileOperations.SingleAsync(o => o.Id == op.Id);
        Assert.Equal("Completed", completedOp.Status);
        Assert.Equal(1, completedOp.CompletedFiles);
        Assert.Equal(0, completedOp.FailedFiles);
        Assert.Single(gw.Uploads);
        Assert.Equal("smg", gw.Uploads[0].Server);
        Assert.Equal("/teams", gw.Uploads[0].Dir);
        Assert.Equal("report.pdf", gw.Uploads[0].Name);
        Assert.Single(gw.Deletes);
        Assert.Equal("/home/report.pdf", gw.Deletes[0]);

        // Live refresh: the destination folder and (for a move) the source folder.
        var scopes = realtime.Events.Where(e => e.Event == RealtimeEvents.FilesChanged).Select(e => e.Payload as string).ToList();
        Assert.Contains("smg:/teams", scopes);
        Assert.Contains("spj:/home", scopes);
        Assert.Contains(realtime.Events, e => e.Event == RealtimeEvents.FileOperationFinished);
    }

    [Fact]
    public async Task FileOperationWorker_FailsOperationsLeftRunningByAPreviousProcess()
    {
        using var t = new TestDb();
        var user = t.AddUser("USER1");
        FileOperation Op(string status, DateTime? started) => new()
        {
            Id = Guid.NewGuid(), UserId = user.Id, Kind = "move", SourceServer = "spj", DestServer = "smg", DestDir = "/teams",
            ItemsJson = "[]", Status = status, TotalFiles = 3, CompletedFiles = 1, FailedFiles = 0, BytesTransferred = 0,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10), StartedAt = started,
        };
        var orphan = Op("Running", DateTime.UtcNow.AddMinutes(-9));   // pod killed mid-transfer
        var queued = Op("Queued", null);
        var done = Op("Completed", DateTime.UtcNow.AddMinutes(-9));
        t.Db.FileOperations.AddRange(orphan, queued, done);
        await t.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => t.Db);
        services.AddScoped<IRealtime>(_ => new MockRealtime());
        var sp = services.BuildServiceProvider();
        var worker = new FileOperationWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<FileOperationWorker>.Instance);

        var recovered = await worker.RecoverInterruptedAsync(default);

        Assert.Equal(1, recovered);
        await using var v = t.Verify();
        var o = await v.FileOperations.SingleAsync(x => x.Id == orphan.Id);
        Assert.Equal("Failed", o.Status);                // never resumed blindly: a half-done move must not run twice
        Assert.NotNull(o.FinishedAt);
        Assert.Contains("interrupted", o.Error);
        Assert.Equal("Queued", (await v.FileOperations.SingleAsync(x => x.Id == queued.Id)).Status);
        Assert.Equal("Completed", (await v.FileOperations.SingleAsync(x => x.Id == done.Id)).Status);
    }

    [Theory]
    [InlineData("SPJ", "/home/docs/", "spj:/home/docs")]
    [InlineData("spj", "home", "spj:/home")]
    [InlineData("krw", "", "krw:/")]
    [InlineData("krw", "/", "krw:/")]
    [InlineData("smg", "*", "smg:*")]
    public void FilesRealtime_Scope_IsNormalized(string server, string dir, string expected)
        => Assert.Equal(expected, FilesRealtime.Scope(server, dir));

    [Theory]
    [InlineData("/home/docs/a.pdf", "/home/docs")]
    [InlineData("/home", "/")]
    [InlineData("/home/docs/", "/home")]
    public void FilesRealtime_ParentDir(string path, string expected)
        => Assert.Equal(expected, FilesRealtime.ParentDir(path));

    [Fact]
    public async Task FilesRealtime_Push_SwallowsFailures_AndDedupes()
    {
        var ok = new MockRealtime();
        await FilesRealtime.PushAsync(ok, "spj", ["/a", "/a/", "/b"]);
        Assert.Equal(["spj:/a", "spj:/b"], ok.Events.Select(e => (string)e.Payload!).ToArray());

        await FilesRealtime.PushAsync(new ThrowingRealtime(), "spj", ["/a"]); // must not throw
    }

    private sealed class ThrowingRealtime : IRealtime
    {
        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) => throw new InvalidOperationException("hub down");
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) => throw new InvalidOperationException("hub down");
        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) => throw new InvalidOperationException("hub down");
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) => throw new InvalidOperationException("hub down");
    }

    [Fact]
    public async Task FileOperationRetentionWorker_DeletesOldRecords()
    {
        using var t = new TestDb();
        var user = t.AddUser("USER1");
        var old = DateTime.UtcNow.AddDays(-35);
        var fresh = DateTime.UtcNow.AddDays(-5);

        t.Db.FileOperations.AddRange(
            new FileOperation
            {
                Id = Guid.NewGuid(), UserId = user.Id, Kind = "copy", SourceServer = "spj", DestServer = "smg",
                ItemsJson = "[]", Status = "Completed", TotalFiles = 0, CompletedFiles = 0, FailedFiles = 0,
                BytesTransferred = 0, CreatedAt = old
            },
            new FileOperation
            {
                Id = Guid.NewGuid(), UserId = user.Id, Kind = "copy", SourceServer = "spj", DestServer = "smg",
                ItemsJson = "[]", Status = "Completed", TotalFiles = 0, CompletedFiles = 0, FailedFiles = 0,
                BytesTransferred = 0, CreatedAt = fresh
            }
        );
        await t.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => t.Db);
        var sp = services.BuildServiceProvider();

        var worker = new FileOperationRetentionWorker(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<FileOperationRetentionWorker>.Instance);
        var deleted = await worker.RunPurgeAsync(default);

        Assert.Equal(1, deleted);
        await using var v = t.Verify();
        Assert.Single(await v.FileOperations.ToListAsync());
    }
}
