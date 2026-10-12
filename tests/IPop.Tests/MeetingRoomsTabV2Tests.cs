using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Calendar;
using IPop.Web.Components.Calls;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting rooms v2 (Task 8): booking-only room cards, Use now, Enter with password, recording delete and the player with
/// its chat, and the room password box in Calendar.
[Collection("AvatarPhotos")]
public class MeetingRoomsTabV2Tests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static string Hm(DateTime utc) => utc.ToLocalTime().ToString("HH:mm");

    private static MeetingRoomDto Room(string key, string name, string access, MeetingRoomBookingDto? now = null, MeetingRoomBookingDto? next = null,
        int people = 0) =>
        new(key, name, 20, true, true, Enumerable.Range(1, people).Select(i => new MeetingRoomPerson(Guid.NewGuid(), $"Person {i}", false, false)).ToList(),
            now, next, null, access);

    private static MeetingRoomBookingDto Booking(string title, DateTime start, DateTime end, string? organizer = "Sari Ayu", bool mine = false) =>
        new(Guid.NewGuid(), title, start, end, organizer, mine);

    private static readonly DateTime LockedStart = Now.AddMinutes(-20), LockedEnd = Now.AddMinutes(70);
    private static readonly DateTime SoonStart = Now.AddMinutes(6);
    private static readonly DateTime NextStart = Now.AddMinutes(150);
    private static bool NextIsToday => NextStart.ToLocalTime().Date == DateTime.Now.Date;

    private static List<MeetingRoomDto> Rooms() =>
    [
        Room("robusta", "Robusta", "free", next: Booking("Weekly ICT sync", NextStart, NextStart.AddHours(1))),
        Room("arabika", "Arabika", "invited", now: Booking("Production review", Now.AddMinutes(-10), Now.AddMinutes(50)), people: 6),
        Room("kalosi", "Kalosi", "locked", now: Booking("Secret board talk", LockedStart, LockedEnd, organizer: null), people: 4),
        Room("kapal-api", "Kapal Api", "starting", now: Booking("HR town hall", SoonStart, SoonStart.AddHours(1), "Hendra")),
        Room("toraja", "Toraja", "free"),
    ];

    // ---------------------------------------------------------------- card states

    [Fact]
    public void Cards_follow_the_viewers_access()
    {
        using var ctx = Context(new Api { RoomList = Rooms() });
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        var t = cut.FindAll(".mrt-room");

        // free → Use now + small Book button
        Assert.Equal("Use now", t[0].QuerySelector(".mrt-join")!.TextContent.Trim());
        Assert.Equal("Book for later", t[0].QuerySelector(".mrt-mini")!.GetAttribute("title"));
        Assert.Contains("Free now", t[0].TextContent);
        Assert.Equal(NextIsToday ? $"Free until {Hm(NextStart)}" : "Free today", t[0].QuerySelector("small")!.TextContent.Trim());
        Assert.Contains("Next: Weekly ICT sync", t[0].TextContent);
        Assert.Contains("No bookings today", t[4].TextContent);
        Assert.Equal("Free today", t[4].QuerySelector("small")!.TextContent.Trim());

        // invited → Join
        Assert.Equal("Join", t[1].QuerySelector(".mrt-join")!.TextContent.Trim());
        Assert.Contains("You’re invited.", t[1].TextContent);
        Assert.Empty(t[1].QuerySelectorAll(".mrt-mini"));

        // locked → Locked capsule, Booked HH:mm–HH:mm, Enter with password; never the title
        Assert.Equal("Locked", t[2].QuerySelector(".mrt-lock")!.TextContent.Trim());
        Assert.Contains($"Booked {Hm(LockedStart)}–{Hm(LockedEnd)}", t[2].TextContent);
        Assert.Equal("Enter with password", t[2].QuerySelector(".mrt-join")!.TextContent.Trim());
        Assert.Contains("4 people inside", t[2].TextContent);
        Assert.DoesNotContain("Secret", cut.Markup);

        // starting → Starts HH:mm + Join early
        Assert.Equal($"Starts {Hm(SoonStart)}", t[3].QuerySelector(".mrt-soon")!.TextContent.Trim());
        Assert.Equal("Join early", t[3].QuerySelector(".mrt-join")!.TextContent.Trim());
    }

    // ---------------------------------------------------------------- password sheet

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "wrong_password", "That password isn’t right.", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "too_many_attempts", "Too many tries. Wait a few minutes and try again.", false)]
    [InlineData(HttpStatusCode.Conflict, "no_booking", "This room isn’t booked. Use it now or book it first.", false)]
    public void Enter_with_password_posts_the_digits_and_maps_errors_inline(HttpStatusCode status, string code, string message, bool cleared)
    {
        var api = new Api { RoomList = Rooms() };
        api.JoinAnswers.Enqueue((status, $$"""{"code":"{{code}}"}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        cut.FindAll(".mrt-room")[2].QuerySelector(".mrt-join")!.Click();
        var sheet = cut.Find(".mrt-sheet");
        Assert.Equal("Kalosi is booked", sheet.QuerySelector("#mrt-sheet-t")!.TextContent);
        Assert.Contains($"Booked {Hm(LockedStart)}–{Hm(LockedEnd)}", sheet.TextContent);
        Assert.True(cut.Find(".mrt-pin-go").HasAttribute("disabled"));

        cut.Find(".mrt-pin-in").Input("48a2 917");
        Assert.Equal("482917", cut.Find(".mrt-pin-in").GetAttribute("value"));
        Assert.Equal(["4", "8", "2", "9", "1", "7"], cut.FindAll(".mrt-pin span").Select(s => s.TextContent).ToArray());
        cut.Find(".mrt-pin-go").Click();

        cut.WaitForAssertion(() => Assert.Equal(message, cut.Find(".mrt-pin-err").TextContent.Trim()));
        Assert.Equal(["kalosi"], api.Joins);
        Assert.Equal("482917", JsonDocument.Parse(api.JoinBodies.Single()).RootElement.GetProperty("password").GetString());
        Assert.Equal(cleared ? "" : "482917", cut.Find(".mrt-pin-in").GetAttribute("value"));
    }

    [Fact]
    public void Join_answered_with_password_required_opens_the_password_sheet()
    {
        var api = new Api { RoomList = Rooms() };
        api.JoinAnswers.Enqueue((HttpStatusCode.Forbidden, """{"code":"password_required"}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        cut.FindAll(".mrt-room")[1].QuerySelector(".mrt-join")!.Click();   // invited a second ago, removed since
        cut.WaitForAssertion(() => Assert.Equal("Arabika is booked", cut.Find("#mrt-sheet-t").TextContent));
        Assert.Equal("", JsonOrEmpty(api.JoinBodies.Single()));             // the plain Join sent no password
        Assert.Empty(cut.FindAll(".mrt-pin-err"));
    }

    [Fact]
    public void Plain_join_without_a_booking_shows_the_booking_toast()
    {
        var api = new Api { RoomList = Rooms() };
        api.JoinAnswers.Enqueue((HttpStatusCode.Conflict, """{"code":"no_booking"}"""));
        using var ctx = Context(api);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[1].QuerySelector(".mrt-join")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(toasts.Active, t => t.Message == "This room isn’t booked. Use it now or book it first."));
    }

    // ---------------------------------------------------------------- Use now

    [Fact]
    public void Use_now_offers_30_60_90_by_default_and_books_then_joins()
    {
        var api = new Api { RoomList = Rooms() };
        api.UseNowAnswers.Enqueue((HttpStatusCode.OK, $$"""{"meetingId":"{{Guid.NewGuid()}}","join":null}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();
        Assert.Equal("Use Toraja now", cut.Find("#mrt-sheet-t").TextContent);
        Assert.Equal(["30 min", "60 min", "90 min"], cut.FindAll(".mrt-seg3 button").Select(b => b.TextContent.Trim()).ToArray());
        Assert.Equal("60 min", cut.Find(".mrt-seg3 button.on").TextContent.Trim());
        Assert.Equal("Toraja meeting", cut.Find(".mrt-un-title").GetAttribute("value"));
        Assert.NotNull(cut.Find(".mrt-sheet .member-picker"));                          // the shared people picker

        cut.FindAll(".mrt-seg3 button")[2].Click();
        cut.Find(".mrt-un-title").Change("Quick sync");
        cut.Find(".mrt-un-go").Click();

        cut.WaitForAssertion(() => Assert.Equal(["toraja"], api.Joins));
        var body = JsonDocument.Parse(api.UseNowBodies.Single()).RootElement;
        Assert.Equal(90, body.GetProperty("minutes").GetInt32());
        Assert.Equal("Quick sync", body.GetProperty("title").GetString());
        Assert.Equal(0, body.GetProperty("attendeeNiks").GetArrayLength());
        Assert.Equal(["toraja"], api.UseNowRooms);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrt-sheet")));
    }

    [Fact]
    public void Use_now_only_offers_durations_that_fit_before_the_next_booking()
    {
        var rooms = Rooms();
        // Next booking in 75 min: its lock window opens in 65 min, so 30 and 60 fit, 90 does not.
        rooms[4] = Room("toraja", "Toraja", "free", next: Booking("Board", Now.AddMinutes(75), Now.AddMinutes(135)));
        using var ctx = Context(new Api { RoomList = rooms });
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();
        Assert.Equal(["30 min", "60 min"], cut.FindAll(".mrt-seg3 button").Select(b => b.TextContent.Trim()).ToArray());
        Assert.Equal("60 min", cut.Find(".mrt-seg3 button.on").TextContent.Trim());
    }

    [Fact]
    public void Use_now_refused_with_max_minutes_offers_the_shorter_booking()
    {
        var api = new Api { RoomList = Rooms() };
        api.UseNowAnswers.Enqueue((HttpStatusCode.Conflict, """{"code":"room_booked","maxMinutes":25}"""));
        api.UseNowAnswers.Enqueue((HttpStatusCode.OK, $$"""{"meetingId":"{{Guid.NewGuid()}}","join":null}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();
        cut.Find(".mrt-un-go").Click();

        cut.WaitForAssertion(() => Assert.Equal("Use for 25 min", cut.Find(".mrt-un-go").TextContent.Trim()));
        Assert.Empty(api.Joins);
        cut.Find(".mrt-un-go").Click();
        cut.WaitForAssertion(() => Assert.Equal(["toraja"], api.Joins));
        Assert.Equal([60, 25], api.UseNowBodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("minutes").GetInt32()).ToArray());
    }

    [Fact]
    public void Use_now_refused_without_room_left_says_so_and_does_not_join()
    {
        var api = new Api { RoomList = Rooms() };
        api.UseNowAnswers.Enqueue((HttpStatusCode.Conflict, """{"code":"room_booked","maxMinutes":null}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();
        cut.Find(".mrt-un-go").Click();
        cut.WaitForAssertion(() => Assert.Equal("Someone just booked this room. Pick another room or book a later time.", cut.Find(".mrt-sheet-err").TextContent.Trim()));
        Assert.Empty(api.Joins);
    }

    // ---------------------------------------------------------------- recordings

    private static MeetingRecordingDto Rec(string title, bool canDelete, int chat = 0, string status = "ready") =>
        new(Guid.NewGuid(), "robusta", "Robusta", title, "Yudi", Now.AddMinutes(-30), 39, 14L * 1024 * 1024, 5, status, Now.AddDays(90), canDelete, chat);

    [Fact]
    public void Recording_rows_show_full_hd_chat_count_and_delete_only_when_allowed()
    {
        var mine = Rec("Robusta · ad-hoc", canDelete: true, chat: 12);
        var other = Rec("Production review", canDelete: false);
        using var ctx = Context(new Api { RoomList = Rooms(), Recordings = [mine, other] });
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".mrt-rec").Count));
        var rows = cut.FindAll(".mrt-rec");
        var a = rows.Single(r => r.TextContent.Contains("ad-hoc"));
        var b = rows.Single(r => r.TextContent.Contains("Production"));
        Assert.Contains("recorded by Yudi · Full HD", a.TextContent);
        Assert.Contains("Robusta, 5 people · 12 chat messages", a.TextContent);
        Assert.DoesNotContain("chat messages", b.TextContent);
        Assert.NotNull(a.QuerySelector("button.mrt-del"));
        Assert.Null(b.QuerySelector("button.mrt-del"));
    }

    [Fact]
    public void Delete_asks_first_then_deletes_for_everyone_and_removes_the_row()
    {
        var rec = Rec("Robusta · ad-hoc", canDelete: true);
        var api = new Api { RoomList = Rooms(), Recordings = [rec] };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));

        cut.Find(".mrt-rec button.mrt-del").Click();
        Assert.Equal("Delete this recording?", cut.Find(".mrt-alert h3").TextContent);
        Assert.Contains("“Robusta · ad-hoc” and its chat will be removed for all 5 people who were in the meeting.", cut.Find(".mrt-alert").TextContent);
        var buttons = cut.FindAll(".mrt-alert-actions button");
        Assert.Equal(["Cancel", "Delete for everyone"], buttons.Select(b => b.TextContent.Trim()).ToArray());
        Assert.Contains("mrt-danger", buttons[1].ClassName);

        buttons[0].Click();
        Assert.Empty(cut.FindAll(".mrt-alert"));
        Assert.Empty(api.Deletes);

        cut.Find(".mrt-rec button.mrt-del").Click();
        cut.Find(".mrt-alert-actions .mrt-danger").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrt-rec")));
        Assert.Equal([rec.Id], api.Deletes);
        Assert.Empty(cut.FindAll(".mrt-alert"));
    }

    [Fact]
    public void Delete_while_still_recording_says_stop_first_and_keeps_the_row()
    {
        var rec = Rec("Robusta · ad-hoc", canDelete: true);
        var api = new Api { RoomList = Rooms(), Recordings = [rec], DeleteStatus = HttpStatusCode.Conflict };
        using var ctx = Context(api);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));
        cut.Find(".mrt-rec button.mrt-del").Click();
        cut.Find(".mrt-alert-actions .mrt-danger").Click();
        cut.WaitForAssertion(() => Assert.Contains(toasts.Active, t => t.Message == "Stop the recording first."));
        Assert.Single(cut.FindAll(".mrt-rec"));
    }

    [Fact]
    public void Deleting_the_recording_in_the_player_closes_it_with_a_toast()
    {
        var rec = Rec("Robusta · ad-hoc", canDelete: true);
        var api = new Api { RoomList = Rooms(), Recordings = [rec] };
        using var ctx = Context(api);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));
        cut.Find("button.mrt-play").Click();
        cut.WaitForAssertion(() => cut.Find(".mrt-player video"));
        Assert.Contains("Full HD 1080p", cut.Find(".mrt-player-bar").TextContent);

        cut.Find(".mrt-player button.mrt-del").Click();
        cut.Find(".mrt-alert-actions .mrt-danger").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrt-player")));
        Assert.Contains(toasts.Active, t => t.Message == "This recording was deleted.");
        Assert.Empty(cut.FindAll(".mrt-rec"));
    }

    [Fact]
    public void Player_shows_the_meeting_chat_and_a_timestamp_seeks_the_video()
    {
        var rec = Rec("Production review", canDelete: false, chat: 3);
        var api = new Api { RoomList = Rooms(), Recordings = [rec] };
        api.Chat[rec.Id] = $$"""
            [{"id":"{{Guid.NewGuid()}}","userId":"{{Guid.Empty}}","name":"","text":"Budi started recording","sentAt":"2026-10-12T03:00:00Z","kind":"system","offsetSeconds":0},
             {"id":"{{Guid.NewGuid()}}","userId":"{{Guid.NewGuid()}}","name":"Sari Ayu","text":"Can everyone see?","sentAt":"2026-10-12T03:00:12Z","kind":"text","offsetSeconds":12},
             {"id":"{{Guid.NewGuid()}}","userId":"{{Guid.NewGuid()}}","name":"<b>Budi</b>","text":"Sheet: https://ipop.santos.co.id/files/x. javascript:alert(1)","sentAt":"2026-10-12T03:01:15Z","kind":"text","offsetSeconds":3735}]
            """;
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));
        cut.Find("button.mrt-play").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".mrt-side-chat .mrt-ts").Count));
        Assert.Equal("Chat during the meeting", cut.Find(".mrt-side-chat h4").TextContent.Trim());
        Assert.Contains("Budi started recording", cut.Find(".mrt-side-chat .mrt-sysm").TextContent);
        var ts = cut.FindAll(".mrt-side-chat .mrt-ts");
        Assert.Equal(["00:12", "1:02:15"], ts.Select(x => x.TextContent.Trim()).ToArray());
        var budi = cut.FindAll(".mrt-side-chat .mrt-m")[1];
        Assert.Contains("<b>Budi</b>", budi.TextContent);                       // names and text are plain text
        Assert.Empty(budi.QuerySelectorAll("b b"));
        var link = Assert.Single(budi.QuerySelectorAll("a"));
        Assert.Equal("https://ipop.santos.co.id/files/x", link.GetAttribute("href"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));

        ts[1].Click();
        var seek = Assert.Single(ctx.JSInterop.Invocations["ipopRooms.seek"]);
        Assert.Equal(3735, Convert.ToInt32(seek.Arguments[1]));
    }

    [Fact]
    public void Player_without_chat_has_no_side_panel()
    {
        var rec = Rec("Production review", canDelete: false);
        using var ctx = Context(new Api { RoomList = Rooms(), Recordings = [rec] });
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));
        cut.Find("button.mrt-play").Click();
        cut.WaitForAssertion(() => cut.Find(".mrt-player video"));
        Assert.Empty(cut.FindAll(".mrt-side-chat"));
        Assert.Empty(cut.FindAll(".mrt-player button.mrt-del"));
    }

    // ---------------------------------------------------------------- Calendar password box

    private static readonly Guid Organizer = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Attendee = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Outsider = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Theory]
    [InlineData("organizer", true, true)]
    [InlineData("attendee", true, false)]
    [InlineData("outsider", false, false)]
    public void Calendar_password_box_shows_for_organizer_and_attendees_and_new_password_only_for_the_organizer(string who, bool box, bool regenerate)
    {
        var meetingId = Guid.NewGuid();
        var api = new Api();
        var me = who switch { "organizer" => Organizer, "attendee" => Attendee, _ => Outsider };
        // The API sends the password only to the organizer and attendees (MeetingDto.RoomPassword).
        api.Meetings[meetingId] = $$"""{"id":"{{meetingId}}","organizerId":"{{Organizer}}","roomPassword":{{(who == "outsider" ? "null" : "\"482917\"")}}}""";
        using var ctx = Context(api, me);
        var cut = ctx.Render<CalendarRoomPassword>(p => p
            .Add(c => c.MeetingId, meetingId).Add(c => c.Location, "SJA Hall · Toraja").Add(c => c.MeetingStatus, "scheduled"));

        if (!box)
        {
            cut.WaitForAssertion(() => Assert.Equal(1, api.MeetingGets));
            Assert.Empty(cut.FindAll(".mrt-pw-box"));
            return;
        }
        cut.WaitForAssertion(() => Assert.Equal("482 917", cut.Find(".mrt-pw-box .mrt-pw-v").TextContent.Trim()));
        Assert.Equal("Room password", cut.Find(".mrt-pw-box .mrt-pw-k").TextContent.Trim());
        Assert.NotNull(cut.Find(".mrt-pw-box button[title='Copy']"));
        Assert.Equal(regenerate, cut.FindAll(".mrt-pw-box button[title='New password']").Count == 1);
        Assert.Contains("Invited people don’t need it.", cut.Markup);
    }

    [Theory]
    [InlineData("Lantai 3", "scheduled")]
    [InlineData("SJA Hall · Toraja", "cancelled")]
    public void Calendar_password_box_is_not_asked_for_outside_hall_rooms_or_for_cancelled_meetings(string location, string status)
    {
        var meetingId = Guid.NewGuid();
        var api = new Api();
        api.Meetings[meetingId] = $$"""{"id":"{{meetingId}}","organizerId":"{{Organizer}}","roomPassword":"482917"}""";
        using var ctx = Context(api, Organizer);
        var cut = ctx.Render<CalendarRoomPassword>(p => p
            .Add(c => c.MeetingId, meetingId).Add(c => c.Location, location).Add(c => c.MeetingStatus, status));
        Thread.Sleep(100);
        Assert.Equal(0, api.MeetingGets);
        Assert.Empty(cut.FindAll(".mrt-pw-box"));
    }

    [Fact]
    public void Calendar_copy_and_new_password_after_confirming()
    {
        var meetingId = Guid.NewGuid();
        var api = new Api();
        api.Meetings[meetingId] = $$"""{"id":"{{meetingId}}","organizerId":"{{Organizer}}","roomPassword":"482917"}""";
        api.NewPassword = "105330";
        using var ctx = Context(api, Organizer);
        ctx.JSInterop.Setup<bool>("ipopLinks.copy", _ => true).SetResult(true);
        var confirm = ctx.Services.GetRequiredService<ConfirmService>();
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        var cut = ctx.Render<CalendarRoomPassword>(p => p
            .Add(c => c.MeetingId, meetingId).Add(c => c.Location, "SJA Hall · Toraja").Add(c => c.MeetingStatus, "scheduled"));
        cut.WaitForAssertion(() => cut.Find(".mrt-pw-box"));

        cut.Find(".mrt-pw-box button[title='Copy']").Click();
        cut.WaitForAssertion(() => Assert.Equal("482917", Assert.Single(ctx.JSInterop.Invocations["ipopLinks.copy"]).Arguments[0]));
        cut.WaitForAssertion(() => Assert.Contains(toasts.Active, t => t.Message == "Password copied."));

        cut.Find(".mrt-pw-box button[title='New password']").Click();
        cut.WaitForAssertion(() => Assert.NotNull(confirm.Current));          // the click is dispatched on the renderer
        Assert.Equal("Make a new password?", confirm.Current!.Title);
        Assert.Equal("The old one stops working for people who haven't joined yet.", confirm.Current.Body);
        confirm.Resolve(false);
        cut.WaitForAssertion(() => Assert.Null(confirm.Current));
        Assert.Empty(api.PasswordPosts);

        cut.Find(".mrt-pw-box button[title='New password']").Click();
        cut.WaitForAssertion(() => Assert.NotNull(confirm.Current));
        confirm.Resolve(true);
        cut.WaitForAssertion(() => Assert.Equal("105 330", cut.Find(".mrt-pw-v").TextContent.Trim()));
        Assert.Equal([meetingId], api.PasswordPosts);
    }

    // ---------------------------------------------------------------- overlay: chat link segments are cached (Task 7 minor)

    [Fact]
    public void Overlay_does_not_re_split_chat_lines_on_every_tick()
    {
        using var ctx = Context(new Api());
        var state = ctx.Services.GetRequiredService<MeetingRoomState>();
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, Organizer.ToString()));
        for (var i = 0; i < 5; i++)
            state.OnRoomChat($$"""{"id":"{{Guid.NewGuid()}}","userId":"{{Attendee}}","name":"Budi","text":"line {{i}} http://a.b/{{i}}","sentAt":"2026-10-12T03:1{{i}}:00Z","kind":"text"}""");
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-chat-btn").Click();
        Assert.Equal(5, cut.FindAll(".mr-chat .mr-m a").Count);
        var first = cut.Instance.SegmentRuns;
        Assert.Equal(5, first);
        for (var i = 0; i < 4; i++) cut.InvokeAsync(() => state.OnRoomPeople([]));   // per-second ticks / speaking updates
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mr-chat .mr-m a").Count));
        Assert.Equal(first, cut.Instance.SegmentRuns);
    }

    // ---------------------------------------------------------------- review fix round 1

    [Fact]
    public void Use_now_with_15_to_29_minutes_left_offers_that_time_locally()
    {
        var rooms = Rooms();
        // Next booking in 35 min: its lock window opens in ~25 min, so none of 30/60/90 fit.
        rooms[4] = Room("toraja", "Toraja", "free", next: Booking("Board", Now.AddMinutes(35).AddSeconds(30), Now.AddMinutes(95)));
        var api = new Api { RoomList = rooms };
        api.UseNowAnswers.Enqueue((HttpStatusCode.OK, $$"""{"meetingId":"{{Guid.NewGuid()}}","join":null}"""));
        api.JoinAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, """{"code":"media_unavailable"}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();

        Assert.Empty(cut.FindAll(".mrt-seg3 button"));
        var go = cut.Find(".mrt-un-go");
        Assert.Equal("Use for 25 min", go.TextContent.Trim());
        Assert.False(go.HasAttribute("disabled"));
        go.Click();
        cut.WaitForAssertion(() => Assert.Single(api.UseNowBodies));
        Assert.Equal(25, JsonDocument.Parse(api.UseNowBodies.Single()).RootElement.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public void Use_now_with_under_15_minutes_left_cannot_book()
    {
        var rooms = Rooms();
        rooms[4] = Room("toraja", "Toraja", "free", next: Booking("Board", Now.AddMinutes(20), Now.AddMinutes(80)));
        using var ctx = Context(new Api { RoomList = rooms });
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();
        Assert.True(cut.Find(".mrt-un-go").HasAttribute("disabled"));
    }

    [Fact]
    public void Pin_input_keeps_only_six_digits_in_the_field_and_backspace_follows()
    {
        using var ctx = Context(new Api { RoomList = Rooms() });
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[2].QuerySelector(".mrt-join")!.Click();

        cut.Find(".mrt-pin-in").Input("123456");
        cut.Find(".mrt-pin-in").Input("1234567");                 // a 7th digit is dropped, in the field too
        Assert.Equal("123456", cut.Find(".mrt-pin-in").GetAttribute("value"));
        Assert.Equal("6", cut.Find(".mrt-pin-in").GetAttribute("maxlength"));
        cut.Find(".mrt-pin-in").Input("12345");                   // backspace removes the last real digit
        Assert.Equal("12345", cut.Find(".mrt-pin-in").GetAttribute("value"));
        Assert.Equal(["1", "2", "3", "4", "5", "·"], cut.FindAll(".mrt-pin span").Select(s => s.TextContent).ToArray());
        cut.Find(".mrt-pin-in").Input("12345a");                  // a letter never reaches the field
        Assert.Equal("12345", cut.Find(".mrt-pin-in").GetAttribute("value"));
        // The field writes back through JS so the DOM value equals the filtered digits even when Blazor skips the diff.
        Assert.Contains(ctx.JSInterop.Invocations["ipopRooms.setValue"], i => (string?)i.Arguments[1] == "12345");
    }

    [Fact]
    public void A_successful_password_join_clears_the_pin()
    {
        var api = new Api { RoomList = Rooms() };
        api.JoinAnswers.Enqueue((HttpStatusCode.OK, """{"key":"kalosi","name":"Kalosi","liveKitRoom":"hall-kalosi","url":"wss://x","token":"t","videoAllowed":true,"identity":"me"}"""));
        using var ctx = Context(api);
        ctx.JSInterop.Setup<bool>("ipopRooms.join", _ => true).SetResult(true);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[2].QuerySelector(".mrt-join")!.Click();
        cut.Find(".mrt-pin-in").Input("482917");
        cut.Find(".mrt-pin-go").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrt-sheet")));
        var pin = typeof(MeetingRoomsTab).GetField("pin", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal("", pin.GetValue(cut.Instance));
    }

    [Fact]
    public void After_use_now_the_card_is_busy_while_the_rooms_reload()
    {
        var api = new Api { RoomList = Rooms() };
        api.UseNowAnswers.Enqueue((HttpStatusCode.OK, $$"""{"meetingId":"{{Guid.NewGuid()}}","join":null}"""));
        api.JoinAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, """{"code":"media_unavailable"}"""));
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(Slow);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.Click();
        api.RoomsGate = new TaskCompletionSource();               // the reload after booking is slow
        cut.Find(".mrt-un-go").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrt-sheet")));
        Assert.True(cut.FindAll(".mrt-room")[4].QuerySelector(".mrt-join")!.HasAttribute("disabled"));
        api.RoomsGate.SetResult();
        cut.WaitForAssertion(() => Assert.Equal(["toraja"], api.Joins));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void In_meeting_people_sheet_shows_the_room_password_only_to_who_the_api_gives_it(bool invited)
    {
        var meetingId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var api = new Api();
        // GET /api/meetings/{id}: organizer/invitee get the password; a password guest is not an attendee (404).
        if (invited) api.Meetings[meetingId] = $$"""{"id":"{{meetingId}}","organizerId":"{{Organizer}}","roomPassword":"482917"}""";
        using var ctx = Context(api, Organizer);
        var state = ctx.Services.GetRequiredService<MeetingRoomState>();
        state.SetForTest(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", true, (invited ? Organizer : Outsider).ToString()));
        state.OnRoomMetadata($$$"""{"booking":{"meetingId":"{{{meetingId}}}","organizerId":"{{{Organizer}}}","endAt":"{{{Now.AddHours(1):o}}}"}}""");
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-people-btn").Click();

        if (invited)
        {
            cut.WaitForAssertion(() => Assert.Contains("482 917", cut.Find(".mr-people .mr-pw-row").TextContent));
            Assert.NotNull(cut.Find(".mr-pw-row .mr-pw-copy"));
        }
        else
        {
            cut.WaitForAssertion(() => Assert.Equal(1, api.MeetingGets));
            Assert.Empty(cut.FindAll(".mr-pw-row"));
            Assert.DoesNotContain("482", cut.Markup);
        }
    }

    // ---------------------------------------------------------------- harness

    private static void Slow(ComponentParameterCollectionBuilder<MeetingRoomsTab> p) => p.Add(c => c.PollInterval, TimeSpan.FromHours(1));

    private static string JsonOrEmpty(string body) => string.IsNullOrWhiteSpace(body) ? "" : body;

    private static BunitContext Context(Api api, Guid? me = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        if (me is { } id)
            typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
                new DirectoryEmployeeDto("ME", "Me", null, null, null, null, null, null, "me@x", null, null, "on", UserId: id));
        var calls = new CallState(auth, http, js);
        var toasts = new ToastService();
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(new MeetingRoomState(auth, js, calls, toasts));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddSingleton<PeopleLinks>();
        ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    private sealed class Api : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        public List<MeetingRoomDto> RoomList { get; set; } = [];
        public volatile TaskCompletionSource? RoomsGate;
        public List<MeetingRecordingDto> Recordings { get; set; } = [];
        public Queue<(HttpStatusCode, string)> JoinAnswers { get; } = new();
        public Queue<(HttpStatusCode, string)> UseNowAnswers { get; } = new();
        public List<string> Joins { get; } = [];
        public List<string> JoinBodies { get; } = [];
        public List<string> UseNowRooms { get; } = [];
        public List<string> UseNowBodies { get; } = [];
        public List<Guid> Deletes { get; } = [];
        public HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.NoContent;
        public Dictionary<Guid, string> Chat { get; } = [];
        public Dictionary<Guid, string> Meetings { get; } = [];
        public string NewPassword { get; set; } = "000000";
        public List<Guid> PasswordPosts { get; } = [];
        private int meetingGets;
        public int MeetingGets => Volatile.Read(ref meetingGets);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var seg = path.Split('/');
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms")
            {
                if (RoomsGate is { } gate) await gate.Task;
                return Json(RoomList);
            }
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms/recordings") return Json(Recordings);
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/meeting-rooms/recordings/") && path.EndsWith("/chat"))
                return Raw(HttpStatusCode.OK, Chat.TryGetValue(Guid.Parse(seg[^2]), out var c) ? c : "[]");
            if (request.Method == HttpMethod.Delete && path.StartsWith("/api/meeting-rooms/recordings/"))
            {
                lock (Deletes) Deletes.Add(Guid.Parse(seg[^1]));
                return new HttpResponseMessage(DeleteStatus) { Content = new StringContent(DeleteStatus == HttpStatusCode.Conflict ? """{"code":"still_recording"}""" : "", Encoding.UTF8, "application/json") };
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/ticket"))
                return Json(new { url = $"/api/meeting-rooms/recordings/{seg[^2]}/file?t=tk" });
            if (request.Method == HttpMethod.Post && path.EndsWith("/join"))
            {
                lock (Joins) { Joins.Add(seg[^2]); JoinBodies.Add(body); }
                return JoinAnswers.TryDequeue(out var a) ? Raw(a.Item1, a.Item2) : Raw(HttpStatusCode.ServiceUnavailable, """{"code":"media_unavailable"}""");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/use-now"))
            {
                lock (UseNowRooms) { UseNowRooms.Add(seg[^2]); UseNowBodies.Add(body); }
                return UseNowAnswers.TryDequeue(out var a) ? Raw(a.Item1, a.Item2) : Raw(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/meetings/") && Guid.TryParse(seg[^1], out var mid))
            {
                Interlocked.Increment(ref meetingGets);
                return Meetings.TryGetValue(mid, out var m) ? Raw(HttpStatusCode.OK, m) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/room-password"))
            {
                lock (PasswordPosts) PasswordPosts.Add(Guid.Parse(seg[^2]));
                return Json(new { password = NewPassword });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T body) => Raw(HttpStatusCode.OK, JsonSerializer.Serialize(body, Web));
        private static HttpResponseMessage Raw(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
