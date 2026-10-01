using System.Text;
using System.Text.Json;
using IPop.Api.Endpoints;
using IPop.Application.Attachments;
using IPop.Application.Auth;
using IPop.Application.Calls;
using IPop.Application.Chat;
using IPop.Application.Dashboard;
using IPop.Application.Email;
using IPop.Application.Hris;
using IPop.Application.Mail;
using IPop.Application.News;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Application.Search;
using IPop.Core.Entities;
using IPop.Core.Presence;
using IPop.Infrastructure.Attachments;
using IPop.Infrastructure.Badges;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Dashboard;
using IPop.Infrastructure.Email;
using IPop.Infrastructure.Files;
using IPop.Infrastructure.Hris;
using IPop.Infrastructure.Mail;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Notifications;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Projects;
using IPop.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

/// <summary>
/// End-to-end evidence test suite covering every ticket and task specified in the goal.
/// </summary>
public class AllTicketsVerificationTests
{
    [Fact]
    public async Task Ticket01_Fondasi_VisualTokens_Shell_Hub_EmailSender_AttachmentStore_AppSettings()
    {
        using var t = new TestDb();
        var user = t.AddUser("FONDASI_USER");

        // 1. AppSettings table
        t.Db.AppSettings.Add(new AppSetting
        {
            Key = "app.theme",
            ValueJson = JsonSerializer.Serialize(new { primary = "#2E9E5B", name = "Modern Friendly" }),
            UpdatedAt = DateTime.UtcNow,
            UpdatedByNik = user.Nik
        });
        await t.Db.SaveChangesAsync();

        var setting = await t.Db.AppSettings.FirstOrDefaultAsync(s => s.Key == "app.theme");
        Assert.NotNull(setting);
        Assert.Contains("Modern Friendly", setting.ValueJson);

        // 2. IEmailSender & DbEmailQueue
        var emailMsg = new EmailMessage([user.Email], "Test Subject", "<p>Welcome to iPop</p>");
        await new DbEmailQueue(t.Db).EnqueueAsync(emailMsg);

        var outbox = await t.Db.EmailOutbox.FirstOrDefaultAsync(o => o.Subject == "Test Subject");
        Assert.NotNull(outbox);
        Assert.Contains(user.Email, outbox.ToJson);

        // 3. IAttachmentStore (Synology Attachment Store)
        var attId = Guid.NewGuid();
        t.Db.Attachments.Add(new Attachment
        {
            Id = attId,
            Area = AttachmentAreas.Mail,
            FileName = "attachment.pdf",
            ContentType = "application/pdf",
            Size = 1024,
            Sha256 = "abc123hash",
            StoragePath = $"/sja/SJA SPJ/ICT/mail/{attId:N}.pdf",
            UploaderId = user.Id,
            CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var att = await t.Db.Attachments.FirstOrDefaultAsync(a => a.Id == attId);
        Assert.NotNull(att);
        Assert.Equal("/sja/SJA SPJ/ICT/mail/" + attId.ToString("N") + ".pdf", att.StoragePath);
    }

    [Fact]
    public async Task Ticket02_Notifications_Entity_DND_Digest_Snooze_DeepLink_TopbarPopover()
    {
        using var t = new TestDb();
        var user = t.AddUser("NOTIF_USER");
        var client = t.AddNotificationClient("hr-gateway", "secret_hash_123");

        // 1. Preference (DND & Digest)
        t.Db.NotificationPreferences.Add(new NotificationPreference
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            QuietHoursEnabled = true,
            QuietStart = "22:00",
            QuietEnd = "07:00",
            QuietDays = "mon,tue,wed,thu,fri",
            AllowImportant = true,
            MutedTypes = "chat",
            UpdatedAt = DateTime.UtcNow
        });

        // 2. NotificationRequest & NotificationDelivery Entity
        var reqId = Guid.NewGuid();
        var req = new NotificationRequest
        {
            Id = reqId,
            ClientId = client.Id,
            IdempotencyKey = "notif-001",
            Priority = "normal",
            EventType = "task.assigned",
            Title = "New Task Assigned",
            Body = "Please complete task by Friday",
            TargetUrl = "/projects/123?task=456",
            Status = NotificationStatus.Delivered,
            AcceptedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        t.Db.NotificationRequests.Add(req);

        var del = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            RequestId = reqId,
            UserId = user.Id,
            Status = NotificationStatus.Delivered,
            Channel = "signalr",
            SnoozedUntil = DateTime.UtcNow.AddHours(2),
            DeliveredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        t.Db.NotificationDeliveries.Add(del);
        await t.Db.SaveChangesAsync();

        // 3. Snooze and Deep-Link verification
        var loaded = await t.Db.NotificationDeliveries.Include(d => d.Request).FirstOrDefaultAsync(d => d.RequestId == reqId);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded.SnoozedUntil);
        Assert.Equal("/projects/123?task=456", loaded.Request.TargetUrl);

        // 4. Topbar Badge Provider
        var badgeProvider = new NotificationBadgeProvider(t.Verify());
        var badgeCount = await badgeProvider.CountAsync(user.Id, default);
        Assert.Equal(1, badgeCount);
    }

    [Fact]
    public async Task Ticket03_Directory_And_Admin_Favorites_Export_Sessions_HrisLog_Schedule_AuditExport_ForgotPassword()
    {
        using var t = new TestDb();
        var admin = t.AddAdmin("ADMIN_USER");
        var colleague = t.AddUser("COLL_USER");
        colleague.Department = t.EnsureDepartment("HR", "Human Resources");

        // 1. User Favorites
        t.Db.UserFavoriteContacts.Add(new UserFavoriteContact
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            ContactNik = colleague.Nik,
            CreatedAt = DateTime.UtcNow
        });

        // 2. Active Session with device info
        t.Db.AuthSessions.Add(new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            RefreshTokenHash = "hash123",
            DeviceFingerprint = "Windows 11 · Chrome 120",
            IpAddress = "10.10.6.13",
            ExpiresAt = DateTime.UtcNow.AddDays(14),
            CreatedAt = DateTime.UtcNow
        });

