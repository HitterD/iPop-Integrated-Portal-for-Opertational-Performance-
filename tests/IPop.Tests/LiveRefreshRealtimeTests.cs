using System.Collections.Concurrent;
using IPop.Api.Endpoints;
using IPop.Application.Mail;
using IPop.Application.News;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.News;
using Xunit;

namespace IPop.Tests;

/// Live refresh: Mail, News, Calendar and Meetings push a "changed" hint over the hub so open
/// pages reload in place. These pin who receives each event, and that a hub failure never
/// fails the write.
public class LiveRefreshRealtimeTests
{
    private sealed record Sent(string Target, string Event, object? Payload);

    private sealed class RecordingRealtime(bool fail = false) : IRealtime
    {
        public ConcurrentQueue<Sent> Sends { get; } = new();

        private Task Record(string target, string evt, object? payload)
        {
            if (fail) return Task.FromException(new InvalidOperationException("hub down"));
            Sends.Enqueue(new Sent(target, evt, payload));
            return Task.CompletedTask;
        }

        public Task ToAllAsync(string evt, object? payload = null, CancellationToken ct = default) => Record("*", evt, payload);
        public Task ToUserAsync(string nik, string evt, object? payload = null, CancellationToken ct = default) => Record(nik, evt, payload);
        public async Task ToUsersAsync(IEnumerable<string> niks, string evt, object? payload = null, CancellationToken ct = default)
        {
            foreach (var n in niks) await Record(n, evt, payload);
        }
        public Task ToGroupAsync(string group, string evt, object? payload = null, CancellationToken ct = default) => Record("group:" + group, evt, payload);

        public bool Got(string target, string evt) => Sends.Any(s => s.Target == target && s.Event == evt);
        public int? MailBadge(string nik) => Sends.LastOrDefault(s => s.Target == nik && s.Event == RealtimeEvents.BadgeChanged
            && s.Payload is RealtimeArgs a && Equals(a.Values[0], "mail"))?.Payload is RealtimeArgs r ? (int?)r.Values[1] : null;
    }

    [Fact]
    public async Task Mail_send_pushes_mailChanged_to_recipient_and_sender()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var rt = new RecordingRealtime();

        var res = await new MailService(t.Verify(), null, rt).SendAsync(a.Id,
            new ComposeMailRequest("Hi", "<p>x</p>", [b.Nik], [], []), default);

        Assert.True(res.Success, res.Error);
        Assert.True(rt.Got("B", RealtimeEvents.MailChanged));
        Assert.True(rt.Got("A", RealtimeEvents.MailChanged));
        Assert.Equal(1, rt.MailBadge("B"));
    }

    [Fact]
    public async Task Mail_markRead_pushes_mailChanged_and_fresh_badge_to_actor()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var sent = await new MailService(t.Verify()).SendAsync(a.Id,
            new ComposeMailRequest("Hi", "<p>x</p>", [b.Nik], [], []), default);
        var rt = new RecordingRealtime();

        var res = await new MailService(t.Verify(), null, rt).MarkReadAsync(b.Id, sent.MailId!.Value, default);

        Assert.True(res.Success, res.Error);
        Assert.True(rt.Got("B", RealtimeEvents.MailChanged));
        Assert.Equal(0, rt.MailBadge("B"));
        Assert.False(rt.Got("A", RealtimeEvents.MailChanged));
    }

    [Fact]
    public async Task Mail_hub_failure_never_fails_the_write()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new MailService(t.Verify(), null, new RecordingRealtime(fail: true));

        var sent = await svc.SendAsync(a.Id, new ComposeMailRequest("Hi", "<p>x</p>", [b.Nik], [], []), default);
        Assert.True(sent.Success, sent.Error);
        var read = await new MailService(t.Verify(), null, new RecordingRealtime(fail: true)).MarkReadAsync(b.Id, sent.MailId!.Value, default);
        Assert.True(read.Success, read.Error);
    }

    [Fact]
    public async Task News_writes_broadcast_newsChanged_to_all()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var rt = new RecordingRealtime();

        var created = await new NewsService(t.Verify(), null, null, rt).CreateAsync(hr.Id,
            new CreateNewsRequest("news", "Title", "Body"), default);
        Assert.True(created.Success, created.Error);
        Assert.True(rt.Got("*", RealtimeEvents.NewsChanged));

        foreach (var step in new Func<NewsService, Task<bool>>[]
        {
            async s => (await s.PublishAsync(hr.Id, created.News!.Id, default)).Success,
            async s => (await s.PinAsync(hr.Id, created.News!.Id, default)).Success,
            async s => (await s.DeleteAsync(hr.Id, created.News!.Id, default)).Success,
        })
        {
            var stepRt = new RecordingRealtime();
            Assert.True(await step(new NewsService(t.Verify(), null, null, stepRt)));
            Assert.True(stepRt.Got("*", RealtimeEvents.NewsChanged));
        }
    }

    [Fact]
    public async Task News_hub_failure_never_fails_the_write()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var created = await new NewsService(t.Verify(), null, null, new RecordingRealtime(fail: true)).CreateAsync(hr.Id,
            new CreateNewsRequest("news", "Title", "Body", Mode: "publish"), default);
        Assert.True(created.Success, created.Error);
    }

    [Fact]
    public async Task Meeting_change_reaches_organizer_attendees_and_removed_users()
    {
        using var t = new TestDb();
        var org = t.AddUser("ORG");
        var att = t.AddUser("ATT");
        var gone = t.AddUser("GONE");
        var other = t.AddUser("OTHER");
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(), Title = "Sync", StartAt = DateTime.UtcNow.AddHours(1), EndAt = DateTime.UtcNow.AddHours(2),
            OrganizerId = org.Id, CreatedAt = DateTime.UtcNow,
        };
        t.Db.Meetings.Add(meeting);
        t.Db.MeetingAttendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = att.Id });
        await t.Db.SaveChangesAsync();
        var rt = new RecordingRealtime();

        await MeetingEndpoints.NotifyMeetingChangedAsync(t.Verify(), rt, meeting.Id, [gone.Id], default);

        foreach (var nik in new[] { "ORG", "ATT", "GONE" })
        {
            Assert.Contains(rt.Sends, s => s.Target == nik && s.Event == RealtimeEvents.MeetingChanged && Equals(s.Payload, meeting.Id));
            Assert.True(rt.Got(nik, RealtimeEvents.CalendarChanged));
        }
        Assert.DoesNotContain(rt.Sends, s => s.Target == "OTHER");

        // Hub failure is swallowed.
        await MeetingEndpoints.NotifyMeetingChangedAsync(t.Verify(), new RecordingRealtime(fail: true), meeting.Id, [], default);
    }

    [Fact]
    public async Task Personal_calendar_event_notifies_only_its_owner()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        t.AddUser("OTHER");
        var rt = new RecordingRealtime();

        await CalendarEndpoints.NotifyChangedAsync(t.Verify(), rt, null, [owner.Id], default);

        Assert.True(rt.Got("OWN", RealtimeEvents.CalendarChanged));
        Assert.DoesNotContain(rt.Sends, s => s.Target == "OTHER" || s.Event == RealtimeEvents.MeetingChanged);
    }
}
