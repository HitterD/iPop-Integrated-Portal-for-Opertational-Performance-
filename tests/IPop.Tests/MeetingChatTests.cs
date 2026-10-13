using System.Security.Claims;
using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FakeRoomAdmin = IPop.Tests.MeetingRoomServiceTests.FakeRoomAdmin;

namespace IPop.Tests;

/// Meeting rooms v2 §3 / §2.10: in-meeting chat through the API, relayed on LiveKit topic ipop.chat, saved with a running
/// server recording, cleaned up after the booking.
public class MeetingChatTests
{
    private readonly RoomOccupancyTracker occ = new(DateTime.MinValue, TimeSpan.MaxValue);
    private readonly MeetingChatRateLimiter limiter = new();
    private readonly MeetingRecordingServiceTests.FakeEgress eg = new();

    private sealed record World(TestDb T, User Organizer, User Invited, User Guest, User Outsider, User Admin, Guid MeetingId, FakeRoomAdmin Lk);

    /// Kalosi booked now by Organizer with Invited; Guest admitted with the password; all three in the LiveKit room.
    private static async Task<World> SetupAsync()
    {
        var t = new TestDb();
        var org = t.AddUser("ORG"); var inv = t.AddUser("INV"); var guest = t.AddUser("GST"); var outsider = t.AddUser("OUT"); var admin = t.AddAdmin("ADM");
        var (r, id, _) = await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id,
            new("Weekly sync", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(55), null, "SJA Hall · Kalosi", null, [inv.Id], null, 15, null), default, nowUtc: DateTime.UtcNow.AddMinutes(-6));
        Assert.Equal(MeetingEndpoints.CreateResult.Created, r);
        using (var db = t.Verify())
        {
            db.MeetingRoomGuests.Add(new MeetingRoomGuest { MeetingId = id!.Value, UserId = guest.Id, AdmittedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
        var lk = new FakeRoomAdmin();
        lk.Put("kalosi", org.Id.ToString(), inv.Id.ToString(), guest.Id.ToString());
        return new World(t, org, inv, guest, outsider, admin, id!.Value, lk);
    }

    private MeetingChatService Chat(World w, IPopDbContext? db = null) => new(db ?? w.T.Db, w.Lk, limiter, occ);

    private MeetingRecordingService Recs(World w) =>
        new(w.T.Db, w.Lk, eg, new DbSettings(w.T.Db), MeetingRecordingServiceTests.Config(), occ);

    private static HttpContext Http(User u, params string[] roles) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("nik", u.Nik) }.Concat(roles.Select(r => new Claim(ClaimTypes.Role, r))), "Bearer")),
    };
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;
    private static string? Code(IResult r) => r is IValueHttpResult { Value: { } v }
        ? JsonSerializer.SerializeToElement(v).TryGetProperty("code", out var c) ? c.GetString() : null : null;

    private Task<IResult> Post(World w, User u, string? text) =>
        MeetingRoomEndpoints.ChatSendAsync("kalosi", new(text), Http(u), w.T.Db, Chat(w), default);
    private Task<IResult> History(World w, User u) => MeetingRoomEndpoints.ChatHistoryAsync("kalosi", Http(u), w.T.Db, Chat(w), default);

    [Fact]
    public async Task Organizer_invited_and_guest_who_are_in_the_room_can_post_and_everyone_allowed_reads_it_in_order()
    {
        var w = await SetupAsync(); using var t = w.T;
        Assert.Equal(200, Status(await Post(w, w.Organizer, "  hello  ")));
        Assert.Equal(200, Status(await Post(w, w.Invited, "hi")));
        Assert.Equal(200, Status(await Post(w, w.Guest, "hey")));

        var (list, err) = await Chat(w).HistoryAsync(w.Guest.Id, "kalosi", default);
        Assert.Null(err);
        Assert.Equal(new[] { "hello", "hi", "hey" }, list!.Select(m => m.Text));
        Assert.Equal("User ORG", list[0].Name);
        Assert.All(list, m => Assert.Equal("text", m.Kind));
        Assert.All(list, m => Assert.Null(m.OffsetSeconds));
        var row = await t.Verify().MeetingChatMessages.FirstAsync(m => m.Text == "hello");
        Assert.Equal(w.MeetingId, row.MeetingId);
        Assert.Equal("kalosi", row.RoomKey);
        Assert.Null(row.RecordingId);
    }

    [Fact]
    public async Task Posting_requires_being_in_the_LiveKit_room()
    {
        var w = await SetupAsync(); using var t = w.T;
        w.Lk.Put("kalosi", w.Organizer.Id.ToString());   // Invited left the room
        var r = await Post(w, w.Invited, "still here?");
        Assert.Equal(403, Status(r));
        Assert.Equal("not_in_room", Code(r));
        Assert.Empty(t.Verify().MeetingChatMessages);
        Assert.Empty(w.Lk.DataSent);
    }

    [Fact]
    public async Task An_outsider_in_the_LiveKit_room_can_neither_post_nor_read()
    {
        var w = await SetupAsync(); using var t = w.T;
        await Post(w, w.Organizer, "private");
        w.Lk.Put("kalosi", w.Organizer.Id.ToString(), w.Outsider.Id.ToString(), w.Admin.Id.ToString());
        Assert.Equal(403, Status(await Post(w, w.Outsider, "let me in")));
        Assert.Equal(403, Status(await History(w, w.Outsider)));
        // Admin has no bypass either.
        Assert.Equal(403, Status(await History(w, w.Admin)));
        Assert.Equal(200, Status(await History(w, w.Invited)));   // reading does not need being inside
        Assert.Single(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task A_declined_attendee_loses_chat_access()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify())
        {
            await db.MeetingAttendees.Where(a => a.MeetingId == w.MeetingId && a.UserId == w.Invited.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.RsvpStatus, "decline"));
        }
        Assert.Equal(403, Status(await Post(w, w.Invited, "x")));
        Assert.Equal(403, Status(await History(w, w.Invited)));
    }

    [Fact]
    public async Task Empty_and_too_long_messages_are_rejected()
    {
        var w = await SetupAsync(); using var t = w.T;
        var empty = await Post(w, w.Organizer, "   \n ");
        Assert.Equal(400, Status(empty)); Assert.Equal("empty", Code(empty));
        Assert.Equal(400, Status(await Post(w, w.Organizer, null)));
        var tooLong = await Post(w, w.Organizer, new string('a', 2001));
        Assert.Equal(400, Status(tooLong)); Assert.Equal("too_long", Code(tooLong));
        // 2000 characters (after trimming) is fine.
        Assert.Equal(200, Status(await Post(w, w.Organizer, "  " + new string('a', 2000) + "  ")));
        Assert.Single(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task No_booking_means_no_chat()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new FakeRoomAdmin(); lk.Put("toraja", a.Id.ToString());
        var chat = new MeetingChatService(t.Db, lk, limiter, occ);
        Assert.Equal("no_booking", (await chat.SendAsync(a.Id, "toraja", "hi", default)).Error);
        Assert.Equal("no_booking", (await chat.HistoryAsync(a.Id, "toraja", default)).Error);
        Assert.Equal("unknown_room", (await chat.SendAsync(a.Id, "nope", "hi", default)).Error);
    }

    [Fact]
    public async Task Eleventh_message_within_ten_seconds_is_rate_limited()
    {
        var w = await SetupAsync(); using var t = w.T;
        for (var i = 0; i < 10; i++) Assert.Equal(200, Status(await Post(w, w.Organizer, $"m{i}")));
        var r = await Post(w, w.Organizer, "one too many");
        Assert.Equal(429, Status(r));
        Assert.Equal("rate_limited", Code(r));
        Assert.Equal(200, Status(await Post(w, w.Invited, "others are not affected")));
        Assert.Equal(11, t.Verify().MeetingChatMessages.Count());
    }

    [Fact]
    public void Rate_limiter_window_slides()
    {
        var l = new MeetingChatRateLimiter(); var u = Guid.NewGuid(); var t0 = DateTime.UtcNow;
        for (var i = 0; i < 10; i++) Assert.True(l.TryAcquire(u, t0.AddSeconds(i * 0.5)));
        Assert.False(l.TryAcquire(u, t0.AddSeconds(9.9)));
        Assert.True(l.TryAcquire(u, t0.AddSeconds(10.1)));    // the first one left the window
        Assert.False(l.TryAcquire(u, t0.AddSeconds(10.2)));
    }

    [Fact]
    public async Task A_message_is_relayed_on_topic_ipop_chat_without_any_NIK()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify())   // a display name that does not contain the NIK
            await db.Users.Where(u => u.Id == w.Invited.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Name, "Ina Wulandari"));
        var dto = (await Chat(w).SendAsync(w.Invited.Id, "kalosi", "see https://x.test", default)).Message!;
        var (room, payload, topic) = Assert.Single(w.Lk.DataSent);
        Assert.Equal(FakeRoomAdmin.Media("kalosi", w.MeetingId), room);
        Assert.Equal("ipop.chat", topic);
        var json = System.Text.Encoding.UTF8.GetString(payload);
        Assert.DoesNotContain(w.Invited.Nik, json);
        Assert.DoesNotContain("nik", json, StringComparison.OrdinalIgnoreCase);
        var e = JsonDocument.Parse(json).RootElement;
        Assert.Equal(new[] { "id", "userId", "name", "text", "sentAt", "kind" }, e.EnumerateObject().Select(p => p.Name));
        Assert.Equal(dto.Id, e.GetProperty("id").GetGuid());
        Assert.Equal(w.Invited.Id, e.GetProperty("userId").GetGuid());
        Assert.Equal("Ina Wulandari", e.GetProperty("name").GetString());
        Assert.Equal("see https://x.test", e.GetProperty("text").GetString());
        Assert.Equal("text", e.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_relay_failure_does_not_fail_the_post()
    {
        var w = await SetupAsync(); using var t = w.T;
        w.Lk.SendDataThrows = true;
        var r = await Post(w, w.Organizer, "stored anyway");
        Assert.Equal(200, Status(r));
        Assert.Single(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task Messages_during_a_server_recording_are_saved_with_it_with_their_offset_plus_system_lines()
    {
        var w = await SetupAsync(); using var t = w.T;
        await Post(w, w.Organizer, "before");
        var rec = (await Recs(w).StartAsync(w.Invited.Id, "kalosi", default)).Recording!;
        using (var db = t.Verify())   // the recording started 90 s ago
            await db.MeetingRecordings.Where(r => r.Id == rec.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.StartedAt, r => r.StartedAt.AddSeconds(-90)));
        var during = (await Chat(w, t.Verify()).SendAsync(w.Guest.Id, "kalosi", "during", default)).Message!;
        Assert.InRange(during.OffsetSeconds!.Value, 89, 92);
        t.Db.ChangeTracker.Clear();
        Assert.Null((await Recs(w).StopAsync(w.Organizer.Id, rec.Id, default)).Error);
        await Post(w, w.Organizer, "after");

        var rows = await t.Verify().MeetingChatMessages.OrderBy(m => m.SentAt).ToListAsync();
        Assert.Equal(new[] { "before", "User INV started recording", "during", "Recording stopped", "after" }, rows.Select(m => m.Text));
        Assert.Equal(new Guid?[] { null, rec.Id, rec.Id, rec.Id, null }, rows.Select(m => m.RecordingId));
        var system = rows.Where(m => m.Kind == "system").ToList();
        Assert.Equal(2, system.Count);
        Assert.All(system, m => Assert.Equal(Guid.Empty, m.UserId));
        Assert.All(system, m => Assert.Equal(w.MeetingId, m.MeetingId));
        // System lines are relayed the same way.
        Assert.Contains(w.Lk.DataSent, d => d.Topic == "ipop.chat" && System.Text.Encoding.UTF8.GetString(d.Payload).Contains("\"kind\":\"system\""));

        // ChatCount counts the lines saved with the recording.
        using (var db = t.Verify())
            await db.MeetingRecordings.Where(r => r.Id == rec.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, MeetingRecordingStatus.Ready));
        var listed = Assert.Single(await new MeetingRecordingService(t.Verify(), w.Lk, new MeetingRecordingServiceTests.FakeEgress(),
            new DbSettings(t.Verify()), MeetingRecordingServiceTests.Config()).ListAsync(w.Guest.Id, false, default));
        Assert.Equal(3, listed.ChatCount);
    }

    [Fact]
    public async Task Ad_hoc_recording_writes_no_system_line()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new FakeRoomAdmin(); lk.Put("toraja", a.Id.ToString());
        await new MeetingRecordingService(t.Db, lk, new MeetingRecordingServiceTests.FakeEgress(), new DbSettings(t.Db),
            MeetingRecordingServiceTests.Config(), occ).StartAsync(a.Id, "toraja", default);
        Assert.Empty(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task Recording_chat_is_for_its_viewers_and_admins_only_ordered_by_offset()
    {
        var w = await SetupAsync(); using var t = w.T;
        var rec = (await Recs(w).StartAsync(w.Organizer.Id, "kalosi", default)).Recording!;
        await Post(w, w.Invited, "first");
        using (var db = t.Verify())
        {
            // A late line saved out of SentAt order: the endpoint sorts by offset.
            db.MeetingChatMessages.Add(new MeetingChatMessage
            {
                Id = Guid.NewGuid(), MeetingId = w.MeetingId, RoomKey = "kalosi", UserId = w.Guest.Id, Kind = "text", Text = "at 5 s",
                SentAt = DateTime.UtcNow.AddMinutes(1), RecordingId = rec.Id, OffsetSeconds = 5,
            });
            db.MeetingChatMessages.Add(new MeetingChatMessage
            {
                Id = Guid.NewGuid(), MeetingId = w.MeetingId, RoomKey = "kalosi", UserId = w.Guest.Id, Kind = "text", Text = "other recording",
                SentAt = DateTime.UtcNow, RecordingId = Guid.NewGuid(), OffsetSeconds = 1,
            });
            db.SaveChanges();
        }
        var chat = Chat(w, t.Verify());
        var ok = await MeetingRoomEndpoints.RecordingChatAsync(rec.Id, Http(w.Invited), t.Verify(), chat, default);
        Assert.Equal(200, Status(ok));
        var list = Assert.IsAssignableFrom<IReadOnlyList<MeetingChatMessageDto>>(((IValueHttpResult)ok).Value);
        Assert.Equal(new[] { "User ORG started recording", "first", "at 5 s" }, list.Select(m => m.Text));

        Assert.Equal(404, Status(await MeetingRoomEndpoints.RecordingChatAsync(rec.Id, Http(w.Outsider), t.Verify(), chat, default)));
        Assert.Equal(200, Status(await MeetingRoomEndpoints.RecordingChatAsync(rec.Id, Http(w.Admin, "Admin"), t.Verify(), chat, default)));
        Assert.Equal(404, Status(await MeetingRoomEndpoints.RecordingChatAsync(Guid.NewGuid(), Http(w.Admin, "Admin"), t.Verify(), chat, default)));
    }

    // ---------------------------------------------------------------- cleanup

    private IServiceProvider Services(TestDb t, ILiveKitRoomAdmin lk)
    {
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddSingleton(lk);
        sc.AddSingleton(occ);
        return sc.BuildServiceProvider();
    }

    private static void AddLine(IPopDbContext db, Guid meetingId, string text, Guid? recordingId = null, string room = "kalosi") =>
        db.MeetingChatMessages.Add(new MeetingChatMessage
        {
            Id = Guid.NewGuid(), MeetingId = meetingId, RoomKey = room, UserId = Guid.Empty, Kind = "text", Text = text,
            SentAt = DateTime.UtcNow, RecordingId = recordingId, OffsetSeconds = recordingId is null ? null : 3,
        });

    private static async Task EndBookingAsync(TestDb t, Guid meetingId, DateTime endUtc)
    {
        using var db = t.Verify();
        await db.Meetings.Where(m => m.Id == meetingId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.StartAt, endUtc.AddHours(-1)).SetProperty(m => m.EndAt, endUtc));
    }

    [Fact]
    public async Task Cleanup_deletes_chat_an_hour_after_the_booking_ended_and_keeps_recording_lines()
    {
        var w = await SetupAsync(); using var t = w.T;
        var recId = Guid.NewGuid();
        using (var db = t.Verify()) { AddLine(db, w.MeetingId, "plain"); AddLine(db, w.MeetingId, "recorded", recId); db.SaveChanges(); }
        var now = DateTime.UtcNow;
        await EndBookingAsync(t, w.MeetingId, now.AddMinutes(-61));
        w.Lk.Put("kalosi");   // empty

        using (var sp = (ServiceProvider)Services(t, w.Lk)) await MeetingRoomBookingWorker.TickAsync(sp, now, default);

        var left = await t.Verify().MeetingChatMessages.ToListAsync();
        Assert.Equal("recorded", Assert.Single(left).Text);
    }

    [Fact]
    public async Task Cleanup_waits_an_hour_after_the_end()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify()) { AddLine(db, w.MeetingId, "plain"); db.SaveChanges(); }
        var now = DateTime.UtcNow;
        await EndBookingAsync(t, w.MeetingId, now.AddMinutes(-50));
        w.Lk.Put("kalosi");
        using (var sp = (ServiceProvider)Services(t, w.Lk)) await MeetingRoomBookingWorker.TickAsync(sp, now, default);
        Assert.Single(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task Cleanup_keeps_the_chat_of_a_booking_still_overrunning_an_occupied_room()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify()) { AddLine(db, w.MeetingId, "plain"); db.SaveChanges(); }
        var now = DateTime.UtcNow;
        await EndBookingAsync(t, w.MeetingId, now.AddMinutes(-70));
        // Occupied without a break since before the end (tracker started at MinValue, never seen empty).
        using (var sp = (ServiceProvider)Services(t, w.Lk)) await MeetingRoomBookingWorker.TickAsync(sp, now, default);
        Assert.Empty(w.Lk.Removed);
        Assert.Single(t.Verify().MeetingChatMessages);

        // Everyone leaves: the next pass deletes it.
        w.Lk.Put("kalosi");
        using (var sp = (ServiceProvider)Services(t, w.Lk)) await MeetingRoomBookingWorker.TickAsync(sp, now.AddSeconds(30), default);
        Assert.Empty(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task Cleanup_skips_a_room_whose_LiveKit_state_is_unknown()
    {
        var w = await SetupAsync(); using var t = w.T;
        using (var db = t.Verify()) { AddLine(db, w.MeetingId, "plain"); db.SaveChanges(); }
        var now = DateTime.UtcNow;
        await EndBookingAsync(t, w.MeetingId, now.AddMinutes(-70));
        w.Lk.ThrowRooms.Add("hall-kalosi");
        using (var sp = (ServiceProvider)Services(t, w.Lk)) await MeetingRoomBookingWorker.TickAsync(sp, now, default);
        Assert.Single(t.Verify().MeetingChatMessages);
    }

    [Fact]
    public async Task Deleting_a_recording_deletes_its_chat_and_the_purge_does_too()
    {
        var w = await SetupAsync(); using var t = w.T;
        var rec = (await Recs(w).StartAsync(w.Organizer.Id, "kalosi", default)).Recording!;
        await Post(w, w.Invited, "during");
        t.Db.ChangeTracker.Clear();
        await Recs(w).StopAsync(w.Organizer.Id, rec.Id, default);
        using (var db = t.Verify())
        {
            await db.MeetingRecordings.Where(r => r.Id == rec.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, MeetingRecordingStatus.Ready));
            AddLine(db, w.MeetingId, "orphan of an old deleted recording", Guid.NewGuid());
            db.SaveChanges();
        }
        Assert.Equal(3, t.Verify().MeetingChatMessages.Count(m => m.RecordingId == rec.Id));
        t.Db.ChangeTracker.Clear();

        Assert.Null(await Recs(w).DeleteAsync(w.Organizer.Id, false, rec.Id, default));
        Assert.Equal(0, t.Verify().MeetingChatMessages.Count(m => m.RecordingId == rec.Id));
        Assert.Equal(1, t.Verify().MeetingChatMessages.Count());
    }

    [Fact]
    public async Task Purge_removes_the_chat_of_recordings_it_deletes()
    {
        var w = await SetupAsync(); using var t = w.T;
        var failed = new MeetingRecording
        {
            Id = Guid.NewGuid(), RoomKey = "kalosi", Title = "old", EgressId = "EG_x", StartedById = w.Organizer.Id, MeetingId = w.MeetingId,
            StartedAt = DateTime.UtcNow.AddDays(-400), Status = MeetingRecordingStatus.Failed,
        };
        var deleted = new MeetingRecording
        {
            Id = Guid.NewGuid(), RoomKey = "kalosi", Title = "gone", EgressId = "EG_y", StartedById = w.Organizer.Id, MeetingId = w.MeetingId,
            StartedAt = DateTime.UtcNow.AddDays(-1), Status = MeetingRecordingStatus.Deleted,
        };
        using (var db = t.Verify())
        {
            db.MeetingRecordings.AddRange(failed, deleted);
            AddLine(db, w.MeetingId, "a", failed.Id); AddLine(db, w.MeetingId, "b", deleted.Id); AddLine(db, w.MeetingId, "c");
            db.SaveChanges();
        }
        var sc = new ServiceCollection();
        sc.AddScoped<IPopDbContext>(_ => t.Verify());
        sc.AddScoped<IPop.Application.Settings.ISettings>(sp => new DbSettings(sp.GetRequiredService<IPopDbContext>()));
        sc.AddSingleton<IPop.Application.Files.IFileAccessService>(new MeetingRecordingWorkerTests.Nas());
        using var provider = sc.BuildServiceProvider();
        await MeetingRecordingWorker.PurgeAsync(provider, DateTime.UtcNow, default);
        Assert.Equal("c", Assert.Single(t.Verify().MeetingChatMessages).Text);
    }
}
