using IPop.Api.Endpoints;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarDelegateEndpoints;
using static IPop.Api.Endpoints.CalendarEndpoints;
using static IPop.Api.Endpoints.CalendarSharingEndpoints;

namespace IPop.Tests;

/// Calendar sharing stage 2: delegates. View sees details except Private; Edit/Full act on the
/// owner's behalf (owner stays owner/organizer, the delegate is recorded); Full also gets the
/// owner's invites and answers them. Grants notify both people and are audited.
public class CalendarDelegateTests
{
    private sealed class RecordingGateway : INotificationGatewayService
    {
        public List<(string Type, string Title, string Message, List<Guid> To)> Sent { get; } = [];

        public Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message,
            NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Sent.Add((eventType, title, message, target.UserIds?.ToList() ?? []));
            return Task.FromResult(new NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "delivered", 1, DateTime.UtcNow));
        }

        public Task<NotificationGatewayClientInfo?> AuthenticateClientAsync(string clientId, string secret, string? remoteIp, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationEnqueueResult> EnqueueAsync(Guid clientId, NotificationEnqueueRequest req, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkReadAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> MarkAcknowledgedAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
        public Task<NotificationDeliveryState?> RetryDeliveryAsync(Guid requestId, Guid userId, CancellationToken ct) => throw new NotImplementedException();
    }

    private static DateTime Day(int offset) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(offset), DateTimeKind.Utc);

    private static async Task<CalendarEvent> Event(TestDb t, Guid owner, string title, DateTime start, string? visibility = null,
        IReadOnlyList<Guid>? attendees = null, Guid? actedBy = null)
    {
        var (r, ev, _) = await CreateEventAsync(t.Db, owner,
            new SaveEventRequest(title, start, start.AddHours(1), Location: "Room 2", AttendeeIds: attendees, Visibility: visibility), default, actedBy);
        Assert.Equal(SaveResult.Ok, r);
        return ev!;
    }

    [Fact]
    public async Task Grant_change_revoke_notifies_both_and_audits()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1");
        var gw = new RecordingGateway();

        Assert.Equal(GrantResult.Granted, await GrantAsync(t.Db, gw, null, boss.Id, "P1", DelegateLevel.Edit, boss.Id, default));
        Assert.Equal(DelegateLevel.Edit, Assert.Single(await ListDelegatesAsync(t.Db, boss.Id, default)).Level);
        Assert.Equal("B1", Assert.Single(await ListManagedAsync(t.Db, pa.Id, default)).Nik);
        // The owner did it, so only the delegate hears about it.
        Assert.Equal([pa.Id], Assert.Single(gw.Sent).To);

        Assert.Equal(GrantResult.Unchanged, await GrantAsync(t.Db, gw, null, boss.Id, "P1", DelegateLevel.Edit, boss.Id, default));
        Assert.Equal(GrantResult.Changed, await GrantAsync(t.Db, gw, null, boss.Id, "P1", DelegateLevel.Full, boss.Id, default));
        Assert.True(await RevokeAsync(t.Db, gw, null, boss.Id, "P1", boss.Id, default));
        Assert.Empty(await ListManagedAsync(t.Db, pa.Id, default));

        var actions = await t.Db.AuditLogs.Where(a => a.Entity == "CalendarDelegate").Select(a => a.Action).ToListAsync();
        Assert.Equal(["calendar.delegate.change", "calendar.delegate.grant", "calendar.delegate.revoke"], actions.Order().ToList());
    }

    [Fact]
    public async Task Admin_grant_notifies_owner_and_delegate()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1"); var admin = t.AddAdmin("A1");
        var gw = new RecordingGateway();
        await GrantAsync(t.Db, gw, null, boss.Id, "P1", DelegateLevel.View, admin.Id, default);
        Assert.Equal(2, gw.Sent.Count);
        Assert.Contains(gw.Sent, s => s.To.SequenceEqual([boss.Id]) && s.Message.Contains("Set by"));
        Assert.Contains(gw.Sent, s => s.To.SequenceEqual([pa.Id]));
        Assert.Equal("A1", (await t.Db.AuditLogs.SingleAsync(a => a.Entity == "CalendarDelegate")).ActorNik);
    }

    [Fact]
    public async Task Grant_rejects_self_unknown_and_bad_level()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); t.AddUser("P1"); t.AddUser("X1", active: false);
        Assert.Equal(GrantResult.Self, await GrantAsync(t.Db, null, null, boss.Id, "B1", DelegateLevel.View, boss.Id, default));
        Assert.Equal(GrantResult.NotFound, await GrantAsync(t.Db, null, null, boss.Id, "X1", DelegateLevel.View, boss.Id, default));
        Assert.Equal(GrantResult.InvalidLevel, await GrantAsync(t.Db, null, null, boss.Id, "P1", "owner", boss.Id, default));
    }

    [Fact]
    public async Task Delegates_see_busy_details_but_not_private()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1"); var other = t.AddUser("O1");
        await Event(t, boss.Id, "Board prep", Day(2).AddHours(2), "busy");
        await Event(t, boss.Id, "Doctor", Day(2).AddHours(4), "private");
        await SaveSettingsAsync(t.Db, boss.Id, new SettingsDto(CalendarVisibility.Hidden, true), default);
        await GrantAsync(t.Db, null, null, boss.Id, "P1", DelegateLevel.View, boss.Id, default);

        var seen = await BuildPeopleAsync(t.Db, pa.Id, [boss.Id], Day(1), Day(4), default);
        Assert.Equal("Board prep", seen.Single(i => i.Visibility == "busy").Title);
        Assert.Null(seen.Single(i => i.Visibility == "private").Title);
        Assert.All(await BuildPeopleAsync(t.Db, other.Id, [boss.Id], Day(1), Day(4), default), i => Assert.Null(i.Title));

        var feed = await BuildManagedFeedAsync(t.Db, boss.Id, DelegateLevel.View, Day(1), Day(4), default);
        Assert.Equal(["Board prep", "Private"], feed.Select(f => f.Title));
        Assert.All(feed, f => Assert.False(f.CanEdit));
        Assert.Null(feed.Single(f => f.Title == "Private").EventId);
    }

    [Fact]
    public async Task View_delegate_can_open_details_but_not_edit()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1");
        var ev = await Event(t, boss.Id, "Board prep", Day(2).AddHours(2));
        var secret = await Event(t, boss.Id, "Doctor", Day(2).AddHours(4), "private");
        await GrantAsync(t.Db, null, null, boss.Id, "P1", DelegateLevel.View, boss.Id, default);

        var d = await LoadDetailAsync(t.Db, ev.Id, pa.Id, default);
        Assert.NotNull(d);
        Assert.False(d!.CanEdit);
        Assert.Equal(DelegateLevel.View, d.DelegateLevel);
        Assert.Null(await LoadDetailAsync(t.Db, secret.Id, pa.Id, default));
        // A view delegate's write stays their own, so the owner check refuses it.
        Assert.Equal((pa.Id, (Guid?)null), await ActorForEventAsync(t.Db, ev.Id, pa.Id, default));
        Assert.Equal(SaveResult.Forbidden, (await DeleteEventAsync(t.Db, ev.Id, pa.Id, default)).Item1);
    }

    [Fact]
    public async Task Edit_delegate_creates_and_changes_on_behalf()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1"); var guest = t.AddUser("G1");
        await GrantAsync(t.Db, null, null, boss.Id, "P1", DelegateLevel.Edit, boss.Id, default);

        var (owner, actedBy, denied) = await ResolveOwnerAsync(t.Db, pa.Id, "B1", DelegateLevel.Edit, default);
        Assert.Null(denied);
        Assert.Equal(boss.Id, owner);
        var ev = await Event(t, owner, "Budget review", Day(3).AddHours(2), attendees: [guest.Id], actedBy: actedBy);
        var meeting = await t.Db.Meetings.SingleAsync(m => m.Id == ev.MeetingId);
        Assert.Equal(boss.Id, meeting.OrganizerId);
        Assert.Equal(pa.Id, meeting.SentById);

        var d = await LoadDetailAsync(t.Db, ev.Id, guest.Id, default);
        Assert.Equal(pa.Name, d!.SentBy);
        Assert.Equal(pa.Name, d.CreatedBy);
        Assert.True((await LoadDetailAsync(t.Db, ev.Id, pa.Id, default))!.CanEdit);

        var (o2, by2) = await ActorForEventAsync(t.Db, ev.Id, pa.Id, default);
        Assert.Equal((boss.Id, (Guid?)pa.Id), (o2, by2));
        var (r, updated, _) = await UpdateEventAsync(t.Db, ev.Id, o2,
            new SaveEventRequest("Budget review v2", ev.StartAt, ev.EndAt, AttendeeIds: [guest.Id]), default, by2);
        Assert.Equal(SaveResult.Ok, r);
        Assert.Equal(pa.Id, updated!.UpdatedById);
        Assert.Equal(SaveResult.Ok, (await DeleteEventAsync(t.Db, ev.Id, o2, default)).Item1);
    }

    [Fact]
    public async Task Edit_delegate_cannot_touch_private_or_answer_invites()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1"); var stranger = t.AddUser("S1");
        var secret = await Event(t, boss.Id, "Doctor", Day(2).AddHours(4), "private");
        await GrantAsync(t.Db, null, null, boss.Id, "P1", DelegateLevel.Edit, boss.Id, default);

        Assert.Equal((pa.Id, (Guid?)null), await ActorForEventAsync(t.Db, secret.Id, pa.Id, default));
        Assert.NotNull((await ResolveOwnerAsync(t.Db, pa.Id, "B1", DelegateLevel.Full, default)).Error);
        Assert.NotNull((await ResolveOwnerAsync(t.Db, stranger.Id, "B1", DelegateLevel.Edit, default)).Error);
    }

    [Fact]
    public async Task Full_delegate_gets_the_owners_invites_and_answers_them()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1"); var organizer = t.AddUser("O1");
        await GrantAsync(t.Db, null, null, boss.Id, "P1", DelegateLevel.Full, boss.Id, default);
        var gw = new RecordingGateway();

        var (_, ev, invited) = await CreateEventAsync(t.Db, organizer.Id,
            new SaveEventRequest("Quarterly", Day(4).AddHours(2), Day(4).AddHours(3), AttendeeIds: [boss.Id]), default);
        await NotifyInvitedAsync(t.Db, gw, ev!, organizer.Id, invited, null, default);
        Assert.Contains(gw.Sent, s => s.To.SequenceEqual([boss.Id]));
        Assert.Contains(gw.Sent, s => s.To.SequenceEqual([pa.Id]) && s.Title.Contains("for " + boss.Name));

        // The meeting's row belongs to the organizer, yet the boss's delegate can open it and answer for them.
        var seen = await LoadDetailAsync(t.Db, ev!.Id, pa.Id, default);
        Assert.NotNull(seen);
        Assert.False(seen!.CanEdit);
        Assert.Equal("B1", seen.DelegateForNik);
        Assert.Equal(DelegateLevel.Full, seen.DelegateLevel);

        var (owner, actedBy, denied) = await ResolveOwnerAsync(t.Db, pa.Id, "B1", DelegateLevel.Full, default);
        Assert.Null(denied);
        Assert.Equal(MeetingEndpoints.RsvpResult.Applied,
            await MeetingEndpoints.ApplyRsvpAsync(t.Db, ev!.MeetingId!.Value, owner, "accept", null, default, actedBy));
        var d = await LoadDetailAsync(t.Db, ev.Id, organizer.Id, default);
        var row = d!.Attendees.Single(a => a.UserId == boss.Id);
        Assert.Equal("accept", row.Rsvp);
        Assert.Equal(pa.Name, row.RespondedBy);
    }

    [Fact]
    public async Task Owner_hears_about_delegate_changes_unless_turned_off()
    {
        using var t = new TestDb();
        var boss = t.AddUser("B1"); var pa = t.AddUser("P1");
        var gw = new RecordingGateway();
        await NotifyOwnerOfChangeAsync(t.Db, gw, null, boss.Id, pa.Id, "added", "Lunch", default);
        Assert.Equal([boss.Id], Assert.Single(gw.Sent).To);

        await SaveSettingsAsync(t.Db, boss.Id, new SettingsDto(CalendarVisibility.Busy, false), default);
        await NotifyOwnerOfChangeAsync(t.Db, gw, null, boss.Id, pa.Id, "added", "Lunch 2", default);
        Assert.Single(gw.Sent);
    }
}
