using IPop.Application.Notes;
using IPop.Application.Notifications;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// A to-do with a person and a due date reminds that person the day before and on the day, once each, in THEIR time zone.
public class NotesReminderWorkerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class RecordingGateway : INotificationGatewayService
    {
        public List<(string Type, string Title, string Message, Guid To)> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("gateway down");
            Sent.Add((eventType, title, message, target.UserId ?? Guid.Empty));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static Task<int> RunAsync(TestDb t, RecordingGateway g, DateTime nowUtc)
        => NotesReminderWorker.RunOnceAsync(t.Verify(), new NoteService(t.Verify()), g, nowUtc, NullLogger.Instance, Ct);

    private static NoteService Notes(TestDb t) => new(t.Verify());

    private static async Task<(Guid Page, Guid Assignee)> SetUpAsync(TestDb t, string? assigneeZone = "Asia/Jakarta")
    {
        var owner = t.AddUser("o");
        var assignee = t.AddUser("a");
        if (assigneeZone is not null)
        {
            var u = await t.Verify().Users.FirstAsync(x => x.Id == assignee.Id);
            u.TimeZoneId = assigneeZone;
            await t.Verify().SaveChangesAsync();
        }
        var page = (await Notes(t).CreateAsync(owner.Id, new CreateNoteRequest(null, "Weekly", null, null, null, null), Ct)).Data!.Id;
        await Notes(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(assignee.Id, null, NoteLevels.Edit), Ct);
        return (page, assignee.Id);
    }

    private static async Task SnapshotAsync(TestDb t, Guid page, Guid by, params NoteTodoDueDto[] items)
    {
        var owner = await t.Verify().NotePages.Where(p => p.Id == page).Select(p => p.OwnerId).FirstAsync();
        var version = await t.Verify().NotePages.Where(p => p.Id == page).Select(p => p.StateVersion).FirstAsync();
        var r = await Notes(t).SnapshotAsync(owner, page, new NoteSnapshotRequest("AQ==", version, [], "x", 0, 0, items), Ct);
        Assert.True(r.Success, r.Error);
    }

    [Fact]
    public async Task Saving_a_page_stores_its_due_dated_todos_and_replaces_them_on_the_next_save()
    {
        using var t = new TestDb();
        var (page, assignee) = await SetUpAsync(t);
        await SnapshotAsync(t, page, assignee, new NoteTodoDueDto("k1", assignee, "2026-12-01", "Order SSD", false), new NoteTodoDueDto("k2", assignee, "2026-12-03", "Call vendor", false));
        Assert.Equal(2, await t.Verify().NoteTodoDues.CountAsync(x => x.PageId == page));

        await SnapshotAsync(t, page, assignee, new NoteTodoDueDto("k1", assignee, "2026-12-01", "Order SSD", true));   // k2 deleted, k1 done
        var left = await t.Verify().NoteTodoDues.Where(x => x.PageId == page).ToListAsync();
        Assert.Single(left);
        Assert.True(left[0].Done);
    }

    [Fact]
    public async Task A_sent_reminder_stays_sent_when_the_page_is_saved_again_but_a_moved_date_starts_over()
    {
        using var t = new TestDb();
        var (page, assignee) = await SetUpAsync(t);
        await SnapshotAsync(t, page, assignee, new NoteTodoDueDto("k1", assignee, "2026-12-01", "Order SSD", false));
        // Verify() hands out a NEW context on each call, so mark the row through one context that tracks it.
        await using (var marker = t.Verify())
        {
            var row = await marker.NoteTodoDues.FirstAsync(x => x.PageId == page);
            row.DayBeforeSentAt = DateTime.UtcNow; row.DaySentAt = DateTime.UtcNow;
            await marker.SaveChangesAsync();
        }

        await SnapshotAsync(t, page, assignee, new NoteTodoDueDto("k1", assignee, "2026-12-01", "Order SSD (edited text)", false));
        var same = await t.Verify().NoteTodoDues.FirstAsync(x => x.PageId == page);
        Assert.NotNull(same.DayBeforeSentAt);
        Assert.NotNull(same.DaySentAt);

        await SnapshotAsync(t, page, assignee, new NoteTodoDueDto("k1", assignee, "2026-12-09", "Order SSD", false));
        var moved = await t.Verify().NoteTodoDues.FirstAsync(x => x.PageId == page);
        Assert.Null(moved.DayBeforeSentAt);
        Assert.Null(moved.DaySentAt);
    }

    [Fact]
    public async Task Bad_input_is_ignored_not_stored()
    {
        using var t = new TestDb();
        var (page, assignee) = await SetUpAsync(t);
        await SnapshotAsync(t, page, assignee,
            new NoteTodoDueDto("ok", assignee, "2026-12-01", "fine", false),
            new NoteTodoDueDto("bad-date", assignee, "tomorrow", "x", false),
            new NoteTodoDueDto("", assignee, "2026-12-01", "no key", false),
            new NoteTodoDueDto(new string('k', 65), assignee, "2026-12-01", "long key", false));
        Assert.Equal(["ok"], await t.Verify().NoteTodoDues.Where(x => x.PageId == page).Select(x => x.ItemKey).ToListAsync());
    }

    [Fact]
    public void Time_zone_lookup_falls_back_to_Jakarta_then_UTC_and_never_throws()
    {
        Assert.NotNull(NotesReminderWorker.ResolveTimeZone("Asia/Jakarta"));
        Assert.NotNull(NotesReminderWorker.ResolveTimeZone("Not/AZone"));
        Assert.NotNull(NotesReminderWorker.ResolveTimeZone(null));
    }

    [Fact]
    public void The_local_day_rule_in_the_assignees_zone_decides_tomorrow_versus_today()
    {
        // 2026-12-01 23:30 in Jakarta (UTC+7) is 16:30 UTC the same day; a to-do due 2026-12-02 is "tomorrow" there, while
        // for someone in UTC-8 the same instant is still 2026-12-01 08:30, so a 2026-12-02 item is also "tomorrow".
        // At 2026-12-01 17:30 UTC it is already 2026-12-02 00:30 in Jakarta: the same item is "today" there, "tomorrow" in UTC.
        var jakarta = NotesReminderWorker.ResolveTimeZone("Asia/Jakarta");
        var utc = TimeZoneInfo.Utc;
        var instant = new DateTime(2026, 12, 1, 17, 30, 0, DateTimeKind.Utc);
        var due = new DateOnly(2026, 12, 2);
        Assert.Equal(0, due.DayNumber - DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instant, jakarta)).DayNumber);
        Assert.Equal(1, due.DayNumber - DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instant, utc)).DayNumber);
    }

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_day_before_and_the_day_each_send_once_and_never_again()
    {
        using var t = new TestDb();
        var (page, who) = await SetUpAsync(t);
        await SnapshotAsync(t, page, who, new NoteTodoDueDto("k", who, "2026-12-02", "Order SSD", false));
        var g = new RecordingGateway();

        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 11, 29, 3)));                       // 2 days before: nothing
        Assert.Equal(1, await RunAsync(t, g, Utc(2026, 12, 1, 1)));                        // 08:00 Jakarta on 1 Dec = the day before
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 1, 9)));                        // same day again: not repeated
        Assert.Equal(1, await RunAsync(t, g, Utc(2026, 12, 2, 1)));                        // the day itself
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 2, 10)));
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 3, 1)));                        // overdue: nothing
        Assert.Equal(["To-do due tomorrow", "To-do due today"], g.Sent.Select(x => x.Title).ToArray());
        Assert.All(g.Sent, x => Assert.Equal(who, x.To));
        Assert.Contains("Order SSD", g.Sent[0].Message);
        Assert.Contains("Weekly", g.Sent[0].Message);
    }

    [Fact]
    public async Task The_assignees_own_time_zone_decides_when_the_day_starts()
    {
        using var t = new TestDb();
        var (page, who) = await SetUpAsync(t, "Asia/Jakarta");
        await SnapshotAsync(t, page, who, new NoteTodoDueDto("k", who, "2026-12-02", "Call", false));
        var g = new RecordingGateway();
        // 2026-12-01 17:30 UTC is already 2 Dec 00:30 in Jakarta: the item is due TODAY there (UTC would still say tomorrow).
        Assert.Equal(1, await RunAsync(t, g, Utc(2026, 12, 1, 17, 30)));
        Assert.Equal("To-do due today", g.Sent[0].Title);
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 2, 1)));                        // and "tomorrow" is never sent after "today"
    }

    [Fact]
    public async Task Done_items_unassigned_items_people_who_lost_access_and_trashed_pages_get_nothing()
    {
        using var t = new TestDb();
        var (page, who) = await SetUpAsync(t);
        var owner = await t.Verify().NotePages.Where(p => p.Id == page).Select(p => p.OwnerId).FirstAsync();
        await SnapshotAsync(t, page, who,
            new NoteTodoDueDto("done", who, "2026-12-02", "Finished", true),
            new NoteTodoDueDto("nobody", null, "2026-12-02", "No person", false),
            new NoteTodoDueDto("live", who, "2026-12-02", "Live", false));
        var g = new RecordingGateway();

        // the assignee loses access to the page
        var share = (await Notes(t).GetSharesAsync(owner, page, Ct)).Data!.Shares.Single();
        await Notes(t).RemoveShareAsync(owner, page, share.Id, Ct);
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 1, 1)));

        // access back, but the page is in Trash
        await Notes(t).AddShareAsync(owner, page, new AddNoteShareRequest(who, null, NoteLevels.Edit), Ct);
        await Notes(t).TrashAsync(owner, page, Ct);
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 1, 1)));

        // restored: only the live, assigned, open item is reminded
        await Notes(t).RestoreAsync(owner, page, Ct);
        Assert.Equal(1, await RunAsync(t, g, Utc(2026, 12, 1, 1)));
        Assert.Equal("Live", g.Sent.Single().Message.Split('“', '”')[1]);
    }

    [Fact]
    public async Task A_failed_publish_is_not_marked_sent_and_is_retried_on_the_next_pass()
    {
        using var t = new TestDb();
        var (page, who) = await SetUpAsync(t);
        await SnapshotAsync(t, page, who, new NoteTodoDueDto("k", who, "2026-12-02", "Order SSD", false));
        var g = new RecordingGateway { Fail = true };
        Assert.Equal(0, await RunAsync(t, g, Utc(2026, 12, 1, 1)));
        Assert.Null((await t.Verify().NoteTodoDues.FirstAsync()).DayBeforeSentAt);
        g.Fail = false;
        Assert.Equal(1, await RunAsync(t, g, Utc(2026, 12, 1, 1, 10)));
        Assert.NotNull((await t.Verify().NoteTodoDues.FirstAsync()).DayBeforeSentAt);
    }
}
