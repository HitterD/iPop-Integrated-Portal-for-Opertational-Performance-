using IPop.Core.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

// Alias: the sibling folder IPop.Infrastructure.Mail creates a namespace that shadows the Mail entity type
// inside the IPop.Infrastructure tree. Pin the entity via an alias that wins for this file.
using Mail = IPop.Core.Entities.Mail;

namespace IPop.Infrastructure.Persistence;

public class IPopDbContext(DbContextOptions<IPopDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Division> Divisions => Set<Division>();
    public DbSet<OrgNode> OrgNodes => Set<OrgNode>();
    public DbSet<UserOrgNode> UserOrgNodes => Set<UserOrgNode>();
    public DbSet<DirGroup> DirGroups => Set<DirGroup>();
    public DbSet<UserDirGroup> UserDirGroups => Set<UserDirGroup>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<HrisSyncState> HrisSyncState => Set<HrisSyncState>();
    public DbSet<HrisSyncRun> HrisSyncRuns => Set<HrisSyncRun>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<SynologyCredential> SynologyCredentials => Set<SynologyCredential>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<UserFavoriteContact> UserFavoriteContacts => Set<UserFavoriteContact>();

    // Chat entities
    public DbSet<ChatRoom> ChatRooms => Set<ChatRoom>();
    public DbSet<ChatRoomMember> ChatRoomMembers => Set<ChatRoomMember>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageEdit> ChatMessageEdits => Set<ChatMessageEdit>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<ChatReaction> ChatReactions => Set<ChatReaction>();
    public DbSet<ChatReadReceipt> ChatReadReceipts => Set<ChatReadReceipt>();
    public DbSet<ChatPinnedMessage> ChatPinnedMessages => Set<ChatPinnedMessage>();
    public DbSet<ChatSavedMessage> ChatSavedMessages => Set<ChatSavedMessage>();
    public DbSet<ChatRoomUserSettings> ChatRoomUserSettings => Set<ChatRoomUserSettings>();
    public DbSet<ChatMention> ChatMentions => Set<ChatMention>();
    public DbSet<ChatMembershipAudit> ChatMembershipAudits => Set<ChatMembershipAudit>();

    // Notification Gateway entities
    public DbSet<NotificationGatewayClient> NotificationGatewayClients => Set<NotificationGatewayClient>();
    public DbSet<NotificationRequest> NotificationRequests => Set<NotificationRequest>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    // Kalender & Rapat
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingAttendee> MeetingAttendees => Set<MeetingAttendee>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<MeetingReminder> MeetingReminders => Set<MeetingReminder>();

    // Mail entities (use fully-qualified name to avoid shadowing by sibling namespace IPop.Infrastructure.Mail)
    public DbSet<IPop.Core.Entities.Mail> Mails => Set<IPop.Core.Entities.Mail>();
    public DbSet<MailRecipient> MailRecipients => Set<MailRecipient>();
    public DbSet<MailAttachment> MailAttachments => Set<MailAttachment>();
    // Surat Lanjutan (Fase 3)
    public DbSet<MailLabel> MailLabels => Set<MailLabel>();
    public DbSet<MailLabelAssignment> MailLabelAssignments => Set<MailLabelAssignment>();
    public DbSet<MailRule> MailRules => Set<MailRule>();
    public DbSet<MailTemplate> MailTemplates => Set<MailTemplate>();
    public DbSet<MailAutoReplySetting> MailAutoReplySettings => Set<MailAutoReplySetting>();

    // Projects entities
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectTaskAssignee> ProjectTaskAssignees => Set<ProjectTaskAssignee>();
    public DbSet<ProjectRoom> ProjectRooms => Set<ProjectRoom>();
    public DbSet<WorkCalendarEntry> WorkCalendarEntries => Set<WorkCalendarEntry>();
    // Diskusi Tugas (Fase 2)
    public DbSet<ProjectTaskComment> ProjectTaskComments => Set<ProjectTaskComment>();
    public DbSet<ProjectTaskActivity> ProjectTaskActivities => Set<ProjectTaskActivity>();
    public DbSet<ProjectTaskAttachment> ProjectTaskAttachments => Set<ProjectTaskAttachment>();
    public DbSet<ProjectTaskReminder> ProjectTaskReminders => Set<ProjectTaskReminder>();
    // Rencana & Laporan (Fase 4)
    public DbSet<ProjectTemplate> ProjectTemplates => Set<ProjectTemplate>();
    public DbSet<ProjectTemplateTask> ProjectTemplateTasks => Set<ProjectTemplateTask>();
    public DbSet<ProjectMilestone> ProjectMilestones => Set<ProjectMilestone>();

    // News & Memos entities (single table with type enum: news/announcement/memo)
    public DbSet<NewsPost> NewsPosts => Set<NewsPost>();
    public DbSet<NewsTarget> NewsTargets => Set<NewsTarget>();
    public DbSet<NewsAcknowledgement> NewsAcknowledgements => Set<NewsAcknowledgement>();
    public DbSet<NewsAudit> NewsAudits => Set<NewsAudit>();

    // Calls stub entities (signaling only — no media transport this wave)
    public DbSet<CallRoom> CallRooms => Set<CallRoom>();
    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();
    public DbSet<CallHistory> CallHistories => Set<CallHistory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Site>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<Division>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Nik).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasOne(x => x.Department).WithMany(d => d.Members).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Division).WithMany().HasForeignKey(x => x.DivisionId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<AuthSession>(e =>
        {
            e.HasIndex(x => x.RefreshTokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.Sessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<HrisSyncState>().HasData(new HrisSyncState { Id = 1 });
        b.Entity<HrisSyncRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StartedAt);
        });
        b.Entity<AuditLog>(e => e.HasIndex(x => x.CreatedAt));
        b.Entity<UserFavoriteContact>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ContactNik }).IsUnique();
            e.Property(x => x.ContactNik).HasMaxLength(32);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentRoleId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Permission>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<RolePermission>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.PermissionId, x.DepartmentCode });
            e.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Permission).WithMany(p => p.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserPermissionOverride>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PermissionId, x.DepartmentCode });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Permission).WithMany(p => p.UserOverrides).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SynologyCredential>(e =>
        {
            e.HasKey(x => new { x.UserId, x.Server });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrgNode>(e =>
        {
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<UserOrgNode>(e =>
        {
            e.HasKey(x => new { x.UserId, x.OrgNodeId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.OrgNode).WithMany(n => n.Members).HasForeignKey(x => x.OrgNodeId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DirGroup>(e =>
        {
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<UserDirGroup>(e =>
        {
            e.HasKey(x => new { x.UserId, x.DirGroupId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DirGroup).WithMany(g => g.Members).HasForeignKey(x => x.DirGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        // Chat entities
        b.Entity<ChatRoom>(e =>
        {
            e.HasIndex(x => x.LastMessageAt);
            e.HasIndex(x => x.Type);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.AvatarColor).HasMaxLength(9); // #RRGGBB / #RRGGBBAA
        });

        b.Entity<ChatRoomMember>(e =>
        {
            e.HasIndex(x => new { x.RoomId, x.UserId }).IsUnique().HasFilter("\"LeftAt\" IS NULL");
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Room).WithMany(r => r.Members).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.HasIndex(x => new { x.RoomId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.RoomId, x.CreatedAt });
            e.HasIndex(x => new { x.ThreadParentId, x.CreatedAt });
            e.Property(x => x.Content).HasMaxLength(4000);
            e.HasOne(x => x.Room).WithMany(r => r.Messages).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReplyTo).WithMany().HasForeignKey(x => x.ReplyToId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ThreadParent).WithMany().HasForeignKey(x => x.ThreadParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChatMessageEdit>(e =>
        {
            e.HasIndex(x => x.MessageId);
            e.HasOne(x => x.Message).WithMany(m => m.EditHistory).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatAttachment>(e =>
        {
            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => new { x.CreatedAt, x.MessageId });
            e.HasOne(x => x.Message).WithMany(m => m.Attachments).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatReaction>(e =>
        {
            e.HasKey(x => new { x.MessageId, x.UserId, x.Emoji });
            e.HasOne(x => x.Message).WithMany(m => m.Reactions).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatReadReceipt>(e =>
        {
            e.HasKey(x => new { x.MessageId, x.UserId });
            e.Property(x => x.DeliveredAt).IsRequired(false);
            e.Property(x => x.ReadAt).IsRequired(false);
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Message).WithMany(m => m.ReadReceipts).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatPinnedMessage>(e =>
        {
            e.HasIndex(x => new { x.RoomId, x.MessageId }).IsUnique();
            e.HasIndex(x => new { x.RoomId, x.PinnedAt });
            e.HasOne(x => x.Room).WithMany(r => r.PinnedMessages).HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PinnedByUser).WithMany().HasForeignKey(x => x.PinnedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChatSavedMessage>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.MessageId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.SavedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatRoomUserSettings>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoomId });
            e.Property(x => x.ReadReceiptsEnabled).HasDefaultValue(true);
            e.HasIndex(x => new { x.UserId, x.IsArchived });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMention>(e =>
        {
            e.HasIndex(x => new { x.MessageId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.NotifiedAt, x.NextAttemptAt }).HasFilter("\"NotifiedAt\" IS NULL");
            e.HasOne(x => x.Message).WithMany(m => m.Mentions).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMembershipAudit>(e =>
        {
            e.HasIndex(x => new { x.RoomId, x.CreatedAt, x.Id });
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.OldValue).HasMaxLength(200);
            e.Property(x => x.NewValue).HasMaxLength(200);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TargetUser).WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Restrict);
        });

        // Mail entities (use FQN to avoid 'Mail' namespace shadowing from sibling folder IPop.Infrastructure.Mail)
        b.Entity<IPop.Core.Entities.Mail>(e =>
        {
            e.HasIndex(x => new { x.SenderId, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.ScheduledAt });
            e.HasIndex(x => new { x.ThreadRootId, x.ThreadCount });
            e.HasIndex(x => new { x.InReplyToId, x.CreatedAt });
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.Importance).HasMaxLength(16).HasDefaultValue("normal");
            e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.InReplyTo).WithMany().HasForeignKey(x => x.InReplyToId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ThreadRoot).WithMany().HasForeignKey(x => x.ThreadRootId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MailRecipient>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Folder, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.Folder, x.IsPinned, x.CreatedAt });
            e.HasIndex(x => new { x.MailId, x.UserId }).IsUnique();
            e.HasIndex(x => x.DeliveredAt);
            e.Property(x => x.RecipientType).HasMaxLength(8);
            e.Property(x => x.Folder).HasMaxLength(16);
            e.HasOne(x => x.Mail).WithMany(m => m.Recipients).HasForeignKey(x => x.MailId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MailAttachment>(e =>
        {
            e.HasIndex(x => x.MailId);
            e.HasIndex(x => new { x.MailId, x.SortOrder });
            e.HasOne(x => x.Mail).WithMany(m => m.Attachments).HasForeignKey(x => x.MailId).OnDelete(DeleteBehavior.Cascade);
        });

        // Surat Lanjutan (Fase 3)
        b.Entity<MailLabel>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(9);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MailLabelAssignment>(e =>
        {
            e.HasIndex(x => new { x.LabelId, x.MailId }).IsUnique();
            e.HasIndex(x => x.MailId);
            e.HasOne(x => x.Label).WithMany(l => l.Assignments).HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Mail).WithMany().HasForeignKey(x => x.MailId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MailRule>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.IsEnabled });
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.SenderContains).HasMaxLength(200);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Label).WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MailTemplate>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MailAutoReplySetting>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Projects entities
        b.Entity<Project>(e =>
        {
            e.HasIndex(x => x.Year);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => new { x.Year, x.Status });
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Risk).HasMaxLength(2000);
            e.Property(x => x.Obstacle).HasMaxLength(2000);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        // Rencana & Laporan (Fase 4)
        b.Entity<ProjectTemplate>(e =>
        {
            e.HasIndex(x => new { x.IsActive, x.Name });
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasOne(x => x.SourceProject).WithMany().HasForeignKey(x => x.SourceProjectId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ProjectTemplateTask>(e =>
        {
            e.HasIndex(x => new { x.TemplateId, x.SortOrder });
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Priority).HasMaxLength(32);
            e.HasOne(x => x.Template).WithMany(t => t.Tasks).HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProjectMilestone>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.DueDate });
            e.Property(x => x.Title).HasMaxLength(300);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ProjectMember>(e =>
        {
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Role).HasMaxLength(32);
            e.HasOne(x => x.Project).WithMany(p => p.Members).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProjectTask>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.ParentTaskId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => new { x.ProjectId, x.Status, x.SortOrder });
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Priority).HasMaxLength(32);
            e.Property(x => x.Dependencies).HasMaxLength(2000);
            e.Property(x => x.RecurrenceRule).HasMaxLength(200);
            e.HasOne(x => x.Project).WithMany(p => p.Tasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParentTask).WithMany(t => t.Subtasks).HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectTaskComment>(e =>
        {
            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
            e.Property(x => x.Content).HasMaxLength(4000);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentCommentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectTaskActivity>(e =>
        {
            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.OldValue).HasMaxLength(500);
            e.Property(x => x.NewValue).HasMaxLength(500);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectTaskAttachment>(e =>
        {
            e.HasIndex(x => new { x.TaskId, x.CreatedAt });
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.SynologyServer).HasMaxLength(32);
            e.Property(x => x.SynologyPath).HasMaxLength(1024);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectTaskReminder>(e =>
        {
            e.HasIndex(x => new { x.TaskId, x.SentAt });
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProjectTaskAssignee>(e =>
        {
            e.HasKey(x => new { x.TaskId, x.UserId });            e.HasIndex(x => x.UserId);
            e.Property(x => x.Role).HasMaxLength(32);
            e.HasOne(x => x.Task).WithMany(t => t.Assignees).HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ipop-21 PERFECTION: ProjectRoom (chat room per project) + WorkCalendarEntry
        b.Entity<ProjectRoom>(e =>
        {
            e.HasKey(x => x.ProjectId);
            e.HasIndex(x => x.RoomId).IsUnique();
            e.HasOne(x => x.Project).WithOne().HasForeignKey<ProjectRoom>(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Room).WithOne().HasForeignKey<ProjectRoom>(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WorkCalendarEntry>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Date }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        // Notification Gateway entities
        b.Entity<NotificationGatewayClient>(e =>
        {
            e.HasIndex(x => x.ClientId).IsUnique();
            e.Property(x => x.ClientId).HasMaxLength(80);
            e.Property(x => x.SecretHash).HasMaxLength(200);
            e.Property(x => x.AllowedIps).HasMaxLength(400);
            e.Property(x => x.AllowedEventTypes).HasMaxLength(400);
        });

        b.Entity<NotificationRequest>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.Priority, x.Status, x.CreatedAt });
            e.Property(x => x.IdempotencyKey).HasMaxLength(120);
            e.Property(x => x.Priority).HasMaxLength(16);
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.TargetEmployeeNik).HasMaxLength(64);
            e.Property(x => x.TargetRole).HasMaxLength(64);
            e.HasOne(x => x.Client).WithMany(c => c.Requests).HasForeignKey(x => x.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NotificationDelivery>(e =>
        {
            e.HasIndex(x => new { x.RequestId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasOne(x => x.Request).WithMany(r => r.Deliveries).HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        // Preferensi notifikasi (Fase 4): satu baris per pengguna.
        b.Entity<NotificationPreference>(e =>
        {
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.QuietStart).HasMaxLength(5);
            e.Property(x => x.QuietEnd).HasMaxLength(5);
            e.Property(x => x.MutedTypes).HasMaxLength(500);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Kalender & Rapat
        b.Entity<Meeting>(e =>
        {
            e.HasIndex(x => x.StartAt);
            e.HasIndex(x => x.OrganizerId);
            e.HasIndex(x => x.RoomId);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.HasOne(x => x.Organizer).WithMany().HasForeignKey(x => x.OrganizerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<MeetingAttendee>(e =>
        {
            e.HasIndex(x => new { x.MeetingId, x.UserId }).IsUnique();
            e.Property(x => x.RsvpStatus).HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasOne(x => x.Meeting).WithMany(m => m.Attendees).HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CalendarEvent>(e =>
        {
            e.HasIndex(x => x.StartAt);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.RoomId);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Type).HasMaxLength(16);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Meeting).WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<MeetingReminder>(e =>
        {
            e.HasIndex(x => new { x.SendAt, x.SentAt });
            e.HasOne(x => x.Meeting).WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsPost>(e =>
        {
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Type);
            e.HasIndex(x => x.IsPinned);
            e.HasIndex(x => new { x.Status, x.PublishAt });
            e.HasIndex(x => new { x.Type, x.Status, x.CreatedAt });
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Body).HasMaxLength(20000);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.RejectionReason).HasMaxLength(2000);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Approver).WithMany().HasForeignKey(x => x.ApproverId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<NewsTarget>(e =>
        {
            e.HasIndex(x => x.NewsPostId);
            e.HasIndex(x => new { x.Kind, x.Value });
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Value).HasMaxLength(200);
            e.HasOne(x => x.NewsPost).WithMany(p => p.Targets).HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsAcknowledgement>(e =>
        {
            e.HasKey(x => new { x.NewsPostId, x.UserId });
            e.HasOne(x => x.NewsPost).WithMany(p => p.Acknowledgements).HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<NewsAudit>(e =>
        {
            e.HasIndex(x => new { x.NewsPostId, x.CreatedAt, x.Id });
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.HasOne(x => x.NewsPost).WithMany(p => p.Audits).HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        });

        // Calls stub — pure signaling; no media transport this wave.
        b.Entity<CallRoom>(e =>
        {
            e.HasIndex(x => new { x.CreatorId, x.Status });
            e.HasIndex(x => x.Status);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Quality).HasMaxLength(8);
            e.HasOne(x => x.Creator).WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CallParticipant>(e =>
        {
            e.HasIndex(x => new { x.CallId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne(x => x.Call).WithMany(c => c.Participants).HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CallHistory>(e =>
        {
            e.HasIndex(x => new { x.PerformerId, x.StartedAt });
            e.Property(x => x.Direction).HasMaxLength(8);
            e.Property(x => x.Result).HasMaxLength(16);
            e.HasOne(x => x.Call).WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Performer).WithMany().HasForeignKey(x => x.PerformerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
