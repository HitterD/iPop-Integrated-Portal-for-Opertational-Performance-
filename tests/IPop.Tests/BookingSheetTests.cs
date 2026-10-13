using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Meetings;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §2/§3 (mockup screen 2 Fits/Clash + 3 Repeat check): the booking sheet that replaces Use now.
[Collection("AvatarPhotos")]
public class BookingSheetTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static readonly DateOnly Day = new(2026, 10, 13);
    private static DateTime At(int h, int m = 0, int days = 0) => new DateTime(2026, 10, 13, h, m, 0, DateTimeKind.Utc).AddDays(days).AddHours(-7);
    /// 09:50 WIB, like the mockup.
    private static readonly DateTime NowUtc = At(9, 50);

    private static RoomBlockDto Block(string title, DateTime s, DateTime e, bool visible = true) =>
        new(visible ? Guid.NewGuid() : null, visible ? title : "Booked", s, e, false, visible);

    private static RoomDayDto KalosiDay(DateOnly? date = null, params RoomBlockDto[] blocks)
    {
        var d = date ?? Day;
        var shift = d.DayNumber - Day.DayNumber;
        return new("kalosi", "Kalosi", 20, d, At(7, days: shift), At(19, days: shift), blocks);
    }

    private static RoomDayDto MockupDay() => KalosiDay(null,
        Block("Morning huddle", At(7, 30), At(8, 30)),
        Block("Weekly ICT", At(10), At(11)),
        Block("Finance sync", At(15, 45), At(17)));

    private static IRenderedComponent<BookingSheet> Render(BunitContext ctx, string mode, DateTime? prefill = null, string room = "kalosi",
        List<string>? joined = null, List<string>? booked = null) =>
        ctx.Render<BookingSheet>(p => p.Add(c => c.RoomKey, room).Add(c => c.Mode, mode).Add(c => c.PrefillStart, prefill)
            .Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib).Add(c => c.RefreshDelay, TimeSpan.Zero)
            .Add(c => c.OnJoin, k => joined?.Add(k)).Add(c => c.OnBooked, k => booked?.Add(k)));

    private static string Primary(IRenderedComponent<BookingSheet> cut) => cut.Find(".bs-go").TextContent.Trim();

    [Fact]
    public void Header_and_fields_follow_the_mockup()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".tl-track")));
        Assert.Equal("Book Kalosi", cut.Find("#bs-title").TextContent);
        Assert.Contains("Kalosi · up to 20 people · Video on", cut.Find(".sh-h small").TextContent);
        Assert.Contains("r-kalosi", cut.Find("section.sheet2").ClassList);
        Assert.Equal(["Now", "30m", "1h", "2h", "Until 17:00"], cut.FindAll(".qc").Select(b => b.TextContent.Trim()).ToArray());
        Assert.Equal("1 h", cut.Find(".durt").TextContent.Trim());
        Assert.Contains("1h", cut.Find(".qc.on").TextContent);
        Assert.Contains("Room locks for this booking; invited people join directly; you get a password for guests.", cut.Find(".note-s").TextContent);
        Assert.Equal("More options…", cut.Find(".bs-more").TextContent.Trim());
        Assert.Equal("Book", Primary(cut));
        Assert.Equal(["Other rooms free at this time:", "Robusta· free", "Toraja· free"],
            new[] { cut.Find(".others > span").TextContent }.Concat(cut.FindAll(".rchip").Select(c => c.TextContent.Trim())).ToArray());
    }

    [Fact]
    public void Now_mode_starts_now_and_says_Book_and_join_capped_before_the_next_booking()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeNow);
        cut.WaitForAssertion(() => Assert.Equal("Book and join", Primary(cut)));
        Assert.Contains("on", cut.Find(".qc.now").ClassList);
        // 09:50 + 1 h would run into Weekly ICT at 10:00; the lock window opens 09:50 → Use now can't fit at all.
        Assert.Contains("Until 09:50 (next booking)", cut.Find(".bs-cap").TextContent);
        Assert.True(cut.Find(".bs-go").HasAttribute("disabled"));
    }

    [Fact]
    public void Now_chip_on_a_free_room_books_and_joins_with_endAt()
    {
        var api = new Api { Days = { ["toraja"] = KalosiDay() with { Key = "toraja", Name = "Toraja" } } };
        api.UseNow.Enqueue((HttpStatusCode.OK, $$"""{"meetingId":"{{Guid.NewGuid()}}","join":null}"""));
        using var ctx = Context(api);
        var joined = new List<string>();
        var cut = Render(ctx, BookingSheet.ModeBook, At(13), room: "toraja", joined: joined);
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.Find(".qc.now").Click();
        Assert.Equal("Book and join", Primary(cut));
        cut.Find(".bs-title").Input("Quick sync");
        cut.Find(".bs-go").Click();

        cut.WaitForAssertion(() => Assert.Equal(["toraja"], joined));
        var body = JsonDocument.Parse(api.UseNowBodies.Single()).RootElement;
        Assert.Equal(NowUtc.AddHours(1), body.GetProperty("endAt").GetDateTime().ToUniversalTime());
        Assert.Equal("Quick sync", body.GetProperty("title").GetString());
        Assert.Equal(0, body.GetProperty("attendeeNiks").GetArrayLength());
        Assert.False(body.TryGetProperty("minutes", out _));
        Assert.Empty(api.EventBodies);
    }

    [Fact]
    public void Future_start_says_Book_and_posts_a_room_calendar_event()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var booked = new List<string>();
        var cut = Render(ctx, BookingSheet.ModeBook, At(13), booked: booked);
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.Find(".bs-title").Input("Q4 network rollout review");
        cut.FindAll(".qc")[3].Click();                 // 2h
        cut.Find(".bs-go").Click();

        cut.WaitForAssertion(() => Assert.Equal(["kalosi"], booked));
        var b = JsonDocument.Parse(api.EventBodies.Single()).RootElement;
        Assert.Equal("Q4 network rollout review", b.GetProperty("title").GetString());
        Assert.Equal(At(13), b.GetProperty("startAt").GetDateTime().ToUniversalTime());
        Assert.Equal(At(15), b.GetProperty("endAt").GetDateTime().ToUniversalTime());
        Assert.Equal("SJA Hall · Kalosi", b.GetProperty("location").GetString());
        Assert.Equal("room", b.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("recurrence").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("overrides").ValueKind);
        Assert.Empty(api.UseNowBodies);
    }

    [Fact]
    public void Book_mode_without_prefill_starts_at_the_next_free_slot_today()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook);
        // Next quarter after 09:50 is 10:00, Weekly ICT holds 10–11 → 11:00–12:00.
        cut.WaitForAssertion(() => Assert.Equal("11:00–12:00", cut.Find(".tl-sel").GetAttribute("aria-valuetext")));
        Assert.Empty(cut.FindAll(".tl-clash"));
    }

    [Fact]
    public void Clash_disables_the_primary_with_a_hint_and_Try_fixes_it()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(10, 30));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".tl-clash")));
        cut.FindAll(".qc")[2].Click();                 // 1h → 10:30–11:30 still clashes
        Assert.Contains("Clashes with Weekly ICT 10:00–11:00", cut.Find(".tl-clash").TextContent);
        Assert.True(cut.Find(".bs-go").HasAttribute("disabled"));
        Assert.Equal("Try 11:00–12:00", cut.Find(".tl-try").TextContent.Trim());

        cut.Find(".tl-try").Click();
        Assert.Empty(cut.FindAll(".tl-clash"));
        Assert.False(cut.Find(".bs-go").HasAttribute("disabled"));
        Assert.Equal("11:00–12:00", cut.Find(".tl-sel").GetAttribute("aria-valuetext"));
    }

    [Fact]
    public void Other_room_chip_switches_the_room_and_reloads_its_day()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay(), ["toraja"] = KalosiDay() with { Key = "toraja", Name = "Toraja" } } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(10, 30));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".tl-clash")));
        cut.FindAll(".rchip").Single(c => c.TextContent.Contains("Toraja")).Click();
        cut.WaitForAssertion(() => Assert.Equal("Book Toraja", cut.Find("#bs-title").TextContent));
        Assert.Contains("r-toraja", cut.Find("section.sheet2").ClassList);
        Assert.Empty(cut.FindAll(".tl-clash"));
        Assert.Contains("/api/meeting-rooms/toraja/availability?date=2026-10-13", api.Gets);
    }

    [Fact]
    public void Repeat_shows_the_check_with_booked_count_and_Skip_or_Move_shape_the_payload()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        var dates = Enumerable.Range(0, 6).Select(i => Day.AddDays(7 * (i + 1))).ToList();
        api.Preview = JsonSerializer.Serialize(new
        {
            total = 6, booked = 2,
            occurrences = dates.Select((d, i) => new
            {
                date = d.ToString("yyyy-MM-dd"), startAt = At(13, days: 7 * (i + 1)), endAt = At(15, days: 7 * (i + 1)),
                free = i is not (2 or 4), bookedBy = i == 2 ? "Finance sync" : i == 4 ? "Booked" : null,
                freeRooms = i is 2 or 4 ? new[] { "toraja", "robusta" } : new[] { "toraja" }, reason = i is 2 or 4 ? "booked" : null,
            }),
        }, Web);
        using var ctx = Context(api);
        var booked = new List<string>();
        var cut = Render(ctx, BookingSheet.ModeBook, At(13), booked: booked);
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.FindAll(".qc")[3].Click();
        cut.Find(".bs-repeat .app-select-trigger").Click();
        cut.FindAll(".bs-repeat .app-select-opt").Single(o => o.TextContent.Contains("Weekly on Tuesday · 4 times")).MouseDown();
        cut.Find(".bs-go").Click();

        cut.WaitForAssertion(() => Assert.Contains("2 of 6 dates are booked", cut.Find(".bs-sum").TextContent));
        var preview = JsonDocument.Parse(api.PreviewBodies.Single()).RootElement;
        Assert.Equal("W:2;C:4", preview.GetProperty("recurrence").GetString());
        Assert.Equal("kalosi", preview.GetProperty("roomKey").GetString());
        Assert.Contains("Booked by “Finance sync” 13:00–15:00", cut.FindAll(".rp.clashed")[0].TextContent);
        Assert.Contains("Already booked 13:00–15:00", cut.FindAll(".rp.clashed")[1].TextContent);   // hidden title: not “Booked by “Booked””
        Assert.DoesNotContain("“Booked”", cut.Markup);
        Assert.Equal("Skip clashes and book 4", Primary(cut));

        // Move date 3 to Toraja; date 5 stays skipped.
        cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!.Click();
        cut.FindAll(".bs-move-menu [role=menuitemradio]")[0].Click();
        Assert.Equal("Book 5 · 1 in Toraja", Primary(cut));
        cut.Find(".bs-go").Click();
        cut.WaitForAssertion(() => Assert.Equal(["kalosi"], booked));

        var body = JsonDocument.Parse(api.EventBodies.Single()).RootElement;
        Assert.Equal("W:2;C:4", body.GetProperty("recurrence").GetString());
        var ov = body.GetProperty("overrides").EnumerateArray().ToList();
        Assert.Equal(2, ov.Count);
        Assert.Equal(dates[2].ToString("yyyy-MM-dd"), ov[0].GetProperty("date").GetString());
        Assert.False(ov[0].GetProperty("skip").GetBoolean());
        Assert.Equal("toraja", ov[0].GetProperty("roomKey").GetString());
        Assert.Equal(dates[4].ToString("yyyy-MM-dd"), ov[1].GetProperty("date").GetString());
        Assert.True(ov[1].GetProperty("skip").GetBoolean());
    }

    /// v3 gaps: "Move to" offers every room free on that date (a small menu with colour swatches), not just the first.
    [Fact]
    public void Move_to_offers_every_free_room_and_the_button_counts_them()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        var dates = Enumerable.Range(0, 4).Select(i => Day.AddDays(7 * (i + 1))).ToList();
        api.Preview = JsonSerializer.Serialize(new
        {
            total = 4, booked = 2,
            occurrences = dates.Select((d, i) => new
            {
                date = d.ToString("yyyy-MM-dd"), startAt = At(13, days: 7 * (i + 1)), endAt = At(14, days: 7 * (i + 1)),
                free = i is not (1 or 3), bookedBy = i is 1 or 3 ? "Finance sync" : null,
                freeRooms = i == 1 ? new[] { "toraja", "robusta", "arabika" } : i == 3 ? new[] { "kapal-api" } : Array.Empty<string>(),
                reason = i is 1 or 3 ? "booked" : null,
            }),
        }, Web);
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.Find(".bs-repeat .app-select-trigger").Click();
        cut.FindAll(".bs-repeat .app-select-opt")[2].MouseDown();
        cut.Find(".bs-go").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".rp.clashed").Count));
        Assert.Equal("Skip clashes and book 2", Primary(cut));

        // One free room: a plain choice, as before.
        var single = cut.FindAll(".rp.clashed")[1];
        Assert.Empty(single.QuerySelectorAll(".bs-move"));
        Assert.Contains("Move to Kapal Api", single.TextContent);

        // Three free rooms: "Move to…" opens a menu of all three with swatches.
        var moveBtn = cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!;
        Assert.Equal("Move to…", moveBtn.TextContent.Trim());
        Assert.Equal("menu", moveBtn.GetAttribute("aria-haspopup"));
        moveBtn.Click();
        var opts = cut.FindAll(".bs-move-menu [role=menuitemradio]");
        Assert.Equal(["Toraja", "Robusta", "Arabika"], opts.Select(o => o.TextContent.Trim()).ToArray());
        Assert.Equal(["r-toraja", "r-robusta", "r-arabika"], opts.Select(o => o.QuerySelector(".sw")!.ClassList.First(c => c.StartsWith("r-"))).ToArray());
        opts[1].Click();
        Assert.Empty(cut.FindAll(".bs-move-menu"));
        Assert.Equal("Move to Robusta", cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!.TextContent.Trim());
        Assert.Contains("on", cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!.ClassList);
        Assert.Equal("Book 3 · 1 in Robusta", Primary(cut));

        cut.FindAll(".rp.clashed")[1].QuerySelectorAll(".seg2 button")[1].Click();
        Assert.Equal("Book 4 · 2 moved", Primary(cut));

        // Escape closes the menu without choosing; Skip undoes the move.
        cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!.Click();
        cut.Find(".bs-move-menu").KeyDown("Escape");
        Assert.Empty(cut.FindAll(".bs-move-menu"));
        cut.FindAll(".rp.clashed")[0].QuerySelectorAll(".seg2 button")[0].Click();
        Assert.Equal("Move to…", cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!.TextContent.Trim());
        Assert.Equal("Book 3 · 1 in Kapal Api", Primary(cut));

        cut.FindAll(".rp.clashed")[0].QuerySelector("button.bs-move")!.Click();
        cut.FindAll(".bs-move-menu [role=menuitemradio]")[2].Click();
        cut.Find(".bs-go").Click();
        cut.WaitForAssertion(() => Assert.Single(api.EventBodies));
        var ov = JsonDocument.Parse(api.EventBodies.Single()).RootElement.GetProperty("overrides").EnumerateArray().ToList();
        Assert.Equal(["arabika", "kapal-api"], ov.Select(o => o.GetProperty("roomKey").GetString()).ToArray());
    }

    [Fact]
    public void Repeat_race_409_with_a_date_highlights_that_row()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        var d = Day.AddDays(7);
        api.Preview = JsonSerializer.Serialize(new
        {
            total = 1, booked = 0,
            occurrences = new[] { new { date = d.ToString("yyyy-MM-dd"), startAt = At(13, days: 7), endAt = At(14, days: 7), free = true, bookedBy = (string?)null, freeRooms = Array.Empty<string>(), reason = (string?)null } },
        }, Web);
        api.EventAnswer = (HttpStatusCode.Conflict, $$"""{"code":"room_booked","message":"Booked","date":"{{d:yyyy-MM-dd}}"}""");
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.Find(".bs-repeat .app-select-trigger").Click();
        cut.FindAll(".bs-repeat .app-select-opt")[2].MouseDown();
        cut.Find(".bs-go").Click();
        cut.WaitForAssertion(() => Assert.Contains("All 1 dates are free", cut.Find(".bs-sum").TextContent));
        cut.Find(".bs-go").Click();
        cut.WaitForAssertion(() => Assert.Contains("failed", cut.Find(".rp").ClassList));
        Assert.Contains("was just booked by someone else", cut.Find(".bs-err").TextContent);
    }

    [Fact]
    public void Booked_meanwhile_reloads_the_day_and_shows_the_clash()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        api.EventAnswer = (HttpStatusCode.Conflict, """{"code":"room_booked","message":"Booked"}""");
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        api.Days["kalosi"] = MockupDay() with { Blocks = [.. MockupDay().Blocks, Block("Board", At(13), At(14))] };
        cut.Find(".bs-go").Click();
        cut.WaitForAssertion(() => Assert.Contains("Clashes with Board 13:00–14:00", cut.Find(".tl-clash").TextContent));
        Assert.Equal("Someone just booked this time. Pick another time or another room.", cut.Find(".bs-err").TextContent.Trim());
    }

    [Fact]
    public void More_options_navigates_with_the_prefill_and_hands_people_over_in_memory()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.Find(".bs-title").Input("Weekly ICT");
        // Add someone through the picker's suggestion list.
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ip-row")));
        cut.Find(".ip-row").Click();
        cut.Find(".bs-more").Click();

        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        var uri = new Uri(nav.Uri);
        Assert.Equal("/calendar", uri.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("1", q["new"]);
        Assert.Equal("room", q["kind"]);
        Assert.Equal("kalosi", q["room"]);
        Assert.Equal(At(13), DateTime.Parse(q["start"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        Assert.Equal(At(14), DateTime.Parse(q["end"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        Assert.Equal("Weekly ICT", q["title"]);
        Assert.DoesNotContain("NIK", nav.Uri);
        var handed = ctx.Services.GetRequiredService<MeetingDraftHandoff>().Take();
        Assert.Equal(["Alphin Santoso"], handed!.Select(p => p.Name).ToArray());
        Assert.Null(ctx.Services.GetRequiredService<MeetingDraftHandoff>().Take());
    }

    [Fact]
    public void Limits_are_checked_locally_with_the_server_messages()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(11));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        cut.FindAll(".qc")[4].Click();                 // Until 17:00 → 11:00–17:00 clashes with Finance sync
        Assert.NotNull(cut.Find(".tl-clash"));
        Assert.Empty(cut.FindAll(".bs-limit"));
    }

    /// Picks a time from the pill's 15-minute list (v3 gaps: the sheet's times are pills, not typed fields).
    private static void Type(IRenderedComponent<BookingSheet> cut, string field, string value)
    {
        cut.Find($"button.dp-pill.{field}").Click();
        cut.FindAll(".dp-topt").Single(b => b.TextContent == value).Click();
    }

    [Fact]
    public void Date_and_times_are_pills_with_the_v3_limits()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        Assert.Empty(cut.FindAll(".when input"));
        Assert.Equal("Today, 13 Oct", cut.Find("button.dp-pill.bs-date .dp-pill-t").TextContent);
        Assert.Equal("13:00", cut.Find("button.dp-pill.bs-start").TextContent.Trim());
        Assert.Equal("14:00", cut.Find("button.dp-pill.bs-end").TextContent.Trim());

        // Start: nothing before now (09:50) today.
        cut.Find("button.dp-pill.bs-start").Click();
        string Dis(string t) => cut.FindAll(".dp-topt").Single(b => b.TextContent == t).GetAttribute("aria-disabled")!;
        Assert.Equal(("true", "false"), (Dis("09:45"), Dis("10:00")));
        cut.Find("button.dp-pill.bs-start").Click();
        // End: after the start, at most 8 h.
        cut.Find("button.dp-pill.bs-end").Click();
        Assert.Equal(("true", "false", "false", "true"), (Dis("13:00"), Dis("13:15"), Dis("21:00"), Dis("21:15")));
        cut.FindAll(".dp-topt").Single(b => b.TextContent == "15:30").Click();
        Assert.Equal("2 h 30 min", cut.Find(".durt").TextContent.Trim());

        // Date: today .. 60 days ahead.
        cut.Find("button.dp-pill.bs-date").Click();
        Assert.Equal("true", cut.Find(".dp-day[data-date='2026-10-12']").GetAttribute("aria-disabled"));
        cut.Find(".dp-day[data-date='2026-10-20']").Click();
        cut.WaitForAssertion(() => Assert.Equal("Tue 20 Oct", cut.Find("button.dp-pill.bs-date .dp-pill-t").TextContent));
    }

    /// QA Task 11: an end before the start (20:00 → 01:00) crossed midnight and said "at least 15 minutes". With the pill
    /// list such an end can't be picked at all; 00:00 (the end of the day) still can.
    [Fact]
    public void End_before_start_cannot_be_picked()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(11));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        Type(cut, "bs-start", "18:00");
        cut.Find("button.dp-pill.bs-end").Click();
        string Dis(string t) => cut.FindAll(".dp-topt").Single(b => b.TextContent == t).GetAttribute("aria-disabled")!;
        Assert.Equal(("true", "false"), (Dis("01:00"), Dis("00:00")));
        cut.FindAll(".dp-topt").Single(b => b.TextContent == "01:00").Click();
        Assert.Equal("19:00", cut.Find("button.dp-pill.bs-end").TextContent.Trim());
        Assert.Empty(cut.FindAll(".bs-limit"));
    }

    /// QA Task 11: a time already over today (08:00 at 09:50) said "at least 15 minutes" and asked the server for free
    /// rooms from now to 09:00 (400). Past slots are now disabled; no /free query with to ≤ from is ever sent.
    [Fact]
    public void A_start_already_over_cannot_be_picked_and_asks_no_bad_free_rooms()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(11));
        cut.WaitForAssertion(() => Assert.Equal("Book", Primary(cut)));
        lock (api.Gets) api.Gets.Clear();
        Type(cut, "bs-start", "08:00");
        Assert.Equal("11:00", cut.Find("button.dp-pill.bs-start").TextContent.Trim());
        Assert.Empty(cut.FindAll(".bs-limit"));
        lock (api.Gets)
            foreach (var g in api.Gets.Where(g => g.StartsWith("/api/meeting-rooms/free")))
            {
                var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + g).Query);
                Assert.True(DateTime.Parse(q["from"]!, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    < DateTime.Parse(q["to"]!, null, System.Globalization.DateTimeStyles.RoundtripKind), g);
            }
    }

    [Fact]
    public void No_key_in_markup()
    {
        var api = new Api { Days = { ["kalosi"] = MockupDay() } };
        using var ctx = Context(api);
        var cut = Render(ctx, BookingSheet.ModeBook, At(13));
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ip-row")));
        cut.Find(".ip-row").Click();
        Assert.DoesNotContain("NIK8", cut.Markup);
    }

    // ---------------------------------------------------------------- harness

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    internal static BunitContext Context(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new InviteApi(auth));
        ctx.Services.AddSingleton(new MeetingDraftHandoff());
        ctx.Services.AddScoped<OverlayStack>();
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    internal sealed class Api : HttpMessageHandler
    {
        public Dictionary<string, RoomDayDto> Days { get; } = [];
        public Queue<(HttpStatusCode, string)> UseNow { get; } = new();
        public (HttpStatusCode, string) EventAnswer { get; set; } = (HttpStatusCode.OK, "{}");
        public string Preview { get; set; } = """{"total":0,"booked":0,"occurrences":[]}""";
        public List<string> Gets { get; } = [];
        public List<string> UseNowBodies { get; } = [];
        public List<string> EventBodies { get; } = [];
        public List<string> PreviewBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (request.Method == HttpMethod.Get) lock (Gets) Gets.Add(request.RequestUri.PathAndQuery);
            var seg = path.Split('/');
            if (request.Method == HttpMethod.Get && path.EndsWith("/availability"))
                return Days.TryGetValue(seg[^2], out var d) ? Json(d) : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms/free")
            {
                var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var from = DateTime.Parse(q["from"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                var to = DateTime.Parse(q["to"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                var rooms = new[] { ("robusta", "Robusta"), ("arabika", "Arabika"), ("kalosi", "Kalosi"), ("kapal-api", "Kapal Api"), ("toraja", "Toraja") };
                return Json(rooms.Select(r =>
                {
                    var busy = r.Item1 == "arabika" || (Days.TryGetValue(r.Item1, out var dd) && dd.Blocks.Any(b => b.StartAt < to && from < b.EndAt))
                        || (r.Item1 == "kapal-api");
                    return new RoomFreeDto(r.Item1, r.Item2, 20, !busy, busy ? "Booked" : null);
                }).ToList());
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/use-now"))
            {
                lock (UseNowBodies) UseNowBodies.Add(body);
                return UseNow.TryDequeue(out var a) ? Raw(a.Item1, a.Item2) : Raw(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (request.Method == HttpMethod.Post && path == "/api/calendar/series/preview")
            {
                lock (PreviewBodies) PreviewBodies.Add(body);
                return Raw(HttpStatusCode.OK, Preview);
            }
            if (request.Method == HttpMethod.Post && path == "/api/calendar/events")
            {
                lock (EventBodies) EventBodies.Add(body);
                return Raw(EventAnswer.Item1, EventAnswer.Item2);
            }
            if (path == "/api/invite/suggestions")
                return Json(new InviteSuggestionsDto([new("NIK800001", "Alphin Santoso", "ICT")], [], [], []));
            if (path == "/api/invite/busy") return Json(Array.Empty<PersonBusyDto>());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T body) => Raw(HttpStatusCode.OK, JsonSerializer.Serialize(body, Web));
        private static HttpResponseMessage Raw(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
