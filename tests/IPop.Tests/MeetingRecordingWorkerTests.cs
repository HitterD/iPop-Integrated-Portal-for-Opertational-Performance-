using IPop.Application.Files;
using IPop.Application.Notifications;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class MeetingRecordingWorkerTests : IDisposable
{
    private readonly List<string> _dirs = [];
    public void Dispose() { foreach (var dir in _dirs) if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    private sealed class Nas : IFileAccessService
    {
        // Only the service-account members are used here; every other member throws (DispatchProxy-free fake).
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public int Downloads { get; private set; }
        public bool FailUpload { get; set; }
        public bool HasServiceAccount => true;
        public string? ServiceServer => "ict";
        public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct) { if (FailUpload) throw new IOException("NAS offline"); Uploaded.Add($"{dir}/{filename}"); return Task.CompletedTask; }
        public Task DeleteAsServiceAsync(string path, CancellationToken ct) { Deleted.Add(path); return Task.CompletedTask; }
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) { Downloads++; return Task.FromResult<Stream>(new MemoryStream([1, 2, 3])); }
        public Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(Guid userId, string server, string path, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateFolderAsync(Guid userId, string server, string folderPath, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyMoveAsync(Guid userId, string server, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
        public Task<SynoShareLink> CreateSharingLinkAsync(Guid userId, string server, string path, string? password, int? expiresInDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(Guid userId, string server, string? path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSharingLinkAsync(Guid userId, string server, string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SynoStorageInfo> GetStorageInfoAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiteConnectResult>> ConnectAllAsync(Guid userId, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public void DropSession(Guid userId, string server) => throw new NotSupportedException();
        public void DropAllSessions(Guid userId) => throw new NotSupportedException();
    }
    private sealed class Gateway : INotificationGatewayService
    {
        public List<(string EventType, string Title, NotificationTarget Target, string? Url)> Sent { get; } = [];

        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            foreach (var user in target.UserIds ?? (target.UserId is { } id ? new[] { id } : Array.Empty<Guid>()))
                Sent.Add((eventType, title, new NotificationTarget(UserId: user), targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private (ServiceProvider Sp, string Spool) Services(TestDb t, MeetingRecordingServiceTests.FakeEgress eg, Nas nas, Gateway gw)
    {
        var spool = Path.Combine(Path.GetTempPath(), "ipop-rec-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(spool);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["MeetingRooms:SpoolDir"] = spool, ["MeetingRooms:NasFolder"] = "/sja/SJA SPJ/ICT/recordings" }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton<IConfiguration>(cfg);
        sc.AddScoped(_ => t.Verify());
        sc.AddScoped<ISettings>(p => new DbSettings(p.GetRequiredService<IPopDbContext>()));
        sc.AddSingleton<ILiveKitEgress>(eg);
        sc.AddSingleton<IFileAccessService>(nas);
        sc.AddSingleton<INotificationGatewayService>(gw);
        return (sc.BuildServiceProvider(), spool);
    }

    private static MeetingRecording Row(TestDb t, MeetingRecordingStatus status, Guid by, params Guid[] viewers)
    {
        var id = Guid.NewGuid();
        var r = new MeetingRecording
        {
            Id = id, RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", StartedById = by, StartedAt = DateTime.UtcNow.AddHours(-1),
            Status = status, SpoolPath = "kalosi/2026-10-11/1000-x.mp4",
            Viewers = viewers.Select(v => new MeetingRecordingViewer { RecordingId = id, UserId = v }).ToList(),
        };
        t.Db.MeetingRecordings.Add(r); t.Db.SaveChanges();
        return r;
    }

    [Theory]
    [InlineData("EGRESS_COMPLETE")]
    [InlineData("EGRESS_LIMIT_REACHED")]
    public async Task Complete_egress_is_uploaded_to_the_nas_folder_and_viewers_are_notified(string status)
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var row = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id, b.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new(status, "/out/kalosi/2026-10-11/1000-x.mp4", 1234, 125_000_000_000, null);
        var nas = new Nas(); var gw = new Gateway();
        var (provider, spool) = Services(t, eg, nas, gw);
        using var sp = provider;
        Directory.CreateDirectory(Path.Combine(spool, "kalosi", "2026-10-11"));
        await File.WriteAllBytesAsync(Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4"), [1, 2, 3]);

        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(provider, default));

        var saved = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Ready, saved.Status);
        Assert.Equal("/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-11/1000-x.mp4", saved.NasPath);
        Assert.Equal((1234L, 125), (saved.SizeBytes!.Value, saved.DurationSeconds!.Value));
        Assert.Equal(["/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-11/1000-x.mp4"], nas.Uploaded);
        Assert.False(File.Exists(Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4")));
        Assert.Equal(2, gw.Sent.Count(s => s.EventType == "meeting.recording.ready"));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), gw.Sent.Select(s => s.Target.UserId!.Value).Order());
        Assert.All(gw.Sent, s => Assert.Equal($"/calls?tab=rooms&recording={row.Id}", s.Url));
    }

    [Theory]
    [InlineData("EGRESS_FAILED")]
    [InlineData("EGRESS_ABORTED")]
    public async Task Failed_egress_marks_failed_and_tells_who_started_it(string status)
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new(status, null, null, null, "chrome crashed");
        var gw = new Gateway(); var (provider, _) = Services(t, eg, new Nas(), gw);
        using var sp = provider;
        await MeetingRecordingWorker.FinishAsync(provider, default);
        Assert.Equal(MeetingRecordingStatus.Failed, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
        var sent = Assert.Single(gw.Sent, s => s.EventType == "meeting.recording.failed");
        Assert.Equal(a.Id, sent.Target.UserId); Assert.Equal("/calls?tab=rooms", sent.Url);
        using var db = t.Verify(); var saved = await db.MeetingRecordings.SingleAsync();
        Assert.Equal("chrome crashed", saved.Error); Assert.NotNull(saved.EndedAt);
    }

    [Fact]
    public async Task Purge_deletes_old_and_admin_deleted_files_from_the_nas()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var old = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var fresh = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var gone = Row(t, MeetingRecordingStatus.Deleted, a.Id, a.Id);
        var db = t.Verify();
        foreach (var r in db.MeetingRecordings) r.NasPath = $"/n/{r.Id}.mp4";
        db.MeetingRecordings.Single(r => r.Id == old.Id).StartedAt = DateTime.UtcNow.AddDays(-91);
        db.SaveChanges();
        var nas = new Nas(); var (provider, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway());

        using var sp = provider;
        Assert.Equal(2, await MeetingRecordingWorker.PurgeAsync(provider, DateTime.UtcNow, default));
        Assert.Equal(new[] { $"/n/{old.Id}.mp4", $"/n/{gone.Id}.mp4" }.Order(), nas.Deleted.Order());
        var after = t.Verify().MeetingRecordings.ToDictionary(r => r.Id);
        Assert.Equal(MeetingRecordingStatus.Deleted, after[old.Id].Status);
        Assert.Null(after[gone.Id].NasPath);
        Assert.Equal(MeetingRecordingStatus.Ready, after[fresh.Id].Status);
    }

    private IConfiguration CacheConfig(string dir) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["MeetingRooms:CacheDir"] = dir }).Build();

    [Fact]
    public async Task Cache_copies_once_and_concurrent_scoped_instances_share_the_seekable_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-cache-" + Guid.NewGuid().ToString("N")); _dirs.Add(dir);
        var nas = new Nas(); var row = new MeetingRecording { RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", Id = Guid.NewGuid(), NasPath = "/n/file.mp4" };
        var paths = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => new RecordingFileCache(nas, CacheConfig(dir)).OpenAsync(row, default)));
        Assert.Equal(1, nas.Downloads);
        Assert.All(paths, path => Assert.Equal(paths[0], path));
        await using var file = File.OpenRead(paths[0]!);
        Assert.True(file.CanSeek); Assert.Equal(3, file.Length); file.Seek(1, SeekOrigin.Begin); Assert.Equal(2, file.ReadByte());
        Assert.False(File.Exists(paths[0] + ".part"));
    }

    [Fact]
    public async Task Cache_without_nas_path_does_not_download()
    {
        var nas = new Nas(); var cache = new RecordingFileCache(nas, CacheConfig("unused"));
        Assert.Null(await cache.OpenAsync(new MeetingRecording { RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1" }, default)); Assert.Equal(0, nas.Downloads);
    }

    [Fact]
    public async Task Cache_removes_untouched_files_but_preserves_recent_and_requested_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-cache-" + Guid.NewGuid().ToString("N")); _dirs.Add(dir); Directory.CreateDirectory(dir);
        var row = new MeetingRecording { RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", Id = Guid.NewGuid(), NasPath = "/n/file.mp4" };
        var current = Path.Combine(dir, row.Id.ToString("N") + ".mp4");
        var old = Path.Combine(dir, "old.mp4"); var recent = Path.Combine(dir, "recent.mp4");
        foreach (var path in new[] { current, old, recent }) await File.WriteAllBytesAsync(path, [1, 2, 3]);
        File.SetLastAccessTimeUtc(current, DateTime.UtcNow.AddHours(-25)); File.SetLastAccessTimeUtc(old, DateTime.UtcNow.AddHours(-25));
        var nas = new Nas(); Assert.Equal(current, await new RecordingFileCache(nas, CacheConfig(dir)).OpenAsync(row, default));
        Assert.False(File.Exists(old)); Assert.True(File.Exists(recent)); Assert.Equal(0, nas.Downloads);
        Assert.True(File.GetLastAccessTimeUtc(current) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Upload_failure_preserves_spool_and_processing_state_for_retry()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new("EGRESS_COMPLETE", null, 3, null, null);
        var nas = new Nas { FailUpload = true }; var gw = new Gateway(); var (sp, spool) = Services(t, eg, nas, gw); using var provider = sp;
        var local = Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4"); Directory.CreateDirectory(Path.GetDirectoryName(local)!); await File.WriteAllBytesAsync(local, [1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => MeetingRecordingWorker.FinishAsync(sp, default));
        Assert.True(File.Exists(local)); using var db = t.Verify(); Assert.Equal(MeetingRecordingStatus.Processing, (await db.MeetingRecordings.SingleAsync()).Status); Assert.Empty(gw.Sent);
        nas.FailUpload = false; Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));
        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default)); Assert.Single(nas.Uploaded); Assert.Single(gw.Sent);
    }

    [Theory]
    [InlineData("EGRESS_ACTIVE")]
    [InlineData("EGRESS_COMPLETE")]
    public async Task Active_or_missing_spool_recordings_are_retried_without_notification(string status)
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new(status, null, null, null, null);
        var nas = new Nas(); var gw = new Gateway(); var (sp, _) = Services(t, eg, nas, gw); using var provider = sp;
        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default)); Assert.Empty(nas.Uploaded); Assert.Empty(gw.Sent);
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task Retention_uses_settings_clamps_to_one_day_and_preserves_active_recordings(int days, int effectiveDays)
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var expired = Row(t, MeetingRecordingStatus.Failed, a.Id, a.Id);
        var boundary = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var active = Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        using (var db = t.Verify())
        {
            foreach (var row in db.MeetingRecordings) { row.NasPath = $"/n/{row.Id}.mp4"; row.StartedAt = now.AddDays(-effectiveDays).AddSeconds(-1); }
            db.MeetingRecordings.Single(r => r.Id == boundary.Id).StartedAt = now.AddDays(-effectiveDays);
            await db.SaveChangesAsync(); await new DbSettings(db).SetAsync(SettingKeys.MeetingRoomsRetentionDays, days, null);
        }
        var nas = new Nas(); var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway()); using var provider = sp;
        Assert.Equal(1, await MeetingRecordingWorker.PurgeAsync(sp, now, default)); Assert.Equal($"/n/{expired.Id}.mp4", Assert.Single(nas.Deleted));
        Assert.Equal(0, await MeetingRecordingWorker.PurgeAsync(sp, now, default));
        using var after = t.Verify(); Assert.Equal(MeetingRecordingStatus.Recording, (await after.MeetingRecordings.SingleAsync(r => r.Id == active.Id)).Status);
    }
}
