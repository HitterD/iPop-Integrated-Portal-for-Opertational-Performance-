using IPop.Application;
using IPop.Application.Hub;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Hub;

/// The Hub (docs/superpowers/specs/2026-11-21-the-hub-design.md). Confidential: every read and write checks
/// membership; nobody outside a topic (not admins, not heads) can open it. Notifications and realtime are
/// best effort — a failed push never fails the request.
public sealed class HubService(
    IPopDbContext db,
    INotificationGatewayService gateway,
    IRealtime realtime,
    IProjectService projects,
    ILogger<HubService> log) : IHubService
{
    public const int LateAfterWorkingDays = 3;
    public const int RemindAfterWorkingDays = 2;
    private static readonly TimeSpan RemindCooldown = TimeSpan.FromHours(1);

    // ── Reads ─────────────────────────────────────────────────────────────

    public async Task<HubListDto> ListAsync(Guid me, string? filter, string? q, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var mine = await (from m in db.HubMembers.AsNoTracking()
                          join t in db.HubTopics.AsNoTracking() on m.TopicId equals t.Id
                          where m.UserId == me && t.DeletedAt == null
                          select new { Topic = t, m.LastReadAt }).ToListAsync(ct);
        var ids = mine.Select(x => x.Topic.Id).ToList();

        var members = await db.HubMembers.AsNoTracking().Where(m => ids.Contains(m.TopicId))
            .Select(m => new { m.TopicId, m.UserId, m.AddedAt }).ToListAsync(ct);
        var asks = await db.HubAsks.AsNoTracking().Where(a => ids.Contains(a.TopicId) && a.AnsweredAt == null)
            .Select(a => new { a.TopicId, a.ToUserId, a.CreatedAt }).ToListAsync(ct);
        var posts = await db.HubPosts.AsNoTracking().Where(p => ids.Contains(p.TopicId))
            .Select(p => new { p.TopicId, p.AuthorId, p.Kind, p.Body, p.CreatedAt }).ToListAsync(ct);
        var people = await People(members.Select(m => m.UserId).Distinct(), ct);

        var rows = new List<HubTopicListItemDto>();
        foreach (var x in mine)
        {
            var t = x.Topic;
            var tp = members.Where(m => m.TopicId == t.Id).OrderBy(m => m.AddedAt).Select(m => people[m.UserId]).ToList();
            var open = asks.Where(a => a.TopicId == t.Id).ToList();
            var replies = posts.Where(p => p.TopicId == t.Id && p.Kind == "reply").ToList();
            var last = replies.OrderByDescending(p => p.CreatedAt).FirstOrDefault();
            var unread = posts.Any(p => p.TopicId == t.Id && p.AuthorId != me && (x.LastReadAt == null || p.CreatedAt > x.LastReadAt));
            var waiting = open.Where(a => a.ToUserId == me).ToList();
            rows.Add(new HubTopicListItemDto(
                t.Id, t.Kind, t.Title, StatusOf(t), t.CreatedAt, t.LastActivityAt, t.DueDate, t.SolvedAt,
                people[t.CreatedById], tp, tp.Select(p => p.Department ?? p.Id.ToString()).Distinct().Count(),
                replies.Count, unread, t.SolvedAt == null && waiting.Count > 0,
                t.SolvedAt == null && waiting.Any(a => IsLate(a.CreatedAt, now)),
                t.SolvedAt == null ? open.Select(a => people.TryGetValue(a.ToUserId, out var p) ? PersonName.Short(p.Name) : "").Distinct().ToList() : [],
                t.SolvedAt == null ? open.Where(a => IsLate(a.CreatedAt, now)).Select(a => people.TryGetValue(a.ToUserId, out var p) ? PersonName.Short(p.Name) : "").Distinct().ToList() : [],
                last?.AuthorId is { } la && people.TryGetValue(la, out var lp) ? PersonName.Short(lp.Name) : null,
                last is null ? null : Preview(last.Body)));
        }

        bool ForMe(HubTopicListItemDto r) => r.Status != HubStatus.Solved && (r.WaitingForMe || r.Unread || r.CreatedBy.Id == me);
        var counts = new HubCountsDto(rows.Count(ForMe), rows.Count(r => r.Status == HubStatus.New),
            rows.Count(r => r.Status == HubStatus.Ongoing), rows.Count(r => r.Status == HubStatus.Solved));

        IEnumerable<HubTopicListItemDto> list = (filter ?? "mine") switch
        {
            "new" => rows.Where(r => r.Status == HubStatus.New),
            "ongoing" => rows.Where(r => r.Status == HubStatus.Ongoing),
            "solved" => rows.Where(r => r.Status == HubStatus.Solved),
            "all" => rows,
            _ => rows.Where(ForMe),
        };
        if (!string.IsNullOrWhiteSpace(q))
        {
            var w = q.Trim();
            list = list.Where(r => r.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
                || r.People.Any(p => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                || HubKinds.Label(r.Kind).Contains(w, StringComparison.OrdinalIgnoreCase));
        }
        // Waiting for me first, then late, then most recent activity.
        var ordered = list.OrderByDescending(r => r.WaitingForMe).ThenByDescending(r => r.LateFor.Count > 0)
            .ThenByDescending(r => r.LastActivityAt).ToList();
        return new HubListDto(ordered, counts);
    }

    public async Task<HubResult<HubTopicDto>> GetAsync(Guid me, Guid topicId, bool markRead, CancellationToken ct)
    {
        var member = await db.HubMembers.FirstOrDefaultAsync(m => m.TopicId == topicId && m.UserId == me, ct);
        if (member is null || !await db.HubTopics.AnyAsync(t => t.Id == topicId && t.DeletedAt == null, ct)) return NotFound<HubTopicDto>();
        var readUpTo = member.LastReadAt;
        var dto = await BuildAsync(me, topicId, readUpTo, ct);
        if (markRead)
        {
            member.LastReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await PushBadgeAsync([me], ct);
        }
        return HubResult<HubTopicDto>.Ok(dto);
    }

    public Task<bool> IsMemberAsync(Guid me, Guid topicId, CancellationToken ct) =>
        db.HubMembers.AnyAsync(m => m.TopicId == topicId && m.UserId == me, ct);

    public Task<int> WaitingCountAsync(Guid me, CancellationToken ct) =>
        (from a in db.HubAsks.AsNoTracking()
         join t in db.HubTopics.AsNoTracking() on a.TopicId equals t.Id
         where a.ToUserId == me && a.AnsweredAt == null && t.SolvedAt == null && t.DeletedAt == null
         select a.TopicId).Distinct().CountAsync(ct);

    // ── Writes ────────────────────────────────────────────────────────────

    public async Task<HubResult<HubTopicDto>> CreateAsync(Guid me, CreateHubTopicRequest req, CancellationToken ct)
    {
        var kind = HubKinds.All.Contains(req.Kind ?? "") ? req.Kind! : HubKinds.Other;
        var title = (req.Title ?? "").Trim();
        var need = (req.Need ?? "").Trim();
        if (title.Length < 3) return Fail<HubTopicDto>("invalid", "Write a title people understand without opening the topic.");
        if (need.Length < 3) return Fail<HubTopicDto>("invalid", "Write what you need from them.");
        if (title.Length > HubEvents.TitleMax) title = title[..HubEvents.TitleMax];
        if (need.Length > HubEvents.NeedMax) need = need[..HubEvents.NeedMax];

        var wanted = (req.People ?? []).Where(id => id != me).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive && (u.Id == me || wanted.Contains(u.Id)))
            .Select(u => new { u.Id, u.DepartmentId }).ToListAsync(ct);
        var self = users.FirstOrDefault(u => u.Id == me);
        if (self is null) return Fail<HubTopicDto>("forbidden", "Your account can't start topics.");
        var others = users.Where(u => u.Id != me).ToList();
        if (others.Count == 0) return Fail<HubTopicDto>("rule", "Add at least one person from another department.");
        if (!others.Any(o => o.DepartmentId is null || self.DepartmentId is null || o.DepartmentId != self.DepartmentId))
            return Fail<HubTopicDto>("rule", "Add someone from another department. The Hub is for work across departments.");

        var now = DateTime.UtcNow;
        var topic = new HubTopic
        {
            Id = Guid.NewGuid(), Kind = kind, Title = title, Need = need, DueDate = req.DueDate?.Date,
            CreatedById = me, CreatedAt = now, UpdatedAt = now, LastActivityAt = now,
        };
        db.HubTopics.Add(topic);
        db.HubMembers.Add(new HubMember { TopicId = topic.Id, UserId = me, AddedById = me, AddedAt = now, LastReadAt = now });
        foreach (var o in others)
            db.HubMembers.Add(new HubMember { TopicId = topic.Id, UserId = o.Id, AddedById = me, AddedAt = now });
        await db.SaveChangesAsync(ct);

        var name = await NameAsync(me, ct);
        await NotifyAsync(others.Select(o => o.Id).ToList(), "hub.topic_new",
            $"{name} needs you in The Hub", $"{title}{Due(topic.DueDate)}", topic.Id, "high", ct);
        await PushChangedAsync(topic.Id, ct);
        return HubResult<HubTopicDto>.Ok(await BuildAsync(me, topic.Id, now, ct));
    }

    public async Task<HubResult<HubTopicDto>> PostAsync(Guid me, Guid topicId, CreateHubPostRequest req, CancellationToken ct)
    {
        var topic = await MemberTopicAsync(me, topicId, ct);
        if (topic is null) return NotFound<HubTopicDto>();
        if (topic.SolvedAt is not null) return Fail<HubTopicDto>("solved", "This topic is solved. Reopen it to reply.");
        var body = (req.Body ?? "").Trim();
        var files = (req.AttachmentIds ?? []).Distinct().ToList();
        if (body.Length == 0 && files.Count == 0) return Fail<HubTopicDto>("invalid", "Write something first.");
        if (body.Length > HubEvents.BodyMax) body = body[..HubEvents.BodyMax];
        if (body.Length == 0) body = files.Count == 1 ? "Shared a file." : $"Shared {files.Count} files.";

        // One level: a reply to a reply hangs under the same top post.
        Guid? parent = null;
        if (req.ParentId is { } pid)
        {
            var p = await db.HubPosts.AsNoTracking().Where(x => x.Id == pid && x.TopicId == topicId)
                .Select(x => new { x.Id, x.ParentId }).FirstOrDefaultAsync(ct);
            parent = p?.ParentId ?? p?.Id;
        }

        var now = DateTime.UtcNow;
        var post = new HubPost { Id = Guid.NewGuid(), TopicId = topicId, AuthorId = me, ParentId = parent, Body = body, Kind = "reply", CreatedAt = now };
        db.HubPosts.Add(post);

        // Files uploaded for this topic and not yet tied to a post.
        if (files.Count > 0)
            await db.HubAttachments.Where(a => a.TopicId == topicId && files.Contains(a.AttachmentId) && a.UploaderId == me && a.PostId == null)
                .ForEachAsync(a => a.PostId = post.Id, ct);

        // My reply answers every question waiting for me in this topic.
        var answered = await db.HubAsks.Where(a => a.TopicId == topicId && a.ToUserId == me && a.AnsweredAt == null).ToListAsync(ct);
        foreach (var a in answered) { a.AnsweredAt = now; a.AnsweredPostId = post.Id; }

        HubAsk? ask = null;
        if (req.AskUserId is { } to && to != me)
        {
            if (!await db.HubMembers.AnyAsync(m => m.TopicId == topicId && m.UserId == to, ct))
                return Fail<HubTopicDto>("invalid", "That person isn't in this topic. Add them first.");
            ask = new HubAsk
            {
                Id = Guid.NewGuid(), TopicId = topicId, PostId = post.Id, ToUserId = to, AskedById = me,
                Question = body.Length > 500 ? body[..500] : body, CreatedAt = now,
            };
            db.HubAsks.Add(ask);
        }

        if (me != topic.CreatedById) topic.HasReply = true;
        topic.LastActivityAt = now;
        topic.UpdatedAt = now;
        var meMember = await db.HubMembers.FirstAsync(m => m.TopicId == topicId && m.UserId == me, ct);
        meMember.LastReadAt = now;
        await db.SaveChangesAsync(ct);

        var name = await NameAsync(me, ct);
        var everyone = await MemberIdsAsync(topicId, ct);
        var others = everyone.Where(id => id != me && id != ask?.ToUserId).ToList();
        if (ask is not null)
            await NotifyAsync([ask.ToUserId], "hub.ask", $"{name} needs your answer", $"{Preview(body)} · {topic.Title}", topicId, "high", ct);
        await NotifyAsync(others, "hub.reply", $"{name} replied in The Hub", $"{Preview(body)} · {topic.Title}", topicId, "normal", ct);
        await PushChangedAsync(topicId, ct);
        if (answered.Count > 0) await PushBadgeAsync([me], ct);
        return HubResult<HubTopicDto>.Ok(await BuildAsync(me, topicId, now, ct));
    }

    public async Task<HubResult<HubTopicDto>> AddMembersAsync(Guid me, Guid topicId, AddHubMembersRequest req, CancellationToken ct)
    {
        var topic = await MemberTopicAsync(me, topicId, ct);
        if (topic is null) return NotFound<HubTopicDto>();
        var already = await MemberIdsAsync(topicId, ct);
        var wanted = (req.People ?? []).Distinct().Where(id => !already.Contains(id)).ToList();
        var users = await db.Users.AsNoTracking().Where(u => u.IsActive && wanted.Contains(u.Id)).Select(u => new { u.Id, u.Name }).ToListAsync(ct);
        if (users.Count == 0) return Fail<HubTopicDto>("invalid", "Pick someone who isn't in this topic yet.");
        var now = DateTime.UtcNow;
        foreach (var u in users) db.HubMembers.Add(new HubMember { TopicId = topicId, UserId = u.Id, AddedById = me, AddedAt = now });
        var name = await NameAsync(me, ct);
        db.HubPosts.Add(new HubPost
        {
            Id = Guid.NewGuid(), TopicId = topicId, Kind = "system", CreatedAt = now,
            Body = $"{name} added {string.Join(", ", users.Select(u => PersonName.Display(u.Name)))}. They can read the whole topic.",
        });
        topic.LastActivityAt = now;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(users.Select(u => u.Id).ToList(), "hub.added", $"{name} added you to a topic", topic.Title, topicId, "normal", ct);
        await PushChangedAsync(topicId, ct);
        return HubResult<HubTopicDto>.Ok(await BuildAsync(me, topicId, now, ct));
    }

    public async Task<HubResult<bool>> RemindAsync(Guid me, Guid topicId, Guid askId, CancellationToken ct)
    {
        var topic = await MemberTopicAsync(me, topicId, ct);
        if (topic is null) return NotFound<bool>();
        var ask = await db.HubAsks.FirstOrDefaultAsync(a => a.Id == askId && a.TopicId == topicId, ct);
        if (ask is null || ask.AnsweredAt is not null || topic.SolvedAt is not null) return Fail<bool>("invalid", "Nothing to remind about.");
        var now = DateTime.UtcNow;
        if (ask.LastRemindedAt is { } last && now - last < RemindCooldown)
            return Fail<bool>("rate_limited", "Already reminded in the last hour.");
        ask.LastRemindedAt = now;
        await db.SaveChangesAsync(ct);
        var name = await NameAsync(me, ct);
        await NotifyAsync([ask.ToUserId], "hub.remind", $"{name} is waiting for your answer", $"{Preview(ask.Question)} · {topic.Title}", topicId, "high", ct);
        return HubResult<bool>.Ok(true);
    }

    public async Task<HubResult<HubTopicDto>> SolveAsync(Guid me, Guid topicId, SolveHubTopicRequest req, CancellationToken ct)
    {
        var topic = await MemberTopicAsync(me, topicId, ct);
        if (topic is null) return NotFound<HubTopicDto>();
        if (topic.SolvedAt is not null) return Fail<HubTopicDto>("solved", "This topic is already solved.");
        var summary = (req.Summary ?? "").Trim();
        if (summary.Length < 3) return Fail<HubTopicDto>("invalid", "Write how it was solved, in one or two sentences.");
        if (summary.Length > HubEvents.SummaryMax) summary = summary[..HubEvents.SummaryMax];

        // Follow-up tasks first: if Projects refuses one, nothing is marked solved.
        var made = new List<string>();
        foreach (var task in (req.Tasks ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Title)))
        {
            var title = task.Title!.Trim();
            if (title.Length > 300) title = title[..300];
            var created = await projects.CreateTaskAsync(me, new CreateTaskRequest(task.ProjectId, null, title,
                $"From The Hub topic “{topic.Title}”.", "todo", null, null, task.DueDate?.Date, null, null, "hub"), ct);
            if (!created.Success)
                return Fail<HubTopicDto>(created.Error == "forbidden" ? "forbidden" : "invalid",
                    $"Projects didn't accept “{title}”: {Explain(created.Error)}");
            var assigned = await projects.AssignUserAsync(me, created.Task!.Id, task.AssigneeId, null, ct);
            if (!assigned.Success)
                return Fail<HubTopicDto>("invalid", $"“{title}” was added to Projects, but the person isn't a member of that project.");
            made.Add($"{created.Task.Id}|{task.ProjectId}");
            try { await realtime.ToAllAsync(RealtimeEvents.ProjectChanged, task.ProjectId, ct); } catch { /* advisory */ }
        }

        var now = DateTime.UtcNow;
        topic.SolvedAt = now;
        topic.SolvedById = me;
        topic.SolvedSummary = summary + (made.Count > 0 ? "\n[tasks]" + string.Join(",", made) : "");
        topic.LastActivityAt = now;
        topic.UpdatedAt = now;
        var name = await NameAsync(me, ct);
        db.HubPosts.Add(new HubPost
        {
            Id = Guid.NewGuid(), TopicId = topicId, Kind = "system", CreatedAt = now,
            Body = $"{name} marked the topic as solved." + (made.Count > 0 ? $" {made.Count} follow-up task{(made.Count > 1 ? "s were" : " was")} added to Projects." : ""),
        });
        await db.SaveChangesAsync(ct);

        var others = (await MemberIdsAsync(topicId, ct)).Where(id => id != me).ToList();
        await NotifyAsync(others, "hub.solved", $"Solved: {topic.Title}", Preview(summary), topicId, "normal", ct);
        await PushChangedAsync(topicId, ct);
        await PushBadgeAsync(others.Append(me), ct);
        return HubResult<HubTopicDto>.Ok(await BuildAsync(me, topicId, now, ct));
    }

    public async Task<HubResult<HubTopicDto>> ReopenAsync(Guid me, Guid topicId, CancellationToken ct)
    {
        var topic = await MemberTopicAsync(me, topicId, ct);
        if (topic is null) return NotFound<HubTopicDto>();
        if (topic.SolvedAt is null) return Fail<HubTopicDto>("invalid", "This topic is still open.");
        var now = DateTime.UtcNow;
        topic.SolvedAt = null;
        topic.SolvedById = null;
        topic.SolvedSummary = null;
        topic.LastActivityAt = now;
        var name = await NameAsync(me, ct);
        db.HubPosts.Add(new HubPost { Id = Guid.NewGuid(), TopicId = topicId, Kind = "system", CreatedAt = now, Body = $"{name} reopened the topic." });
        await db.SaveChangesAsync(ct);
        var others = (await MemberIdsAsync(topicId, ct)).Where(id => id != me).ToList();
        await NotifyAsync(others, "hub.reopened", $"Reopened: {topic.Title}", $"{name} reopened this topic.", topicId, "normal", ct);
        await PushChangedAsync(topicId, ct);
        return HubResult<HubTopicDto>.Ok(await BuildAsync(me, topicId, now, ct));
    }

    public async Task<HubResult<bool>> LinkUploadAsync(Guid me, Guid topicId, Guid attachmentId, CancellationToken ct)
    {
        if (await MemberTopicAsync(me, topicId, ct) is null) return NotFound<bool>();
        db.HubAttachments.Add(new HubAttachment { TopicId = topicId, AttachmentId = attachmentId, UploaderId = me, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
        return HubResult<bool>.Ok(true);
    }

    // ── Reminder worker step (public for tests) ──────────────────────────

    /// Day 2: a reminder to the person. Day 3: their department head gets a reminder without the topic text.
    public async Task<int> RunRemindersAsync(DateTime now, CancellationToken ct)
    {
        var open = await (from a in db.HubAsks
                          join t in db.HubTopics on a.TopicId equals t.Id
                          where a.AnsweredAt == null && t.SolvedAt == null && t.DeletedAt == null
                          select new { Ask = a, t.Title }).ToListAsync(ct);
        var sent = 0;
        foreach (var x in open)
        {
            var a = x.Ask;
            var days = WorkingDaysBetween(a.CreatedAt, now);
            if (days >= LateAfterWorkingDays && a.HeadToldAt is null)
            {
                a.HeadToldAt = now;
                var who = await db.Users.AsNoTracking().Where(u => u.Id == a.ToUserId)
                    .Select(u => new { u.Name, HeadId = u.Department != null ? u.Department.HeadUserId : null }).FirstOrDefaultAsync(ct);
                if (who?.HeadId is { } head && head != a.ToUserId)
                {
                    await NotifyAsync([head], "hub.head_reminder", $"{PersonName.Display(who.Name)} has a late answer in The Hub",
                        "Someone has been waiting 3 working days. The topic is confidential, so only the people in it can open it.", null, "normal", ct,
                        $"hub-head-{a.Id}");
                    sent++;
                }
            }
            else if (days >= RemindAfterWorkingDays && a.LastRemindedAt is null)
            {
                a.LastRemindedAt = now;
                await NotifyAsync([a.ToUserId], "hub.remind", "Someone is waiting for your answer", $"{Preview(a.Question)} · {x.Title}", a.TopicId, "high", ct,
                    $"hub-remind-{a.Id}");
                sent++;
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    public static int WorkingDaysBetween(DateTime from, DateTime to)
    {
        var d = from.Date; var end = to.Date; var n = 0;
        while (d < end)
        {
            d = d.AddDays(1);
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        }
        return n;
    }

    public static bool IsLate(DateTime askedAt, DateTime now) => WorkingDaysBetween(askedAt, now) >= LateAfterWorkingDays;

    public static string StatusOf(HubTopic t) => t.SolvedAt is not null ? HubStatus.Solved : t.HasReply ? HubStatus.Ongoing : HubStatus.New;

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<HubTopicDto> BuildAsync(Guid me, Guid topicId, DateTime? readUpTo, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var t = await db.HubTopics.AsNoTracking().FirstAsync(x => x.Id == topicId, ct);
        var memberIds = await db.HubMembers.AsNoTracking().Where(m => m.TopicId == topicId).OrderBy(m => m.AddedAt).Select(m => m.UserId).ToListAsync(ct);
        var posts = await db.HubPosts.AsNoTracking().Where(p => p.TopicId == topicId).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var asks = await db.HubAsks.AsNoTracking().Where(a => a.TopicId == topicId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var links = await db.HubAttachments.AsNoTracking().Where(a => a.TopicId == topicId).ToListAsync(ct);
        var attIds = links.Select(l => l.AttachmentId).ToList();
        var atts = await db.Attachments.AsNoTracking().Where(a => attIds.Contains(a.Id) && a.DeletedAt == null)
            .Select(a => new { a.Id, a.FileName, a.ContentType, a.Size }).ToListAsync(ct);
        var ids = memberIds.Concat(posts.Where(p => p.AuthorId != null).Select(p => p.AuthorId!.Value))
            .Concat(asks.Select(a => a.AskedById)).Concat(asks.Select(a => a.ToUserId)).Append(t.CreatedById)
            .Concat(links.Select(l => l.UploaderId)).Concat(t.SolvedById is { } s ? [s] : []).Distinct();
        var people = await People(ids, ct);

        var files = (from l in links
                     join a in atts on l.AttachmentId equals a.Id
                     orderby l.CreatedAt
                     select new HubFileDto(a.Id, a.FileName, a.ContentType, a.Size, $"/api/attachments/{a.Id}", l.PostId,
                         people.TryGetValue(l.UploaderId, out var up) ? PersonName.Short(up.Name) : "")).ToList();
        var askByPost = asks.GroupBy(a => a.PostId).ToDictionary(g => g.Key, g => g.First());
        var postDtos = posts.Select(p =>
        {
            askByPost.TryGetValue(p.Id, out var ask);
            return new HubPostDto(p.Id, p.ParentId, p.AuthorId is { } a && people.TryGetValue(a, out var ap) ? ap : null, p.Body, p.Kind, p.CreatedAt,
                p.AuthorId != me && readUpTo is { } r && p.CreatedAt > r, files.Where(f => f.PostId == p.Id).ToList(),
                ask?.ToUserId, ask is null ? null : PersonName.Short(people[ask.ToUserId].Name), ask?.AnsweredAt is not null);
        }).ToList();
        var askDtos = asks.Select(a => new HubAskDto(a.Id, a.PostId, people[a.ToUserId], people[a.AskedById], a.Question, a.CreatedAt,
            a.AnsweredAt, a.AnsweredAt is null && IsLate(a.CreatedAt, now), a.LastRemindedAt)).ToList();
        var members = memberIds.Select(id => people[id]).ToList();

        var (summary, taskRefs) = SplitSummary(t.SolvedSummary);
        var tasks = new List<HubTaskLinkDto>();
        if (taskRefs.Count > 0)
        {
            var taskIds = taskRefs.Select(x => x.Task).ToList();
            var rows = await (from k in db.ProjectTasks.AsNoTracking()
                              join pr in db.Projects.AsNoTracking() on k.ProjectId equals pr.Id
                              where taskIds.Contains(k.Id)
                              select new { k.Id, k.ProjectId, k.Title, Project = pr.Name, k.DueDate }).ToListAsync(ct);
            var assignees = await (from a in db.ProjectTaskAssignees.AsNoTracking()
                                   join u in db.Users.AsNoTracking() on a.UserId equals u.Id
                                   where taskIds.Contains(a.TaskId)
                                   select new { a.TaskId, u.Name }).ToListAsync(ct);
            tasks = rows.Select(r => new HubTaskLinkDto(r.Id, r.ProjectId, r.Title, r.Project,
                PersonName.Short(assignees.FirstOrDefault(a => a.TaskId == r.Id)?.Name), r.DueDate)).ToList();
        }

        return new HubTopicDto(t.Id, t.Kind, t.Title, t.Need, StatusOf(t), t.CreatedAt, t.DueDate, people[t.CreatedById], members,
            members.Select(m => m.Department ?? "No department").Distinct().ToList(), postDtos, askDtos, files,
            t.SolvedAt, t.SolvedById is { } sb ? people[sb] : null, summary, tasks, readUpTo);
    }

    internal static (string? Summary, List<(Guid Task, Guid Project)> Tasks) SplitSummary(string? raw)
    {
        if (raw is null) return (null, []);
        var i = raw.IndexOf("\n[tasks]", StringComparison.Ordinal);
        if (i < 0) return (raw, []);
        var list = new List<(Guid, Guid)>();
        foreach (var part in raw[(i + 8)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split('|');
            if (bits.Length == 2 && Guid.TryParse(bits[0], out var tk) && Guid.TryParse(bits[1], out var pj)) list.Add((tk, pj));
        }
        return (raw[..i], list);
    }

    private async Task<Dictionary<Guid, HubPersonDto>> People(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var rows = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, Dept = u.Department != null ? u.Department.Name : null, u.PhotoPath }).ToListAsync(ct);
        var map = rows.ToDictionary(r => r.Id, r => new HubPersonDto(r.Id, PersonName.Display(r.Name), r.Dept,
            r.PhotoPath is { Length: > 0 } ph ? $"/api/photos/{ph}" : null));
        foreach (var id in list.Where(id => !map.ContainsKey(id))) map[id] = new HubPersonDto(id, "Former employee", null, null);
        return map;
    }

    private async Task<HubTopic?> MemberTopicAsync(Guid me, Guid topicId, CancellationToken ct) =>
        await db.HubMembers.AnyAsync(m => m.TopicId == topicId && m.UserId == me, ct)
            ? await db.HubTopics.FirstOrDefaultAsync(t => t.Id == topicId && t.DeletedAt == null, ct)
            : null;

    private Task<List<Guid>> MemberIdsAsync(Guid topicId, CancellationToken ct) =>
        db.HubMembers.AsNoTracking().Where(m => m.TopicId == topicId).Select(m => m.UserId).ToListAsync(ct);

    private async Task<string> NameAsync(Guid id, CancellationToken ct) =>
        PersonName.Short(await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Name).FirstOrDefaultAsync(ct));

    private async Task NotifyAsync(List<Guid> to, string type, string title, string message, Guid? topicId, string priority, CancellationToken ct,
        string? idempotencyKey = null)
    {
        if (to.Count == 0) return;
        try
        {
            var url = topicId is { } id ? $"/hub/{id}" : "/hub";
            var target = new NotificationTarget(UserIds: to);
            if (idempotencyKey is null) await gateway.PublishInternalAsync("hub", type, title, message, target, priority, url, ct);
            else await gateway.PublishInternalAsync("hub", type, title, message, target, idempotencyKey, priority, url, ct);
        }
        catch (Exception ex) { log.LogWarning(ex, "Hub notification {Type} failed", type); }
        await PushBadgeAsync(to, ct);
    }

    private async Task PushChangedAsync(Guid topicId, CancellationToken ct)
    {
        try
        {
            var niks = await (from m in db.HubMembers.AsNoTracking()
                              join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                              where m.TopicId == topicId
                              select u.Nik).ToListAsync(ct);
            await realtime.ToUsersAsync(niks, HubEvents.Changed, topicId, ct);
        }
        catch (Exception ex) { log.LogDebug(ex, "Hub realtime push failed"); }
    }

    private async Task PushBadgeAsync(IEnumerable<Guid> users, CancellationToken ct)
    {
        try
        {
            foreach (var id in users.Distinct())
            {
                var nik = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.Nik).FirstOrDefaultAsync(ct);
                if (nik is null) continue;
                await realtime.ToUserAsync(nik, RealtimeEvents.BadgeChanged, new RealtimeArgs(HubEvents.BadgeKey, await WaitingCountAsync(id, ct)), ct);
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "Hub badge push failed"); }
    }

    private static string Preview(string body)
    {
        var one = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= 120 ? one : one[..117] + "…";
    }

    private static string Due(DateTime? due) => due is { } d ? $" · answer by {d:d MMM}" : "";

    private static string Explain(string? error) => error switch
    {
        "forbidden" => "you can't add tasks to that project.",
        "not_found" => "that project wasn't found.",
        _ => error ?? "unknown error.",
    };

    private static HubResult<T> NotFound<T>() => HubResult<T>.Fail("not_found", "Topic not found.");
    private static HubResult<T> Fail<T>(string code, string message) => HubResult<T>.Fail(code, message);
}
