using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// News & Memos contract (2026-09-28): only HR/Admin create, publish, pin and unpublish; the
/// post's author and HR/Admin edit and delete it; publishing is direct (no approval step);
/// legacy pending/rejected rows read as drafts.
public class NewsServiceTests
{
    [Fact]
    public async Task HappyPath_hr_creates_publishes_and_reader_acks()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var reader = t.AddUser("READER");
        var svcHr = new NewsService(t.Verify());
        var svcReader = new NewsService(t.Verify());

        // 1) create draft (targeted to reader + pinned + mandatory ack)
        var created = await svcHr.CreateAsync(hr.Id, new CreateNewsRequest(
            Type: "announcement", Title: "Q3 Town Hall", Body: "All hands body", RequiresAcknowledgement: true, IsPinned: true,
            Targets: [new NewsTargetInput("user", reader.Nik)]), default);
        Assert.True(created.Success, created.Error);
        var id = created.News!.Id;
        Assert.Equal("draft", created.News.Status);

        // draft is invisible to readers
        Assert.Null(await svcReader.GetAsync(id, reader.Id, default));

        // 2) direct publish
        var published = await new NewsService(t.Verify()).PublishAsync(hr.Id, id, default);
        Assert.True(published.Success, published.Error);
        Assert.Equal("published", published.News!.Status);
        Assert.NotNull(published.News.PublishedAt);

        var feed = await new NewsService(t.Verify()).ListAsync(reader.Id, new NewsListFilter(Page: 1, PageSize: 20), default);
        Assert.Contains(feed.Items, x => x.Id == id);

        // 3) reader acks (idempotent)
        var acked = await new NewsService(t.Verify()).AcknowledgeAsync(reader.Id, id, default);
        Assert.True(acked.Success, acked.Error);
        Assert.True(acked.News!.IsAcknowledged);
        Assert.True((await new NewsService(t.Verify()).AcknowledgeAsync(reader.Id, id, default)).Success);

