using IPop.Application.Notes;
using IPop.Application.Notifications;
using IPop.Infrastructure.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// Repeating pages: one copy per rule per local day, never before 06:00 Jakarta, never back-filled, never for a trashed template, and only
/// the people who can open the new page are told.
public class NotesRecurrenceWorkerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NoteService Svc(TestDb t) => new(t.Verify());

    private sealed class RecordingGateway : INotificationGatewayService
    {
        public List<(string Type, string Title, string Message, Guid To, string? Url)> Sent { get; } = [];
        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Sent.Add((eventType, title, message, target.UserId ?? Guid.Empty, targetUrl));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }
        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    // Jakarta is UTC+7: 2026-11-24 (a Tuesday) at 06:30 local = 23:30 UTC the day before
    private static DateTime Jkt(int day, int hour, int minute = 0) => new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-7);

    private static Task<int> Run(TestDb t, RecordingGateway g, DateTime utcNow)
        => NotesRecurrenceWorker.RunOnceAsync(t.Verify(), Svc(t), g, utcNow, NullLogger.Instance, Ct);

    private static async Task<(Guid Owner, Guid Reader, Guid Folder, Guid Template)> SetUpAsync(TestDb t, string rule = "FREQ=DAILY", int startDay = 23)
    {
        var owner = t.AddUser("rr1"); var reader = t.AddUser("rr2");
        var folder = (await Svc(t).CreateAsync(owner.Id, new CreateNoteRequest(null, "Meetings", null, null, null, null), Ct)).Data!.Id;
        await Svc(t).AddShareAsync(owner.Id, folder, new AddNoteShareRequest(reader.Id, null, NoteLevels.View), Ct);
        var template = (await Svc(t).CreateAsync(owner.Id, new CreateNoteRequest(folder, "Weekly ICT", "🗓", null, null, null), Ct)).Data!.Id;
        t.Db.NoteRecurrences.Add(new IPop.Core.Entities.NoteRecurrence
        {
            Id = Guid.NewGuid(), PageId = template, OwnerId = owner.Id, Rule = rule, StartsOn = new DateOnly(2026, 11, startDay), CreatedAt = DateTime.UtcNow,
        });
        t.Db.SaveChanges();
        return (owner.Id, reader.Id, folder, template);
    }

    [Fact]
    public async Task A_due_rule_makes_one_page_next_to_the_template_titled_with_the_date_and_tells_the_people_who_can_open_it()
    {
        using var t = new TestDb();
        var (owner, reader, folder, template) = await SetUpAsync(t);
        var g = new RecordingGateway();

        Assert.Equal(1, await Run(t, g, Jkt(24, 6, 30)));

        var page = t.Verify().NotePages.Single(p => p.Title == "Weekly ICT — 24 Nov 2026");
        Assert.Equal(folder, page.ParentId);
        Assert.Equal(owner, page.OwnerId);
        Assert.Equal("🗓", page.Emoji);
        Assert.NotEqual(template, page.Id);
        var notice = Assert.Single(g.Sent);
        Assert.Equal(reader, notice.To);                       // the owner is not told about their own page
        Assert.Equal("note.recurring_page", notice.Type);
        Assert.Equal($"/notes/{page.Id}", notice.Url);
        var rule = t.Verify().NoteRecurrences.Single();
        Assert.Equal(new DateOnly(2026, 11, 24), rule.LastCreatedFor);
        Assert.Equal(page.Id, rule.LastPageId);
    }

    [Fact]
    public async Task Running_again_the_same_day_makes_nothing_more()
    {
        using var t = new TestDb();
        await SetUpAsync(t);
        var g = new RecordingGateway();
        Assert.Equal(1, await Run(t, g, Jkt(24, 6, 30)));
        Assert.Equal(0, await Run(t, g, Jkt(24, 6, 31)));
        Assert.Equal(0, await Run(t, g, Jkt(24, 20)));
        Assert.Equal(2, t.Verify().NotePages.Count(p => p.Title.StartsWith("Weekly ICT")));   // the template and one page
        Assert.Single(g.Sent);
    }

    [Fact]
    public async Task Nothing_is_made_before_06_00_Jakarta_time()
    {
        using var t = new TestDb();
        await SetUpAsync(t);
        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(24, 5, 59)));
        Assert.Equal(1, await Run(t, new RecordingGateway(), Jkt(24, 6, 0)));
    }

    [Fact]
    public async Task Missed_days_are_not_back_filled_and_the_start_day_is_respected()
    {
        using var t = new TestDb();
        await SetUpAsync(t, startDay: 24);
        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(23, 9)));        // before the rule was set up
        Assert.Equal(1, await Run(t, new RecordingGateway(), Jkt(27, 9)));        // days 24..26 were missed: only today's page is made
        Assert.Single(t.Verify().NotePages.Where(p => p.Title.Contains("— ")));
        Assert.Equal("Weekly ICT — 27 Nov 2026", t.Verify().NotePages.Single(p => p.Title.Contains("— ")).Title);
    }

    [Fact]
    public async Task A_workday_rule_skips_the_weekend()
    {
        using var t = new TestDb();
        await SetUpAsync(t, rule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", startDay: 23);
        Assert.Equal(1, await Run(t, new RecordingGateway(), Jkt(27, 9)));        // Friday
        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(28, 9)));        // Saturday
        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(29, 9)));        // Sunday
        Assert.Equal(1, await Run(t, new RecordingGateway(), Jkt(30, 9)));        // Monday
    }

    [Fact]
    public async Task A_trashed_template_makes_nothing_and_its_rule_is_switched_off()
    {
        using var t = new TestDb();
        var (owner, _, _, template) = await SetUpAsync(t);
        Assert.True((await Svc(t).TrashAsync(owner, template, Ct)).Success);
        var g = new RecordingGateway();
        Assert.Equal(0, await Run(t, g, Jkt(24, 9)));
        Assert.Empty(g.Sent);
        Assert.False(t.Verify().NoteRecurrences.Single().Enabled);
        Assert.DoesNotContain(t.Verify().NotePages.ToList(), p => p.Title.Contains("— "));
    }

    [Fact]
    public async Task A_person_who_lost_edit_access_to_the_template_makes_nothing()
    {
        using var t = new TestDb();
        var owner = t.AddUser("rr3"); var editor = t.AddUser("rr4");
        var template = (await Svc(t).CreateAsync(owner.Id, new CreateNoteRequest(null, "Weekly", null, null, null, null), Ct)).Data!.Id;
        await Svc(t).AddShareAsync(owner.Id, template, new AddNoteShareRequest(editor.Id, null, NoteLevels.Edit), Ct);
        Assert.True((await Svc(t).SetRecurrenceAsync(editor.Id, template, new SetNoteRecurrenceRequest("daily", null, null), new DateOnly(2026, 11, 23), Ct)).Success);

        var share = t.Db.NoteShares.Single(s => s.PageId == template && s.UserId == editor.Id);
        t.Db.NoteShares.Remove(share); t.Db.SaveChanges();

        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(24, 9)));
        Assert.DoesNotContain(t.Verify().NotePages.ToList(), p => p.Title.Contains("— "));
    }

    [Fact]
    public async Task A_disabled_rule_makes_nothing()
    {
        using var t = new TestDb();
        var (owner, _, _, template) = await SetUpAsync(t);
        Assert.True((await Svc(t).StopRecurrenceAsync(owner, template, Ct)).Success);
        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(24, 9)));
        Assert.Null((await Svc(t).GetRecurrenceAsync(owner, template, Ct)).Data);
    }

    [Fact]
    public async Task The_copy_keeps_the_content_and_files_of_the_template_and_is_a_normal_page()
    {
        using var t = new TestDb();
        var (owner, _, _, template) = await SetUpAsync(t);
        var tpl = t.Db.NotePages.Single(p => p.Id == template);
        tpl.YState = [1, 2, 3, 4]; tpl.PlainText = "Agenda: status k3s"; tpl.Excerpt = "Agenda: status k3s"; t.Db.SaveChanges();

        await Run(t, new RecordingGateway(), Jkt(24, 9));

        var copy = t.Verify().NotePages.Single(p => p.Title.Contains("— "));
        Assert.Equal([1, 2, 3, 4], copy.YState);
        Assert.Equal("Agenda: status k3s", copy.Excerpt);
        Assert.Equal(owner, copy.OwnerId);
        // and it opens like any page for its owner
        Assert.True((await Svc(t).GetPageAsync(owner, copy.Id, Ct)).Success);
    }

    [Fact]
    public async Task More_due_rules_than_one_batch_are_all_made_in_a_single_pass()
    {
        using var t = new TestDb();
        var owner = t.AddUser("rr8");
        for (var i = 0; i < 205; i++)
        {
            var page = (await Svc(t).CreateAsync(owner.Id, new CreateNoteRequest(null, $"T{i}", null, null, null, null), Ct)).Data!.Id;
            t.Db.NoteRecurrences.Add(new IPop.Core.Entities.NoteRecurrence
            {
                Id = Guid.NewGuid(), PageId = page, OwnerId = owner.Id, Rule = "FREQ=DAILY", StartsOn = new DateOnly(2026, 11, 23), CreatedAt = DateTime.UtcNow.AddSeconds(i),
            });
        }
        t.Db.SaveChanges();

        // rows that were just claimed leave the query, so the second batch must not skip over the ones that follow
        Assert.Equal(205, await Run(t, new RecordingGateway(), Jkt(24, 9)));
        Assert.Equal(0, await Run(t, new RecordingGateway(), Jkt(24, 10)));
    }

    // ── set up / stop through the service ──

    [Fact]
    public async Task Setting_a_repeat_needs_edit_access_a_valid_rule_and_a_normal_page()
    {
        using var t = new TestDb();
        var owner = t.AddUser("rr5"); var viewer = t.AddUser("rr6");
        var page = (await Svc(t).CreateAsync(owner.Id, new CreateNoteRequest(null, "P", null, null, null, null), Ct)).Data!.Id;
        var board = (await Svc(t).CreateAsync(owner.Id, new CreateNoteRequest(null, "B", null, null, null, null, "board"), Ct)).Data!.Id;
        await Svc(t).AddShareAsync(owner.Id, page, new AddNoteShareRequest(viewer.Id, null, NoteLevels.View), Ct);
        var today = new DateOnly(2026, 11, 23);

        Assert.False((await Svc(t).SetRecurrenceAsync(viewer.Id, page, new SetNoteRecurrenceRequest("daily", null, null), today, Ct)).Success);
        Assert.Equal("invalid_rule", (await Svc(t).SetRecurrenceAsync(owner.Id, page, new SetNoteRecurrenceRequest("hourly", null, null), today, Ct)).Error);
        Assert.Equal("not_a_page", (await Svc(t).SetRecurrenceAsync(owner.Id, board, new SetNoteRecurrenceRequest("daily", null, null), today, Ct)).Error);

        var ok = await Svc(t).SetRecurrenceAsync(owner.Id, page, new SetNoteRecurrenceRequest("weekly", ["MO", "WE"], null), today, Ct);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal("Every Monday and Wednesday", ok.Data!.Description);
        Assert.Equal(today, ok.Data.StartsOn);
        // changing it replaces the rule (one per page) and starts again from today
        var changed = await Svc(t).SetRecurrenceAsync(owner.Id, page, new SetNoteRecurrenceRequest("workdays", null, null), new DateOnly(2026, 11, 25), Ct);
        Assert.Equal("Every weekday", changed.Data!.Description);
        Assert.Single(t.Verify().NoteRecurrences);
    }

    [Fact]
    public async Task A_person_can_have_at_most_20_repeats()
    {
        using var t = new TestDb();
        var me = t.AddUser("rr7");
        for (var i = 0; i < NoteRecurrenceRules.MaxRulesPerPerson; i++)
        {
            var p = (await Svc(t).CreateAsync(me.Id, new CreateNoteRequest(null, $"P{i}", null, null, null, null), Ct)).Data!.Id;
            Assert.True((await Svc(t).SetRecurrenceAsync(me.Id, p, new SetNoteRecurrenceRequest("daily", null, null), new DateOnly(2026, 11, 23), Ct)).Success);
        }
        var extra = (await Svc(t).CreateAsync(me.Id, new CreateNoteRequest(null, "one more", null, null, null, null), Ct)).Data!.Id;
        Assert.Equal("too_many", (await Svc(t).SetRecurrenceAsync(me.Id, extra, new SetNoteRecurrenceRequest("daily", null, null), new DateOnly(2026, 11, 23), Ct)).Error);
    }

    [Fact]
    public async Task Deleting_the_template_for_good_deletes_its_rule()
    {
        using var t = new TestDb();
        var (owner, _, _, template) = await SetUpAsync(t);
        Assert.True((await Svc(t).TrashAsync(owner, template, Ct)).Success);
        Assert.True((await Svc(t).PurgeAsync(owner, template, Ct)).Success);
        Assert.Empty(t.Verify().NoteRecurrences);
    }
}
