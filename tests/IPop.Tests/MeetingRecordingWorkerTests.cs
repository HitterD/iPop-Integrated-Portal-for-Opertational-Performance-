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
using Microsoft.Extensions.Logging;
using Xunit;

namespace IPop.Tests;

public class MeetingRecordingWorkerTests : IDisposable
{
    private readonly List<string> _dirs = [];
    public void Dispose() { foreach (var dir in _dirs) if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    internal sealed class Nas : IFileAccessService
    {
        // Only the service-account members are used here; every other member throws (DispatchProxy-free fake).
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public int Downloads { get; private set; }
        public bool FailUpload { get; set; }
        public string? FailUploadFilename { get; set; }
        public string? FailDeletePath { get; set; }
        public Func<Stream>? DownloadStream { get; set; }
        public bool HasServiceAccount => true;
        public string? ServiceServer => "ict";
        public Action? OnUpload { get; set; }
        public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct) { OnUpload?.Invoke(); if (FailUpload || filename == FailUploadFilename) throw new IOException("NAS offline"); Uploaded.Add($"{dir}/{filename}"); return Task.CompletedTask; }
        public Task DeleteAsServiceAsync(string path, CancellationToken ct) { if (path == FailDeletePath) throw new IOException("delete failed"); Deleted.Add(path); return Task.CompletedTask; }
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) { Downloads++; return Task.FromResult<Stream>(DownloadStream?.Invoke() ?? new MemoryStream([1, 2, 3])); }
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
            if (target.Role is not null) Sent.Add((eventType, title, target, targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private (ServiceProvider Sp, string Spool) Services(TestDb t, ILiveKitEgress eg, Nas nas, Gateway gw,
        MeetingRoomServiceTests.FakeRoomAdmin? rooms = null)
    {
        var spool = Path.Combine(Path.GetTempPath(), "ipop-rec-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(spool);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["MeetingRooms:SpoolDir"] = spool, ["MeetingRooms:NasFolder"] = "/sja/SJA SPJ/ICT/recordings" }).Build();
        if (eg is MeetingRecordingServiceTests.FakeEgress fake)
        foreach (var info in fake.Infos.ToList())
        {
            var recording = t.Verify().MeetingRecordings.FirstOrDefault(x => x.EgressId == info.Key);
            if (recording?.MeetingId is { } id && info.Value.Room is null)
                fake.Infos[info.Key] = info.Value with { Room = MeetingRoomMedia.Default.Room(recording.RoomKey, id) };
        }
        var sc = new ServiceCollection();
        sc.AddSingleton<IConfiguration>(cfg);
        sc.AddScoped(_ => t.Verify());
        sc.AddScoped<ISettings>(p => new DbSettings(p.GetRequiredService<IPopDbContext>()));
        sc.AddSingleton<ILiveKitEgress>(eg);
        sc.AddSingleton<ILiveKitRoomAdmin>(rooms ?? new MeetingRoomServiceTests.FakeRoomAdmin());
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
        r.MeetingId = MeetingRoomTestBookings.Ensure(t, "kalosi", viewers.Prepend(by).Distinct().ToArray());
        t.Db.MeetingRecordings.Add(r); t.Db.SaveChanges();
        return r;
    }

    [Fact]
    public async Task Recording_pass_adds_actual_participants_without_duplicates_or_egress_viewers()
    {
        using var t = new TestDb();
        var a = t.AddUser("VIEW_EXISTING"); var b = t.AddUser("VIEW_JOINED");
        var absent = t.AddUser("VIEW_TOKEN_ONLY");
        var row = Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var rooms = new MeetingRoomServiceTests.FakeRoomAdmin();
        rooms.Rooms["hall-kalosi"] = [
            new(a.Id.ToString(), "Existing", [], new Dictionary<string, string>()),
            new(b.Id.ToString(), "Joined", [], new Dictionary<string, string>()),
            new(b.Id.ToString(), "Repeated", [], new Dictionary<string, string>()),
            new("EG_x", "Recorder", [], new Dictionary<string, string>()),
        ];
        MeetingRoomTestBookings.Ensure(t, "kalosi", a.Id, b.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new("EGRESS_ACTIVE", null, null, null, null);
        var (sp, _) = Services(t, eg, new Nas(), new Gateway(), rooms);
        using var provider = sp;

        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default));
        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default));

        using var db = t.Verify();
        var viewers = await db.MeetingRecordingViewers.Where(v => v.RecordingId == row.Id).Select(v => v.UserId).ToListAsync();
        Assert.Equal(new[] { a.Id, b.Id }.Order(), viewers.Order());
        Assert.DoesNotContain(absent.Id, viewers);
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

    [Fact]
    public async Task Recording_deleted_during_its_upload_stays_deleted_and_the_uploaded_file_is_removed()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var row = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new("EGRESS_COMPLETE", "/out/kalosi/2026-10-11/1000-x.mp4", 1234, 125_000_000_000, null);
        var nas = new Nas(); var gw = new Gateway();
        // The starter deletes it from the Rooms tab while the worker is copying the file to the NAS.
        nas.OnUpload = () => { using var db = t.Verify(); db.MeetingRecordings.Single(r => r.Id == row.Id).Status = MeetingRecordingStatus.Deleted; db.SaveChanges(); };
        var (provider, spool) = Services(t, eg, nas, gw);
        using var sp = provider;
        Directory.CreateDirectory(Path.Combine(spool, "kalosi", "2026-10-11"));
        await File.WriteAllBytesAsync(Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4"), [1, 2, 3]);

        await MeetingRecordingWorker.FinishAsync(provider, default);

        var saved = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Deleted, saved.Status);
        Assert.Null(saved.NasPath);
        Assert.Equal(["/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-11/1000-x.mp4"], nas.Deleted);
        Assert.Empty(gw.Sent);
        Assert.False(File.Exists(Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4")));
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
    public async Task Failed_egress_clears_the_room_recording_banner()
    {
        // QA 2026-10-12: Egress aborted ("Start signal not received") but the room metadata kept the recording, so every
        // participant saw "Recording · started by …" forever and Stop answered 404.
        using var t = new TestDb(); var a = t.AddUser("A1");
        var row = Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new("EGRESS_ABORTED", null, null, null, "Start signal not received");
        var rooms = new MeetingRoomServiceTests.FakeRoomAdmin();
        var (provider, _) = Services(t, eg, new Nas(), new Gateway(), rooms);
        using var sp = provider;
        await MeetingRecordingWorker.FinishAsync(provider, default);
        Assert.Contains(rooms.MetadataUpdates, m => m.Room == MeetingRoomMedia.Default.Room("kalosi", row.MeetingId!.Value) && !m.Metadata.Contains("recording"));
    }

    [Fact]
    public async Task Purge_deletes_old_and_admin_deleted_files_from_the_nas()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var old = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var fresh = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var gone = Row(t, MeetingRecordingStatus.Deleted, a.Id, a.Id);
        var db = t.Verify();
        foreach (var r in db.MeetingRecordings) r.NasPath = $"/sja/SJA SPJ/ICT/recordings/{r.Id}.mp4";
        db.MeetingRecordings.Single(r => r.Id == old.Id).StartedAt = DateTime.UtcNow.AddDays(-91);
        db.SaveChanges();
        var nas = new Nas(); var (provider, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway());

        using var sp = provider;
        Assert.Equal(2, await MeetingRecordingWorker.PurgeAsync(provider, DateTime.UtcNow, default));
        Assert.Equal(new[] { $"/sja/SJA SPJ/ICT/recordings/{old.Id}.mp4", $"/sja/SJA SPJ/ICT/recordings/{gone.Id}.mp4" }.Order(), nas.Deleted.Order());
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
        var nas = new Nas(); var row = new MeetingRecording { RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", Id = Guid.NewGuid(), NasPath = "/sja/SJA SPJ/ICT/recordings/file.mp4" };
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
        var row = new MeetingRecording { RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", Id = Guid.NewGuid(), NasPath = "/sja/SJA SPJ/ICT/recordings/file.mp4" };
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
        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default));
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
            foreach (var row in db.MeetingRecordings) { row.NasPath = $"/sja/SJA SPJ/ICT/recordings/{row.Id}.mp4"; row.StartedAt = now.AddDays(-effectiveDays).AddSeconds(-1); }
            db.MeetingRecordings.Single(r => r.Id == boundary.Id).StartedAt = now.AddDays(-effectiveDays);
            await db.SaveChangesAsync(); await new DbSettings(db).SetAsync(SettingKeys.MeetingRoomsRetentionDays, days, null);
        }
        var nas = new Nas(); var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway()); using var provider = sp;
        Assert.Equal(1, await MeetingRecordingWorker.PurgeAsync(sp, now, default)); Assert.Equal($"/sja/SJA SPJ/ICT/recordings/{expired.Id}.mp4", Assert.Single(nas.Deleted));
        Assert.Equal(0, await MeetingRecordingWorker.PurgeAsync(sp, now, default));
        using var after = t.Verify(); Assert.Equal(MeetingRecordingStatus.Recording, (await after.MeetingRecordings.SingleAsync(r => r.Id == active.Id)).Status);
    }

