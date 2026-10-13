using System.Security.Claims;
using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Microsoft.AspNetCore.Http;
using Xunit;
using static IPop.Api.Endpoints.InvitePickerEndpoints;

namespace IPop.Tests;

/// Meeting scheduling v3 §4: data for the one invite picker — suggestions before typing, search by name or
/// department, whole-group expand and batch free/busy (blocks only, never titles; calendar access v2).
public class InvitePickerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static DateTime Day(int offset) => DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(offset), DateTimeKind.Utc);

    private static User Person(TestDb t, string nik, string name, Department? dept = null, bool active = true)
    {
        var u = t.AddUser(nik, active);
        u.Name = name;
        u.Position = "Secret position " + nik;
        u.DepartmentId = dept?.Id;
        t.Db.SaveChanges();
        return u;
    }

    private static Department Dept(TestDb t, string name, bool active = true)
    {
        var d = new Department { Id = Guid.NewGuid(), Code = name.ToUpperInvariant(), Name = name, IsActive = active };
        t.Db.Departments.Add(d);
        t.Db.SaveChanges();
        return d;
    }

    private static void Meeting(TestDb t, Guid organizer, DateTime start, string status = "aktif", params Guid[] attendees)
    {
        var m = new Meeting { Id = Guid.NewGuid(), Title = "M", StartAt = start, EndAt = start.AddHours(1), OrganizerId = organizer, Status = status };
        t.Db.Meetings.Add(m);
        foreach (var a in attendees.Prepend(organizer).Distinct())
            t.Db.MeetingAttendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = a, RsvpStatus = a == organizer ? "accept" : "pending" });
        t.Db.SaveChanges();
    }

    private static HttpContext Http(User u) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("nik", u.Nik)], "Bearer")),
    };
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;
    private static string Json(object o) => JsonSerializer.Serialize(o, Web);

    // ---------------------------------------------------------------- suggestions

    [Fact]
    public async Task Frequent_is_ordered_by_co_attendance_and_skips_me_inactive_old_and_cancelled()
    {
        using var t = new TestDb();
        var me = Person(t, "S1", "Me");
        var a = Person(t, "S2", "Ani"); var b = Person(t, "S3", "Budi"); var c = Person(t, "S4", "Citra");
        var gone = Person(t, "S5", "Gone", active: false); var old = Person(t, "S6", "Old"); var cx = Person(t, "S7", "Cancelled");
        Meeting(t, me.Id, Day(-3), attendees: [a.Id, b.Id, gone.Id]);
        Meeting(t, b.Id, Day(-2), attendees: [me.Id, a.Id]);             // I attended
        Meeting(t, me.Id, Day(2), attendees: [a.Id, c.Id]);              // upcoming counts too
        Meeting(t, me.Id, Day(-120), attendees: [old.Id]);               // older than 90 days
        Meeting(t, me.Id, Day(-1), MeetingStatus.Cancelled, cx.Id);
        Meeting(t, a.Id, Day(-1), attendees: [c.Id]);                    // not mine

        var s = await SuggestionsAsync(t.Db, me.Id, DateTime.UtcNow, default);
        Assert.Equal(["Ani", "Budi", "Citra"], s.Frequent.Select(p => p.Name));
        Assert.Equal(["S2", "S3", "S4"], s.Frequent.Select(p => p.Key));
    }

    [Fact]
    public async Task Department_people_chat_groups_and_departments_are_suggested()
    {
        using var t = new TestDb();
        var ict = Dept(t, "ICT"); var fin = Dept(t, "Finance"); Dept(t, "Empty"); var closed = Dept(t, "Closed", active: false);
        var me = Person(t, "S1", "Me", ict);
        Person(t, "S2", "Zed", ict); Person(t, "S3", "Ari", ict); Person(t, "S4", "Inactive", ict, active: false);
        Person(t, "S5", "Fina", fin); Person(t, "S6", "Closed one", closed);

        var g1 = t.AddRoom("group", "ICT IPOP"); g1.LastMessageAt = DateTime.UtcNow.AddHours(-5); g1.AvatarColor = "#2e9e5b";
        var g2 = t.AddRoom("group", "Lunch"); g2.LastMessageAt = DateTime.UtcNow;
        var left = t.AddRoom("group", "Left group");
        var dm = t.AddRoom("dm");
        t.Db.SaveChanges();
        t.AddMember(g1.Id, me.Id); t.AddMember(g1.Id, t.Db.Users.Single(u => u.Nik == "S2").Id);
        t.AddMember(g2.Id, me.Id);
        t.AddMember(left.Id, me.Id, leftAt: DateTime.UtcNow);
        t.AddMember(dm.Id, me.Id);

        var s = await SuggestionsAsync(t.Db, me.Id, DateTime.UtcNow, default);
        Assert.Equal(["Ari", "Zed"], s.Department.Select(p => p.Name));
        Assert.All(s.Department, p => Assert.Equal("ICT", p.Department));
        Assert.Equal(["Lunch", "ICT IPOP"], s.ChatGroups.Select(g => g.Name));
        Assert.Equal(2, s.ChatGroups.Single(g => g.Name == "ICT IPOP").Members);
        Assert.Equal("#2e9e5b", s.ChatGroups.Single(g => g.Name == "ICT IPOP").Color);
        Assert.All(s.ChatGroups, g => Assert.Equal("chat", g.Kind));
        Assert.Equal(["Finance", "ICT"], s.Departments.Select(d => d.Name));
        Assert.Equal(3, s.Departments.Single(d => d.Name == "ICT").Members);   // me, Zed, Ari (inactive not counted)
        Assert.All(s.Departments, d => Assert.Equal("department", d.Kind));
    }

    [Fact]
    public async Task Picker_dtos_carry_no_position_or_visible_nik_field()
    {
        using var t = new TestDb();
        var ict = Dept(t, "ICT");
        var me = Person(t, "S1", "Me", ict);
        Person(t, "S2", "Ani", ict);
        var json = Json(await SuggestionsAsync(t.Db, me.Id, DateTime.UtcNow, default));
        Assert.DoesNotContain("Secret position", json);
        Assert.DoesNotContain("\"nik\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["key", "name", "department", "photoUrl"],
            JsonDocument.Parse(json).RootElement.GetProperty("department")[0].EnumerateObject().Select(p => p.Name));
    }

    // ---------------------------------------------------------------- search

    [Fact]
    public async Task Search_matches_name_or_department_and_needs_two_characters()
    {
        using var t = new TestDb();
        var fin = Dept(t, "Finance"); var ict = Dept(t, "ICT");
        var me = Person(t, "S1", "Me", ict);
        Person(t, "S2", "Fina Lestari", ict); Person(t, "S3", "Budi", fin); Person(t, "S4", "Joko", ict);
        Person(t, "S5", "Finn Gone", ict, active: false);
        var grp = t.AddRoom("group", "Finance squad"); t.AddMember(grp.Id, me.Id);
        var other = t.AddRoom("group", "Finance secret");   // I'm not in it

        var r = await SearchAsync(t.Db, me.Id, "fin", default);
        Assert.Equal(["Budi", "Fina Lestari"], r.People.Select(p => p.Name));
        Assert.Contains(r.Groups, g => g.Kind == "department" && g.Name == "Finance" && g.Members == 1);
        Assert.Contains(r.Groups, g => g.Kind == "chat" && g.Name == "Finance squad");
        Assert.DoesNotContain(r.Groups, g => g.Name == "Finance secret");

        Assert.Empty((await SearchAsync(t.Db, me.Id, "f", default)).People);
        Assert.Empty((await SearchAsync(t.Db, me.Id, " f ", default)).Groups);
        Assert.Empty((await SearchAsync(t.Db, me.Id, null, default)).People);
        // LIKE metacharacters are literal.
        Assert.Empty((await SearchAsync(t.Db, me.Id, "%%", default)).People);
    }

    // ---------------------------------------------------------------- expand

    [Fact]
    public async Task Expand_chat_requires_membership_and_excludes_me()
    {
        using var t = new TestDb();
        var me = Person(t, "S1", "Me"); var a = Person(t, "S2", "Ani"); var b = Person(t, "S3", "Budi");
        var gone = Person(t, "S4", "Gone", active: false); var leaver = Person(t, "S5", "Leaver");
        var mine = t.AddRoom("group", "Mine");
        t.AddMember(mine.Id, me.Id); t.AddMember(mine.Id, a.Id); t.AddMember(mine.Id, b.Id);
        t.AddMember(mine.Id, gone.Id); t.AddMember(mine.Id, leaver.Id, leftAt: DateTime.UtcNow);
        var theirs = t.AddRoom("group", "Theirs"); t.AddMember(theirs.Id, a.Id);
        var wasIn = t.AddRoom("group", "Was in"); t.AddMember(wasIn.Id, me.Id, leftAt: DateTime.UtcNow); t.AddMember(wasIn.Id, a.Id);

        var (err, people) = await ExpandAsync(t.Db, me.Id, "chat", mine.Id, default);
        Assert.Null(err);
        Assert.Equal(["Ani", "Budi"], people!.Select(p => p.Name));

        Assert.Equal(ExpandError.Forbidden, (await ExpandAsync(t.Db, me.Id, "chat", theirs.Id, default)).Error);
        Assert.Equal(ExpandError.Forbidden, (await ExpandAsync(t.Db, me.Id, "chat", wasIn.Id, default)).Error);
        Assert.Equal(ExpandError.NotFound, (await ExpandAsync(t.Db, me.Id, "chat", Guid.NewGuid(), default)).Error);
        Assert.Equal(ExpandError.InvalidKind, (await ExpandAsync(t.Db, me.Id, "team", mine.Id, default)).Error);

        Assert.Equal(403, Status(await ExpandHttpAsync("chat", theirs.Id, Http(me), t.Db, default)));
        Assert.Equal(404, Status(await ExpandHttpAsync("chat", Guid.NewGuid(), Http(me), t.Db, default)));
        Assert.Equal(400, Status(await ExpandHttpAsync("x", mine.Id, Http(me), t.Db, default)));
        Assert.Equal(200, Status(await ExpandHttpAsync("chat", mine.Id, Http(me), t.Db, default)));
    }

    [Fact]
    public async Task Expand_department_lists_active_members_without_me()
    {
        using var t = new TestDb();
        var ict = Dept(t, "ICT");
        var me = Person(t, "S1", "Me", ict); Person(t, "S2", "Zed", ict); Person(t, "S3", "Ari", ict);
        Person(t, "S4", "Gone", ict, active: false);
        var (err, people) = await ExpandAsync(t.Db, me.Id, "department", ict.Id, default);
        Assert.Null(err);
        Assert.Equal(["Ari", "Zed"], people!.Select(p => p.Name));
        Assert.All(people!, p => Assert.Equal("ICT", p.Department));
        Assert.Equal(ExpandError.NotFound, (await ExpandAsync(t.Db, me.Id, "department", Guid.NewGuid(), default)).Error);
    }

    // ---------------------------------------------------------------- busy

    [Fact]
    public async Task Busy_shows_times_only_for_a_private_meeting_and_never_a_title()
    {
        using var t = new TestDb();
        var me = Person(t, "S1", "Me"); var a = Person(t, "S2", "Ani"); var b = Person(t, "S3", "Budi");
        var gone = Person(t, "S4", "Gone", active: false);
        var start = Day(3).AddHours(3);
        var m = new Meeting { Id = Guid.NewGuid(), Title = "Salary review", StartAt = start, EndAt = start.AddHours(1), OrganizerId = a.Id,
            Visibility = CalendarVisibility.Private };
        t.Db.Meetings.Add(m);
        t.Db.MeetingAttendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = a.Id, RsvpStatus = "accept" });
        t.Db.CalendarEvents.Add(new CalendarEvent { Id = Guid.NewGuid(), OwnerId = b.Id, Title = "Dentist", StartAt = start.AddHours(-3),
            EndAt = start.AddHours(-2), Visibility = CalendarVisibility.Private, Type = "kerja" });
        await t.Db.SaveChangesAsync();

        var (err, rows) = await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S2", "S3", "S4", "NOPE"], start.AddMinutes(-30), start.AddMinutes(30)), default);
        Assert.Null(err);
        Assert.Equal(["S2", "S3", "S4", "NOPE"], rows!.Select(r => r.Key));
        var ani = rows![0];
        Assert.Equal(("busy", start, start.AddHours(1)), (ani.State, ani.BusyFrom, ani.BusyTo));
        Assert.Equal(("free", (DateTime?)null), (rows[1].State, rows[1].BusyFrom));
        Assert.Equal("unknown", rows[2].State);
        Assert.Equal("unknown", rows[3].State);

        var json = Json(rows);
        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Salary", json);
        Assert.DoesNotContain("Dentist", json);
    }

    [Fact]
    public async Task Busy_merges_back_to_back_blocks_into_one_range()
    {
        using var t = new TestDb();
        var me = Person(t, "S1", "Me"); var a = Person(t, "S2", "Ani");
        var start = Day(3).AddHours(3);
        foreach (var (s, e) in new[] { (0, 60), (60, 90), (150, 180) })
            t.Db.CalendarEvents.Add(new CalendarEvent { Id = Guid.NewGuid(), OwnerId = a.Id, Title = "x", StartAt = start.AddMinutes(s),
                EndAt = start.AddMinutes(e), Type = "kerja" });
        await t.Db.SaveChangesAsync();
        var (_, rows) = await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S2"], start.AddMinutes(30), start.AddMinutes(200)), default);
        Assert.Equal((start, start.AddMinutes(90)), (rows![0].BusyFrom!.Value, rows[0].BusyTo!.Value));
    }

    [Fact]
    public async Task Busy_rejects_too_many_keys_and_bad_ranges()
    {
        using var t = new TestDb();
        var me = Person(t, "S1", "Me");
        var f = Day(2);
        var keys = Enumerable.Range(0, 201).Select(i => $"K{i}").ToList();
        Assert.Equal(BusyError.TooManyKeys, (await BusyAsync(t.Db, me.Id, new InviteBusyRequest(keys, f, f.AddHours(1)), default)).Error);
        Assert.Null((await BusyAsync(t.Db, me.Id, new InviteBusyRequest(keys.Take(200).ToList(), f, f.AddHours(1)), default)).Error);
        Assert.Equal(BusyError.InvalidRange, (await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S1"], f, f), default)).Error);
        Assert.Equal(BusyError.InvalidRange, (await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S1"], f, f.AddHours(25)), default)).Error);
        Assert.Equal(BusyError.InvalidRange, (await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S1"], null, f), default)).Error);
        Assert.Null((await BusyAsync(t.Db, me.Id, new InviteBusyRequest(null, f, f.AddHours(24)), default)).Error);

        Assert.Equal(400, Status(await BusyHttpAsync(new InviteBusyRequest(keys, f, f.AddHours(1)), Http(me), t.Db, default)));
        Assert.Equal(400, Status(await BusyHttpAsync(new InviteBusyRequest(["S1"], f, f.AddHours(25)), Http(me), t.Db, default)));
        Assert.Equal(200, Status(await BusyHttpAsync(new InviteBusyRequest(["S1"], f, f.AddHours(1)), Http(me), t.Db, default)));
    }

    [Fact]
    public async Task Busy_can_leave_out_the_meeting_being_edited()
    {
        using var t = new TestDb();
        var me = Person(t, "S1", "Me"); var a = Person(t, "S2", "Ani");
        var start = Day(3).AddHours(3);
        var m = new Meeting { Id = Guid.NewGuid(), Title = "Edit me", StartAt = start, EndAt = start.AddHours(1), OrganizerId = me.Id };
        t.Db.Meetings.Add(m);
        t.Db.MeetingAttendees.Add(new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = a.Id, RsvpStatus = "accept" });
        await t.Db.SaveChangesAsync();
        Assert.Equal("busy", (await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S2"], start, start.AddHours(1)), default)).Rows![0].State);
        Assert.Equal("free", (await BusyAsync(t.Db, me.Id, new InviteBusyRequest(["S2"], start, start.AddHours(1), m.Id), default)).Rows![0].State);
    }
}