        var audits = await new NewsService(t.Verify()).GetAuditsAsync(id, hr.Id, default);
        Assert.Contains(audits, a => a.Action == "created");
        Assert.Contains(audits, a => a.Action == "published");
        Assert.Contains(audits, a => a.Action == "acknowledged");
    }

    [Fact]
    public async Task Create_with_mode_publish_goes_live_and_schedule_needs_future_time()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var reader = t.AddUser("R");

        var live = await new NewsService(t.Verify()).CreateAsync(hr.Id,
            new CreateNewsRequest("news", "Live now", "body", Mode: "publish"), default);
        Assert.True(live.Success, live.Error);
        Assert.Equal("published", live.News!.Status);
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(live.News.Id, reader.Id, default));

        var badSchedule = await new NewsService(t.Verify()).CreateAsync(hr.Id,
            new CreateNewsRequest("news", "Past schedule", "body", PublishAt: DateTime.UtcNow.AddHours(-1), Mode: "schedule"), default);
        Assert.False(badSchedule.Success);
        Assert.Equal("schedule_requires_future_time", badSchedule.Error);

        var scheduled = await new NewsService(t.Verify()).CreateAsync(hr.Id,
            new CreateNewsRequest("news", "Later", "body", PublishAt: DateTime.UtcNow.AddDays(2), Mode: "schedule"), default);
        Assert.True(scheduled.Success, scheduled.Error);
        Assert.Null(await new NewsService(t.Verify()).GetAsync(scheduled.News!.Id, reader.Id, default));
        var sched = await new NewsService(t.Verify()).ListAsync(hr.Id, new NewsListFilter(View: "scheduled"), default);
        Assert.Contains(sched.Items, x => x.Id == scheduled.News.Id);
    }

    [Fact]
    public async Task Employee_cannot_create_edit_publish_or_delete()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var emp = t.AddUser("EMP");
        // Employees carry news.write in production; that permission must not unlock authoring.
        var role = t.EnsureRole("Employee");
        var write = new Permission { Id = Guid.NewGuid(), Code = "news.write", Description = "Write news" };
        t.Db.Permissions.Add(write);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = write.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = emp.Id, RoleId = role.Id });
        await t.Db.SaveChangesAsync();

        var create = await new NewsService(t.Verify()).CreateAsync(emp.Id, new CreateNewsRequest("news", "t", "b"), default);
        Assert.False(create.Success); Assert.Equal("forbidden", create.Error);

        var post = await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("news", "t", "b", Mode: "publish"), default);
        var id = post.News!.Id;
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UpdateAsync(emp.Id, id, new UpdateNewsRequest(Title: "x"), default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UnpublishAsync(emp.Id, id, default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).PublishAsync(emp.Id, id, default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).DeleteAsync(emp.Id, id, default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).RemindAckAsync(emp.Id, id, default)).Error);
        Assert.Null(await new NewsService(t.Verify()).GetAckReportAsync(emp.Id, id, default));
        Assert.False(await new NewsService(t.Verify()).CanManageAsync(emp.Id, default));
        Assert.True(await new NewsService(t.Verify()).CanManageAsync(hr.Id, default));
    }

    [Fact]
    public async Task Any_manager_can_edit_another_managers_published_post_and_edit_is_marked()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var adm = t.AddAdmin("ADM");
        var reader = t.AddUser("R");
        var c = await new NewsService(t.Verify()).CreateAsync(hr.Id,
            new CreateNewsRequest("memo", "Policy", "v1", RequiresAcknowledgement: true, Mode: "publish"), default);
        var id = c.News!.Id;
        Assert.Null(c.News.EditedAt);
        await new NewsService(t.Verify()).AcknowledgeAsync(reader.Id, id, default);

        var edit = await new NewsService(t.Verify()).UpdateAsync(adm.Id, id, new UpdateNewsRequest(Body: "v2"), default);
        Assert.True(edit.Success, edit.Error);
        Assert.Equal("published", edit.News!.Status);
        Assert.NotNull(edit.News.EditedAt);
        Assert.Equal("v2", edit.News.Body);

        // Acknowledgements survive the edit.
        var detail = await new NewsService(t.Verify()).GetAsync(id, reader.Id, default);
        Assert.True(detail!.IsAcknowledged);

        var audits = await new NewsService(t.Verify()).GetAuditsAsync(id, hr.Id, default);
        Assert.Contains(audits, a => a.Action == "edited" && a.Detail == "after_publish" && a.ActorId == adm.Id);
    }

    [Fact]
    public async Task Scheduling_future_publish_hidden_until_time()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var reader = t.AddUser("R");

        var future = DateTime.UtcNow.AddDays(7);
        var created = await new NewsService(t.Verify()).CreateAsync(author.Id, new CreateNewsRequest("news", "Future Post", "body", PublishAt: future), default);
        Assert.True(created.Success);
        var pub = await new NewsService(t.Verify()).PublishAsync(author.Id, created.News!.Id, default);
        Assert.True(pub.Success);
        var feed = await new NewsService(t.Verify()).ListAsync(reader.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(feed.Items, x => x.Id == created.News.Id);
        Assert.Null(await new NewsService(t.Verify()).GetAsync(created.News.Id, reader.Id, default));
    }

    [Fact]
    public async Task Targeting_department_filters_feed()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var dept = t.EnsureDepartment("ENG", "Engineering");
        var readerIn = t.AddUser("RIN"); t.AssignDepartment(readerIn.Id, dept.Id);
        var readerOut = t.AddUser("ROUT");

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id, new CreateNewsRequest("memo", "Eng memo", "body",
            Targets: [new NewsTargetInput("department", "ENG")], Mode: "publish"), default);
        Assert.True(created.Success);

        var feedIn = await new NewsService(t.Verify()).ListAsync(readerIn.Id, new NewsListFilter(), default);
        Assert.Contains(feedIn.Items, x => x.Id == created.News!.Id);
        var feedOut = await new NewsService(t.Verify()).ListAsync(readerOut.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(feedOut.Items, x => x.Id == created.News!.Id);
    }

    [Fact]
    public async Task Legacy_pending_and_rejected_read_as_drafts_and_can_be_published()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var ids = new List<Guid>();
        foreach (var legacy in new[] { "pending", "rejected" })
        {
            var c = await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("news", legacy, "b"), default);
            ids.Add(c.News!.Id);
            var row = t.Db.NewsPosts.Single(x => x.Id == c.News.Id);
            row.Status = legacy;
        }
        await t.Db.SaveChangesAsync();

        var drafts = await new NewsService(t.Verify()).ListAsync(hr.Id, new NewsListFilter(View: "drafts"), default);
        Assert.Equal(2, drafts.Items.Count(x => ids.Contains(x.Id)));
        Assert.All(drafts.Items, x => Assert.Equal("draft", x.Status));
        Assert.Equal("draft", (await new NewsService(t.Verify()).GetAsync(ids[0], hr.Id, default))!.Status);
        var counts = await new NewsService(t.Verify()).GetCountsAsync(hr.Id, default);
        Assert.Equal(2, counts.Drafts);

        var pub = await new NewsService(t.Verify()).PublishAsync(hr.Id, ids[0], default);
        Assert.True(pub.Success); Assert.Equal("published", pub.News!.Status);
        // editing a legacy rejected row turns it into a plain draft
        var upd = await new NewsService(t.Verify()).UpdateAsync(hr.Id, ids[1], new UpdateNewsRequest(Title: "fixed"), default);
        Assert.True(upd.Success);
        Assert.Equal("draft", t.Verify().NewsPosts.Single(x => x.Id == ids[1]).Status);
    }

    [Fact]
    public async Task Create_rejects_invalid_type_and_title_rules()
    {
        using var t = new TestDb();
        var a = t.AddHr("A");
        var svc = new NewsService(t.Verify());
        var badType = await svc.CreateAsync(a.Id, new CreateNewsRequest("spam", "t", "b"), default);
        Assert.False(badType.Success); Assert.Equal("invalid_type", badType.Error);
        var noTitle = await svc.CreateAsync(a.Id, new CreateNewsRequest("news", " ", "b"), default);
        Assert.False(noTitle.Success); Assert.Equal("title_required", noTitle.Error);
        var longTitle = await svc.CreateAsync(a.Id, new CreateNewsRequest("news", new string('x', 301), "b"), default);
        Assert.False(longTitle.Success); Assert.Equal("title_too_long", longTitle.Error);
        var noBody = await svc.CreateAsync(a.Id, new CreateNewsRequest("news", "t", " "), default);
        Assert.False(noBody.Success); Assert.Equal("body_required", noBody.Error);
    }

    [Fact]
    public async Task Targets_validation_rejects_bad_kind_and_all_value()
    {
        using var t = new TestDb();
        var a = t.AddHr("A");
        var svc = new NewsService(t.Verify());
        var badKind = await svc.CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b",
            Targets: [new NewsTargetInput("group", "ENG")]), default);
        Assert.False(badKind.Success); Assert.Equal("invalid_target_kind", badKind.Error);
        var allWithVal = await svc.CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b",
            Targets: [new NewsTargetInput("all", "x")]), default);
        Assert.False(allWithVal.Success); Assert.Equal("target_value_forbidden_for_all", allWithVal.Error);
        var needVal = await svc.CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b",
            Targets: [new NewsTargetInput("user", " ")]), default);
        Assert.False(needVal.Success); Assert.Equal("target_value_required", needVal.Error);
    }

    [Fact]
    public async Task Pin_requires_manager_and_audit()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var reader = t.AddUser("R");
        var adm = t.AddAdmin("ADM");
        var c = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("news", "t", "b"), default);
        var id = c.News!.Id;
        var forbid = await new NewsService(t.Verify()).PinAsync(reader.Id, id, default);
        Assert.False(forbid.Success); Assert.Equal("forbidden", forbid.Error);
        var ok = await new NewsService(t.Verify()).PinAsync(adm.Id, id, default);
        Assert.True(ok.Success); Assert.True(ok.News!.IsPinned); Assert.NotNull(ok.News.PinnedAt);
        await new NewsService(t.Verify()).PublishAsync(author.Id, id, default);
        var listPinned = await new NewsService(t.Verify()).ListAsync(adm.Id,
            new NewsListFilter(PinnedOnly: true), default);
        Assert.Contains(listPinned.Items, x => x.Id == id);
        var unpin = await new NewsService(t.Verify()).UnpinAsync(author.Id, id, default);
        Assert.True(unpin.Success); Assert.False(unpin.News!.IsPinned);
        await new NewsService(t.Verify()).PinAsync(adm.Id, id, default);
        Assert.False((await new NewsService(t.Verify()).UnpinAsync(reader.Id, id, default)).Success);
        var audits = await new NewsService(t.Verify()).GetAuditsAsync(id, adm.Id, default);
        Assert.Contains(audits, a => a.Action == "pinned");
    }

    [Fact]
    public async Task Publish_unpublish_guards_forbidden_and_status()
    {
        using var t = new TestDb();
        var a = t.AddHr("A"); var r = t.AddUser("R"); var adm = t.AddAdmin("ADM");
        var c = await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b"), default);
        var id = c.News!.Id;
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).PublishAsync(r.Id, id, default)).Error);
        // unpublishing a draft is not a valid transition
        Assert.Equal("invalid_transition", (await new NewsService(t.Verify()).UnpublishAsync(adm.Id, id, default)).Error);
        var pub = await new NewsService(t.Verify()).PublishAsync(adm.Id, id, default);
        Assert.True(pub.Success); Assert.Equal("published", pub.News!.Status);
        // publish idempotent when already published
        var pub2 = await new NewsService(t.Verify()).PublishAsync(a.Id, id, default);
        Assert.True(pub2.Success); Assert.Equal("published", pub2.News!.Status);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UnpublishAsync(r.Id, id, default)).Error);
        var unpub = await new NewsService(t.Verify()).UnpublishAsync(a.Id, id, default);
        Assert.True(unpub.Success); Assert.Equal("draft", unpub.News!.Status);
        Assert.False((await new NewsService(t.Verify()).UnpublishAsync(adm.Id, id, default)).Success);
    }

    [Fact]
    public async Task Acknowledge_guards_not_published_and_targeting_and_counts_all()
    {
        using var t = new TestDb();
        var a = t.AddHr("A");
        var r = t.AddUser("R"); var outsider = t.AddUser("OUT");
        var dept = t.EnsureDepartment("ENG", "Engineering");
        t.AssignDepartment(r.Id, dept.Id);
        var draft = await new NewsService(t.Verify()).CreateAsync(a.Id,
            new CreateNewsRequest("announcement", "t", "b", RequiresAcknowledgement: true,
                Targets: [new NewsTargetInput("department", "ENG")]), default);
        var id = draft.News!.Id;
        Assert.Equal("not_published", (await new NewsService(t.Verify()).AcknowledgeAsync(r.Id, id, default)).Error);
        await new NewsService(t.Verify()).PublishAsync(a.Id, id, default);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).AcknowledgeAsync(outsider.Id, id, default)).Error);
        var ack = await new NewsService(t.Verify()).AcknowledgeAsync(r.Id, id, default);
        Assert.True(ack.Success); Assert.True(ack.News!.IsAcknowledged);
        Assert.Equal(1, ack.News.AckCount);
        // all-target counts all active users
        var all = await new NewsService(t.Verify()).CreateAsync(a.Id,
            new CreateNewsRequest("news", "all post", "b2", RequiresAcknowledgement: true, Mode: "publish"), default);
        var detailAll = await new NewsService(t.Verify()).GetAsync(all.News!.Id, r.Id, default);
        Assert.NotNull(detailAll);
        Assert.True(detailAll!.AckTotal >= 3); // at least A/R/OUT
    }

    [Fact]
    public async Task List_filters_type_ack_search_and_targeting_role_user()
    {
        using var t = new TestDb();
        var a = t.AddHr("A");
        var roleUser = t.AddUser("RU"); t.EnsureRole("Manager"); t.AssignRole(roleUser.Id, "Manager");
        var nikUser = t.AddUser("NIK1");
        var outsider = t.AddUser("OUT");
        await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("memo", "Finance memo uniqueXYZ", "body1",
            RequiresAcknowledgement: true, Targets: [new NewsTargetInput("role", "Manager")], Mode: "publish"), default);
        await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "other", "body2",
            Targets: [new NewsTargetInput("user", "NIK1")], Mode: "publish"), default);
        var byType = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new NewsListFilter(Type: "memo"), default);
        Assert.All(byType.Items, x => Assert.Equal("memo", x.Type));
        var byAck = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new NewsListFilter(RequiresAckOnly: true), default);
        Assert.All(byAck.Items, x => Assert.True(x.RequiresAcknowledgement));
        var bySearch = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new NewsListFilter(Search: "uniqueXYZ"), default);
        Assert.True(bySearch.TotalCount <= byType.TotalCount + 5);
        var roleFeed = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new(), default);
        Assert.Contains(roleFeed.Items, x => x.Title.Contains("Finance memo"));
        var outFeed = await new NewsService(t.Verify()).ListAsync(outsider.Id, new(), default);
        Assert.DoesNotContain(outFeed.Items, x => x.Title.Contains("Finance memo"));
        var nikFeed = await new NewsService(t.Verify()).ListAsync(nikUser.Id, new(), default);
        Assert.Contains(nikFeed.Items, x => x.Targets.Any(tt => tt.Kind == "user" && tt.Value == "NIK1"));
    }

    [Fact]
    public async Task Read_unread_bookmark_views_and_counts()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var r = t.AddUser("R");
        var r2 = t.AddUser("R2");
        var p1 = (await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("memo", "m", "b", RequiresAcknowledgement: true, Mode: "publish"), default)).News!.Id;
        var p2 = (await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("news", "n", "b", Mode: "publish"), default)).News!.Id;

        var c0 = await new NewsService(t.Verify()).GetCountsAsync(r.Id, default);
        Assert.Equal(2, c0.All); Assert.Equal(2, c0.Unread); Assert.Equal(1, c0.NeedsAck); Assert.Equal(0, c0.Saved);
        Assert.False(c0.CanManage); Assert.Equal(0, c0.Drafts);

        await new NewsService(t.Verify()).MarkReadAsync(r.Id, p1, true, default);
        await new NewsService(t.Verify()).MarkReadAsync(r.Id, p1, true, default); // idempotent
        await new NewsService(t.Verify()).MarkReadAsync(r2.Id, p1, true, default);
        await new NewsService(t.Verify()).SetBookmarkAsync(r.Id, p2, true, default);

        var unread = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(View: "unread"), default);
        Assert.Equal([p2], unread.Items.Select(x => x.Id));
        var saved = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(View: "saved"), default);
        Assert.Equal([p2], saved.Items.Select(x => x.Id));
        Assert.True(saved.Items[0].IsSaved);

        var mostRead = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(Sort: "mostRead"), default);
        Assert.Equal(p1, mostRead.Items[0].Id);
        Assert.Equal(2, mostRead.Items[0].ViewCount);
        Assert.True(mostRead.Items[0].IsRead);

        // mark unread keeps the view but brings the post back into Unread
        await new NewsService(t.Verify()).MarkReadAsync(r.Id, p1, false, default);
        await new NewsService(t.Verify()).SetBookmarkAsync(r.Id, p2, false, default);
        var c1 = await new NewsService(t.Verify()).GetCountsAsync(r.Id, default);
        Assert.Equal(2, c1.Unread); Assert.Equal(0, c1.Saved);
        Assert.Equal(2, (await new NewsService(t.Verify()).GetAsync(p1, hr.Id, default))!.ViewCount);
    }

    [Fact]
    public async Task Ack_report_lists_pending_and_done_readers_for_managers()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var dept = t.EnsureDepartment("ENG", "Engineering");
        var r1 = t.AddUser("R1"); t.AssignDepartment(r1.Id, dept.Id);
        var r2 = t.AddUser("R2"); t.AssignDepartment(r2.Id, dept.Id);
        var id = (await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("memo", "m", "b",
            RequiresAcknowledgement: true, Targets: [new NewsTargetInput("department", "ENG")],
            AckDueAt: DateTime.UtcNow.AddDays(3), Mode: "publish"), default)).News!.Id;
        await new NewsService(t.Verify()).AcknowledgeAsync(r1.Id, id, default);

        var report = await new NewsService(t.Verify()).GetAckReportAsync(hr.Id, id, default);
        Assert.NotNull(report);
        Assert.Equal(2, report!.Total);
        Assert.Equal(1, report.Acknowledged);
        Assert.Equal(r2.Id, report.Entries[0].UserId); // pending first
        Assert.Null(report.Entries[0].AcknowledgedAt);
        Assert.NotNull(report.Entries.Single(e => e.UserId == r1.Id).AcknowledgedAt);
        Assert.NotNull((await new NewsService(t.Verify()).GetAsync(id, r1.Id, default))!.AckDueAt);
    }

    [Fact]
    public async Task Delete_removes_post_and_writes_global_audit()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var id = (await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("news", "Gone soon", "b", Mode: "publish"), default)).News!.Id;
        var del = await new NewsService(t.Verify()).DeleteAsync(hr.Id, id, default);
        Assert.True(del.Success, del.Error);
        await using var v = t.Verify();
        Assert.False(await v.NewsPosts.AnyAsync(x => x.Id == id));
        Assert.True(await v.AuditLogs.AnyAsync(a => a.Action == "news.delete" && a.EntityId == id.ToString()));
        Assert.Equal("not_found", (await new NewsService(t.Verify()).DeleteAsync(hr.Id, id, default)).Error);
    }

    /// Seeds a post authored by a plain employee (e.g. created while they held HR, or legacy data):
    /// authoring goes through CreateAsync, which is HR/Admin-only.
    private static async Task<Guid> SeedPostByAsync(TestDb t, Guid authorId, string status)
    {
        var now = DateTime.UtcNow;
        var post = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = "Mine", Body = "b", Status = status, AuthorId = authorId,
            PublishedAt = status == "published" ? now : null, CreatedAt = now, UpdatedAt = now,
        };
        t.Db.NewsPosts.Add(post);
        await t.Db.SaveChangesAsync();
        return post.Id;
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("rejected")]
    [InlineData("published")]
    public async Task Author_can_edit_and_delete_own_post(string status)
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTH");
        var id = await SeedPostByAsync(t, author.Id, status);

        var detail = await new NewsService(t.Verify()).GetAsync(id, author.Id, default);
        Assert.NotNull(detail);
        Assert.True(detail!.CanEdit);
        Assert.True(detail.CanDelete);
        Assert.False(detail.CanPublish);
        Assert.False(detail.CanManage);

        var edit = await new NewsService(t.Verify()).UpdateAsync(author.Id, id, new UpdateNewsRequest(Title: "Edited", Body: "v2"), default);
        Assert.True(edit.Success, edit.Error);
        Assert.Equal("Edited", edit.News!.Title);

        var del = await new NewsService(t.Verify()).DeleteAsync(author.Id, id, default);
        Assert.True(del.Success, del.Error);
        Assert.False(await t.Verify().NewsPosts.AnyAsync(x => x.Id == id));
    }

    [Fact]
    public async Task Author_cannot_pin_publish_or_unpublish_own_post()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTH");
        var id = await SeedPostByAsync(t, author.Id, "published");

        // Pin state in an edit is ignored for authors (the editor always sends it).
        var edit = await new NewsService(t.Verify()).UpdateAsync(author.Id, id, new UpdateNewsRequest(Body: "v2", IsPinned: true), default);
        Assert.True(edit.Success, edit.Error);
        Assert.False(edit.News!.IsPinned);

        Assert.Equal("forbidden", (await new NewsService(t.Verify()).PinAsync(author.Id, id, default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UnpublishAsync(author.Id, id, default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).PublishAsync(author.Id, id, default)).Error);
        Assert.Null(await new NewsService(t.Verify()).GetAckReportAsync(author.Id, id, default));
    }

    [Fact]
    public async Task Other_employee_cannot_edit_or_delete_and_sees_no_flags()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTH");
        var other = t.AddUser("OTHER");
        var id = await SeedPostByAsync(t, author.Id, "published");

        var detail = await new NewsService(t.Verify()).GetAsync(id, other.Id, default);
        Assert.NotNull(detail);
        Assert.False(detail!.CanEdit);
        Assert.False(detail.CanDelete);
        Assert.False(detail.CanPublish);

        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UpdateAsync(other.Id, id, new UpdateNewsRequest(Title: "x"), default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).DeleteAsync(other.Id, id, default)).Error);

        // Someone else's draft stays invisible.
        var draft = await SeedPostByAsync(t, author.Id, "draft");
        Assert.Null(await new NewsService(t.Verify()).GetAsync(draft, other.Id, default));
    }

    [Fact]
    public async Task Hr_can_edit_publish_pin_and_delete_any_post()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTH");
        var hr = t.AddHr("HR1");
        var id = await SeedPostByAsync(t, author.Id, "draft");

        var detail = await new NewsService(t.Verify()).GetAsync(id, hr.Id, default);
        Assert.True(detail!.CanEdit && detail.CanDelete && detail.CanPublish && detail.CanManage);

        Assert.True((await new NewsService(t.Verify()).UpdateAsync(hr.Id, id, new UpdateNewsRequest(Body: "hr edit", IsPinned: true), default)).News!.IsPinned);
        Assert.True((await new NewsService(t.Verify()).PublishAsync(hr.Id, id, default)).Success);
        Assert.True((await new NewsService(t.Verify()).UnpinAsync(hr.Id, id, default)).Success);
        Assert.True((await new NewsService(t.Verify()).DeleteAsync(hr.Id, id, default)).Success);
    }

    [Fact]
    public async Task Attachments_link_on_create_and_policy_follows_post_visibility()
    {
        using var t = new TestDb();
        var hr = t.AddHr("HR1");
        var dept = t.EnsureDepartment("ENG", "Engineering");
        var inside = t.AddUser("IN"); t.AssignDepartment(inside.Id, dept.Id);
        var outsider = t.AddUser("OUT");
        var file = new Attachment
        {
            Id = Guid.NewGuid(), Area = "news", FileName = "policy.pdf", ContentType = "application/pdf", Size = 1234,
            Sha256 = "x", StoragePath = "news/x", UploaderId = hr.Id, CreatedAt = DateTime.UtcNow,
        };
        var foreign = new Attachment
        {
            Id = Guid.NewGuid(), Area = "chat", FileName = "chat.png", ContentType = "image/png", Size = 1,
            Sha256 = "y", StoragePath = "chat/y", UploaderId = hr.Id, CreatedAt = DateTime.UtcNow,
        };
        t.Db.Attachments.AddRange(file, foreign);
        await t.Db.SaveChangesAsync();

        // Unlinked file: uploader/manager only.
        var policy = new NewsAttachmentPolicy(t.Verify(), new NewsService(t.Verify()));
        Assert.True(await policy.CanReadAsync(file.Id, hr.Id, default));
        Assert.False(await policy.CanReadAsync(file.Id, inside.Id, default));

        var created = await new NewsService(t.Verify()).CreateAsync(hr.Id, new CreateNewsRequest("memo", "With file", "b",
            Targets: [new NewsTargetInput("department", "ENG")], Mode: "publish", AttachmentIds: [file.Id, foreign.Id]), default);
        Assert.True(created.Success, created.Error);
        var detail = await new NewsService(t.Verify()).GetAsync(created.News!.Id, inside.Id, default);
        var only = Assert.Single(detail!.Attachments!); // non-news area files are ignored
        Assert.Equal(file.Id, only.Id);

        policy = new NewsAttachmentPolicy(t.Verify(), new NewsService(t.Verify()));
        Assert.True(await policy.CanReadAsync(file.Id, inside.Id, default));
        Assert.False(await policy.CanReadAsync(file.Id, outsider.Id, default));

        // Removing the file on edit unlinks it.
        var upd = await new NewsService(t.Verify()).UpdateAsync(hr.Id, created.News.Id, new UpdateNewsRequest(AttachmentIds: []), default);
        Assert.True(upd.Success);
        Assert.Empty(upd.News!.Attachments ?? []);
    }

    [Fact]
    public async Task Get_and_audits_visibility_and_scheduled_gate()
    {
        using var t = new TestDb();
        var a = t.AddHr("A"); var adm = t.AddAdmin("ADM"); var r = t.AddUser("R");
        var fut = DateTime.UtcNow.AddDays(3);
        var c = await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "sched", "b", PublishAt: fut, Mode: "schedule"), default);
        Assert.True(c.Success, c.Error);
        Assert.Null(await new NewsService(t.Verify()).GetAsync(c.News!.Id, r.Id, default));
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(c.News.Id, a.Id, default));
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(c.News.Id, adm.Id, default));
        Assert.Empty(await new NewsService(t.Verify()).GetAuditsAsync(c.News.Id, r.Id, default));
        Assert.NotEmpty(await new NewsService(t.Verify()).GetAuditsAsync(c.News.Id, a.Id, default));
        var hide = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(hide.Items, x => x.Id == c.News.Id);
        var show = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(IncludeScheduled: true), default);
        Assert.DoesNotContain(show.Items, x => x.Id == c.News.Id); // readers never see future posts
    }

    // ──────────────────────────────────────────────────────────────────────
    // ipop-23 P2-1: News project targeting
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_target_create_validation_and_storage()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var owner = t.AddUser("OWN");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        var svc = new NewsService(t.Verify());

        var created = await svc.CreateAsync(author.Id, new CreateNewsRequest("news", "Proj post", "body",
            Targets: [new NewsTargetInput("project", proj.Id.ToString())]), default);
        Assert.True(created.Success, created.Error);
        Assert.Contains(created.News!.Targets, x => x.Kind == "project" && x.Value == proj.Id.ToString());

        var bad = await svc.CreateAsync(author.Id, new CreateNewsRequest("news", "Bad", "body",
            Targets: [new NewsTargetInput("project", "Alpha")]), default);
        Assert.False(bad.Success);
        Assert.Equal("invalid_target_value", bad.Error);

        var empty = await svc.CreateAsync(author.Id, new CreateNewsRequest("news", "Empty", "body",
            Targets: [new NewsTargetInput("project", " ")]), default);
        Assert.False(empty.Success);
        Assert.Equal("target_value_required", empty.Error);
    }

    [Fact]
    public async Task Project_target_visible_to_members_in_list_and_detail()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var outsider = t.AddUser("OUT");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        t.AddProjectMember(proj.Id, member.Id, "member");

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("memo", "Project memo", "body",
                Targets: [new NewsTargetInput("project", proj.Id.ToString())], Mode: "publish"), default);
        Assert.True(created.Success);
        var id = created.News!.Id;

        var memberFeed = await new NewsService(t.Verify()).ListAsync(member.Id, new NewsListFilter(), default);
        Assert.Contains(memberFeed.Items, x => x.Id == id);
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(id, member.Id, default));

        var outFeed = await new NewsService(t.Verify()).ListAsync(outsider.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(outFeed.Items, x => x.Id == id);
        Assert.Null(await new NewsService(t.Verify()).GetAsync(id, outsider.Id, default));

        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(id, owner.Id, default));
    }

    [Fact]
    public async Task Project_target_ack_total_counts_members()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var owner = t.AddUser("OWN");
        var member1 = t.AddUser("M1");
        var member2 = t.AddUser("M2");
        var outsider = t.AddUser("OUT");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        t.AddProjectMember(proj.Id, member1.Id, "member");
        t.AddProjectMember(proj.Id, member2.Id, "viewer");

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("news", "Ack me", "body", RequiresAcknowledgement: true,
                Targets: [new NewsTargetInput("project", proj.Id.ToString())], Mode: "publish"), default);
        Assert.True(created.Success);
        var id = created.News!.Id;

        // Ack denominator = project members (author + owner + m1 + m2 = 4)
        var detail = await new NewsService(t.Verify()).GetAsync(id, member1.Id, default);
        Assert.NotNull(detail);
        Assert.Equal(4, detail!.AckTotal);

        Assert.Equal("forbidden", (await new NewsService(t.Verify()).AcknowledgeAsync(outsider.Id, id, default)).Error);

        var ack = await new NewsService(t.Verify()).AcknowledgeAsync(member1.Id, id, default);
        Assert.True(ack.Success);
        Assert.Equal(1, ack.News!.AckCount);
        Assert.Equal(4, ack.News.AckTotal);
    }

    [Fact]
    public async Task Project_target_normalizes_guid_format_and_list_matches()
    {
        using var t = new TestDb();
        var author = t.AddHr("A");
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        t.AddProjectMember(proj.Id, member.Id, "member");

        var nFormat = proj.Id.ToString("N");
        Assert.DoesNotContain("-", nFormat);

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("memo", "Normalized project memo", "body",
                Targets: [new NewsTargetInput("project", nFormat)]), default);
        Assert.True(created.Success, created.Error);

        var stored = await t.Verify().NewsTargets.AsNoTracking()
            .Where(x => x.NewsPostId == created.News!.Id && x.Kind == "project")
            .Select(x => x.Value).FirstOrDefaultAsync();
        Assert.Equal(proj.Id.ToString("D"), stored);

        var upd = await new NewsService(t.Verify()).UpdateAsync(author.Id, created.News!.Id,
            new UpdateNewsRequest(Title: "edited title", Targets: [new NewsTargetInput("project", proj.Id.ToString("B"))]), default);
        Assert.True(upd.Success, upd.Error);
        var stored2 = await t.Verify().NewsTargets.AsNoTracking()
            .Where(x => x.NewsPostId == created.News.Id && x.Kind == "project")
            .Select(x => x.Value).FirstOrDefaultAsync();
        Assert.Equal(proj.Id.ToString("D"), stored2);

        await new NewsService(t.Verify()).PublishAsync(author.Id, created.News.Id, default);

        var feed = await new NewsService(t.Verify()).ListAsync(member.Id, new NewsListFilter(), default);
        Assert.Contains(feed.Items, x => x.Id == created.News.Id);
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(created.News.Id, member.Id, default));
    }

    [Fact]
    public async Task Condolence_post_lifecycle_and_filter()
    {
        using var t = new TestDb();
        var author = t.AddHr("AUTHOR_CONDOLENCE");
        var reader = t.AddUser("READER_CONDOLENCE");

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id, new CreateNewsRequest(
            Type: "condolence",
            Title: "Berita Duka Cita - Bpk. Ahmad",
            Body: "Telah berpulang ke rahmatullah..."), default);
        Assert.True(created.Success, created.Error);
        Assert.Equal("condolence", created.News!.Type);
        Assert.Equal("draft", created.News.Status);

        var published = await new NewsService(t.Verify()).PublishAsync(author.Id, created.News.Id, default);
        Assert.True(published.Success);
        Assert.Equal("published", published.News!.Status);

        var feed = await new NewsService(t.Verify()).ListAsync(reader.Id, new NewsListFilter(Type: "condolence"), default);
        Assert.Contains(feed.Items, x => x.Id == created.News.Id);

        var detail = await new NewsService(t.Verify()).GetAsync(created.News.Id, reader.Id, default);
        Assert.NotNull(detail);
        Assert.Equal("condolence", detail!.Type);
    }

    [Fact]
    public async Task News_NonAdminWithPublishPermission_CanCreatePublishAndPin()
    {
        using var t = new TestDb();
        var publisher = t.AddUser("PUBLISHER");

        // Any role carrying news.publish is a news manager, not only the seeded HR role.
        var role = new Role { Id = Guid.NewGuid(), Name = "NewsEditor" };
        var perm = new Permission { Id = Guid.NewGuid(), Code = "news.publish", Description = "Publish news" };
        t.Db.Roles.Add(role);
        t.Db.Permissions.Add(perm);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = publisher.Id, RoleId = role.Id });
        await t.Db.SaveChangesAsync();

        var created = await new NewsService(t.Verify()).CreateAsync(publisher.Id, new CreateNewsRequest(
            Type: "news", Title: "Allowed Publish", Body: "Body"), default);
        Assert.True(created.Success, created.Error);
        var id = created.News!.Id;

        var pubResult = await new NewsService(t.Verify()).PublishAsync(publisher.Id, id, default);
        Assert.True(pubResult.Success);
        Assert.Equal("published", pubResult.News!.Status);

        var pinResult = await new NewsService(t.Verify()).PinAsync(publisher.Id, id, default);
        Assert.True(pinResult.Success);
        Assert.True(pinResult.News!.IsPinned);
    }

    [Fact]
    public async Task News_RemindAck_SendsNotificationForMandatoryPost()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADMIN");
        var svc = new NewsService(t.Verify());

        var created = await svc.CreateAsync(admin.Id, new CreateNewsRequest(
            Type: "memo", Title: "Important Memo", Body: "Body", RequiresAcknowledgement: true, Mode: "publish"), default);
        var id = created.News!.Id;

        var remind = await svc.RemindAckAsync(admin.Id, id, default);
        Assert.True(remind.Success);

        await using var v = t.Verify();
        var audits = await v.NewsAudits.Where(a => a.NewsPostId == id).ToListAsync();
        Assert.Contains(audits, a => a.Action == "reminded_ack");
    }
}
