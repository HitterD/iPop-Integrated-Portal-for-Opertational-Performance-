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
    public DbSet<OrgPosition> OrgPositions => Set<OrgPosition>();
    public DbSet<OrgPositionHolder> OrgPositionHolders => Set<OrgPositionHolder>();
    public DbSet<OrgChartPublication> OrgChartPublications => Set<OrgChartPublication>();
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
    public DbSet<FileOperation> FileOperations => Set<FileOperation>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<UserFavoriteContact> UserFavoriteContacts => Set<UserFavoriteContact>();
    public DbSet<UserTutorialState> UserTutorialStates => Set<UserTutorialState>();

    // Chat entities
    public DbSet<ChatRoom> ChatRooms => Set<ChatRoom>();
    public DbSet<ChatRoomMember> ChatRoomMembers => Set<ChatRoomMember>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageEdit> ChatMessageEdits => Set<ChatMessageEdit>();
    public DbSet<UserPhoto> UserPhotos => Set<UserPhoto>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<ChatReaction> ChatReactions => Set<ChatReaction>();
    public DbSet<ChatReadReceipt> ChatReadReceipts => Set<ChatReadReceipt>();
    public DbSet<ChatPinnedMessage> ChatPinnedMessages => Set<ChatPinnedMessage>();
    public DbSet<ChatSavedMessage> ChatSavedMessages => Set<ChatSavedMessage>();
    public DbSet<ChatRoomUserSettings> ChatRoomUserSettings => Set<ChatRoomUserSettings>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<ChatMention> ChatMentions => Set<ChatMention>();
    public DbSet<ChatMembershipAudit> ChatMembershipAudits => Set<ChatMembershipAudit>();
    public DbSet<ChatSticker> ChatStickers => Set<ChatSticker>();
    public DbSet<UserSticker> UserStickers => Set<UserSticker>();
    public DbSet<StickerUsage> StickerUsages => Set<StickerUsage>();

    // Notification Gateway entities
    public DbSet<NotificationGatewayClient> NotificationGatewayClients => Set<NotificationGatewayClient>();
    public DbSet<NotificationRequest> NotificationRequests => Set<NotificationRequest>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<PushJob> PushJobs => Set<PushJob>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();
    // Kalender & Rapat
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingAttendee> MeetingAttendees => Set<MeetingAttendee>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<MeetingReminder> MeetingReminders => Set<MeetingReminder>();
    public DbSet<CalendarSettings> CalendarSettings => Set<CalendarSettings>();
    public DbSet<CalendarSubscription> CalendarSubscriptions => Set<CalendarSubscription>();
    public DbSet<CalendarDelegate> CalendarDelegates => Set<CalendarDelegate>();

    // Mail entities (use fully-qualified name to avoid shadowing by sibling namespace IPop.Infrastructure.Mail)
    public DbSet<IPop.Core.Entities.Mail> Mails => Set<IPop.Core.Entities.Mail>();
    public DbSet<MailRecipient> MailRecipients => Set<MailRecipient>();
    public DbSet<MailGroup> MailGroups => Set<MailGroup>();
    public DbSet<MailGroupMember> MailGroupMembers => Set<MailGroupMember>();
    public DbSet<MailNotifyJob> MailNotifyJobs => Set<MailNotifyJob>();
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
    public DbSet<ProjectGroup> ProjectGroups => Set<ProjectGroup>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectTaskAssignee> ProjectTaskAssignees => Set<ProjectTaskAssignee>();
    public DbSet<ProjectField> ProjectFields => Set<ProjectField>();
    public DbSet<ProjectTaskFieldValue> ProjectTaskFieldValues => Set<ProjectTaskFieldValue>();
    public DbSet<ProjectTaskDependency> ProjectTaskDependencies => Set<ProjectTaskDependency>();
    public DbSet<ProjectSavedView> ProjectSavedViews => Set<ProjectSavedView>();
    public DbSet<TaskTimeEntry> TaskTimeEntries => Set<TaskTimeEntry>();
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
    // Notes
    public DbSet<NotePage> NotePages => Set<NotePage>();
    public DbSet<NoteUpdate> NoteUpdates => Set<NoteUpdate>();
    public DbSet<NoteShare> NoteShares => Set<NoteShare>();
    public DbSet<NoteFavorite> NoteFavorites => Set<NoteFavorite>();
    public DbSet<NoteVisit> NoteVisits => Set<NoteVisit>();
    public DbSet<NoteVersion> NoteVersions => Set<NoteVersion>();
    public DbSet<NoteThread> NoteThreads => Set<NoteThread>();
    public DbSet<NoteComment> NoteComments => Set<NoteComment>();
    public DbSet<NoteAttachment> NoteAttachments => Set<NoteAttachment>();

    // News & Memos entities (single table with type enum: news/announcement/memo)
    public DbSet<NewsPost> NewsPosts => Set<NewsPost>();
    public DbSet<NewsTarget> NewsTargets => Set<NewsTarget>();
    public DbSet<NewsAcknowledgement> NewsAcknowledgements => Set<NewsAcknowledgement>();
    public DbSet<NewsAudit> NewsAudits => Set<NewsAudit>();
    public DbSet<NewsComment> NewsComments => Set<NewsComment>();
    public DbSet<NewsLike> NewsLikes => Set<NewsLike>();
    public DbSet<NewsRead> NewsReads => Set<NewsRead>();
    public DbSet<NewsBookmark> NewsBookmarks => Set<NewsBookmark>();
    public DbSet<NewsAttachment> NewsAttachments => Set<NewsAttachment>();
    public DbSet<NewsVideo> NewsVideos => Set<NewsVideo>();
    public DbSet<NewsVideoView> NewsVideoViews => Set<NewsVideoView>();
    // Composer (private drafts + media references)
    public DbSet<NewsComposerDraft> NewsComposerDrafts => Set<NewsComposerDraft>();
    public DbSet<NewsComposerMedia> NewsComposerMedia => Set<NewsComposerMedia>();
    public DbSet<NewsComposerDraftMedia> NewsComposerDraftMedia => Set<NewsComposerDraftMedia>();
    public DbSet<NewsComposerDraftAttachment> NewsComposerDraftAttachments => Set<NewsComposerDraftAttachment>();
    public DbSet<NewsPostMedia> NewsPostMedia => Set<NewsPostMedia>();

    // Calls stub entities (signaling only — no media transport this wave)
    public DbSet<CallRoom> CallRooms => Set<CallRoom>();
    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();
    public DbSet<CallHistory> CallHistories => Set<CallHistory>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<SearchHistory> SearchHistories => Set<SearchHistory>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<TicketUserState> TicketUserStates => Set<TicketUserState>();
    public DbSet<TicketRefCounter> TicketRefCounters => Set<TicketRefCounter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Department>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Head).WithMany().HasForeignKey(x => x.HeadUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Site>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<Division>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Head).WithMany().HasForeignKey(x => x.HeadUserId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Nik).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasOne(x => x.Department).WithMany(d => d.Members).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Division).WithMany().HasForeignKey(x => x.DivisionId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<UserPhoto>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.Property(x => x.ContentType).HasMaxLength(32);
            e.Property(x => x.LargeContentType).HasMaxLength(32);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuthSession>(e =>
        {
            e.HasIndex(x => x.RefreshTokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasOne(x => x.User).WithMany(u => u.Sessions).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<HrisSyncState>().HasData(new HrisSyncState { Id = 1 });
        b.Entity<HrisSyncRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StartedAt);
            e.Property(x => x.TriggeredBy).HasMaxLength(32);
        });
        b.Entity<AuditLog>(e => e.HasIndex(x => x.CreatedAt));
        b.Entity<UserFavoriteContact>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ContactNik }).IsUnique();
            e.Property(x => x.ContactNik).HasMaxLength(32);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserTutorialState>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ModuleKey }).IsUnique();
            e.Property(x => x.ModuleKey).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16);
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
        b.Entity<FileOperation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(8);
            e.Property(x => x.SourceServer).HasMaxLength(16);
            e.Property(x => x.DestServer).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(24);
            e.Property(x => x.DestDir).HasMaxLength(1024);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
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
        b.Entity<OrgPosition>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            // NoAction (checked at statement end) so Publish can replace the whole chart in one delete.
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Site).WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.ParentId);
        });
        b.Entity<OrgPositionHolder>(e =>
        {
            e.HasKey(x => new { x.PositionId, x.UserId });
            e.HasOne(x => x.Position).WithMany(p => p.Holders).HasForeignKey(x => x.PositionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<OrgChartPublication>(e => e.Property(x => x.PublishedByNik).HasMaxLength(50));
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
            e.Property(x => x.AvatarUrl).HasMaxLength(500);
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

        b.Entity<ChatSticker>(e =>
        {
            e.Property(x => x.StorageServer).HasMaxLength(100);
            e.Property(x => x.StoragePath).HasMaxLength(500);
            e.HasIndex(x => x.IsOfficial);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserSticker>(e =>
        {
            e.HasKey(x => new { x.UserId, x.StickerId });
            e.HasIndex(x => new { x.UserId, x.AddedAt });
            e.HasIndex(x => x.StickerId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sticker).WithMany().HasForeignKey(x => x.StickerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StickerUsage>(e =>
        {
            e.HasKey(x => new { x.UserId, x.StickerKey });
            e.Property(x => x.StickerKey).HasMaxLength(80);
            e.HasIndex(x => new { x.UserId, x.LastUsedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserBlock>(e =>
        {
            e.HasKey(x => new { x.UserId, x.BlockedUserId });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BlockedUser).WithMany().HasForeignKey(x => x.BlockedUserId).OnDelete(DeleteBehavior.Cascade);
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

        b.Entity<MailGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => x.OwnerId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MailGroupMember>(e =>
        {
            e.HasKey(x => new { x.GroupId, x.UserId });
            e.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MailNotifyJob>(e =>
        {
            e.HasIndex(x => new { x.DoneAt, x.NextAttemptAt });
            e.Property(x => x.LastError).HasMaxLength(1000);
        });

        b.Entity<MailAttachment>(e =>
        {
            e.HasIndex(x => x.MailId);
            e.HasIndex(x => new { x.MailId, x.SortOrder });
            e.HasOne(x => x.Mail).WithMany(m => m.Attachments).HasForeignKey(x => x.MailId).IsRequired(false).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(x => x.Color).HasMaxLength(16).HasDefaultValue("#2E9E5B");
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

        b.Entity<ProjectGroup>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.SortOrder });
            e.HasIndex(x => new { x.Id, x.ProjectId }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(16);
            e.HasOne(x => x.Project).WithMany(p => p.Groups).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProjectTask>(e =>
        {
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.ParentTaskId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => new { x.ProjectId, x.Status, x.SortOrder });
            e.HasIndex(x => new { x.ProjectId, x.GroupId });
            e.HasOne(x => x.Group).WithMany(g => g.Tasks)
                .HasForeignKey(x => new { x.GroupId, x.ProjectId })
                .HasPrincipalKey(g => new { g.Id, g.ProjectId }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Priority).HasMaxLength(32);
            e.Property(x => x.Dependencies).HasMaxLength(2000);
            e.Property(x => x.RecurrenceRule).HasMaxLength(200);
            e.HasOne(x => x.Project).WithMany(p => p.Tasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ParentTask).WithMany(t => t.Subtasks).HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
            // Deleted tasks wait in the bin for Undo; every normal query skips them.
            e.HasQueryFilter(x => x.DeletedAt == null);
            e.HasIndex(x => x.DeleteBatchId);
        });

        b.Entity<ProjectTaskDependency>(e =>
        {
            e.HasKey(x => new { x.TaskId, x.DependsOnTaskId });
            e.HasIndex(x => x.DependsOnTaskId);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DependsOn).WithMany().HasForeignKey(x => x.DependsOnTaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProjectSavedView>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.OwnerId });
            e.Property(x => x.Name).HasMaxLength(40);
            e.Property(x => x.StateJson).HasMaxLength(2000);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TaskTimeEntry>(e =>
        {
            e.HasIndex(x => x.TaskId);
            e.HasIndex(x => new { x.UserId, x.StartedAt });
            // One running timer per user; starting another stops the first in the same transaction.
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("IX_TaskTimeEntries_UserId_Running");
            e.Property(x => x.Note).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(16);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // Entries of a soft-deleted task stay hidden with it.
            e.HasQueryFilter(x => x.Task.DeletedAt == null);
        });

        b.Entity<ProjectField>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.SortOrder });
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Type).HasMaxLength(16);
            e.Property(x => x.OptionsJson).HasMaxLength(4000);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProjectTaskFieldValue>(e =>
        {
            e.HasIndex(x => new { x.ProjectTaskId, x.ProjectFieldId }).IsUnique();
            e.HasIndex(x => x.ProjectFieldId);
            e.Property(x => x.Value).HasMaxLength(2048);
            e.HasOne(x => x.Task).WithMany().HasForeignKey(x => x.ProjectTaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Field).WithMany(f => f.Values).HasForeignKey(x => x.ProjectFieldId).OnDelete(DeleteBehavior.Cascade);
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
            e.HasIndex(x => new { x.UserId, x.SnoozedUntil });
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
            e.Property(x => x.PushShowPreview).HasDefaultValue(true);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Web Push: one row per browser; the endpoint identifies it.
        b.Entity<PushSubscription>(e =>
        {
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Endpoint).HasMaxLength(1000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.ApiBase).HasMaxLength(300);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PushJob>(e =>
        {
            e.HasIndex(x => new { x.ExpandedAt, x.NextAttemptAt });
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.EventType).HasMaxLength(100);
            e.Property(x => x.Priority).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.LastError).HasMaxLength(64);
        });
        b.Entity<PushDelivery>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.SubscriptionId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.JobId, x.UserId, x.Kind, x.Key });
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.Code).HasMaxLength(32);
            e.HasOne(x => x.Job).WithMany(j => j.Deliveries).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(x => x.Agenda).HasMaxLength(4000);
            // Existing rows become Busy only, so no old title turns public on rollout.
            e.Property(x => x.Visibility).HasMaxLength(8).HasDefaultValue(CalendarVisibility.Busy);
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
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Link).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Visibility).HasMaxLength(8).HasDefaultValue(CalendarVisibility.Busy);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Meeting).WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<CalendarSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.DefaultVisibility).HasMaxLength(8);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CalendarSubscription>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.TargetUserId }).IsUnique();
            e.Property(x => x.Color).HasMaxLength(8);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TargetUser).WithMany().HasForeignKey(x => x.TargetUserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CalendarDelegate>(e =>
        {
            e.HasIndex(x => new { x.OwnerId, x.DelegateId }).IsUnique();
            e.HasIndex(x => x.DelegateId);
            e.Property(x => x.Level).HasMaxLength(8);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Delegate).WithMany().HasForeignKey(x => x.DelegateId).OnDelete(DeleteBehavior.Cascade);
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
            e.Property(x => x.BodyFormat).HasMaxLength(16).HasDefaultValue("plain");
            e.Property(x => x.CoverMode).HasMaxLength(16).HasDefaultValue("auto");
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Approver).WithMany().HasForeignKey(x => x.ApproverId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<NewsVideo>(e =>
        {
            e.HasIndex(x => x.NewsPostId).IsUnique();
            e.HasIndex(x => x.PopupUntil);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(127);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.LocalPath).HasMaxLength(500);
            e.Property(x => x.NasPath).HasMaxLength(1000);
            e.Property(x => x.ThumbnailPath).HasMaxLength(500);
            e.HasOne(x => x.NewsPost).WithOne().HasForeignKey<NewsVideo>(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsVideoView>(e =>
        {
            e.HasKey(x => new { x.NewsVideoId, x.UserId });
            e.HasOne(x => x.NewsVideo).WithMany(v => v.Views).HasForeignKey(x => x.NewsVideoId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
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

        b.Entity<NewsComment>(e =>
        {
            e.HasIndex(x => new { x.NewsPostId, x.CreatedAt });
            e.Property(x => x.Body).HasMaxLength(2000);
            e.HasOne(x => x.NewsPost).WithMany().HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<NewsLike>(e =>
        {
            e.HasKey(x => new { x.NewsPostId, x.UserId });
            e.HasOne(x => x.NewsPost).WithMany().HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<NewsRead>(e =>
        {
            e.HasKey(x => new { x.NewsPostId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.NewsPost).WithMany().HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsBookmark>(e =>
        {
            e.HasKey(x => new { x.NewsPostId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.NewsPost).WithMany().HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsAttachment>(e =>
        {
            e.HasKey(x => new { x.NewsPostId, x.AttachmentId });
            e.HasIndex(x => x.AttachmentId);
            e.HasOne(x => x.NewsPost).WithMany(p => p.Attachments).HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── News composer: private drafts and their media/attachment references ──
        b.Entity<NewsComposerDraft>(e =>
        {
            e.HasIndex(x => new { x.OwnerId, x.State, x.UpdatedAt });
            e.HasIndex(x => x.PostId);
            e.Property(x => x.State).HasMaxLength(16);
            e.Property(x => x.BodyFormat).HasMaxLength(16);
            e.Property(x => x.CoverMode).HasMaxLength(16);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Body).HasMaxLength(20000);
            // Version is the compare-and-swap token: EF also treats it as the concurrency token so a
            // tracked save cannot slip past a concurrent one.
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            // A discarded draft never deletes its post; deleting a post discards the revision drafts.
            e.HasOne(x => x.Post).WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsComposerMedia>(e =>
        {
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => new { x.OwnerId, x.Kind, x.DeletedAt });
            e.HasIndex(x => x.AttachmentId);
            e.HasIndex(x => x.UploadDraftId);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(127);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.LocalPath).HasMaxLength(500);
            e.Property(x => x.NasPath).HasMaxLength(1000);
            e.Property(x => x.ThumbnailPath).HasMaxLength(500);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.SetNull);
            // Deleting a draft must not delete the asset: media lifetime belongs to the cleanup pass.
            e.HasOne(x => x.UploadDraft).WithMany().HasForeignKey(x => x.UploadDraftId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<NewsComposerDraftMedia>(e =>
        {
            e.HasKey(x => new { x.DraftId, x.MediaId });
            e.HasIndex(x => x.MediaId);
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasOne(x => x.Draft).WithMany(d => d.Media).HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NewsComposerDraftAttachment>(e =>
        {
            e.HasKey(x => new { x.DraftId, x.AttachmentId });
            e.HasIndex(x => x.AttachmentId);
            e.HasOne(x => x.Draft).WithMany(d => d.Attachments).HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<NewsPostMedia>(e =>
        {
            e.HasKey(x => new { x.NewsPostId, x.MediaId });
            e.HasIndex(x => x.MediaId);
            e.Property(x => x.Role).HasMaxLength(16);
            e.HasOne(x => x.NewsPost).WithMany(p => p.Media).HasForeignKey(x => x.NewsPostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Cascade);
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
            // Group calls: one live call per chat group is looked up by (ChatRoomId, Status).
            e.HasIndex(x => new { x.ChatRoomId, x.Status });
            e.HasOne(x => x.ChatRoom).WithMany().HasForeignKey(x => x.ChatRoomId).OnDelete(DeleteBehavior.SetNull);
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
        b.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
        });
        b.Entity<Attachment>(e =>
        {
            e.Property(x => x.Area).HasMaxLength(32);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(127);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => new { x.Area, x.CreatedAt });
            e.HasOne(x => x.Uploader).WithMany().HasForeignKey(x => x.UploaderId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<EmailOutboxMessage>(e =>
        {
            e.ToTable("EmailOutbox");
            e.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
        });
        b.Entity<PasswordResetToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NotePage>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Emoji).HasMaxLength(16);
            e.Property(x => x.Cover).HasMaxLength(16);
            e.Property(x => x.Accent).HasMaxLength(16);
            e.Property(x => x.Font).HasMaxLength(8);
            e.Property(x => x.SeedTemplate).HasMaxLength(16);
            e.Property(x => x.Excerpt).HasMaxLength(300);
            e.Property(x => x.PendingContributors).HasMaxLength(2000);
            e.Property(x => x.ImportSource).HasMaxLength(16);
            e.Property(x => x.ImportKey).HasMaxLength(80);
            e.HasIndex(x => new { x.OwnerId, x.DeletedAt });
            e.HasIndex(x => new { x.OwnerId, x.ImportKey });
            e.HasIndex(x => x.ParentId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteUpdate>(e =>
        {
            e.HasIndex(x => x.PageId);
            e.HasOne<NotePage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteShare>(e =>
        {
            e.Property(x => x.Level).HasMaxLength(8);
            e.HasIndex(x => x.PageId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.RoomId);
            e.HasOne(x => x.Page).WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Room).WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteFavorite>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PageId });
            e.HasOne<NotePage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteVisit>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PageId });
            e.HasIndex(x => new { x.UserId, x.VisitedAt });
            e.HasOne<NotePage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteVersion>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Contributors).HasMaxLength(2000);
            e.HasIndex(x => new { x.PageId, x.CreatedAt });
            e.HasOne<NotePage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteThread>(e =>
        {
            e.Property(x => x.Quote).HasMaxLength(300);
            e.Property(x => x.Anchor).HasMaxLength(2000);
            e.HasIndex(x => x.PageId);
            e.HasOne<NotePage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Comments).WithOne(x => x.Thread).HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteComment>(e =>
        {
            e.Property(x => x.Body).HasMaxLength(2000);
            e.HasIndex(x => new { x.ThreadId, x.CreatedAt });
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NoteAttachment>(e =>
        {
            e.HasKey(x => x.AttachmentId);
            e.HasIndex(x => x.PageId);
            e.HasOne<NotePage>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SearchHistory>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.SearchedAt });
            e.Property(x => x.Query).HasMaxLength(200);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Ticket>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(16);
            e.Property(x => x.Origin).HasMaxLength(8);
            e.Property(x => x.ExternalRef).HasMaxLength(100);
            e.Property(x => x.IdeskTicketId).HasMaxLength(64);
            e.Property(x => x.TicketNumber).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Priority).HasMaxLength(16);
            e.Property(x => x.Module).HasMaxLength(64);
            e.Property(x => x.ModuleName).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(5000);
            e.Property(x => x.Device).HasMaxLength(100);
            e.Property(x => x.Software).HasMaxLength(100);
            e.Property(x => x.CriticalReason).HasMaxLength(500);
            e.Property(x => x.HardwareType).HasMaxLength(100);
            e.Property(x => x.ScheduledDate).HasMaxLength(10);
            e.Property(x => x.ScheduledTime).HasMaxLength(5);
            e.Property(x => x.AssigneeName).HasMaxLength(200);
            e.Property(x => x.Url).HasMaxLength(500);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.ExternalRef).IsUnique();
            e.HasIndex(x => x.IdeskTicketId).IsUnique();
            e.HasIndex(x => new { x.UserId, x.RemovedAt });
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TicketEvent>(e =>
        {
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.FromStatus).HasMaxLength(32);
            e.Property(x => x.ToStatus).HasMaxLength(32);
            e.Property(x => x.Assignee).HasMaxLength(200);
            e.Property(x => x.WebhookEventId).HasMaxLength(100);
            e.HasIndex(x => new { x.TicketId, x.OccurredAt });
            e.HasIndex(x => x.WebhookEventId).IsUnique();
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TicketUserState>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TicketRefCounter>(e =>
        {
            e.HasKey(x => x.Year);
            e.Property(x => x.Year).ValueGeneratedNever();
        });
    }
}