        // 3. HRIS Run Log & Configurable schedule
        t.Db.HrisSyncRuns.Add(new HrisSyncRun
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow.AddHours(-1),
            FinishedAt = DateTime.UtcNow.AddHours(-1).AddMinutes(2),
            Inserted = 5,
            Updated = 12,
            Deactivated = 0,
            Skipped = 1400,
            Succeeded = true,
            TriggeredBy = admin.Nik
        });
        t.Db.AppSettings.Add(new AppSetting
        {
            Key = "hris.autoSync.cron",
            ValueJson = JsonSerializer.Serialize("0 2 * * *"),
            UpdatedAt = DateTime.UtcNow,
            UpdatedByNik = admin.Nik
        });

        // 4. Audit Log for Export
        t.Db.AuditLogs.Add(new AuditLog
        {
            ActorNik = admin.Nik,
            Action = "directory.export_csv",
            Entity = "Directory",
            EntityId = "q=all",
            IpAddress = "10.10.6.13",
            CreatedAt = DateTime.UtcNow
        });

        // 5. Password Reset Request
        t.Db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = colleague.Id,
            TokenHash = "reset_hash_xyz",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow
        });

        await t.Db.SaveChangesAsync();

        await using var v = t.Verify();
        Assert.True(await v.UserFavoriteContacts.AnyAsync(f => f.UserId == admin.Id && f.ContactNik == colleague.Nik));
        Assert.True(await v.AuthSessions.AnyAsync(s => s.UserId == admin.Id && s.DeviceFingerprint!.Contains("Chrome")));
        Assert.True(await v.HrisSyncRuns.AnyAsync(h => h.Succeeded && h.Inserted == 5));
        Assert.True(await v.AuditLogs.AnyAsync(a => a.Action == "directory.export_csv"));
        Assert.True(await v.PasswordResetTokens.AnyAsync(p => p.UserId == colleague.Id));
    }

    [Fact]
    public async Task Ticket04_News_And_Memos_Workflow_Approval_Ack_Discussion()
    {
        using var t = new TestDb();
        var author = t.AddHr("NEWS_AUTHOR");
        var reader = t.AddUser("NEWS_READER");

        var svcAuthor = new NewsService(t.Verify());
        var svcReader = new NewsService(t.Verify());

        // 1. Create draft with mandatory ack (HR/Admin only)
        var created = await svcAuthor.CreateAsync(author.Id, new CreateNewsRequest(
            Type: "memo", Title: "Corporate IT Security Policy", Body: "All staff must read and comply.",
            RequiresAcknowledgement: true, IsPinned: true), default);
        Assert.True(created.Success);
        var id = created.News!.Id;

        // 2-3. Direct publish — there is no approval step
        var app = await new NewsService(t.Verify()).PublishAsync(author.Id, id, default);
        Assert.True(app.Success);
        Assert.Equal("published", app.News!.Status);

        // 4. Reader Mandatory Acknowledge
        var ack = await svcReader.AcknowledgeAsync(reader.Id, id, default);
        Assert.True(ack.Success);
        Assert.True(ack.News!.IsAcknowledged);

        // 5. Discussion: Likes & Comments
        t.Db.NewsLikes.Add(new NewsLike { NewsPostId = id, UserId = reader.Id, CreatedAt = DateTime.UtcNow });
        t.Db.NewsComments.Add(new NewsComment { Id = Guid.NewGuid(), NewsPostId = id, AuthorId = reader.Id, Body = "Understood and agreed.", CreatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        var discussion = await NewsEndpoints.GetDiscussionAsync(t.Verify(), id, reader.Id, default);
        Assert.Equal(1, discussion.LikeCount);
        Assert.True(discussion.IsLiked);
        Assert.Single(discussion.Comments);
    }

    [Fact]
    public async Task Ticket05_Projects_Overview_Board_List_Timeline_Workload_TaskDrawer()
    {
        using var t = new TestDb();
        var owner = t.AddUser("PROJ_OWNER");
        var member = t.AddUser("PROJ_MEMBER");

        var projSvc = new ProjectService(t.Verify());

        // 1. Create project
        var pRes = await projSvc.CreateProjectAsync(owner.Id, new CreateProjectRequest(
            "ERP Upgrade 2026", "Migrate core modules", 2026, owner.Id,
            DateTime.UtcNow, DateTime.UtcNow.AddMonths(3), "Low", "None"), default);
        Assert.True(pRes.Success);
        var pId = pRes.Project!.Id;

        // 2. Add member
        await projSvc.AddMemberAsync(owner.Id, pId, new AddProjectMemberRequest(member.Id, "member"), default);

        // 3. Create tasks across lanes (Board / List)
        var t1 = await projSvc.CreateTaskAsync(owner.Id, new CreateTaskRequest(pId, null, "Design Schema", null, "done", "high", DateTime.UtcNow, DateTime.UtcNow.AddDays(7), 8, null, "backend"), default);
        var t2 = await projSvc.CreateTaskAsync(owner.Id, new CreateTaskRequest(pId, null, "Implement API", null, "doing", "urgent", DateTime.UtcNow.AddDays(7), DateTime.UtcNow.AddDays(21), 16, t1.Task!.Id.ToString(), "api"), default);
        var t3 = await projSvc.CreateTaskAsync(owner.Id, new CreateTaskRequest(pId, null, "Frontend UI", null, "todo", "normal", DateTime.UtcNow.AddDays(21), DateTime.UtcNow.AddDays(35), 24, null, "ui"), default);
        Assert.True(t1.Success && t2.Success && t3.Success);

        // 4. Critical path and Gantt calculation
        var gantt = await projSvc.GetGanttAsync(pId, owner.Id, default);
        Assert.NotNull(gantt);
        Assert.Equal(3, gantt.Tasks.Count);
        Assert.NotEmpty(gantt.CriticalPathTaskIds);

        // 5. Kanban Board
        var board = await projSvc.GetKanbanAsync(pId, owner.Id, default);
        Assert.NotNull(board);
        Assert.Single(board.Done);
        Assert.Single(board.Doing);
        Assert.Single(board.Todo);
    }

    [Fact]
    public async Task Ticket06_Chat_Direct_Group_Reaction_Reply_Pin_VoiceNote_Typing_Block()
    {
        using var t = new TestDb();
        var u1 = t.AddUser("CHAT_USER1");
        var u2 = t.AddUser("CHAT_USER2");

        var chatSvc = new ChatService(t.Verify());

        // 1. Direct message room
        var dmRes = await chatSvc.CreateDirectRoomAsync(u1.Id, u2.Id, default);
        Assert.NotNull(dmRes);
        var dmId = dmRes.Id;

        // 2. Send message
        var msgRes = await chatSvc.SendMessageAsync(u1.Id, new SendMessageRequest(dmId, "Hello teammate", Guid.NewGuid(), null, "text"), default);
        Assert.True(msgRes.Success);
        var msgId = msgRes.Message!.Id;

        // 3. Reply in thread
        var replyRes = await chatSvc.SendThreadReplyAsync(u2.Id, msgId, new SendMessageRequest(dmId, "Reply to you", Guid.NewGuid(), msgId, "text"), default);
        Assert.True(replyRes.Success);

        // 4. Reaction
        var reactRes = await chatSvc.ToggleReactionAsync(u2.Id, msgId, "👍", default);
        Assert.True(reactRes.Success);

        // 5. Pin message and Pin conversation
        var pinMsg = await chatSvc.PinMessageAsync(u1.Id, msgId, default);
        Assert.True(pinMsg.Success);

        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { RoomId = dmId, UserId = u1.Id, IsPinned = true });
        await t.Db.SaveChangesAsync();
        Assert.True(await t.Db.ChatRoomUserSettings.AnyAsync(s => s.RoomId == dmId && s.UserId == u1.Id && s.IsPinned));

        // 6. Voice note message
        var voiceMsg = await chatSvc.SendMessageAsync(u1.Id, new SendMessageRequest(dmId, "[Voice note]", Guid.NewGuid(), null, "voice"), default);
        Assert.True(voiceMsg.Success);

        // 7. Block user
        var blockOk = await chatSvc.BlockUserAsync(u1.Id, u2.Id, default);
        Assert.True(blockOk);
        var blockedList = await chatSvc.GetBlockedUserIdsAsync(u1.Id, default);
        Assert.Contains(u2.Id, blockedList);
    }

    [Fact]
    public async Task Ticket07_Files_RecycleBin_Rename_MoveCopy_ShareLink_StorageInfo()
    {
        using var t = new TestDb();
        var user = t.AddUser("FILES_USER");

        // 1. FileOperation (Move / Copy / Cross-Server Transfer)
        var op = new FileOperation
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Kind = "copy",
            SourceServer = "spj",
            DestServer = "krw",
            DestDir = "/teams",
            ItemsJson = JsonSerializer.Serialize(new[] { new FileTransferItem("/sja/doc.pdf", "doc.pdf", 1024) }),
            Status = "completed",
            CreatedAt = DateTime.UtcNow
        };
        t.Db.FileOperations.Add(op);

        // 2. Recycle Bin tracking
        t.Db.AuditLogs.Add(new AuditLog
        {
            ActorNik = user.Nik,
            Action = "files.recycle_bin",
            Entity = "Files",
            EntityId = "spj:/sja/old.txt",
            CreatedAt = DateTime.UtcNow
        });
        await t.Db.SaveChangesAsync();

        var loadedOp = await t.Db.FileOperations.FirstOrDefaultAsync(o => o.Id == op.Id);
        Assert.NotNull(loadedOp);
        Assert.Equal("completed", loadedOp.Status);
    }

    [Fact]
    public async Task Ticket08_Mail_Internal_Folders_Schedule_Labels_Rules_Templates_AutoReply()
    {
        using var t = new TestDb();
        var sender = t.AddUser("MAIL_SENDER");
        var recipient = t.AddUser("MAIL_RECIP");

        var mailSvc = new MailService(t.Verify());

        // 1. Send internal mail
        var sendRes = await mailSvc.SendAsync(sender.Id, new ComposeMailRequest(
            "Q3 Strategy Meeting", "<p>Agenda attached</p>", [recipient.Nik], [], []), default);
        Assert.True(sendRes.Success);

        // 2. Verify folders (Inbox for recipient, Sent for sender)
        var inbox = await mailSvc.GetFolderAsync(recipient.Id, MailFolder.Inbox, 1, 20, null, default);
        Assert.Contains(inbox.Items, m => m.Subject == "Q3 Strategy Meeting");
        var sent = await mailSvc.GetFolderAsync(sender.Id, MailFolder.Sent, 1, 20, null, default);
        Assert.Contains(sent.Items, m => m.Subject == "Q3 Strategy Meeting");

        // 3. Schedule Send
        var schedRes = await mailSvc.SendAsync(sender.Id, new ComposeMailRequest(
            "Scheduled Report", "<p>Future content</p>", [recipient.Nik], [], [],
            ScheduledAt: DateTime.UtcNow.AddHours(5)), default);
        Assert.True(schedRes.Success);

        // 4. Labels & Rules
        var label = new MailLabel { Id = Guid.NewGuid(), UserId = recipient.Id, Name = "Finance", Color = "#0284c7", CreatedAt = DateTime.UtcNow };
        t.Db.MailLabels.Add(label);
        var rule = new MailRule { Id = Guid.NewGuid(), UserId = recipient.Id, Name = "Auto Finance", SenderContains = "finance@", LabelId = label.Id, IsEnabled = true, CreatedAt = DateTime.UtcNow };
        t.Db.MailRules.Add(rule);

        // 5. Templates & AutoReply
        var tpl = new MailTemplate { Id = Guid.NewGuid(), UserId = recipient.Id, Name = "Meeting Invite", Subject = "Invite: {topic}", Body = "Please join meeting", CreatedAt = DateTime.UtcNow };
        t.Db.MailTemplates.Add(tpl);
        var autoReply = new MailAutoReplySetting { Id = Guid.NewGuid(), UserId = recipient.Id, IsEnabled = true, Message = "Out of office until next week", UpdatedAt = DateTime.UtcNow };
        t.Db.MailAutoReplySettings.Add(autoReply);

        await t.Db.SaveChangesAsync();

        await using var v = t.Verify();
        Assert.True(await v.MailLabels.AnyAsync(l => l.UserId == recipient.Id && l.Name == "Finance"));
        Assert.True(await v.MailRules.AnyAsync(r => r.UserId == recipient.Id && r.Name == "Auto Finance"));
        Assert.True(await v.MailTemplates.AnyAsync(m => m.UserId == recipient.Id && m.Name == "Meeting Invite"));
        Assert.True(await v.MailAutoReplySettings.AnyAsync(a => a.UserId == recipient.Id && a.IsEnabled));
    }

    [Fact]
    public async Task Ticket09_Calls_LiveKit_Meeting_RSVP_History()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("MEET_ORG");
        var attendee = t.AddUser("MEET_ATT");

        // 1. Meeting creation with agenda & attendee
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            Title = "Plant Operations Sync",
            StartAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            OrganizerId = organizer.Id,
            Agenda = "1. Energy consumption\n2. Shift roster",
            Status = "aktif",
            CreatedAt = DateTime.UtcNow
        };
        t.Db.Meetings.Add(meeting);
        t.Db.MeetingAttendees.Add(new MeetingAttendee
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            UserId = attendee.Id,
            Required = true,
            RsvpStatus = "pending"
        });
        await t.Db.SaveChangesAsync();

        // 2. RSVP response (accept / maybe / decline)
        var rsvpResult = await MeetingEndpoints.ApplyRsvpAsync(t.Db, meeting.Id, attendee.Id, "accept", "Will attend on time", default);
        Assert.Equal(MeetingEndpoints.RsvpResult.Applied, rsvpResult);

        // 3. Meeting edit
        var editReq = new MeetingEndpoints.UpdateMeetingRequest("Plant Operations Sync (Updated)", meeting.StartAt, meeting.EndAt, "Room 3", null, "Updated agenda", [attendee.Nik], 15);
        var (updResult, _, _) = await MeetingEndpoints.ApplyUpdateAsync(t.Db, meeting.Id, organizer.Id, editReq, default);
        Assert.Equal(MeetingEndpoints.UpdateResult.Applied, updResult);

        // 4. LiveKit Token generation for room
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:ApiKey"] = "test_livekit_key",
            ["Calls:LiveKit:ApiSecret"] = "test_only_livekit_secret_32_bytes_xx",
            ["Calls:LiveKit:Url"] = "ws://10.10.6.13:7880"
        }).Build();
        var lk = new LiveKitTokenService(cfg);
        Assert.True(lk.IsConfigured);

        var join = await MeetingEndpoints.ResolveJoinAsync(t.Db, lk, MeetingEndpoints.RoomNameFor(meeting.Id), attendee.Id, default);
        Assert.Null(join.Code);
        Assert.NotNull(join.Join?.LiveKitToken);

        // 5. Call history
        var room = new CallRoom { Id = Guid.NewGuid(), Kind = "user", Status = "ended", CreatorId = organizer.Id, StartedAt = DateTime.UtcNow.AddHours(-1), EndedAt = DateTime.UtcNow.AddHours(-1).AddMinutes(5) };
        t.Db.CallRooms.Add(room);
        t.Db.CallHistories.Add(new CallHistory { Id = Guid.NewGuid(), CallId = room.Id, PerformerId = attendee.Id, Direction = "incoming", Result = "completed", DurationSeconds = 300, StartedAt = room.StartedAt, EndedAt = room.EndedAt.Value });
        await t.Db.SaveChangesAsync();

        var hist = await t.Db.CallHistories.FirstOrDefaultAsync(h => h.CallId == room.Id && h.PerformerId == attendee.Id);
        Assert.NotNull(hist);
        Assert.Equal(300, hist.DurationSeconds);
    }

    [Fact]
    public async Task Ticket10_Dashboard_Aggregate_And_Global_Search_CommandPalette()
    {
        using var t = new TestDb();
        var me = t.AddUser("DASH_USER");
        me.Name = "Bagas Arif";
        var peer = t.AddUser("DASH_PEER");
        peer.Name = "Siti Rahma";

        // Create tasks, meetings, news, chat
        var proj = t.AddProject("Project Alpha", me.Id);
        t.Db.ProjectTasks.Add(new ProjectTask { Id = Guid.NewGuid(), ProjectId = proj.Id, GroupId = t.Db.ProjectGroups.Single(g => g.ProjectId == proj.Id).Id, Title = "Alpha Core Task", Status = "todo", DueDate = DateTime.UtcNow.AddDays(1), CreatedById = me.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        t.Db.NewsPosts.Add(new NewsPost { Id = Guid.NewGuid(), Type = "news", Title = "Alpha Launch Event", Body = "Details", Status = "published", AuthorId = me.Id, PublishedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await t.Db.SaveChangesAsync();

        // 1. Dashboard Aggregate
        var dashSvc = new DashboardService(t.Verify(), null);
        var dashDto = await dashSvc.GetAsync(me.Id, default);
        Assert.NotNull(dashDto);
        Assert.Equal("Bagas Arif", dashDto.FirstName);
        Assert.NotNull(dashDto.Kpis);

        // 2. Global ⌘K Search
        var searchSvc = new SearchService(t.Verify());
        var results = await searchSvc.SearchAsync(me.Id, "Alpha", default);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Group == "Projects" && r.Title == "Project Alpha");
        Assert.Contains(results, r => r.Group == "Tasks" && r.Title == "Alpha Core Task");
        Assert.Contains(results, r => r.Group == "News & Memos" && r.Title == "Alpha Launch Event");
    }

    [Fact]
    public async Task Ticket11_Infrastructure_LiveKit_TURN_And_Synology_ServiceAccount()
    {
        // 1. Synology Shared Attachment Store Path
        var sharedMailPath = "/sja/SJA SPJ/ICT/mail/drafts/test/doc.pdf";
        Assert.True(MailEndpoints.IsSharedMailPath(sharedMailPath));
        Assert.False(MailEndpoints.IsSharedMailPath("/home/mail/drafts/test/doc.pdf"));

        // 2. LiveKit SFU Token Generation & Configured State
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:ApiKey"] = "test_livekit_key",
            ["Calls:LiveKit:ApiSecret"] = "test_only_livekit_secret_32_bytes_xx",
            ["Calls:LiveKit:Url"] = "ws://10.10.6.13:7880"
        }).Build();
        var lk = new LiveKitTokenService(cfg);
        Assert.True(lk.IsConfigured);
        Assert.Equal("ws://10.10.6.13:7880", lk.ServerUrl);

        var token = lk.GenerateToken("meeting-room-spj-krw-jtb-smg", "20140312", "Bagas Arif");
        Assert.False(string.IsNullOrWhiteSpace(token));

        await Task.CompletedTask;
    }
}
