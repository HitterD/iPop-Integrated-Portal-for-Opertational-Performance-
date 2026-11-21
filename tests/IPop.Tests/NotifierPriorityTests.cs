using System.Text;
using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Auth;
using IPop.Application.Documents;
using IPop.Application.Mail;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Notifier 1.5.0 priority inbox, server side (docs/notifier/PRIORITY-150.md "Server (API)"): who caused a
/// notification is stored and shown, mass mail is low priority, leaders/people lists never carry NIK or position,
/// and the preview converter only takes Office files up to 60 MB.
public class NotifierPriorityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string hash, string password) => hash == "h:" + password;
    }

    private sealed class CapturingBroadcaster : INotificationRealtimeBroadcaster
    {
        public List<NotificationDeliveryState> States { get; } = [];
        public Task BroadcastAsync(IReadOnlyList<NotificationDeliveryState> deliveries, CancellationToken ct = default)
        {
            States.AddRange(deliveries);
            return Task.CompletedTask;
        }
    }

    private static NotificationGatewayService Gateway(TestDb t, CapturingBroadcaster? b = null)
        => new(t.Verify(), new FakeHasher(), NullLogger<NotificationGatewayService>.Instance, b ?? new CapturingBroadcaster());

    // ── Actor ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Actor_is_stored_broadcast_and_named_in_the_inbox()
    {
        using var t = new TestDb();
        var boss = t.AddUser("ACT1");
        boss.Name = "Bambang Boss";
        var me = t.AddUser("ACT2");
        me.PresenceLastSeenAt = DateTime.UtcNow;   // online: the delivery goes out live (and is broadcast)
        t.Db.SaveChanges();
        var b = new CapturingBroadcaster();
        var r = await Gateway(t, b).PublishInternalAsync("projects", "project.task_mention", "Boss mentioned you", "hi",
            new NotificationTarget(UserId: me.Id), boss.Id, null, "normal", "/projects/x");
        Assert.True(r.Success, r.Error);

        await using var v = t.Verify();
        Assert.Equal(boss.Id, (await v.NotificationRequests.SingleAsync(x => x.Id == r.RequestId)).ActorUserId);
        Assert.Equal(boss.Id, Assert.Single(b.States).ActorId);

        var inbox = await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default);
        var row = Assert.Single(inbox.Items);
        Assert.Equal(boss.Id, row.ActorId);
        Assert.Equal(boss.Name, row.ActorName);
        Assert.Equal("project.task_mention", row.EventType);   // the app classifies by the full event type
        var json = JsonSerializer.Serialize(inbox, Web);
        Assert.DoesNotContain("ACT1", json);   // the actor's name only, never their NIK
    }

    [Fact]
    public async Task Without_an_actor_the_old_overloads_still_work_and_leave_it_empty()
    {
        using var t = new TestDb();
        var me = t.AddUser("ACT3");
        var r = await Gateway(t).PublishInternalAsync("system", "system.ops", "Disk", "low", new NotificationTarget(UserId: me.Id));
        Assert.True(r.Success);
        var row = Assert.Single((await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default)).Items);
        Assert.Null(row.ActorId);
        Assert.Null(row.ActorName);
    }

    [Fact]
    public async Task Chat_mention_records_the_sender_as_actor()
    {
        using var t = new TestDb();
        var a = t.AddUser("ACT4");
        var b = t.AddUser("ACT5");
        var room = t.AddRoom("group", "Team");
        var cast = new CapturingBroadcaster();
        await new NotificationGateway(NullLogger<NotificationGateway>.Instance, t.Verify(), cast)
            .SendMentionNotificationAsync(b.Id, a.Name, "Team", "hi @B", false, default, Guid.NewGuid(), room.Id, a.Id);
        await using var v = t.Verify();
        Assert.Equal(a.Id, (await v.NotificationRequests.SingleAsync()).ActorUserId);
        Assert.Equal(a.Id, Assert.Single(cast.States).ActorId);
    }

    private sealed class RecordingGateway : INotificationGatewayService
    {
        public List<(string EventType, string Priority, Guid? Actor)> Calls { get; } = [];
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
            => Record(eventType, priority, null);
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, Guid? actorUserId, string? idempotencyKey, string priority = "normal", string? targetUrl = null,
            CancellationToken ct = default) => Record(eventType, priority, actorUserId);
        private Task<NotificationEnqueueResult> Record(string e, string p, Guid? a)
        {
            Calls.Add((e, p, a));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    // ── Mail ─────────────────────────────────────────────────────────────────

    private static ComposeMailRequest Mail(IReadOnlyList<string> to, int? confirmed = null, string? importance = null)
        => new("Hello", "<p>x</p>", to, [], [], ConfirmedRecipientCount: confirmed, Importance: importance);

    [Fact]
    public async Task Mass_mail_is_published_low_with_the_sender_as_actor()
    {
        using var t = new TestDb();
        var sender = t.AddUser("MM0");
        var people = Enumerable.Range(1, MailLimits.ConfirmAbove + 1).Select(i => t.AddUser($"MM{i:D3}")).ToList();
        var gw = new RecordingGateway();
        var sent = await new MailService(t.Verify(), gw).SendAsync(sender.Id, Mail(people.Select(p => p.Nik).ToList(), people.Count), default);
        Assert.True(sent.Success, sent.Error);
        await new MailService(t.Verify(), gw).ProcessNotifyJobsAsync(default);
        var call = Assert.Single(gw.Calls);
        Assert.Equal(("mail.new", "low", (Guid?)sender.Id), call);
    }

    [Theory]
    [InlineData(null, "normal")]
    [InlineData("high", "high")]
    [InlineData("low", "normal")]
    public async Task Personal_mail_keeps_its_importance(string? importance, string priority)
    {
        using var t = new TestDb();
        var sender = t.AddUser("PM1");
        var to = t.AddUser("PM2");
        var gw = new RecordingGateway();
        Assert.True((await new MailService(t.Verify(), gw).SendAsync(sender.Id, Mail([to.Nik], importance: importance), default)).Success);
        await new MailService(t.Verify(), gw).ProcessNotifyJobsAsync(default);
        Assert.Equal(("mail.new", priority, (Guid?)sender.Id), Assert.Single(gw.Calls));
    }

    [Fact]
    public async Task Low_priority_is_accepted_by_the_gateway()
    {
        using var t = new TestDb();
        var me = t.AddUser("LOW1");
        var r = await Gateway(t).PublishInternalAsync("mail", "mail.new", "All staff", "x", new NotificationTarget(UserId: me.Id), priority: "low");
        Assert.True(r.Success, r.Error);
        Assert.Equal("low", Assert.Single((await NotificationGatewayEndpoints.ListInboxCore(t.Verify(), me.Id, null, null, 1, 20, default)).Items).Priority);
    }

    // ── Leaders / people ─────────────────────────────────────────────────────

    private static User Person(TestDb t, string nik, string name, Department? dept = null, bool active = true)
    {
        var u = t.AddUser(nik, active);
        u.Name = name;
        u.Position = "SECRET POSITION " + nik;
        u.DepartmentId = dept?.Id;
        t.Db.SaveChanges();
        return u;
    }

    private static void Chain(TestDb t, string nik, string? level = null, params (int Slot, string Nik)[] slots)
    {
        var c = new ApproverChain { Nik = nik, SourceLevel = level, UpdatedAt = DateTime.UtcNow };
        foreach (var (slot, n) in slots) c.SetSlot(slot, n);
        t.Db.ApproverChains.Add(c);
        t.Db.SaveChanges();
    }

    [Fact]
    public async Task Leaders_are_my_chain_plus_gm_director_and_bod_level_without_me_or_any_nik()
    {
        using var t = new TestDb();
        var sales = new Department { Id = Guid.NewGuid(), Code = "SLS", Name = "Sales" };
        t.Db.Departments.Add(sales);
        t.Db.SaveChanges();
        var me = Person(t, "L000", "Me Myself", sales);
        var mgr = Person(t, "L001", "Maya Manager", sales);
        var pm = Person(t, "L002", "Pak Plant");
        var gm = Person(t, "L003", "Gita GM");
        var dir = Person(t, "L004", "Dedi Director");
        var lvlGm = Person(t, "L005", "Lina Level");       // GM by the workbook level, in nobody's slot
        var other = Person(t, "L006", "Oscar Other");      // in a chain, but only as a manager of someone else
        var gone = Person(t, "L007", "Gone Boss", active: false);
        var vip = Person(t, "L008", "Vera Vip", sales);
        var vipGone = Person(t, "L009", "Vince Gone", active: false);

        Chain(t, me.Nik, null, (1, mgr.Nik), (6, pm.Nik), (8, gm.Nik), (9, dir.Nik));
        Chain(t, lvlGm.Nik, " general manager ");
        Chain(t, "X1", null, (1, other.Nik), (8, gone.Nik), (9, me.Nik));   // I'm a Director for someone: not my own leader
        Chain(t, "X2", "DIRECTOR");                                          // no iPop account: ignored
        t.Db.UserUiPrefs.Add(new UserUiPref
        {
            Id = Guid.NewGuid(), UserId = me.Id, Key = "notifier",
            Json = JsonSerializer.Serialize(new { vips = new[] { vip.Id, vipGone.Id, Guid.NewGuid() } }), UpdatedAt = DateTime.UtcNow,
        });
        t.Db.SaveChanges();

        NotifierEndpoints.ForgetLeaders(me.Id);
        var r = await NotifierEndpoints.LeadersAsync(t.Verify(), me.Id, DateTime.UtcNow, default);

        Assert.Equal(
            [("Maya Manager", "Manager"), ("Pak Plant", "Plant Manager"), ("Gita GM", "General Manager"), ("Dedi Director", "Director"),
             ("Lina Level", NotifierEndpoints.TopLevelReason)],
            r.Leaders.Select(l => (l.Name, l.Reason)).ToList());
        Assert.Equal("Sales", r.Leaders[0].Department);
        Assert.DoesNotContain(r.Leaders, l => l.Id == me.Id || l.Id == other.Id || l.Id == gone.Id);
        var v = Assert.Single(r.Vips);
        Assert.Equal((vip.Id, "Vera Vip", "Sales"), (v.Id, v.Name, v.Department));

        var json = JsonSerializer.Serialize(r, Web);
        Assert.DoesNotContain("\"L0", json);
        Assert.DoesNotContain("SECRET POSITION", json);
        Assert.DoesNotContain("nik", json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(["leaders", "vips"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "name", "department", "reason"], doc.RootElement.GetProperty("leaders")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "name", "department"], doc.RootElement.GetProperty("vips")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Leaders_are_cached_for_ten_minutes_but_vips_are_always_fresh()
    {
        using var t = new TestDb();
        var me = Person(t, "C000", "Me");
        var mgr = Person(t, "C001", "Mgr");
        Chain(t, me.Nik, null, (1, mgr.Nik));
        NotifierEndpoints.ForgetLeaders(me.Id);
        var now = DateTime.UtcNow;
        Assert.Single((await NotifierEndpoints.LeadersAsync(t.Verify(), me.Id, now, default)).Leaders);

        var gm = Person(t, "C002", "Gm");
        (await t.Db.ApproverChains.SingleAsync(c => c.Nik == me.Nik)).Slot8Nik = gm.Nik;
        t.Db.UserUiPrefs.Add(new UserUiPref { Id = Guid.NewGuid(), UserId = me.Id, Key = "notifier",
            Json = JsonSerializer.Serialize(new { vips = new[] { gm.Id } }), UpdatedAt = now });
        await t.Db.SaveChangesAsync();

        var cached = await NotifierEndpoints.LeadersAsync(t.Verify(), me.Id, now.AddMinutes(9), default);
        Assert.Single(cached.Leaders);
        Assert.Single(cached.Vips);
        Assert.Equal(2, (await NotifierEndpoints.LeadersAsync(t.Verify(), me.Id, now.AddMinutes(11), default)).Leaders.Count);
    }

    [Fact]
    public async Task People_search_by_name_or_email_needs_two_letters_and_never_returns_nik()
    {
        using var t = new TestDb();
        var me = Person(t, "P000", "Budi Me");
        Person(t, "P001", "Budi Santoso");
        Person(t, "P002", "Ani Budiman");
        Person(t, "P003", "Budi Gone", active: false);
        var mail = Person(t, "P004", "Citra");
        mail.Email = "citra.budi@santos.co.id";
        await t.Db.SaveChangesAsync();

        Assert.Empty(await NotifierEndpoints.PeopleAsync(t.Verify(), me.Id, "b", default));
        var found = await NotifierEndpoints.PeopleAsync(t.Verify(), me.Id, " BUDI ", default);
        Assert.Equal(["Ani Budiman", "Budi Santoso", "Citra"], found.Select(p => p.Name));
        var json = JsonSerializer.Serialize(found, Web);
        Assert.DoesNotContain("P00", json);
        Assert.DoesNotContain("SECRET POSITION", json);

        for (var i = 0; i < 20; i++) Person(t, $"Q{i:D3}", $"Zed {i:D2}");
        Assert.Equal(12, (await NotifierEndpoints.PeopleAsync(t.Verify(), me.Id, "zed", default)).Count);
    }

    // ── Convert preview ──────────────────────────────────────────────────────

    private sealed class FakeDocs : IWordDocumentService, IPresentationService, ISpreadsheetService
    {
        public List<string> Calls { get; } = [];
        public bool Fail { get; set; }
        private Task<byte[]> Pdf(string what)
        {
            Calls.Add(what);
            return Fail ? throw new InvalidDataException("broken") : Task.FromResult("%PDF-fake"u8.ToArray());
        }
        public Task<byte[]> ConvertToPdfAsync(Stream s, CancellationToken ct = default) => Pdf("word");
        Task<byte[]> IPresentationService.ConvertToPdfAsync(Stream s, CancellationToken ct) => Pdf("slides");
        Task<byte[]> ISpreadsheetService.ConvertToPdfAsync(Stream s, CancellationToken ct) => Pdf("sheet");
        Task<byte[]> ISpreadsheetService.ConvertToPdfAsync(Stream s, string fileName, CancellationToken ct) => Pdf("sheet" + fileName);
        public Task<byte[]> CreateSampleDocumentAsync(string title, string content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> ExtractTextAsync(Stream s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> ReplaceTextAsync(Stream s, IDictionary<string, string> r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> CreateSamplePresentationAsync(string t, string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetSlideCountAsync(Stream s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> CreateSampleSpreadsheetAsync(string n, IEnumerable<string[]> r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string[]>> ReadSheetDataAsync(Stream s, string? n = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetSheetCountAsync(Stream s, string fileName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Task<(int Status, byte[]? Pdf, string? Message)> Convert(FakeDocs d, string? name, Stream body, long? length = null)
        => NotifierEndpoints.ConvertPreviewAsync(body, length, name, d, d, d, default);

    private static MemoryStream Bytes(int n = 10) => new(Encoding.ASCII.GetBytes(new string('x', n)));

    [Theory]
    [InlineData("Report.docx", "word")]
    [InlineData("old.DOC", "word")]
    [InlineData("memo.rtf", "word")]
    [InlineData("memo.odt", "word")]
    [InlineData("Deck.pptx", "slides")]
    [InlineData("Budget.xlsx", "sheet.xlsx")]
    [InlineData("Budget.xls", "sheet.xls")]
    [InlineData("data.csv", "sheet.csv")]
    public async Task Office_files_convert_to_pdf(string name, string converter)
    {
        var d = new FakeDocs();
        var (status, pdf, _) = await Convert(d, name, Bytes());
        Assert.Equal(200, status);
        Assert.NotNull(pdf);
        Assert.Equal(converter, Assert.Single(d.Calls));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("archive.zip")]
    [InlineData("old.ppt")]
    [InlineData("noext")]
    [InlineData(null)]
    public async Task Other_types_are_415(string? name)
    {
        var d = new FakeDocs();
        Assert.Equal(415, (await Convert(d, name, Bytes())).Status);
        Assert.Empty(d.Calls);
    }

    [Fact]
    public async Task Over_60_mb_is_413_by_header_or_by_the_bytes_actually_sent()
    {
        var d = new FakeDocs();
        Assert.Equal(413, (await Convert(d, "big.docx", Bytes(), NotifierEndpoints.MaxPreviewBytes + 1)).Status);
        using var big = new MemoryStream(new byte[NotifierEndpoints.MaxPreviewBytes + 1]);
        Assert.Equal(413, (await Convert(d, "big.docx", big)).Status);   // no/false Content-Length
        Assert.Empty(d.Calls);
    }

    [Fact]
    public async Task Empty_or_broken_files_do_not_throw()
    {
        var d = new FakeDocs();
        Assert.Equal(400, (await Convert(d, "a.docx", new MemoryStream())).Status);
        d.Fail = true;
        Assert.Equal(422, (await Convert(d, "a.docx", Bytes())).Status);
    }
}
