using IPop.Application.Mail;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class MassMailTests
{
    private static List<User> AddUsers(TestDb t, string prefix, int n, bool active = true)
    {
        var now = DateTime.UtcNow;
        var users = Enumerable.Range(0, n).Select(i => new User
        {
            Id = Guid.NewGuid(), Nik = $"{prefix}{i:D4}", Name = $"{prefix} {i:D4}", Email = $"{prefix}{i}@t.co",
            IsActive = active, CreatedAt = now, UpdatedAt = now,
        }).ToList();
        t.Db.Users.AddRange(users);
        t.Db.SaveChanges();
        return users;
    }

    private static DirGroup AddShared(TestDb t, string name, IEnumerable<User> members)
    {
        var g = new DirGroup { Id = Guid.NewGuid(), Name = name };
        t.Db.DirGroups.Add(g);
        foreach (var u in members) t.Db.Add(new UserDirGroup { UserId = u.Id, DirGroupId = g.Id });
        t.Db.SaveChanges();
        return g;
    }

    private static MailGroup AddPersonal(TestDb t, User owner, string name, IEnumerable<User> members)
    {
        var g = new MailGroup { Id = Guid.NewGuid(), OwnerId = owner.Id, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.MailGroups.Add(g);
        foreach (var u in members) t.Db.MailGroupMembers.Add(new MailGroupMember { GroupId = g.Id, UserId = u.Id });
        t.Db.SaveChanges();
        return g;
    }

    private static ComposeMailRequest Req(IReadOnlyList<string> to, IReadOnlyList<MailGroupRef>? groups = null,
        int? confirmed = null, bool hide = false, Guid? inReplyTo = null, IReadOnlyList<string>? cc = null)
        => new("Hello", "<p>x</p>", to, cc ?? [], [], InReplyToId: inReplyTo, Groups: groups, HideRecipients: hide, ConfirmedRecipientCount: confirmed);

    [Fact]
    public async Task Limit_counts_people_after_dedupe_across_fields_and_groups()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var people = AddUsers(t, "P", 1001);
        var g = AddShared(t, "Everyone", people.Take(1000));
        var svc = new MailService(t.Verify());

        // 1000 via the group + the same 1000 typed in Cc → still 1000 people.
        var ok = await svc.SendAsync(a.Id, Req([], [new("to", "shared", g.Id)], confirmed: 1000,
            cc: people.Take(1000).Select(p => p.Nik).ToList()), default);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(1000, ok.RecipientCount);

        // One more distinct person tips it over.
        var tooMany = await svc.SendAsync(a.Id, Req([people[1000].Nik], [new("to", "shared", g.Id)], confirmed: 1001), default);
        Assert.False(tooMany.Success);
        Assert.Equal("too_many_recipients", tooMany.Error);
        Assert.Equal(1001, tooMany.RecipientCount);
    }

    [Fact]
    public async Task Above_50_requires_matching_confirmed_count()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var niks = AddUsers(t, "P", 51).Select(p => p.Nik).ToList();
        var svc = new MailService(t.Verify());

        var none = await svc.SendAsync(a.Id, Req(niks), default);
        Assert.Equal("confirm_required", none.Error);
        Assert.Equal(51, none.RecipientCount);
        var wrong = await svc.SendAsync(a.Id, Req(niks, confirmed: 50), default);
        Assert.Equal("confirm_required", wrong.Error);
        var ok = await svc.SendAsync(a.Id, Req(niks, confirmed: 51), default);
        Assert.True(ok.Success, ok.Error);

        // Exactly 50 needs no confirmation.
        var fifty = await svc.SendAsync(a.Id, Req(niks.Take(50).ToList()), default);
        Assert.True(fifty.Success, fifty.Error);
    }

    [Fact]
    public async Task Group_expansion_skips_inactive_members_and_reports_them()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var active = AddUsers(t, "P", 3);
        var gone = t.AddUser("GONE", active: false);
        var g = AddShared(t, "HSE", active.Append(gone));
        var svc = new MailService(t.Verify());

        var preview = await svc.PreviewRecipientsAsync(a.Id, Req([], [new("to", "shared", g.Id)]), default);
        Assert.Equal(3, preview.Total);
        Assert.Equal("HSE", Assert.Single(preview.Groups).Name);
        Assert.Equal("GONE", Assert.Single(preview.Skipped).Nik);

        var res = await svc.SendAsync(a.Id, Req([], [new("to", "shared", g.Id)]), default);
        Assert.True(res.Success, res.Error);
        Assert.Equal(3, res.RecipientCount);
        var db = t.Verify();
        Assert.False(await db.MailRecipients.AnyAsync(r => r.UserId == gone.Id));
        Assert.True(await db.MailRecipients.Where(r => r.MailId == res.MailId && r.RecipientType == "to").AllAsync(r => r.ViaGroup));
        var stats = await svc.GetStatsAsync(a.Id, res.MailId!.Value, default);
        Assert.Equal("GONE", Assert.Single(stats!.Skipped).Nik);
    }

    [Fact]
    public async Task Typed_inactive_nik_still_fails()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var gone = t.AddUser("GONE", active: false);
        var res = await new MailService(t.Verify()).SendAsync(a.Id, Req([gone.Nik]), default);
        Assert.False(res.Success);
        Assert.Equal("recipient_not_found", res.Error);
    }

    [Fact]
    public async Task Preview_reports_individuals_duplicates_and_invalid()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var p = AddUsers(t, "P", 4);
        var g = AddShared(t, "Team", p.Take(3));
        var preview = await new MailService(t.Verify()).PreviewRecipientsAsync(a.Id,
            Req([p[0].Nik, p[3].Nik, "NOPE"], [new("cc", "shared", g.Id)]), default);
        Assert.Equal(4, preview.Total);
        Assert.Equal(2, preview.Individuals);
        Assert.Equal(1, preview.Duplicates);
        Assert.Contains("NOPE", preview.Invalid);
        Assert.Equal(3, Assert.Single(preview.Groups).Count);
        Assert.False(preview.NeedsConfirm);
    }

    [Fact]
    public async Task Personal_group_is_usable_only_by_its_owner()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var p = AddUsers(t, "P", 2);
        var mine = AddPersonal(t, a, "Mine", p);
        var svc = new MailService(t.Verify());

        Assert.True((await svc.SendAsync(a.Id, Req([], [new("to", "personal", mine.Id)]), default)).Success);
        var other = await svc.SendAsync(b.Id, Req([], [new("to", "personal", mine.Id)]), default);
        Assert.Equal("group_not_found", other.Error);
    }

    [Fact]
    public async Task Mass_mail_blocks_reply_all_for_recipients_but_not_the_sender()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var p = AddUsers(t, "P", 3);
        var g = AddShared(t, "Team", p);
        var svc = new MailService(t.Verify());
        var sent = await svc.SendAsync(a.Id, Req([], [new("to", "shared", g.Id)]), default);
        var id = sent.MailId!.Value;

        var detail = await svc.GetAsync(p[0].Id, id, default);
        Assert.True(detail!.IsMass);
        Assert.False(detail.CanReplyAll);
        Assert.True((await svc.GetAsync(a.Id, id, default))!.CanReplyAll);

        // Reply to the sender only is fine; widening it is blocked.
        Assert.True((await svc.SendAsync(p[0].Id, Req([a.Nik], inReplyTo: id), default)).Success);
        var all = await svc.SendAsync(p[0].Id, Req([a.Nik, p[1].Nik], inReplyTo: id), default);
        Assert.Equal("reply_all_blocked", all.Error);
        var viaGroup = await svc.SendAsync(p[0].Id, Req([a.Nik], [new("to", "shared", g.Id)], inReplyTo: id), default);
        Assert.Equal("reply_all_blocked", viaGroup.Error);

        // The sender can still write to everyone.
        Assert.True((await svc.SendAsync(a.Id, Req([], [new("to", "shared", g.Id)], inReplyTo: id), default)).Success);
    }

    [Fact]
    public async Task Hide_recipients_shows_each_recipient_only_sender_and_self()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var p = AddUsers(t, "P", 3);
        var svc = new MailService(t.Verify());
        var sent = await svc.SendAsync(a.Id, Req(p.Select(x => x.Nik).ToList(), hide: true), default);
        var id = sent.MailId!.Value;

        var mine = await svc.GetAsync(p[1].Id, id, default);
        Assert.True(mine!.HideRecipients);
        Assert.Equal(1, mine.RecipientTotal);
        Assert.DoesNotContain(mine.Recipients, r => r.UserId == p[0].Id || r.UserId == p[2].Id);
        Assert.False(mine.CanReplyAll);

        var page = await svc.GetRecipientsAsync(p[1].Id, id, 1, 50, default);
        Assert.Equal(p[1].Id, Assert.Single(page!.Items).UserId);

        var asSender = await svc.GetAsync(a.Id, id, default);
        Assert.Equal(3, asSender!.RecipientTotal);
    }

    [Fact]
    public async Task Detail_caps_names_and_pages_the_rest()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var niks = AddUsers(t, "P", 120).Select(p => p.Nik).ToList();
        var svc = new MailService(t.Verify());
        var sent = await svc.SendAsync(a.Id, Req(niks, confirmed: 120), default);
        Assert.True(sent.Success, sent.Error);

        var detail = await svc.GetAsync(a.Id, sent.MailId!.Value, default);
        Assert.Equal(120, detail!.RecipientTotal);
        Assert.True(detail.Recipients.Count <= MailLimits.DetailRecipients);
        Assert.Empty(detail.DeliveryState);

        var page3 = await svc.GetRecipientsAsync(a.Id, sent.MailId.Value, 3, 50, default);
        Assert.Equal(120, page3!.TotalCount);
        Assert.Equal(20, page3.Items.Count);
    }

    [Fact]
    public async Task Reopened_draft_returns_every_typed_recipient()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var niks = AddUsers(t, "P", 120).Select(p => p.Nik).ToList();
        var svc = new MailService(t.Verify());
        var saved = await svc.SaveDraftAsync(a.Id, Req(niks), default);
        Assert.True(saved.Success, saved.Error);

        // The composer rebuilds the To field from this; capping it would drop people on the next save.
        var draft = await svc.GetAsync(a.Id, saved.MailId!.Value, default);
        Assert.Equal(120, draft!.Recipients.Count(r => r.RecipientType == "to"));
    }

    [Fact]
    public async Task Stats_and_readers_are_sender_only_and_paged()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var p = AddUsers(t, "P", 60);
        var svc = new MailService(t.Verify());
        var sent = await svc.SendAsync(a.Id, Req(p.Select(x => x.Nik).ToList(), confirmed: 60), default);
        var id = sent.MailId!.Value;
        foreach (var u in p.Take(5)) await new MailService(t.Verify()).MarkReadAsync(u.Id, id, default);

        var stats = await new MailService(t.Verify()).GetStatsAsync(a.Id, id, default);
        Assert.Equal(60, stats!.Sent);
        Assert.Equal(60, stats.Delivered);
        Assert.Equal(5, stats.Read);

        var read = await svc.GetReadersAsync(a.Id, id, true, 1, 50, default);
        Assert.Equal(5, read!.TotalCount);
        var unread = await svc.GetReadersAsync(a.Id, id, false, 2, 50, default);
        Assert.Equal(55, unread!.TotalCount);
        Assert.Equal(5, unread.Items.Count);

        Assert.Null(await svc.GetStatsAsync(p[0].Id, id, default));
        Assert.Null(await svc.GetReadersAsync(p[0].Id, id, true, 1, 50, default));
    }

    private sealed class FlakyGateway : INotificationGatewayService
    {
        public int Failures { get; set; }
        public int Calls { get; private set; }
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Calls++;
            if (Failures-- > 0) throw new InvalidOperationException("gateway down");
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Notify_job_retries_with_backoff_until_the_gateway_recovers()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var gw = new FlakyGateway { Failures = 1 };
        var sent = await new MailService(t.Verify(), gw).SendAsync(a.Id, Req([b.Nik]), default);
        Assert.Equal(0, gw.Calls); // nothing inside the request

        Assert.Equal(1, await new MailService(t.Verify(), gw).ProcessNotifyJobsAsync(default));
        var job = await t.Verify().MailNotifyJobs.SingleAsync(j => j.MailId == sent.MailId);
        Assert.Null(job.DoneAt);
        Assert.Equal(1, job.Attempts);
        Assert.Contains("gateway down", job.LastError);
        Assert.True(job.NextAttemptAt > DateTime.UtcNow);

        // Not due yet → nothing picked up; once due, it succeeds.
        Assert.Equal(0, await new MailService(t.Verify(), gw).ProcessNotifyJobsAsync(default));
        await t.Verify().MailNotifyJobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        Assert.Equal(1, await new MailService(t.Verify(), gw).ProcessNotifyJobsAsync(default));
        job = await t.Verify().MailNotifyJobs.SingleAsync(j => j.MailId == sent.MailId);
        Assert.NotNull(job.DoneAt);
        Assert.Equal(2, gw.Calls);
    }
}
