using IPop.Application.Mail;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace IPop.Tests;

/// Mail read receipts (spec 2026-10-14 §2/§3): a receipt (SeenAt) is recorded only by the explicit
/// "seen" call from the reading pane; mark-read paths touch only the personal unread flag. Receipts are
/// visible to the sender and every To/Cc/Bcc recipient; Bcc rows only to the sender; mass mail = counts.
public class MailReadReceiptsTests
{
    private static readonly DateTime FeatureStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static void StartTracking(TestDb t, DateTime? at = null)
    {
        t.Db.AppSettings.Add(new AppSetting
        {
            Key = MailReceipts.TrackedSinceKey,
            ValueJson = System.Text.Json.JsonSerializer.Serialize(at ?? FeatureStart),
            UpdatedAt = DateTime.UtcNow,
        });
        t.Db.SaveChanges();
    }

    private static MailService Svc(TestDb t, IRealtime? rt = null) => new(t.Verify(), realtime: rt);

    private static async Task<Guid> SendAsync(TestDb t, User from, IReadOnlyList<string> to, IReadOnlyList<string>? cc = null,
        IReadOnlyList<string>? bcc = null, bool hide = false, int? confirmed = null, IReadOnlyList<MailGroupRef>? groups = null)
    {
        var r = await Svc(t).SendAsync(from.Id, new ComposeMailRequest("Hi", "<p>x</p>", to, cc ?? [], bcc ?? [],
            Groups: groups, HideRecipients: hide, ConfirmedRecipientCount: confirmed), default);
        Assert.True(r.Success, r.Error);
        return r.MailId!.Value;
    }

    private static DateTime? SeenAt(TestDb t, Guid mailId, Guid userId)
        => t.Verify().MailRecipients.Single(r => r.MailId == mailId && r.UserId == userId).SeenAt;

    // ── recording ──

    [Fact]
    public async Task Seen_records_once_for_a_recipient_and_is_idempotent()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var id = await SendAsync(t, a, [b.Nik]);

        var first = await Svc(t).RecordSeenAsync(b.Id, id, default);
        Assert.True(first.Found);
        Assert.True(first.Recorded);
        var stamp = SeenAt(t, id, b.Id);
        Assert.NotNull(stamp);

