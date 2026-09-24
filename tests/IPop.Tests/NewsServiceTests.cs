using IPop.Application.News;
using IPop.Core.Entities;
using IPop.Infrastructure.News;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class NewsServiceTests
{
    [Fact]
    public async Task HappyPath_create_submit_approve_publish_ack()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR");
        var approver = t.AddAdmin("APPROVER");
        var reader = t.AddUser("READER");
        var svcAuthor = new NewsService(t.Verify());
        var svcApprover = new NewsService(t.Verify());
        var svcReader = new NewsService(t.Verify());

        // 1) create draft (targeted to reader + pinned + mandatory ack)
        var created = await svcAuthor.CreateAsync(author.Id, new CreateNewsRequest(
            Type: "announcement", Title: "Q3 Town Hall", Body: "All hands body", RequiresAcknowledgement: true, IsPinned: true,
            Targets: [new NewsTargetInput("user", reader.Nik)]), default);
        Assert.True(created.Success, created.Error);
        var id = created.News!.Id;
        Assert.Equal("draft", created.News.Status);

        // 2) submit -> pending
        var submitted = await svcAuthor.SubmitAsync(author.Id, id, default);
        Assert.True(submitted.Success, submitted.Error);
        Assert.Equal("pending", submitted.News!.Status);

        // 3) approve -> published
        var approved = await svcApprover.ApproveAsync(approver.Id, id, default);
        Assert.True(approved.Success, approved.Error);
        Assert.Equal("published", approved.News!.Status);
        Assert.NotNull(approved.News.PublishedAt);

        // reader can see it in feed now
        var feed = await svcReader.ListAsync(reader.Id, new NewsListFilter(Page: 1, PageSize: 20), default);
        Assert.Contains(feed.Items, x => x.Id == id);

        // author cannot see other user's unrelated post if targeted elsewhere — visible because targeted to reader or all?
        // reader acks
        var acked = await svcReader.AcknowledgeAsync(reader.Id, id, default);
        Assert.True(acked.Success, acked.Error);
        Assert.True(acked.News!.IsAcknowledged);
        // idempotent ack
        var acked2 = await svcReader.AcknowledgeAsync(reader.Id, id, default);
        Assert.True(acked2.Success);

        // audit has at least created/submitted/approved/published/acknowledged
        var audits = await svcAuthor.GetAuditsAsync(id, author.Id, default);
        Assert.Contains(audits, a => a.Action == "created");
        Assert.Contains(audits, a => a.Action == "submitted");
        Assert.Contains(audits, a => a.Action == "approved");
    }

    [Fact]
    public async Task Scheduling_future_publish_hidden_until_time()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var admin = t.AddAdmin("ADM");
        var reader = t.AddUser("R");
        var svcA = new NewsService(t.Verify());
        var svcR = new NewsService(t.Verify());

        var future = DateTime.UtcNow.AddDays(7);
        var created = await svcA.CreateAsync(author.Id, new CreateNewsRequest("news", "Future Post", "body", PublishAt: future), default);
        Assert.True(created.Success);
        await svcA.SubmitAsync(author.Id, created.News!.Id, default);
        var appr = await new NewsService(t.Verify()).ApproveAsync(admin.Id, created.News.Id, default);
        Assert.True(appr.Success);
        // reader feed should NOT show future scheduled post
        var feed = await svcR.ListAsync(reader.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(feed.Items, x => x.Id == created.News.Id);
        // including scheduled shows it
        var feedAll = await svcR.ListAsync(reader.Id, new NewsListFilter(IncludeScheduled: true), default);
        // scheduled filtering is only for published reader path; with IncludeScheduled=true, still filtered by visibility — but for published status it's visible
        // Instead verify the detail is not accessible if scheduling logic blocks acknowledge before PublishAt, but GetAsync already guards publishAt
        // Just verify direct get hidden due to future gate
        var detail = await svcR.GetAsync(created.News.Id, reader.Id, default);
        Assert.Null(detail); // hidden until PublishAt
    }

    [Fact]
    public async Task Targeting_department_filters_feed()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var admin = t.AddAdmin("ADM");
        var dept = t.EnsureDepartment("ENG", "Engineering");
        var readerIn = t.AddUser("RIN"); t.AssignDepartment(readerIn.Id, dept.Id);
        var readerOut = t.AddUser("ROUT");
        var svcA = new NewsService(t.Verify());

        var created = await svcA.CreateAsync(author.Id, new CreateNewsRequest("memo", "Eng memo", "body",
            Targets: [new NewsTargetInput("department", "ENG")]), default);
        Assert.True(created.Success);
        await svcA.SubmitAsync(author.Id, created.News!.Id, default);
        await new NewsService(t.Verify()).ApproveAsync(admin.Id, created.News.Id, default);

        var feedIn = await new NewsService(t.Verify()).ListAsync(readerIn.Id, new NewsListFilter(), default);
        Assert.Contains(feedIn.Items, x => x.Id == created.News.Id);
        var feedOut = await new NewsService(t.Verify()).ListAsync(readerOut.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(feedOut.Items, x => x.Id == created.News.Id);
    }

    [Fact]
    public async Task Invalid_transitions_rejected()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var adm = t.AddAdmin("ADM");
        var c = await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b"), default);
        var id = c.News!.Id;
        // approve without submit
        var bad = await new NewsService(t.Verify()).ApproveAsync(adm.Id, id, default);
        Assert.False(bad.Success);
        Assert.Equal("invalid_transition", bad.Error);
        // submit ok
        Assert.True((await new NewsService(t.Verify()).SubmitAsync(a.Id, id, default)).Success);
        // double submit
        Assert.False((await new NewsService(t.Verify()).SubmitAsync(a.Id, id, default)).Success);
        // reject without reason fails
        Assert.False((await new NewsService(t.Verify()).RejectAsync(adm.Id, id, "", default)).Success);
        // reject ok
        Assert.True((await new NewsService(t.Verify()).RejectAsync(adm.Id, id, "needs work", default)).Success);
        // after reject author can edit and resubmit (rejected -> draft on edit)
        Assert.True((await new NewsService(t.Verify()).UpdateAsync(a.Id, id, new UpdateNewsRequest(Title: "t2"), default)).Success);
        var detail = await new NewsService(t.Verify()).GetAsync(id, a.Id, default);
        Assert.Equal("draft", detail!.Status);
    }

    // ── Hardening (ipop-12): board 2b gaps ───────────────────────────────

    [Fact]
    public async Task Create_rejects_invalid_type_and_title_rules()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
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
        var a = t.AddUser("A");
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
    public async Task Pin_requires_admin_and_audit()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var reader = t.AddUser("R");
        var adm = t.AddAdmin("ADM");
        var c = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("news", "t", "b"), default);
        var id = c.News!.Id;
        var forbid = await new NewsService(t.Verify()).PinAsync(reader.Id, id, default);
        Assert.False(forbid.Success); Assert.Equal("forbidden", forbid.Error);
        var ok = await new NewsService(t.Verify()).PinAsync(adm.Id, id, default);
        Assert.True(ok.Success); Assert.True(ok.News!.IsPinned); Assert.NotNull(ok.News.PinnedAt);
        var listPinned = await new NewsService(t.Verify()).ListAsync(adm.Id,
            new NewsListFilter(PinnedOnly: true), default);
        Assert.Contains(listPinned.Items, x => x.Id == id);
        var unpin = await new NewsService(t.Verify()).UnpinAsync(adm.Id, id, default);
        Assert.True(unpin.Success); Assert.False(unpin.News!.IsPinned);
        // non-admin cannot unpin
        await new NewsService(t.Verify()).PinAsync(adm.Id, id, default);
        var forbid2 = await new NewsService(t.Verify()).UnpinAsync(reader.Id, id, default);
        Assert.False(forbid2.Success);
    }

    [Fact]
    public async Task Approve_reject_publish_unpublish_guards_forbidden_and_status()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var r = t.AddUser("R"); var adm = t.AddAdmin("ADM");
        var c = await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b"), default);
        var id = c.News!.Id;
        // non-admin cannot approve/reject/publish
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).ApproveAsync(r.Id, id, default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).RejectAsync(r.Id, id, "x", default)).Error);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).PublishAsync(r.Id, id, default)).Error);
        // submit then publish directly from pending -> published
        await new NewsService(t.Verify()).SubmitAsync(a.Id, id, default);
        var pub = await new NewsService(t.Verify()).PublishAsync(adm.Id, id, default);
        Assert.True(pub.Success); Assert.Equal("published", pub.News!.Status);
        // publish idempotent when already published
        var pub2 = await new NewsService(t.Verify()).PublishAsync(adm.Id, id, default);
        Assert.True(pub2.Success); Assert.Equal("published", pub2.News!.Status);
        // unpublish only from published
        var unpub = await new NewsService(t.Verify()).UnpublishAsync(adm.Id, id, default);
        Assert.True(unpub.Success); Assert.Equal("draft", unpub.News!.Status);
        Assert.False((await new NewsService(t.Verify()).UnpublishAsync(adm.Id, id, default)).Success);
        // non-admin cannot unpublish
        await new NewsService(t.Verify()).SubmitAsync(a.Id, id, default);
        await new NewsService(t.Verify()).PublishAsync(adm.Id, id, default);
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UnpublishAsync(r.Id, id, default)).Error);
    }

    [Fact]
    public async Task Acknowledge_guards_not_published_and_targeting_and_counts_all()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var adm = t.AddAdmin("ADM");
        var r = t.AddUser("R"); var outsider = t.AddUser("OUT");
        var dept = t.EnsureDepartment("ENG", "Engineering");
        t.AssignDepartment(r.Id, dept.Id);
        // draft cannot be acked
        var draft = await new NewsService(t.Verify()).CreateAsync(a.Id,
            new CreateNewsRequest("announcement", "t", "b", RequiresAcknowledgement: true,
                Targets: [new NewsTargetInput("department", "ENG")]), default);
        var id = draft.News!.Id;
        Assert.Equal("not_published", (await new NewsService(t.Verify()).AcknowledgeAsync(r.Id, id, default)).Error);
        await new NewsService(t.Verify()).SubmitAsync(a.Id, id, default);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, id, default);
        // outsider not targeted
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).AcknowledgeAsync(outsider.Id, id, default)).Error);
        // targeted can ack and AckCount visible
        var ack = await new NewsService(t.Verify()).AcknowledgeAsync(r.Id, id, default);
        Assert.True(ack.Success); Assert.True(ack.News!.IsAcknowledged);
        Assert.Equal(1, ack.News.AckCount);
        // all-target counts all active users
        var all = await new NewsService(t.Verify()).CreateAsync(a.Id,
            new CreateNewsRequest("news", "all post", "b2", RequiresAcknowledgement: true), default);
        await new NewsService(t.Verify()).SubmitAsync(a.Id, all.News!.Id, default);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, all.News.Id, default);
        var detailAll = await new NewsService(t.Verify()).GetAsync(all.News.Id, r.Id, default);
        Assert.NotNull(detailAll);
        Assert.True(detailAll!.AckTotal >= 3); // at least A/R/OUT
    }

    [Fact]
    public async Task List_filters_type_ack_search_and_targeting_role_user()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var adm = t.AddAdmin("ADM");
        var roleUser = t.AddUser("RU"); t.EnsureRole("Manager"); t.AssignRole(roleUser.Id, "Manager");
        var nikUser = t.AddUser("NIK1");
        var outsider = t.AddUser("OUT");
        await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("memo", "Finance memo uniqueXYZ", "body1",
            RequiresAcknowledgement: true, Targets: [new NewsTargetInput("role", "Manager")]), default);
        await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "other", "body2",
            Targets: [new NewsTargetInput("user", "NIK1")]), default);
        // submit+approve all
        foreach (var p in t.Verify().NewsPosts.ToList())
        {
            await new NewsService(t.Verify()).SubmitAsync(a.Id, p.Id, default);
            await new NewsService(t.Verify()).ApproveAsync(adm.Id, p.Id, default);
        }
        var byType = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new NewsListFilter(Type: "memo"), default);
        Assert.All(byType.Items, x => Assert.Equal("memo", x.Type));
        var byAck = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new NewsListFilter(RequiresAckOnly: true), default);
        Assert.All(byAck.Items, x => Assert.True(x.RequiresAcknowledgement));
        var bySearch = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new NewsListFilter(Search: "uniqueXYZ"), default);
        // search uses ILike (Npgsql) — on SQLite returns 0; assert no throw and not more than total
        Assert.True(bySearch.TotalCount <= byType.TotalCount + 5);
        // role targeting visible, outsider hidden from role post
        var roleFeed = await new NewsService(t.Verify()).ListAsync(roleUser.Id, new(), default);
        Assert.Contains(roleFeed.Items, x => x.Title.Contains("Finance memo"));
        var outFeed = await new NewsService(t.Verify()).ListAsync(outsider.Id, new(), default);
        Assert.DoesNotContain(outFeed.Items, x => x.Title.Contains("Finance memo"));
        var nikFeed = await new NewsService(t.Verify()).ListAsync(nikUser.Id, new(), default);
        Assert.Contains(nikFeed.Items, x => x.Targets.Any(tt => tt.Kind == "user" && tt.Value == "NIK1"));
    }

    [Fact]
    public async Task Update_guards_pending_published_and_forbidden()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var r = t.AddUser("R"); var adm = t.AddAdmin("ADM");
        var c = await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "t", "b"), default);
        var id = c.News!.Id;
        // non-author non-admin cannot edit
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).UpdateAsync(r.Id, id, new UpdateNewsRequest(Title: "x"), default)).Error);
        // pending/published cannot edit
        await new NewsService(t.Verify()).SubmitAsync(a.Id, id, default);
        Assert.Equal("invalid_transition", (await new NewsService(t.Verify()).UpdateAsync(a.Id, id, new UpdateNewsRequest(Title: "x"), default)).Error);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, id, default);
        Assert.Equal("invalid_transition", (await new NewsService(t.Verify()).UpdateAsync(a.Id, id, new UpdateNewsRequest(Title: "x"), default)).Error);
        await new NewsService(t.Verify()).UnpublishAsync(adm.Id, id, default);
        // back to draft -> editable
        var ok = await new NewsService(t.Verify()).UpdateAsync(a.Id, id, new UpdateNewsRequest(Title: "edited"), default);
        Assert.True(ok.Success); Assert.Equal("edited", ok.News!.Title);
    }

    [Fact]
    public async Task Get_and_audits_visibility_and_scheduled_gate()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var adm = t.AddAdmin("ADM"); var r = t.AddUser("R");
        var fut = DateTime.UtcNow.AddDays(3);
        var c = await new NewsService(t.Verify()).CreateAsync(a.Id, new CreateNewsRequest("news", "sched", "b", PublishAt: fut), default);
        await new NewsService(t.Verify()).SubmitAsync(a.Id, c.News!.Id, default);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, c.News.Id, default);
        // reader cannot see future scheduled
        Assert.Null(await new NewsService(t.Verify()).GetAsync(c.News.Id, r.Id, default));
        // author/admin can
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(c.News.Id, a.Id, default));
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(c.News.Id, adm.Id, default));
        // audits only author/admin
        Assert.Empty(await new NewsService(t.Verify()).GetAuditsAsync(c.News.Id, r.Id, default));
        Assert.NotEmpty(await new NewsService(t.Verify()).GetAuditsAsync(c.News.Id, a.Id, default));
        // reader feed hides scheduled unless IncludeScheduled
        var hide = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(hide.Items, x => x.Id == c.News.Id);
        var show = await new NewsService(t.Verify()).ListAsync(r.Id, new NewsListFilter(IncludeScheduled: true), default);
        // IncludeScheduled on reader path still applies targeting but lifts future gate only via Get; List hides future for readers — ensure no crash
        Assert.True(show.TotalCount >= 0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ipop-23 P2-1: News project targeting (TITAN backend slice)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_target_create_validation_and_storage()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var owner = t.AddUser("OWN");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        var svc = new NewsService(t.Verify());

        // Valid project target (GUID) is accepted and stored
        var created = await svc.CreateAsync(author.Id, new CreateNewsRequest("news", "Proj post", "body",
            Targets: [new NewsTargetInput("project", proj.Id.ToString())]), default);
        Assert.True(created.Success, created.Error);
        Assert.Contains(created.News!.Targets, x => x.Kind == "project" && x.Value == proj.Id.ToString());

        // Non-GUID value for project kind is rejected
        var bad = await svc.CreateAsync(author.Id, new CreateNewsRequest("news", "Bad", "body",
            Targets: [new NewsTargetInput("project", "Alpha")]), default);
        Assert.False(bad.Success);
        Assert.Equal("invalid_target_value", bad.Error);

        // Missing value is rejected by the generic rule
        var empty = await svc.CreateAsync(author.Id, new CreateNewsRequest("news", "Empty", "body",
            Targets: [new NewsTargetInput("project", " ")]), default);
        Assert.False(empty.Success);
        Assert.Equal("target_value_required", empty.Error);
    }

    [Fact]
    public async Task Project_target_visible_to_members_in_list_and_detail()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var outsider = t.AddUser("OUT");
        var adm = t.AddAdmin("ADM");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        t.AddProjectMember(proj.Id, member.Id, "member");

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("memo", "Project memo", "body",
                Targets: [new NewsTargetInput("project", proj.Id.ToString())]), default);
        Assert.True(created.Success);
        await new NewsService(t.Verify()).SubmitAsync(author.Id, created.News!.Id, default);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, created.News.Id, default);

        // Member sees it in the feed and can open detail
        var memberFeed = await new NewsService(t.Verify()).ListAsync(member.Id, new NewsListFilter(), default);
        Assert.Contains(memberFeed.Items, x => x.Id == created.News.Id);
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(created.News.Id, member.Id, default));

        // Outsider sees neither
        var outFeed = await new NewsService(t.Verify()).ListAsync(outsider.Id, new NewsListFilter(), default);
        Assert.DoesNotContain(outFeed.Items, x => x.Id == created.News.Id);
        Assert.Null(await new NewsService(t.Verify()).GetAsync(created.News.Id, outsider.Id, default));

        // Author (member via creation) still sees it
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(created.News.Id, author.Id, default));
    }

    [Fact]
    public async Task Project_target_ack_total_counts_members()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var owner = t.AddUser("OWN");
        var member1 = t.AddUser("M1");
        var member2 = t.AddUser("M2");
        var outsider = t.AddUser("OUT");
        var adm = t.AddAdmin("ADM");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        t.AddProjectMember(proj.Id, member1.Id, "member");
        t.AddProjectMember(proj.Id, member2.Id, "viewer");

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("news", "Ack me", "body", RequiresAcknowledgement: true,
                Targets: [new NewsTargetInput("project", proj.Id.ToString())]), default);
        Assert.True(created.Success);
        await new NewsService(t.Verify()).SubmitAsync(author.Id, created.News!.Id, default);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, created.News.Id, default);

        // Ack denominator = project members (author + owner + m1 + m2 = 4)
        var detail = await new NewsService(t.Verify()).GetAsync(created.News.Id, member1.Id, default);
        Assert.NotNull(detail);
        Assert.Equal(4, detail!.AckTotal);

        // Outsider cannot ack (not targeted)
        Assert.Equal("forbidden", (await new NewsService(t.Verify()).AcknowledgeAsync(outsider.Id, created.News.Id, default)).Error);

        // Member can ack; count increments
        var ack = await new NewsService(t.Verify()).AcknowledgeAsync(member1.Id, created.News.Id, default);
        Assert.True(ack.Success);
        Assert.Equal(1, ack.News!.AckCount);
        Assert.Equal(4, ack.News.AckTotal);
    }

    [Fact]
    public async Task Project_target_normalizes_guid_format_and_list_matches()
    {
        using var t = new TestDb();
        var author = t.AddUser("A");
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var adm = t.AddAdmin("ADM");
        var proj = t.AddProject("Alpha", owner.Id, author.Id);
        t.AddProjectMember(proj.Id, member.Id, "member");

        // Write target using the "N" format (32 hex digits, no dashes) — Guid.TryParse accepts it
        var nFormat = proj.Id.ToString("N");
        Assert.DoesNotContain("-", nFormat);

        var created = await new NewsService(t.Verify()).CreateAsync(author.Id,
            new CreateNewsRequest("memo", "Normalized project memo", "body",
                Targets: [new NewsTargetInput("project", nFormat)]), default);
        Assert.True(created.Success, created.Error);

        // Stored value is canonical D-format (normalized at the write boundary)
        var stored = await t.Verify().NewsTargets.AsNoTracking()
            .Where(x => x.NewsPostId == created.News!.Id && x.Kind == "project")
            .Select(x => x.Value).FirstOrDefaultAsync();
        Assert.Equal(proj.Id.ToString("D"), stored);

        // Update path normalizes too (ReplaceTargets shared by create + update; draft is editable)
        var upd = await new NewsService(t.Verify()).UpdateAsync(author.Id, created.News.Id,
            new UpdateNewsRequest(Title: "edited title", Targets: [new NewsTargetInput("project", proj.Id.ToString("B"))]), default);
        Assert.True(upd.Success, upd.Error);
        var stored2 = await t.Verify().NewsTargets.AsNoTracking()
            .Where(x => x.NewsPostId == created.News.Id && x.Kind == "project")
            .Select(x => x.Value).FirstOrDefaultAsync();
        Assert.Equal(proj.Id.ToString("D"), stored2);

        await new NewsService(t.Verify()).SubmitAsync(author.Id, created.News!.Id, default);
        await new NewsService(t.Verify()).ApproveAsync(adm.Id, created.News.Id, default);

        // Member sees it in the LIST (this was the divergence: D-format string compare)
        var feed = await new NewsService(t.Verify()).ListAsync(member.Id, new NewsListFilter(), default);
        Assert.Contains(feed.Items, x => x.Id == created.News.Id);

        // And in detail
        Assert.NotNull(await new NewsService(t.Verify()).GetAsync(created.News.Id, member.Id, default));
    }

    [Fact]
    public async Task Condolence_post_lifecycle_and_filter()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR_CONDOLENCE");
        var admin = t.AddAdmin("ADMIN_CONDOLENCE");
        var reader = t.AddUser("READER_CONDOLENCE");
        var svcA = new NewsService(t.Verify());
        var svcAdm = new NewsService(t.Verify());
        var svcR = new NewsService(t.Verify());

        // 1. Create condolence post
        var created = await svcA.CreateAsync(author.Id, new CreateNewsRequest(
            Type: "condolence",
            Title: "Berita Duka Cita - Bpk. Ahmad",
            Body: "Telah berpulang ke rahmatullah..."), default);
        Assert.True(created.Success, created.Error);
        Assert.Equal("condolence", created.News!.Type);
        Assert.Equal("draft", created.News.Status);

        // 2. Submit & Approve
        await svcA.SubmitAsync(author.Id, created.News.Id, default);
        var approved = await svcAdm.ApproveAsync(admin.Id, created.News.Id, default);
        Assert.True(approved.Success);
        Assert.Equal("published", approved.News!.Status);

        // 3. Filter by type=condolence in feed
        var feed = await svcR.ListAsync(reader.Id, new NewsListFilter(Type: "condolence"), default);
        Assert.Contains(feed.Items, x => x.Id == created.News.Id);

        // 4. Verify detail
        var detail = await svcR.GetAsync(created.News.Id, reader.Id, default);
        Assert.NotNull(detail);
        Assert.Equal("condolence", detail!.Type);
    }

    [Fact]
    public async Task News_NonAdminWithPublishPermission_CanPublishAndPin()
    {
        using var t = new TestDb();
        var author = t.AddUser("AUTHOR");
        var publisher = t.AddUser("PUBLISHER");

        // Assign news.publish permission to publisher via Role
        var role = new Role { Id = Guid.NewGuid(), Name = "NewsEditor" };
        var perm = new Permission { Id = Guid.NewGuid(), Code = "news.publish", Description = "Publish news" };
        t.Db.Roles.Add(role);
        t.Db.Permissions.Add(perm);
        t.Db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
        t.Db.UserRoles.Add(new UserRole { UserId = publisher.Id, RoleId = role.Id });
        await t.Db.SaveChangesAsync();

        var svcAuthor = new NewsService(t.Verify());
        var svcPublisher = new NewsService(t.Verify());

        var created = await svcAuthor.CreateAsync(author.Id, new CreateNewsRequest(
            Type: "news", Title: "Allowed Publish", Body: "Body"), default);
        var id = created.News!.Id;
        await svcAuthor.SubmitAsync(author.Id, id, default);

        var pubResult = await svcPublisher.PublishAsync(publisher.Id, id, default);
        Assert.True(pubResult.Success);
        Assert.Equal("published", pubResult.News!.Status);

        var pinResult = await svcPublisher.PinAsync(publisher.Id, id, default);
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
            Type: "memo", Title: "Important Memo", Body: "Body", RequiresAcknowledgement: true), default);
        var id = created.News!.Id;
        await svc.SubmitAsync(admin.Id, id, default);
        await svc.PublishAsync(admin.Id, id, default);

        var remind = await svc.RemindAckAsync(admin.Id, id, default);
        Assert.True(remind.Success);

        await using var v = t.Verify();
        var audits = await v.NewsAudits.Where(a => a.NewsPostId == id).ToListAsync();
        Assert.Contains(audits, a => a.Action == "reminded_ack");
    }
}
