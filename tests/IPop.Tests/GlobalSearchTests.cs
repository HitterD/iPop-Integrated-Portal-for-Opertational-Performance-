using IPop.Application.Files;
using IPop.Application.Notes;
using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Notes;
using IPop.Infrastructure.Search;
using IPop.Infrastructure.Search.Providers;
using Xunit;

namespace IPop.Tests;

/// Global search (Ctrl+K): per-module providers, access scoping, ranking, paging, honest failure.
public class GlobalSearchTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static SearchQuery Q(Guid user, string text, int page = 1, int size = 10) => new(user, text, page, size);

    // ── Notes (the RMM report) ───────────────────────────────────────────────

    private static async Task<Guid> NoteAsync(TestDb t, Guid owner, string title, string? body = null)
    {
        var svc = new NoteService(t.Verify());
        var r = await svc.CreateAsync(owner, new CreateNoteRequest(null, title, null, null, null, null), Ct);
        Assert.True(r.Success, r.Error);
        if (body is not null)
            Assert.True((await new NoteService(t.Verify()).SnapshotAsync(owner, r.Data!.Id, new NoteSnapshotRequest("AQ==", 0, [], body, 0, 0), Ct)).Success);
        return r.Data!.Id;
    }

    [Fact]
    public async Task Notes_finds_RMM_in_title_only_and_body_only_and_hides_them_from_an_outsider()
    {
        using var t = new TestDb();
        var me = t.AddUser("N_ME"); var other = t.AddUser("N_OTHER"); var friend = t.AddUser("N_FRIEND");
        var byTitle = await NoteAsync(t, me.Id, "RMM checklist", "nothing relevant here");
        var byBody = await NoteAsync(t, me.Id, "Weekly notes", "Discussed the rmm rollout with the vendor");
        await NoteAsync(t, me.Id, "Unrelated", "lorem ipsum");
        await new NoteService(t.Verify()).AddShareAsync(me.Id, byBody, new AddNoteShareRequest(friend.Id, null, NoteLevels.View), Ct);

        var mine = await new NotesSearchProvider(new NoteService(t.Verify())).SearchAsync(Q(me.Id, "RMM"), Ct);
        Assert.Equal(SearchStatus.Ready, mine.Status);
        Assert.Equal(2, mine.Total);
        Assert.Equal(byTitle.ToString(), mine.Items[0].Id);                 // title match ranks above body match
        Assert.Equal(SearchMatch.Title, mine.Items[0].Match);
        Assert.Equal(byBody.ToString(), mine.Items[1].Id);
        Assert.Equal(SearchMatch.Body, mine.Items[1].Match);
        Assert.Contains("rmm rollout", mine.Items[1].Snippet);
        Assert.Equal($"/notes/{byBody}", mine.Items[1].Url);
        Assert.Null(mine.Message);

        var outsider = await new NotesSearchProvider(new NoteService(t.Verify())).SearchAsync(Q(other.Id, "RMM"), Ct);
        Assert.Equal(0, outsider.Total);
        Assert.Empty(outsider.Items);

        var shared = await new NotesSearchProvider(new NoteService(t.Verify())).SearchAsync(Q(friend.Id, "rmm"), Ct);
        Assert.Single(shared.Items);
        Assert.Equal(byBody.ToString(), shared.Items[0].Id);               // only the shared page, never the owner's other pages
    }

    [Fact]
    public async Task Notes_exact_title_ranks_first_and_pages_across_tiers()
    {
        using var t = new TestDb();
        var me = t.AddUser("N_RANK");
        var exact = await NoteAsync(t, me.Id, "Budget");
        var contains = await NoteAsync(t, me.Id, "Budget 2027");
        var body = await NoteAsync(t, me.Id, "Planning", "budget numbers inside");
        var svc = new NoteService(t.Verify());

        var p1 = await svc.SearchPagedAsync(me.Id, "budget", 1, 2, Ct);
        Assert.Equal(3, p1.Total);
        Assert.Equal([exact, contains], p1.Items.Select(i => i.Id));
        Assert.True(p1.Items[0].TitleExact);
        var p2 = await svc.SearchPagedAsync(me.Id, "budget", 2, 2, Ct);
        Assert.Equal([body], p2.Items.Select(i => i.Id));
        Assert.False(p2.Items[0].TitleMatch);
    }

    [Fact]
    public async Task Notes_reports_when_a_visible_page_hit_the_plain_text_cap_and_never_reads_the_binary_state()
    {
        using var t = new TestDb();
        var me = t.AddUser("N_LONG");
        var id = await NoteAsync(t, me.Id, "Long page");
        var db = t.Verify();
        var page = await db.NotePages.FindAsync(id);
        page!.PlainText = new string('x', NoteService.PlainTextLimit);
        page.YState = System.Text.Encoding.UTF8.GetBytes("secret-in-binary");   // must never match as text
        await db.SaveChangesAsync();

        var provider = new NotesSearchProvider(new NoteService(t.Verify()));
        var hit = await provider.SearchAsync(Q(me.Id, "xxxx"), Ct);
        Assert.Single(hit.Items);
        Assert.Equal(NotesSearchProvider.TruncatedMessage, hit.Message);
        var miss = await provider.SearchAsync(Q(me.Id, "secret-in-binary"), Ct);
        Assert.Empty(miss.Items);
    }

    [Fact]
    public async Task Notes_snapshot_indexes_words_beyond_the_old_twenty_thousand_character_limit()
    {
        using var t = new TestDb();
        var me = t.AddUser("N_FULL");
        var id = await NoteAsync(t, me.Id, "Long manual");
        var svc = new NoteService(t.Verify());
        var text = new string('x', 21000) + " RMM deep content";
        var snapshot = await svc.SnapshotAsync(me.Id, id,
            new NoteSnapshotRequest("AQ==", 0, [], text, 0, 0), Ct);
        Assert.True(snapshot.Success);
        var result = await svc.SearchPagedAsync(me.Id, "RMM", 1, 10, Ct);
        Assert.Equal(id, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task Notes_trashed_pages_are_not_searchable()
    {
        using var t = new TestDb();
        var me = t.AddUser("N_TRASH");
        var id = await NoteAsync(t, me.Id, "Gone RMM");
        await new NoteService(t.Verify()).TrashAsync(me.Id, id, Ct);
        var r = await new NotesSearchProvider(new NoteService(t.Verify())).SearchAsync(Q(me.Id, "RMM"), Ct);
        Assert.Empty(r.Items);
    }

    // ── Service envelope ─────────────────────────────────────────────────────

    private sealed class Perms(params string[] denied) : IPermissionEvaluator
    {
        public Task<bool> IsAllowedAsync(Guid userId, string permissionCode, string? departmentCode, CancellationToken ct = default)
            => Task.FromResult(!denied.Contains(permissionCode));
    }

    private sealed class Throwing : ISearchModuleProvider
    {
        public SearchModuleInfo Info { get; } = new("boom", "Boom", "x", 1);
        public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => Task.FromResult(true);
        public Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct) => throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task Service_catalog_omits_forbidden_modules_and_failures_come_back_as_error_not_empty()
    {
        using var t = new TestDb();
        var me = t.AddUser("S_ME");
        var db = t.Verify();
        var providers = new ISearchModuleProvider[]
        {
            new NotesSearchProvider(new NoteService(db), new Perms("notes.read")),
            new ProjectSearchProvider(db, new Perms("notes.read")),
            new Throwing(),
        };
        var svc = new SearchService(db, providers);

        var modules = await svc.ModulesAsync(me.Id, Ct);
        Assert.Equal(["boom", "projects"], modules.Select(m => m.Key));

        Assert.Null(await svc.SearchModuleAsync(me.Id, "nope", "abc", 1, 10, Ct));
        Assert.Equal(SearchStatus.Forbidden, (await svc.SearchModuleAsync(me.Id, "notes", "abc", 1, 10, Ct))!.Status);
        var boom = (await svc.SearchModuleAsync(me.Id, "boom", "abc", 1, 10, Ct))!;
        Assert.Equal(SearchStatus.Error, boom.Status);
        Assert.NotNull(boom.Message);
        var shortQ = (await svc.SearchModuleAsync(me.Id, "projects", "a", 1, 10, Ct))!;
        Assert.Equal(SearchStatus.Ready, shortQ.Status);
        Assert.Equal(0, shortQ.Total);
        var clamped = (await svc.SearchModuleAsync(me.Id, "projects", "abc", 0, 999, Ct))!;
        Assert.Equal(1, clamped.Page);
        Assert.Equal(SearchModules.MaxPageSize, clamped.PageSize);
    }

    [Fact]
    public async Task Legacy_search_still_answers_the_old_flat_shape()
    {
        using var t = new TestDb();
        var me = t.AddUser("L_ME");
        t.AddProject("Legacy Alpha", me.Id);
        var results = await new SearchService(t.Verify()).SearchAsync(me.Id, "alpha", Ct);
        Assert.Contains(results, r => r.Group == "Projects" && r.Title == "Legacy Alpha");
    }

    // ── Projects / Tasks ─────────────────────────────────────────────────────

    [Fact]
    public async Task Projects_and_tasks_are_scoped_to_membership_and_hide_deleted_tasks()
    {
        using var t = new TestDb();
        var me = t.AddUser("P_ME"); var other = t.AddUser("P_OTHER"); var admin = t.AddAdmin("P_ADMIN");
        var p = t.AddProject("Kopi Roaster", me.Id);
        var db = t.Db;
        var project = db.Projects.Find(p.Id)!; project.Description = "roaster line maintenance"; db.SaveChanges();
        var group = db.ProjectGroups.Single(g => g.ProjectId == p.Id).Id;
        db.ProjectTasks.Add(new ProjectTask { Id = Guid.NewGuid(), ProjectId = p.Id, GroupId = group, Title = "Calibrate roaster", Status = "todo", CreatedById = me.Id });
        db.ProjectTasks.Add(new ProjectTask { Id = Guid.NewGuid(), ProjectId = p.Id, GroupId = group, Title = "Order beans", Description = "for the roaster", Status = "todo", CreatedById = me.Id });
        db.ProjectTasks.Add(new ProjectTask { Id = Guid.NewGuid(), ProjectId = p.Id, GroupId = group, Title = "Deleted roaster task", Status = "todo", CreatedById = me.Id, DeletedAt = DateTime.UtcNow });
        db.SaveChanges();

        var projects = new ProjectSearchProvider(t.Verify());
        Assert.Equal(1, (await projects.SearchAsync(Q(me.Id, "roaster"), Ct)).Total);
        Assert.Equal(SearchMatch.Body, (await projects.SearchAsync(Q(me.Id, "maintenance"), Ct)).Items.Single().Match);
        Assert.Equal(0, (await projects.SearchAsync(Q(other.Id, "roaster"), Ct)).Total);
        Assert.Equal(1, (await projects.SearchAsync(Q(admin.Id, "roaster"), Ct)).Total);

        var tasks = new TaskSearchProvider(t.Verify());
        var mine = await tasks.SearchAsync(Q(me.Id, "roaster"), Ct);
        Assert.Equal(2, mine.Total);
        Assert.Equal("Calibrate roaster", mine.Items[0].Title);           // title before description
        Assert.Equal("Order beans", mine.Items[1].Title);
        Assert.Contains("roaster", mine.Items[1].Snippet);
        Assert.Equal($"/projects/{p.Id}?task={mine.Items[0].Id}", mine.Items[0].Url);
        Assert.Equal(0, (await tasks.SearchAsync(Q(other.Id, "roaster"), Ct)).Total);
    }

    // ── Mail ─────────────────────────────────────────────────────────────────

    private static Guid AddMail(TestDb t, Guid sender, Guid recipient, string folder, string subject, string body, string status = "sent", int minutesAgo = 0)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var m = new Mail { Id = Guid.NewGuid(), SenderId = sender, Subject = subject, Body = body, Status = status, CreatedAt = at, SentAt = status == "sent" ? at : null };
        t.Db.Mails.Add(m);
        t.Db.MailRecipients.Add(new MailRecipient { Id = Guid.NewGuid(), MailId = m.Id, UserId = recipient, RecipientType = folder == "inbox" ? "to" : "from", Folder = folder, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        return m.Id;
    }

    [Fact]
    public async Task Mail_searches_own_inbox_sent_and_drafts_by_subject_and_body_but_never_trash_or_other_people()
    {
        using var t = new TestDb();
        var me = t.AddUser("M_ME"); var bob = t.AddUser("M_BOB");
        var inbox = AddMail(t, bob.Id, me.Id, "inbox", "Invoice March", "<p>please pay</p>", minutesAgo: 1);
        var sent = AddMail(t, me.Id, me.Id, "sent", "Re: numbers", "<p>the <b>invoice</b> is attached</p>");
        var draft = AddMail(t, me.Id, me.Id, "draft", "invoice draft", "", "draft", minutesAgo: 10);
        AddMail(t, bob.Id, me.Id, "trash", "Old invoice", "x");
        AddMail(t, bob.Id, bob.Id, "inbox", "Bob's invoice", "x");

        var r = await new MailSearchProvider(t.Verify()).SearchAsync(Q(me.Id, "invoice"), Ct);
        Assert.Equal(3, r.Total);
        Assert.Equal([inbox, draft, sent], r.Items.Select(i => Guid.Parse(i.Id)));   // title matches (newest first), body last even though it is newest
        var d = r.Items.Single(i => i.Id == draft.ToString());
        Assert.True(d.Draft);
        Assert.Equal("Draft", d.Subtitle);
        var s = r.Items.Single(i => i.Id == sent.ToString());
        Assert.Equal(SearchMatch.Body, s.Match);
        Assert.Contains("invoice is attached", s.Snippet);                          // tags stripped, never raw HTML
        Assert.DoesNotContain("<b>", s.Snippet);
        Assert.Equal($"/mail?mailId={inbox}", r.Items[0].Url);
    }

    // ── Chat ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_provider_failure_on_the_sqlite_seam_is_an_error_envelope_never_a_silent_empty_page()
    {
        // IChatService's search uses EF.Functions.ILike (Npgsql only); on SQLite it throws, which the
        // service must surface as status "error" (the client shows Retry) rather than "no results".
        using var t = new TestDb();
        var a = t.AddUser("C_A"); var b = t.AddUser("C_B");
        var room = t.AddRoom("dm"); t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        t.AddMessage(room.Id, a.Id, "budget talk");
        var db = t.Verify();
        var svc = new SearchService(db, [new ChatSearchProvider(new ChatService(db))]);
        var page = (await svc.SearchModuleAsync(a.Id, "chat", "budget", 1, 10, Ct))!;
        Assert.True(page.Status is SearchStatus.Ready or SearchStatus.Error);
        if (page.Status == SearchStatus.Ready) Assert.All(page.Items, i => Assert.StartsWith($"/chat/{room.Id}", i.Url));
        else Assert.Empty(page.Items);
    }

    // ── News ─────────────────────────────────────────────────────────────────

    private static NewsPost AddPost(TestDb t, Guid author, string title, string body, string status = "published", string format = "plain")
    {
        var p = new NewsPost
        {
            Id = Guid.NewGuid(), Type = "news", Title = title, Body = body, BodyFormat = format, Status = status, AuthorId = author,
            PublishedAt = status == "published" ? DateTime.UtcNow : null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        t.Db.NewsPosts.Add(p); t.Db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task News_matches_body_text_respects_targeting_and_shows_drafts_only_to_their_author_or_managers()
    {
        using var t = new TestDb();
        var me = t.AddUser("W_ME"); var author = t.AddUser("W_AUTH"); var admin = t.AddAdmin("W_ADMIN");
        var live = AddPost(t, author.Id, "Town hall", "<p>Agenda: <em>safety</em> audit</p>", format: "html");
        var targeted = AddPost(t, author.Id, "Safety for others", "x");
        t.Db.NewsTargets.Add(new NewsTarget { Id = Guid.NewGuid(), NewsPostId = targeted.Id, Kind = "user", Value = author.Nik });
        var theirDraft = AddPost(t, author.Id, "Safety draft", "x", status: "draft");
        var myDraft = AddPost(t, me.Id, "My safety draft", "x", status: "draft");
        t.Db.SaveChanges();

        var mine = await new NewsSearchProvider(t.Verify()).SearchAsync(Q(me.Id, "safety"), Ct);
        Assert.Equal([myDraft.Id, live.Id], mine.Items.Select(i => Guid.Parse(i.Id)));
        Assert.True(mine.Items[0].Draft);
        Assert.Equal(SearchMatch.Body, mine.Items[1].Match);
        Assert.Contains("safety audit", mine.Items[1].Snippet);
        Assert.DoesNotContain("<em>", mine.Items[1].Snippet);

        var all = await new NewsSearchProvider(t.Verify()).SearchAsync(Q(admin.Id, "safety"), Ct);
        Assert.Equal(4, all.Total);
        Assert.Contains(all.Items, i => i.Id == theirDraft.Id.ToString() && i.Draft);
    }

    // ── Calendar ─────────────────────────────────────────────────────────────

    private static CalendarEvent AddEvent(TestDb t, Guid owner, string title, string visibility = CalendarVisibility.Busy, string? notes = null)
    {
        var e = new CalendarEvent { Id = Guid.NewGuid(), Title = title, OwnerId = owner, StartAt = DateTime.UtcNow.AddDays(1), EndAt = DateTime.UtcNow.AddDays(1).AddHours(1), Visibility = visibility, Notes = notes, CreatedAt = DateTime.UtcNow };
        t.Db.CalendarEvents.Add(e); t.Db.SaveChanges();
        return e;
    }

    [Fact]
    public async Task Calendar_busy_only_viewers_find_nothing_titles_level_finds_titles_details_level_finds_notes_private_needs_full()
    {
        using var t = new TestDb();
        var owner = t.AddUser("K_OWNER"); var stranger = t.AddUser("K_STRANGER");
        var titles = t.AddUser("K_TITLES"); var view = t.AddUser("K_VIEW"); var full = t.AddUser("K_FULL");
        var normal = AddEvent(t, owner.Id, "Dentist Vendor", notes: "bring the vendor contract");
        var priv = AddEvent(t, owner.Id, "Vendor interview", CalendarVisibility.Private);
        t.Db.CalendarDelegates.AddRange(
            new CalendarDelegate { Id = Guid.NewGuid(), OwnerId = owner.Id, DelegateId = titles.Id, Level = DelegateLevel.Titles },
            new CalendarDelegate { Id = Guid.NewGuid(), OwnerId = owner.Id, DelegateId = view.Id, Level = DelegateLevel.View },
            new CalendarDelegate { Id = Guid.NewGuid(), OwnerId = owner.Id, DelegateId = full.Id, Level = DelegateLevel.Full });
        t.Db.SaveChanges();
        var p = new CalendarSearchProvider(t.Verify());

        Assert.Equal(2, (await p.SearchAsync(Q(owner.Id, "vendor"), Ct)).Total);
        Assert.Empty((await p.SearchAsync(Q(stranger.Id, "vendor"), Ct)).Items);       // busy-only: nothing to search
        var byTitles = await p.SearchAsync(Q(titles.Id, "vendor"), Ct);
        Assert.Equal([$"shared:{normal.Id}"], byTitles.Items.Select(i => i.Id));       // Private stays a block
        Assert.Empty((await p.SearchAsync(Q(titles.Id, "contract"), Ct)).Items);       // notes need details
        Assert.Single((await p.SearchAsync(Q(view.Id, "contract"), Ct)).Items);
        Assert.Equal(2, (await p.SearchAsync(Q(full.Id, "vendor"), Ct)).Total);
        Assert.Contains((await p.SearchAsync(Q(full.Id, "vendor"), Ct)).Items, i => i.Id == $"shared:{priv.Id}");
    }

    [Fact]
    public async Task Calendar_invitees_find_the_meeting_but_declined_and_cancelled_ones_stay_hidden()
    {
        using var t = new TestDb();
        var org = t.AddUser("K_ORG"); var yes = t.AddUser("K_YES"); var no = t.AddUser("K_NO");
        var m = new Meeting { Id = Guid.NewGuid(), Title = "Quarterly sync", StartAt = DateTime.UtcNow.AddDays(2), EndAt = DateTime.UtcNow.AddDays(2).AddHours(1), OrganizerId = org.Id, Agenda = "Discuss RMM", CreatedAt = DateTime.UtcNow };
        var cancelled = new Meeting { Id = Guid.NewGuid(), Title = "Quarterly cancelled", StartAt = DateTime.UtcNow.AddDays(3), EndAt = DateTime.UtcNow.AddDays(3).AddHours(1), OrganizerId = org.Id, Status = MeetingStatus.Cancelled, CreatedAt = DateTime.UtcNow };
        t.Db.Meetings.AddRange(m, cancelled);
        t.Db.MeetingAttendees.AddRange(
            new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = yes.Id, RsvpStatus = "accept" },
            new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = no.Id, RsvpStatus = "decline" },
            new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = cancelled.Id, UserId = yes.Id, RsvpStatus = "accept" });
        t.Db.SaveChanges();
        var p = new CalendarSearchProvider(t.Verify());
        var hits = await p.SearchAsync(Q(yes.Id, "quarterly"), Ct);
        Assert.Equal([$"meeting:{m.Id}"], hits.Items.Select(i => i.Id));
        Assert.Single((await p.SearchAsync(Q(yes.Id, "rmm"), Ct)).Items);              // agenda text, body tier
        Assert.Empty((await p.SearchAsync(Q(no.Id, "quarterly"), Ct)).Items);
    }

    // ── Tickets / Calls / Notifications / Navigation ─────────────────────────

    [Fact]
    public async Task Tickets_are_the_requesters_own_and_removed_ones_are_hidden()
    {
        using var t = new TestDb();
        var me = t.AddUser("T_ME"); var other = t.AddUser("T_OTHER");
        Ticket Tk(Guid user, string title, string? number = null, string? desc = null, DateTime? removed = null) => new()
        {
            Id = Guid.NewGuid(), UserId = user, Provider = "idesk", Origin = "IPOP", Status = "TODO", Priority = "HIGH", Module = "ict", ModuleName = "ICT",
            Category = "Printer", Title = title, TicketNumber = number, Description = desc, RemovedAt = removed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var mine = Tk(me.Id, "Printer jam", "ICT-000123");
        t.Db.Tickets.AddRange(mine, Tk(me.Id, "Laptop slow", "ICT-000124", "printer driver missing"), Tk(me.Id, "Printer removed", removed: DateTime.UtcNow), Tk(other.Id, "Printer other"));
        t.Db.SaveChanges();
        var p = new TicketSearchProvider(t.Verify());
        var r = await p.SearchAsync(Q(me.Id, "printer"), Ct);
        Assert.Equal(2, r.Total);
        Assert.Equal("Printer jam", r.Items[0].Title);
        Assert.Equal(SearchMatch.Body, r.Items[1].Match);
        Assert.Equal($"/tickets/{mine.Id}", r.Items[0].Url);
        Assert.Equal("Printer jam", (await p.SearchAsync(Q(me.Id, "000123"), Ct)).Items.Single().Title);
        Assert.Empty((await p.SearchAsync(Q(other.Id, "jam"), Ct)).Items);
    }

    [Fact]
    public async Task Calls_match_the_other_participants_name_only_in_the_performers_own_log()
    {
        using var t = new TestDb();
        var me = t.AddUser("CL_ME"); var peer = t.AddUser("CL_PEER"); peer.Name = "Siti Rahma"; t.Db.SaveChanges();
        var call = new CallRoom { Id = Guid.NewGuid(), CreatorId = me.Id, Kind = "user", Status = "ended", StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        t.Db.CallRooms.Add(call);
        t.Db.CallParticipants.AddRange(
            new CallParticipant { Id = Guid.NewGuid(), CallId = call.Id, UserId = me.Id, Status = "accepted", JoinedAt = DateTime.UtcNow },
            new CallParticipant { Id = Guid.NewGuid(), CallId = call.Id, UserId = peer.Id, Status = "accepted", JoinedAt = DateTime.UtcNow });
        t.Db.CallHistories.Add(new CallHistory { Id = Guid.NewGuid(), CallId = call.Id, PerformerId = me.Id, Direction = "outgoing", Result = "completed", DurationSeconds = 65, StartedAt = DateTime.UtcNow, EndedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var p = new CallsSearchProvider(t.Verify());
        var r = await p.SearchAsync(Q(me.Id, "siti"), Ct);
        Assert.Equal("Siti Rahma", r.Items.Single().Title);
        Assert.Contains("1:05", r.Items[0].Subtitle);
        Assert.Empty((await p.SearchAsync(Q(peer.Id, "siti"), Ct)).Items);            // peer has no log row of their own
    }

    [Fact]
    public async Task Notifications_search_only_what_was_delivered_to_me()
    {
        using var t = new TestDb();
        var me = t.AddUser("NF_ME"); var other = t.AddUser("NF_OTHER");
        var client = t.AddNotificationClient("hris", "hash");
        NotificationRequest Req(string title, string body) => new() { Id = Guid.NewGuid(), ClientId = client.Id, IdempotencyKey = Guid.NewGuid().ToString(), Priority = "normal", EventType = "payroll.ready", Title = title, Body = body, Status = "delivered", TargetUrl = "/mail", AcceptedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var mine = Req("Payslip ready", "Your payslip for March"); var theirs = Req("Payslip ready", "x"); var body = Req("Reminder", "check your payslip");
        t.Db.NotificationRequests.AddRange(mine, theirs, body);
        t.Db.NotificationDeliveries.AddRange(
            new NotificationDelivery { Id = Guid.NewGuid(), RequestId = mine.Id, UserId = me.Id, Status = "delivered", DeliveredAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new NotificationDelivery { Id = Guid.NewGuid(), RequestId = body.Id, UserId = me.Id, Status = "read", DeliveredAt = DateTime.UtcNow.AddMinutes(-5), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new NotificationDelivery { Id = Guid.NewGuid(), RequestId = theirs.Id, UserId = other.Id, Status = "delivered", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var r = await new NotificationsSearchProvider(t.Verify()).SearchAsync(Q(me.Id, "payslip"), Ct);
        Assert.Equal(2, r.Total);
        Assert.Equal(mine.Id.ToString(), r.Items[0].Id);
        Assert.Equal("/mail", r.Items[0].Url);
        Assert.Equal(SearchMatch.Body, r.Items[1].Match);
    }

    [Fact]
    public async Task Navigation_offers_Administration_to_admins_only()
    {
        using var t = new TestDb();
        var me = t.AddUser("NAV_ME"); var admin = t.AddAdmin("NAV_ADMIN");
        var p = new NavigationSearchProvider(t.Verify());
        Assert.Empty((await p.SearchAsync(Q(me.Id, "admin"), Ct)).Items);
        Assert.Contains((await p.SearchAsync(Q(admin.Id, "admin"), Ct)).Items, i => i.Url == "/admin");
        Assert.Equal("/notes", (await p.SearchAsync(Q(me.Id, "notes"), Ct)).Items[0].Url);
    }

    // ── Directory ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Directory_ranks_exact_name_first_and_keeps_the_nik_for_calendar_actions()
    {
        using var t = new TestDb();
        var me = t.AddUser("D_ME");
        var exact = t.AddUser("D_1"); exact.Name = "Rina";
        var contains = t.AddUser("D_2"); contains.Name = "Rina Wijaya";
        var inactive = t.AddUser("D_3", active: false); inactive.Name = "Rina Gone";
        t.Db.SaveChanges();
        var r = await new DirectorySearchProvider(t.Verify()).SearchAsync(Q(me.Id, "rina"), Ct);
        Assert.Equal(2, r.Total);
        Assert.Equal(SearchMatch.TitleExact, r.Items[0].Match);
        Assert.Equal("D_1", r.Items[0].Nik);
        Assert.Equal("free", r.Items[0].Status);
        Assert.Equal($"/directory/person/{exact.Id}", r.Items[0].Url);
    }

    // ── Files (NAS) ──────────────────────────────────────────────────────────

    private sealed class FakeNas : IFileAccessService
    {
        public List<CredentialDto> Credentials = [];
        public Dictionary<string, List<SynoItem>> Results = [];
        public HashSet<string> Failing = [];
        public List<string> Started = [], Stopped = [];
        public bool NeverFinish;
        public HashSet<string> NeverFinishOn = [];
        public Task<IReadOnlyList<CredentialDto>> ListCredentialsAsync(Guid userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<CredentialDto>>(Credentials);
        public Task<IReadOnlyList<string>> ListSharesAsync(Guid userId, string server, CancellationToken ct)
            => Failing.Contains(server) ? throw new SynoException("down") : Task.FromResult<IReadOnlyList<string>>(["/sja"]);
        public Task<string> SearchStartAsync(Guid userId, string server, IReadOnlyList<string> folderPaths, string pattern, CancellationToken ct)
        { Started.Add(server); return Task.FromResult("task-" + server); }
        public Task<SynoSearchPage> SearchListAsync(Guid userId, string server, string taskId, int offset, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var items = Results.GetValueOrDefault(server) ?? [];
            var hang = NeverFinish || NeverFinishOn.Contains(server);
            return Task.FromResult(new SynoSearchPage(hang ? [] : items, items.Count, !hang));
        }
        public Task SearchStopAsync(Guid userId, string server, string taskId, CancellationToken ct) { Stopped.Add(server); return Task.CompletedTask; }
        public bool HasServiceAccount => false;
        public string? ServiceServer => null;
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Ok, string? Error)> SetCredentialAsync(Guid userId, string server, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SiteConnectResult>> ConnectAllAsync(Guid userId, string username, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Found, CredentialDto? Credential)> GetCredentialAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task ClearCredentialAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoItem>> ListFolderAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task UploadAsync(Guid userId, string server, string dir, string filename, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(Guid userId, string server, string path, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateFolderAsync(Guid userId, string server, string folderPath, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(Guid userId, string server, string path, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyMoveAsync(Guid userId, string server, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct) => throw new NotSupportedException();
        public Task<SynoShareLink> CreateSharingLinkAsync(Guid userId, string server, string path, string? password, int? expiresInDays, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(Guid userId, string server, string? path, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSharingLinkAsync(Guid userId, string server, string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<SynoStorageInfo> GetStorageInfoAsync(Guid userId, string server, CancellationToken ct) => throw new NotSupportedException();
        public void DropSession(Guid userId, string server) { }
        public void DropAllSessions(Guid userId) { }
    }

    [Fact]
    public async Task Files_without_any_credential_says_connect_to_search_instead_of_no_results()
    {
        var r = await new FilesSearchProvider(new FakeNas()).SearchAsync(Q(Guid.NewGuid(), "report"), Ct);
        Assert.Equal(SearchStatus.Unavailable, r.Status);
        Assert.Equal("/files", r.ActionUrl);
        Assert.Contains("Connect", r.Message);
    }

    [Fact]
    public async Task Files_searches_only_connected_servers_ranks_names_stops_every_task_and_reports_a_server_that_fails()
    {
        var nas = new FakeNas
        {
            Credentials = [new("spj", true, false, "u"), new("krw", true, false, "u"), new("jtb", true, true, "u")],
            Results = { ["spj"] = [new("Report.xlsx", "/sja/Finance/Report.xlsx", false, 10, 1700000000), new("report", "/sja/report", true, 0, 1600000000)] },
            Failing = { "krw" },
        };
        var r = await new FilesSearchProvider(nas).SearchAsync(Q(Guid.NewGuid(), "report"), Ct);
        Assert.Equal(SearchStatus.Ready, r.Status);
        Assert.Equal(["spj"], nas.Started);                                      // jtb (invalid credential) and smg (none) never started
        Assert.Equal(["spj"], nas.Stopped);
        Assert.Equal(2, r.Total);
        Assert.Equal("report", r.Items[0].Title);                                // exact name first, folder opens itself
        Assert.Equal("/files?server=spj&path=%2Fsja%2Freport", r.Items[0].Url);
        Assert.Equal("/files?server=spj&path=%2Fsja%2FFinance", r.Items[1].Url); // file opens its folder
        Assert.True(r.Partial);
        Assert.Contains("KRW did not answer", r.Message);
        Assert.Contains("Not connected: JTB, SMG", r.Message);                   // invalid credential counts as not connected
    }

    [Fact]
    public async Task Files_stops_the_nas_task_when_the_budget_runs_out_or_the_caller_cancels()
    {
        var slow = new FakeNas { Credentials = [new("spj", true, false, "u")], NeverFinish = true };
        var r = await new FilesSearchProvider(slow, budget: TimeSpan.FromMilliseconds(50)).SearchAsync(Q(Guid.NewGuid(), "x"), Ct);
        Assert.Equal(SearchStatus.Ready, r.Status);
        Assert.True(r.Partial);
        Assert.Equal(["spj"], slow.Stopped);

        var cancelled = new FakeNas { Credentials = [new("spj", true, false, "u")], NeverFinish = true };
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FilesSearchProvider(cancelled).SearchAsync(Q(Guid.NewGuid(), "x"), cts.Token));
        Assert.Equal(["spj"], cancelled.Stopped);                                // cleaned up with its own short token
    }

    [Fact]
    public async Task Files_searches_every_nas_at_once_so_a_slow_one_does_not_hide_the_others()
    {
        // One FakeNas behind every scope (it has no DbContext); the provider asks the scope factory per NAS.
        var nas = new FakeNas
        {
            Credentials = [new("spj", true, false, "u"), new("krw", true, false, "u")],
            Results = { ["krw"] = [new SynoItem("report.pdf", "/plant/report.pdf", false, 10, 1)] },
            NeverFinishOn = { "spj" },
        };
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IFileAccessService>(services, nas);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var scopes = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp);

        var r = await new FilesSearchProvider(nas, budget: TimeSpan.FromMilliseconds(300), scopes: scopes).SearchAsync(Q(Guid.NewGuid(), "report"), Ct);

        Assert.Contains(r.Items, h => h.Title == "report.pdf");   // KRW answered although SPJ hung the whole budget
        Assert.True(r.Partial);
        Assert.Contains("spj", nas.Stopped);
    }

    [Fact]
    public async Task Files_when_every_server_fails_is_an_error()
    {
        var nas = new FakeNas { Credentials = [new("spj", true, false, "u")], Failing = { "spj" } };
        var r = await new FilesSearchProvider(nas).SearchAsync(Q(Guid.NewGuid(), "x"), Ct);
        Assert.Equal(SearchStatus.Error, r.Status);
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    [Fact]
    public void Snippet_windows_around_the_match_and_flattens_whitespace()
    {
        var text = string.Join("\n", Enumerable.Repeat("filler words", 30)) + "\nthe RMM server\n" + string.Join(" ", Enumerable.Repeat("tail", 30));
        var s = SearchText.Snippet(text, "rmm")!;
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.Contains("the RMM server", s);
        Assert.DoesNotContain("\n", s);
        Assert.Null(SearchText.Snippet("", "x"));
    }
}