        var again = await Svc(t).RecordSeenAsync(b.Id, id, default);
        Assert.True(again.Found);
        Assert.False(again.Recorded);
        Assert.Equal(stamp, SeenAt(t, id, b.Id));
        Assert.Equal(stamp, again.SeenAt);
    }

    [Fact]
    public async Task Seen_does_not_touch_the_personal_unread_flag()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var id = await SendAsync(t, a, [b.Nik]);
        await Svc(t).RecordSeenAsync(b.Id, id, default);
        var row = t.Verify().MailRecipients.Single(r => r.MailId == id && r.UserId == b.Id);
        Assert.False(row.IsRead);
        Assert.Null(row.ReadAt);
    }

    [Theory]
    [InlineData("to")]
    [InlineData("cc")]
    [InlineData("bcc")]
    public async Task Every_recipient_field_can_record(string field)
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var id = field switch
        {
            "to" => await SendAsync(t, a, [b.Nik]),
            "cc" => await SendAsync(t, a, [], cc: [b.Nik]),
            _ => await SendAsync(t, a, [], bcc: [b.Nik]),
        };
        Assert.True((await Svc(t).RecordSeenAsync(b.Id, id, default)).Recorded);
    }

    [Fact]
    public async Task Senders_own_copy_is_ignored_and_strangers_are_not_found()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C");
        var id = await SendAsync(t, a, [b.Nik]);

        var own = await Svc(t).RecordSeenAsync(a.Id, id, default);
        Assert.True(own.Found);
        Assert.False(own.Recorded);
        Assert.Null(SeenAt(t, id, a.Id));

        Assert.False((await Svc(t).RecordSeenAsync(c.Id, id, default)).Found);
        Assert.False((await Svc(t).RecordSeenAsync(b.Id, Guid.NewGuid(), default)).Found);
    }

    [Fact]
    public async Task Mail_sent_before_tracking_started_records_nothing()
    {
        using var t = new TestDb();
        StartTracking(t, DateTime.UtcNow.AddDays(1));
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var id = await SendAsync(t, a, [b.Nik]);
        var r = await Svc(t).RecordSeenAsync(b.Id, id, default);
        Assert.True(r.Found);
        Assert.False(r.Recorded);
        Assert.Null(SeenAt(t, id, b.Id));
    }

    [Fact]
    public async Task Missing_start_setting_is_created_on_first_use_so_older_mail_reports_not_tracked()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var id = await SendAsync(t, a, [b.Nik]);
        await Task.Delay(20);
        var receipts = await Svc(t).GetReceiptsAsync(a.Id, id, default);
        Assert.False(receipts!.Tracked);
        Assert.NotNull(t.Verify().AppSettings.SingleOrDefault(s => s.Key == MailReceipts.TrackedSinceKey));
    }

    [Fact]
    public async Task Seen_pushes_a_receipt_hint_to_the_sender_and_visible_recipients_but_not_bcc()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C"); var h = t.AddUser("H");
        var id = await SendAsync(t, a, [b.Nik], cc: [c.Nik], bcc: [h.Nik]);
        var rt = new RecordingRealtime();
        await Svc(t, rt).RecordSeenAsync(b.Id, id, default);
        var sent = Assert.Single(rt.Sent);
        Assert.Equal(RealtimeEvents.MailReceiptChanged, sent.Evt);
        Assert.Equal(["A", "B", "C"], sent.Niks.Order().ToArray());
    }

    // ── existing read paths never record a receipt ──

    [Fact]
    public async Task Mark_read_mark_all_read_bulk_read_open_thread_and_search_never_set_seen()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var m1 = await SendAsync(t, a, [b.Nik]);
        var m2 = await SendAsync(t, a, [b.Nik]);
        var m3 = await SendAsync(t, a, [b.Nik]);

        await Svc(t).MarkReadAsync(b.Id, m1, default);
        await Svc(t).BulkMarkReadAsync(b.Id, [m2], default);
        await Svc(t).MarkAllReadAsync(b.Id, MailFolder.Inbox, default);
        await Svc(t).GetAsync(b.Id, m3, default);
        await Svc(t).GetThreadAsync(b.Id, m3, 1, 50, default);
        await Svc(t).GetFolderAsync(b.Id, MailFolder.Inbox, 1, 20, new MailSearchRequest(Query: "Hi"), default);
        await Svc(t).RecordDeliveredAsync(b.Id, m3, default);
        await Svc(t).MarkUnreadAsync(b.Id, m1, default);
        await Svc(t).MarkReadAsync(b.Id, m1, default);
        await new IPop.Infrastructure.Search.Providers.MailSearchProvider(t.Verify())
            .SearchAsync(new IPop.Application.Search.SearchQuery(b.Id, "hi", 1, 10), default);

        var rows = t.Verify().MailRecipients.Where(r => r.UserId == b.Id).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsRead));
        Assert.All(rows, r => Assert.Null(r.SeenAt));
    }

    // ── reading receipts ──

    [Fact]
    public async Task Sender_sees_everyone_including_bcc_with_seen_and_delivery()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C"); var h = t.AddUser("H");
        var id = await SendAsync(t, a, [b.Nik], cc: [c.Nik], bcc: [h.Nik]);
        await Svc(t).RecordSeenAsync(b.Id, id, default);
        await Svc(t).RecordSeenAsync(h.Id, id, default);

        var r = await Svc(t).GetReceiptsAsync(a.Id, id, default);
        Assert.NotNull(r);
        Assert.True(r.Tracked);
        Assert.True(r.NamesVisible);
        Assert.Equal(3, r.Total);
        Assert.Equal(2, r.Read);
        Assert.Equal(3, r.Delivered);
        Assert.Equal(["bcc", "cc", "to"], r.People.Select(p => p.Field).Order().ToArray());
        var pb = r.People.Single(p => p.Name == b.Name);
        Assert.Equal(b.Id.ToString("N"), pb.UserKey);
        Assert.NotNull(pb.SeenAt);
        Assert.True(pb.Delivered);
        Assert.False(pb.IsMe);
        Assert.Null(r.People.Single(p => p.Name == c.Name).SeenAt);
        Assert.Equal(0, r.External.Count);
        Assert.Equal("not_available", r.External.Status);
    }

    [Fact]
    public async Task To_and_cc_recipients_see_the_same_list_without_bcc()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C"); var h = t.AddUser("H");
        var id = await SendAsync(t, a, [b.Nik], cc: [c.Nik], bcc: [h.Nik]);
        await Svc(t).RecordSeenAsync(h.Id, id, default);
        await Svc(t).RecordSeenAsync(c.Id, id, default);

        foreach (var viewer in new[] { b, c, h })
        {
            var r = await Svc(t).GetReceiptsAsync(viewer.Id, id, default);
            Assert.NotNull(r);
            Assert.Equal(2, r.Total);
            Assert.Equal(1, r.Read);                                   // the Bcc read never counts for others
            Assert.Equal([b.Name, c.Name], r.People.Select(p => p.Name).Order().ToArray());
            Assert.DoesNotContain(r.People, p => p.Field == "bcc");
            Assert.Equal(viewer != h, r.People.Any(p => p.IsMe));       // a Bcc viewer is not in the list
        }
    }

    [Fact]
    public async Task Strangers_cannot_read_receipts()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var x = t.AddUser("X");
        var id = await SendAsync(t, a, [b.Nik]);
        Assert.Null(await Svc(t).GetReceiptsAsync(x.Id, id, default));
        Assert.Null(await Svc(t).GetReceiptsAsync(a.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Rows_carry_department_site_and_photo_id_but_never_a_nik()
    {
        using var t = new TestDb();
        StartTracking(t);
        var site = new Site { Id = Guid.NewGuid(), Code = "KRW", Name = "SJA Karawang" };
        var dept = new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT" };
        t.Db.Sites.Add(site); t.Db.Departments.Add(dept);
        var a = t.AddUser("A"); var b = t.AddUser("00061058");
        var bu = t.Db.Users.Single(u => u.Id == b.Id);
        bu.Name = "Bagastyo"; bu.Email = "bagastyo@t.co"; bu.SiteId = site.Id; bu.DepartmentId = dept.Id; bu.PhotoPath = "11111111-1111-1111-1111-111111111111";
        t.Db.SaveChanges();
        var id = await SendAsync(t, a, [b.Nik]);

        var r = await Svc(t).GetReceiptsAsync(a.Id, id, default);
        var p = Assert.Single(r!.People);
        Assert.Equal("ICT", p.Department);
        Assert.Equal("SJA Karawang", p.Site);
        Assert.Equal("11111111-1111-1111-1111-111111111111", p.PhotoId);
        var json = System.Text.Json.JsonSerializer.Serialize(r);
        Assert.DoesNotContain("00061058", json);
    }

    [Fact]
    public async Task Untracked_mail_reports_not_tracked_with_no_reads()
    {
        using var t = new TestDb();
        StartTracking(t, DateTime.UtcNow.AddDays(1));
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var id = await SendAsync(t, a, [b.Nik]);
        // Even a stray stamp (e.g. written directly) is not reported for an untracked mail.
        var db = t.Verify();
        db.MailRecipients.Single(r => r.MailId == id && r.UserId == b.Id).SeenAt = DateTime.UtcNow;
        db.SaveChanges();

        var r = await Svc(t).GetReceiptsAsync(b.Id, id, default);
        Assert.False(r!.Tracked);
        Assert.Equal(0, r.Read);
        Assert.All(r.People, p => Assert.Null(p.SeenAt));
    }

    [Fact]
    public async Task Mass_mail_gives_counts_only_to_recipients_and_names_to_the_sender()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A");
        var people = Enumerable.Range(0, 55).Select(i => t.AddUser($"P{i:D3}")).ToList();
        var id = await SendAsync(t, a, people.Select(p => p.Nik).ToList(), confirmed: 55);
        foreach (var p in people.Take(4)) await Svc(t).RecordSeenAsync(p.Id, id, default);

        var asRecipient = await Svc(t).GetReceiptsAsync(people[10].Id, id, default);
        Assert.False(asRecipient!.NamesVisible);
        Assert.Equal(55, asRecipient.Total);
        Assert.Equal(4, asRecipient.Read);
        Assert.Empty(asRecipient.People);
        Assert.Empty(asRecipient.Groups);

        var asSender = await Svc(t).GetReceiptsAsync(a.Id, id, default);
        Assert.True(asSender!.NamesVisible);
        Assert.Equal(55, asSender.People.Count);
    }

    [Fact]
    public async Task Hidden_recipient_list_gives_counts_only_to_recipients()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var c = t.AddUser("C");
        var id = await SendAsync(t, a, [b.Nik, c.Nik], hide: true);
        var r = await Svc(t).GetReceiptsAsync(b.Id, id, default);
        Assert.False(r!.NamesVisible);
        Assert.Empty(r.People);
        Assert.Equal(2, r.Total);
    }

    [Fact]
    public async Task Small_group_mail_lists_groups_with_their_own_read_counts()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var members = Enumerable.Range(0, 3).Select(i => t.AddUser($"G{i}")).ToList();
        var g = new DirGroup { Id = Guid.NewGuid(), Name = "ICT · SJA Karawang" };
        t.Db.DirGroups.Add(g);
        foreach (var m in members) t.Db.Add(new UserDirGroup { UserId = m.Id, DirGroupId = g.Id });
        t.Db.SaveChanges();
        var id = await SendAsync(t, a, [b.Nik], groups: [new MailGroupRef("to", "shared", g.Id)]);
        await Svc(t).RecordSeenAsync(members[0].Id, id, default);

        var r = await Svc(t).GetReceiptsAsync(b.Id, id, default);
        Assert.True(r!.NamesVisible);
        Assert.Equal(4, r.Total);
        var grp = Assert.Single(r.Groups);
        Assert.Equal("ICT · SJA Karawang", grp.Name);
        Assert.Equal(3, grp.Total);
        Assert.Equal(1, grp.Read);
        Assert.Equal(members.Select(m => m.Id.ToString("N")).Order(), grp.MemberKeys.Order());
        Assert.Equal(3, r.People.Count(p => p.ViaGroup));
    }

    [Fact]
    public async Task Bcc_groups_are_hidden_from_non_senders()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var m = t.AddUser("M");
        var g = new DirGroup { Id = Guid.NewGuid(), Name = "Secret" };
        t.Db.DirGroups.Add(g);
        t.Db.Add(new UserDirGroup { UserId = m.Id, DirGroupId = g.Id });
        t.Db.SaveChanges();
        var id = await SendAsync(t, a, [b.Nik], groups: [new MailGroupRef("bcc", "shared", g.Id)]);

        var r = await Svc(t).GetReceiptsAsync(b.Id, id, default);
        Assert.Empty(r!.Groups);
        Assert.Equal(1, r.Total);
        Assert.Single((await Svc(t).GetReceiptsAsync(a.Id, id, default))!.Groups);
    }

    [Fact]
    public async Task Archive_placeholder_accounts_count_as_external_not_available()
    {
        using var t = new TestDb();
        StartTracking(t);
        var a = t.AddUser("A"); var b = t.AddUser("B"); var ext = t.AddUser("EPOP-abc123");
        var id = await SendAsync(t, a, [b.Nik, ext.Nik]);

        var r = await Svc(t).GetReceiptsAsync(a.Id, id, default);
        Assert.Equal(1, r!.Total);
        Assert.Equal(1, r.External.Count);
        Assert.Equal("not_available", r.External.Status);
        Assert.DoesNotContain(r.People, p => p.Name == ext.Name);
    }

    // ── migration ──

    [Fact]
    public void Migration_adds_nullable_seen_at_and_stores_the_tracking_start()
    {
        using var db = new IPopDbContext(new DbContextOptionsBuilder<IPopDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=not_used;Username=not_used;Password=not_used").Options);
        var migrations = db.GetService<IMigrationsAssembly>();
        var entry = Assert.Single(migrations.Migrations, x => x.Key.EndsWith("_MailReadReceipts"));
        var ops = migrations.CreateMigration(entry.Value, db.Database.ProviderName!).UpOperations;
        var col = Assert.Single(ops.OfType<AddColumnOperation>());
        Assert.Equal(("MailRecipients", "SeenAt", true), (col.Table, col.Name, col.IsNullable));
        var sql = Assert.Single(ops.OfType<SqlOperation>()).Sql;
        Assert.Contains("\"AppSettings\"", sql);
        Assert.Contains(MailReceipts.TrackedSinceKey, sql);
        Assert.Contains("ON CONFLICT", sql);
    }

    private sealed class RecordingRealtime : IRealtime
    {
        public List<(string Evt, string[] Niks)> Sent { get; } = [];
        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) { Sent.Add((evt, [nik])); return Task.CompletedTask; }
        public Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default) { Sent.Add((evt, niks.ToArray())); return Task.CompletedTask; }
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