    private sealed class LookupEgress(Func<string, CancellationToken, Task<LiveKitEgressInfo?>> lookup) : ILiveKitEgress
    {
        public bool IsConfigured => true;
        public Task<LiveKitEgressInfo?> GetAsync(string id, CancellationToken ct) => lookup(id, ct);
        public Task<string> StartRoomCompositeAsync(string room, string path, string templateUrl, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_first_egress_lookup_does_not_block_second_recording(bool timeout)
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        var second = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id); second.EgressId = "EG_2"; t.Db.SaveChanges();
        var lookedUp = new List<string>();
        var eg = new LookupEgress((id, _) =>
        {
            lookedUp.Add(id);
            if (lookedUp.Count == 1) throw timeout ? new TaskCanceledException("HTTP timeout") : new IOException("Egress down");
            return Task.FromResult<LiveKitEgressInfo?>(new("EGRESS_COMPLETE", null, 3, null, null, MeetingRoomMedia.Default.Room("kalosi", second.MeetingId!.Value)));
        });
        var nas = new Nas(); var (sp, spool) = Services(t, eg, nas, new Gateway()); using var provider = sp;
        var path = Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default)); Assert.Equal(2, lookedUp.Count);
        using var db = t.Verify(); var rows = await db.MeetingRecordings.ToListAsync();
        Assert.Equal(MeetingRecordingStatus.Processing, rows.Single(r => r.EgressId == lookedUp[0]).Status);
        Assert.Equal(MeetingRecordingStatus.Ready, rows.Single(r => r.EgressId == lookedUp[1]).Status);
    }

    [Fact]
    public async Task Purge_failure_is_isolated_and_successful_deletion_is_persisted()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var bad = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id); var good = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        foreach (var row in new[] { bad, good }) { row.NasPath = $"/sja/SJA SPJ/ICT/recordings/{row.Id}.mp4"; row.StartedAt = DateTime.UtcNow.AddDays(-91); }
        t.Db.SaveChanges(); var nas = new Nas { FailDeletePath = bad.NasPath };
        var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway()); using var provider = sp;
        Assert.Equal(1, await MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, default));
        using var db = t.Verify(); var rows = await db.MeetingRecordings.ToDictionaryAsync(r => r.Id);
        Assert.Equal(MeetingRecordingStatus.Deleted, rows[good.Id].Status); Assert.Null(rows[good.Id].NasPath);
        Assert.Equal(MeetingRecordingStatus.Ready, rows[bad.Id].Status); Assert.Equal(bad.NasPath, rows[bad.Id].NasPath);
        nas.FailDeletePath = null; Assert.Equal(1, await MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, default));
    }

    private sealed class BrokenCopyStream(bool cancel) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
            if (cancel) throw new TaskCanceledException("download timeout");
            throw new IOException("download interrupted");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_failed_copy_removes_partial_file_and_allows_retry(bool cancel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipop-cache-" + Guid.NewGuid().ToString("N")); _dirs.Add(dir);
        var row = new MeetingRecording { Id = Guid.NewGuid(), RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", NasPath = "/sja/SJA SPJ/ICT/recordings/file.mp4" };
        var nas = new Nas { DownloadStream = () => new BrokenCopyStream(cancel) }; var cache = new RecordingFileCache(nas, CacheConfig(dir));
        if (cancel) await Assert.ThrowsAsync<TaskCanceledException>(() => cache.OpenAsync(row, default));
        else await Assert.ThrowsAsync<IOException>(() => cache.OpenAsync(row, default));
        Assert.Empty(Directory.GetFiles(dir));
        nas.DownloadStream = null; Assert.NotNull(await cache.OpenAsync(row, default)); Assert.Equal(2, nas.Downloads);
    }

    private sealed class WarningLogger : ILogger<MeetingRecordingWorker>
    {
        public TaskCompletionSource Warned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Warned.TrySetResult(); }
    }

    [Fact]
    public async Task Background_worker_survives_unrelated_timeout_and_stops_on_host_cancellation()
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => throw new TaskCanceledException("dependency HTTP timeout"));
        using var sp = sc.BuildServiceProvider(); var logger = new WarningLogger();
        using var worker = new MeetingRecordingWorker(sp, logger);
        await worker.StartAsync(default);
        await logger.Warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(worker.ExecuteTask!.IsCompleted);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await worker.StopAsync(stop.Token);
        Assert.True(worker.ExecuteTask.IsCompleted);
    }

    [Fact]
    public async Task Failed_upload_does_not_block_another_ready_recording()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var bad = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        var good = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id); good.SpoolPath = "kalosi/2026-10-11/good.mp4"; t.Db.SaveChanges();
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new("EGRESS_COMPLETE", null, 3, null, null);
        var nas = new Nas { FailUploadFilename = "1000-x.mp4" }; var (sp, spool) = Services(t, eg, nas, new Gateway()); using var provider = sp;
        foreach (var row in new[] { bad, good })
        {
            var path = Path.Combine(spool, row.SpoolPath!); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }
        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));
        using var db = t.Verify(); var rows = await db.MeetingRecordings.ToDictionaryAsync(r => r.Id);
        Assert.Equal(MeetingRecordingStatus.Processing, rows[bad.Id].Status); Assert.Equal(MeetingRecordingStatus.Ready, rows[good.Id].Status);
        Assert.True(File.Exists(Path.Combine(spool, bad.SpoolPath!)));
    }

    [Fact]
    public async Task Host_cancellation_is_not_swallowed_by_finish_or_purge()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), new Nas(), new Gateway()); using var provider = sp;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MeetingRecordingWorker.FinishAsync(sp, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, cancelled.Token));
    }

    // ---------------------------------------------------------------- final review fixes (I1, I2, M5)

    [Fact]
    public async Task Recording_row_without_egress_id_older_than_two_minutes_fails_as_start_interrupted()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var stuck = Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        using (var db = t.Verify())
        {
            var r = db.MeetingRecordings.Single(x => x.Id == stuck.Id);
            r.EgressId = ""; r.StartedAt = DateTime.UtcNow.AddMinutes(-3);
            db.SaveChanges();
        }
        var rooms = new MeetingRoomServiceTests.FakeRoomAdmin();
        var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), new Nas(), new Gateway(), rooms); using var provider = sp;

        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));

        using var after = t.Verify(); var saved = await after.MeetingRecordings.SingleAsync();
        Assert.Equal((MeetingRecordingStatus.Failed, "start interrupted; room egress cessation confirmed"), (saved.Status, saved.Error));
        Assert.NotNull(saved.EndedAt);
        Assert.Contains(rooms.MetadataUpdates, m => m.Room == MeetingRoomMedia.Default.Room("kalosi", stuck.MeetingId!.Value) && !m.Metadata.Contains("recording"));
    }

    [Fact]
    public async Task Recording_row_without_egress_id_still_starting_is_left_alone()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var young = Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        using (var db = t.Verify())
        {
            var r = db.MeetingRecordings.Single(x => x.Id == young.Id);
            r.EgressId = ""; r.StartedAt = DateTime.UtcNow.AddSeconds(-30);
            db.SaveChanges();
        }
        var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), new Nas(), new Gateway()); using var provider = sp;
        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default));
        using var after = t.Verify();
        Assert.Equal(MeetingRecordingStatus.Recording, (await after.MeetingRecordings.SingleAsync()).Status);
    }

    [Fact]
    public async Task Completed_egress_leaves_recording_before_the_upload_so_the_room_is_free_while_the_nas_is_down()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new("EGRESS_COMPLETE", null, 3, null, null);
        var nas = new Nas { FailUpload = true };
        var (sp, spool) = Services(t, eg, nas, new Gateway()); using var provider = sp;
        var local = Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4"); Directory.CreateDirectory(Path.GetDirectoryName(local)!); await File.WriteAllBytesAsync(local, [1, 2, 3]);

        Assert.Equal(0, await MeetingRecordingWorker.FinishAsync(sp, default));

        using (var db = t.Verify())
        {
            var saved = await db.MeetingRecordings.SingleAsync();
            Assert.Equal(MeetingRecordingStatus.Processing, saved.Status);
            Assert.NotNull(saved.EndedAt);
        }
        // The room can be recorded again right away.
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        using var svcDb = t.Verify();
        var svc = new MeetingRecordingService(svcDb, lk, new MeetingRecordingServiceTests.FakeEgress(), new DbSettings(svcDb),
            MeetingRecordingServiceTests.Config());
        Assert.Null((await svc.StartAsync(a.Id, "kalosi", default)).Error);
    }

    [Fact]
    public async Task Processing_row_without_a_file_that_ended_more_than_six_hours_ago_fails_and_tells_the_starter()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var old = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id, b.Id);
        var recent = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        using (var db = t.Verify())
        {
            db.MeetingRecordings.Single(r => r.Id == old.Id).EndedAt = DateTime.UtcNow.AddHours(-7);
            db.MeetingRecordings.Single(r => r.Id == recent.Id).EndedAt = DateTime.UtcNow.AddHours(-1);
            db.SaveChanges();
        }
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new("EGRESS_COMPLETE", null, 3, null, null);
        var gw = new Gateway(); var (sp, _) = Services(t, eg, new Nas { FailUpload = true }, gw); using var provider = sp;

        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));

        using var after = t.Verify(); var rows = await after.MeetingRecordings.ToDictionaryAsync(r => r.Id);
        Assert.Equal((MeetingRecordingStatus.Failed, "upload timed out"), (rows[old.Id].Status, rows[old.Id].Error));
        Assert.Equal(MeetingRecordingStatus.Processing, rows[recent.Id].Status);
        var sent = Assert.Single(gw.Sent);
        Assert.Equal(("meeting.recording.failed", (Guid?)a.Id), (sent.EventType, sent.Target.UserId));
    }

    [Fact]
    public async Task Recording_still_on_the_server_after_six_hours_is_kept_retried_and_admins_are_told_once()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var row = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        using (var db = t.Verify())
        {
            db.MeetingRecordings.Single(r => r.Id == row.Id).EndedAt = DateTime.UtcNow.AddHours(-7);
            db.SaveChanges();
        }
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new("EGRESS_COMPLETE", null, 3, null, null);
        var gw = new Gateway(); var nas = new Nas { FailUpload = true };
        var (sp, spool) = Services(t, eg, nas, gw); using var provider = sp;
        var local = Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!); await File.WriteAllBytesAsync(local, [1, 2, 3]);

        await MeetingRecordingWorker.FinishAsync(sp, default);
        await MeetingRecordingWorker.FinishAsync(sp, default);   // NAS still down

        Assert.True(File.Exists(local));                          // the only copy survives
        using (var mid = t.Verify())
            Assert.Equal((MeetingRecordingStatus.Processing, MeetingRecordingWorker.UploadDelayed),
                ((await mid.MeetingRecordings.SingleAsync()).Status, (await mid.MeetingRecordings.SingleAsync()).Error));
        var alert = Assert.Single(gw.Sent);
        Assert.Equal(("meeting.recording.upload_delayed", "Admin"), (alert.EventType, alert.Target.Role));

        nas.FailUpload = false;                                   // NAS back
        await MeetingRecordingWorker.FinishAsync(sp, default);
        using var after = t.Verify(); var saved = await after.MeetingRecordings.SingleAsync();
        Assert.Equal((MeetingRecordingStatus.Ready, (string?)null), (saved.Status, saved.Error));
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task Purge_retires_failed_rows_without_a_file_once_past_retention()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var oldFailed = Row(t, MeetingRecordingStatus.Failed, a.Id, a.Id);
        var newFailed = Row(t, MeetingRecordingStatus.Failed, a.Id, a.Id);
        using (var db = t.Verify())
        {
            db.MeetingRecordings.Single(r => r.Id == oldFailed.Id).StartedAt = DateTime.UtcNow.AddDays(-91);
            db.SaveChanges();
        }
        var nas = new Nas(); var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway()); using var provider = sp;

        Assert.Equal(1, await MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, default));

        Assert.Empty(nas.Deleted);   // nothing on the NAS to remove
        using var after = t.Verify(); var rows = await after.MeetingRecordings.ToDictionaryAsync(r => r.Id);
        Assert.Equal(MeetingRecordingStatus.Deleted, rows[oldFailed.Id].Status);
        Assert.Equal(MeetingRecordingStatus.Failed, rows[newFailed.Id].Status);
        using var svcDb = t.Verify();
        var svc = new MeetingRecordingService(svcDb, new MeetingRoomServiceTests.FakeRoomAdmin(), new MeetingRecordingServiceTests.FakeEgress(),
            new DbSettings(svcDb), MeetingRecordingServiceTests.Config());
        Assert.DoesNotContain(await svc.ListAllAsync(default), r => r.Id == oldFailed.Id);
    }

    // ---------------------------------------------------------------- re-review follow-ups

    [Fact]
    public async Task Processing_row_without_egress_id_still_times_out_after_six_hours()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var row = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id);
        using (var db = t.Verify())
        {
            var r = db.MeetingRecordings.Single(x => x.Id == row.Id);
            r.EgressId = ""; r.EndedAt = DateTime.UtcNow.AddHours(-7);
            db.SaveChanges();
        }
        var gw = new Gateway(); var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), new Nas(), gw); using var provider = sp;

        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));

        using var after = t.Verify(); var saved = await after.MeetingRecordings.SingleAsync();
        Assert.Equal((MeetingRecordingStatus.Failed, "upload timed out"), (saved.Status, saved.Error));
        Assert.Single(gw.Sent, s => s.EventType == "meeting.recording.failed");
    }

    [Theory]
    [InlineData("start interrupted")]
    [InlineData("egress failed")]
    public async Task Failed_rows_lose_their_local_spool_file(string reason)
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var row = Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        using (var db = t.Verify())
        {
            var r = db.MeetingRecordings.Single(x => x.Id == row.Id);
            if (reason == "start interrupted") { r.EgressId = ""; r.StartedAt = DateTime.UtcNow.AddMinutes(-3); }
            db.SaveChanges();
        }
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        if (reason == "egress failed") eg.Infos["EG_1"] = new("EGRESS_FAILED", null, null, null, "chrome crashed");
        var (sp, spool) = Services(t, eg, new Nas { FailUpload = true }, new Gateway()); using var provider = sp;
        var local = Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!); await File.WriteAllBytesAsync(local, [1, 2, 3]);

        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));

        using var after = t.Verify();
        Assert.Equal(MeetingRecordingStatus.Failed, (await after.MeetingRecordings.SingleAsync()).Status);
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task Failing_without_a_spool_file_is_fine()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress(); eg.Infos["EG_1"] = new("EGRESS_ABORTED", null, null, null, "aborted");
        var (sp, _) = Services(t, eg, new Nas(), new Gateway()); using var provider = sp;   // spool dir does not even exist
        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));
    }
}
