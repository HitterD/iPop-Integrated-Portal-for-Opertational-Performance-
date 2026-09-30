using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Phase 3: timers, time entries, the Actual-hours roll-up, permissions, timesheets and auto-stop.
public sealed class ProjectTimeServiceTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 1, 0, 0, DateTimeKind.Utc); // Monday 08:00 WIB

    private sealed record Ctx(TestDb T, ProjectService Projects, ProjectTimeService Time, Guid Owner, Guid ProjectId) : IDisposable
    {
        public void Dispose() => T.Dispose();
    }

    private static async Task<Ctx> SetupAsync()
    {
        var t = new TestDb();
        var owner = t.AddUser("TT_O");
        var projects = new ProjectService(t.Verify());
        var p = (await projects.CreateProjectAsync(owner.Id, new CreateProjectRequest("Time", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        return new(t, projects, new ProjectTimeService(t.Verify()), owner.Id, p.Id);
    }

    private static async Task<Guid> TaskAsync(Ctx c, string title) =>
        (await c.Projects.CreateTaskAsync(c.Owner, new CreateTaskRequest(c.ProjectId, null, title, null, "todo", null, null, null, null, null, null), default)).Task!.Id;

    private static async Task<Guid> MemberAsync(Ctx c, string nik, string role)
    {
        var u = c.T.AddUser(nik);
        Assert.True((await c.Projects.AddMemberAsync(c.Owner, c.ProjectId, new AddProjectMemberRequest(u.Id, role), default)).Success);
        return u.Id;
    }

    private static decimal? Actual(Ctx c, Guid taskId) =>
        c.T.Verify().ProjectTasks.AsNoTracking().Single(t => t.Id == taskId).ActualHours;

    [Fact]
    public async Task Starting_a_second_timer_stops_the_first_and_rolls_up_actual_hours()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var b = await TaskAsync(c, "B");

        var first = await c.Time.StartAsync(c.Owner, a, default, T0);
        Assert.True(first.Success);
        var second = await c.Time.StartAsync(c.Owner, b, default, T0.AddMinutes(90));
        Assert.True(second.Success);

        var running = await c.Time.GetRunningAsync(c.Owner, default);
        Assert.Equal(b, running!.TaskId);
        Assert.Equal(1.5m, Actual(c, a));
        Assert.Equal(1, await c.T.Verify().TaskTimeEntries.CountAsync(e => e.EndedAt == null));

        // Starting the task that is already running is a no-op.
        var again = await c.Time.StartAsync(c.Owner, b, default, T0.AddMinutes(100));
        Assert.Equal(second.Data!.EntryId, again.Data!.EntryId);

        var stopped = await c.Time.StopAsync(c.Owner, default, T0.AddMinutes(120));
        Assert.Equal(30, stopped.Data!.Minutes);
        Assert.Equal(0.5m, Actual(c, b));
        Assert.Null(await c.Time.GetRunningAsync(c.Owner, default));
    }

    [Fact]
    public async Task A_sub_minute_timer_is_dropped_and_stop_without_timer_is_empty()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        await c.Time.StartAsync(c.Owner, a, default, T0);
        var stop = await c.Time.StopAsync(c.Owner, default, T0.AddSeconds(20));
        Assert.True(stop.Success);
        Assert.Null(stop.Data);
        Assert.Equal(0, await c.T.Verify().TaskTimeEntries.CountAsync());
        Assert.Null((await c.Time.StopAsync(c.Owner, default)).Data);
    }

    [Fact]
    public async Task Manual_entries_edit_and_delete_recompute_the_total()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var e1 = await c.Time.AddManualAsync(c.Owner, a, new(90, T0, "  Rapat  "), default);
        Assert.Equal("Rapat", e1.Data!.Note);
        var e2 = await c.Time.AddManualAsync(c.Owner, a, new(45, T0.AddDays(1), null), default);
        Assert.Equal(2.25m, Actual(c, a));

        var edited = await c.Time.UpdateEntryAsync(c.Owner, e2.Data!.Id, new(15, null, null), default);
        Assert.Equal(T0.AddDays(1).AddMinutes(15), edited.Data!.EndedAt);
        Assert.Equal(1.75m, Actual(c, a));

        Assert.True((await c.Time.DeleteEntryAsync(c.Owner, e1.Data.Id, default)).Success);
        Assert.Equal(0.25m, Actual(c, a));
        await c.Time.DeleteEntryAsync(c.Owner, e2.Data.Id, default);
        Assert.Null(Actual(c, a));

        var view = await c.Time.GetTaskTimeAsync(c.Owner, a, default);
        Assert.Equal(0, view.Data!.TotalMinutes);
    }

    [Fact]
    public async Task Manual_entries_are_validated()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        Assert.Equal("invalid_minutes", (await c.Time.AddManualAsync(c.Owner, a, new(0, T0, null), default)).Error);
        Assert.Equal("invalid_minutes", (await c.Time.AddManualAsync(c.Owner, a, new(24 * 60 + 1, T0, null), default)).Error);
        Assert.Equal("invalid_date", (await c.Time.AddManualAsync(c.Owner, a, new(30, DateTime.UtcNow.AddDays(2), null), default)).Error);
        Assert.Equal("invalid_note", (await c.Time.AddManualAsync(c.Owner, a, new(30, T0, new string('x', 201)), default)).Error);
        Assert.Equal("task_not_found", (await c.Time.AddManualAsync(c.Owner, Guid.NewGuid(), new(30, T0, null), default)).Error);
    }

    [Fact]
    public async Task Members_edit_only_their_own_entries_and_managers_edit_all()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var member = await MemberAsync(c, "TT_M", "member");
        var other = await MemberAsync(c, "TT_M2", "member");
        var manager = await MemberAsync(c, "TT_MG", "manager");
        var viewer = await MemberAsync(c, "TT_V", "viewer");
        var outsider = c.T.AddUser("TT_X").Id;

        var mine = (await c.Time.AddManualAsync(member, a, new(60, T0, null), default)).Data!;
        Assert.Equal("forbidden", (await c.Time.UpdateEntryAsync(other, mine.Id, new(30, null, null), default)).Error);
        Assert.Equal("forbidden", (await c.Time.DeleteEntryAsync(other, mine.Id, default)).Error);
        Assert.True((await c.Time.UpdateEntryAsync(manager, mine.Id, new(30, null, null), default)).Success);

        Assert.Equal("forbidden", (await c.Time.StartAsync(viewer, a, default)).Error);
        Assert.Equal("forbidden", (await c.Time.AddManualAsync(viewer, a, new(10, T0, null), default)).Error);
        Assert.Equal("forbidden", (await c.Time.GetTaskTimeAsync(outsider, a, default)).Error);

        // Everyone in the project reads every entry; CanEdit reflects the rule.
        var asOther = await c.Time.GetTaskTimeAsync(other, a, default);
        Assert.False(Assert.Single(asOther.Data!.Entries).CanEdit);
        Assert.True(Assert.Single((await c.Time.GetTaskTimeAsync(member, a, default)).Data!.Entries).CanEdit);
        Assert.True(Assert.Single((await c.Time.GetTaskTimeAsync(c.Owner, a, default)).Data!.Entries).CanEdit);
        Assert.False((await c.Time.GetTaskTimeAsync(viewer, a, default)).Data!.CanTrack);
    }

    [Fact]
    public async Task Auto_stop_caps_at_ten_hours_and_a_correction_clears_the_flag()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var b = await TaskAsync(c, "B");
        var member = await MemberAsync(c, "TT_M", "member");
        await c.Time.StartAsync(c.Owner, a, default, T0);
        await c.Time.StartAsync(member, b, default, T0.AddHours(5));

        var stopped = await c.Time.AutoStopAsync(T0.AddHours(12), default);
        var s = Assert.Single(stopped);
        Assert.Equal((c.Owner, a, "A"), (s.UserId, s.TaskId, s.TaskTitle));
        Assert.Equal(10m, Actual(c, a));
        Assert.NotNull(await c.Time.GetRunningAsync(member, default));

        var entry = Assert.Single((await c.Time.GetTaskTimeAsync(c.Owner, a, default)).Data!.Entries);
        Assert.True(entry.AutoStopped);
        Assert.Equal(T0.AddHours(10), entry.EndedAt);
        var fixedEntry = await c.Time.UpdateEntryAsync(c.Owner, entry.Id, new(4 * 60, null, "lupa stop"), default);
        Assert.False(fixedEntry.Data!.AutoStopped);
        Assert.Equal(4m, Actual(c, a));
        Assert.Empty(await c.Time.AutoStopAsync(T0.AddHours(12), default));
    }

    [Fact]
    public async Task A_running_timer_cannot_have_its_length_edited()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var run = await c.Time.StartAsync(c.Owner, a, default, T0);
        Assert.Equal("timer_running", (await c.Time.UpdateEntryAsync(c.Owner, run.Data!.EntryId, new(30, null, null), default)).Error);
        Assert.True((await c.Time.UpdateEntryAsync(c.Owner, run.Data.EntryId, new(null, null, "catatan"), default)).Success);
    }

    [Fact]
    public async Task Typed_actual_hours_are_ignored_by_update()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        await c.Time.AddManualAsync(c.Owner, a, new(60, T0, null), default);
        await c.Projects.UpdateTaskAsync(c.Owner, a,
            new UpdateTaskRequest("A2", null, null, null, null, null, 3m, 99m, null, null, null, null, null, null), default);
        Assert.Equal(1m, Actual(c, a));
    }

    [Fact]
    public async Task Timesheet_buckets_by_the_viewers_week_and_lists_every_member()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var member = await MemberAsync(c, "TT_M", "member");
        // Viewer in UTC+7: local Monday 00:00 = Sunday 17:00 UTC. A past week — entries can't be in the future.
        var weekStart = new DateTime(2026, 9, 20, 17, 0, 0, DateTimeKind.Utc);
        await c.Time.AddManualAsync(c.Owner, a, new(60, weekStart.AddHours(1), null), default);      // Mon
        await c.Time.AddManualAsync(c.Owner, a, new(30, weekStart.AddDays(2).AddHours(23), null), default); // Wed
        await c.Time.AddManualAsync(c.Owner, a, new(45, weekStart.AddMinutes(-1), null), default);   // previous week

        var sheet = (await c.Time.GetProjectTimesheetAsync(c.Owner, c.ProjectId, weekStart, default)).Data!;
        var ownerRow = sheet.Rows.Single(r => r.UserId == c.Owner);
        Assert.Equal([60, 0, 30, 0, 0, 0, 0], ownerRow.DayMinutes);
        Assert.Equal(90, sheet.TotalMinutes);
        Assert.Equal(0, sheet.Rows.Single(r => r.UserId == member).TotalMinutes);
        Assert.Equal(c.Owner, sheet.Rows[0].UserId);

        Assert.Equal("forbidden", (await c.Time.GetProjectTimesheetAsync(c.T.AddUser("TT_X").Id, c.ProjectId, weekStart, default)).Error);
    }

    [Fact]
    public async Task Portfolio_timesheet_is_scoped_to_membership_unless_hr()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        var weekStart = T0.Date;
        await c.Time.AddManualAsync(c.Owner, a, new(60, T0, null), default);
        var stranger = c.T.AddUser("TT_S");
        Assert.Empty((await c.Time.GetPortfolioTimesheetAsync(stranger.Id, weekStart, default)).Rows);

        var hr = new Role { Id = Guid.NewGuid(), Name = "HR" };
        c.T.Db.Roles.Add(hr);
        c.T.Db.UserRoles.Add(new UserRole { UserId = stranger.Id, RoleId = hr.Id });
        c.T.Db.SaveChanges();
        Assert.Equal(60, (await c.Time.GetPortfolioTimesheetAsync(stranger.Id, weekStart, default)).TotalMinutes);
        Assert.Equal(60, (await c.Time.GetPortfolioTimesheetAsync(c.Owner, weekStart, default)).TotalMinutes);
    }

    [Fact]
    public async Task Entries_of_a_deleted_task_are_hidden_and_come_back_on_restore()
    {
        using var c = await SetupAsync();
        var a = await TaskAsync(c, "A");
        await c.Time.AddManualAsync(c.Owner, a, new(60, T0, null), default);
        var del = await c.Projects.DeleteTaskAsync(c.Owner, a, default);
        Assert.Equal(0, (await c.Time.GetPortfolioTimesheetAsync(c.Owner, T0.Date, default)).TotalMinutes);
        Assert.Equal("task_not_found", (await c.Time.GetTaskTimeAsync(c.Owner, a, default)).Error);
        Assert.True(del.Success);
    }
}
