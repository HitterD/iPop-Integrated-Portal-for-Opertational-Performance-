using IPop.Application.Attachments;
using IPop.Application.Hub;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Hub;

/// Files in a Hub topic: readable by the topic's members only. An upload not yet linked to a topic: its uploader.
public sealed class HubAttachmentPolicy(IPopDbContext db) : IAttachmentAccessPolicy
{
    public string Area => AttachmentAreas.Hub;

    public async Task<bool> CanReadAsync(Guid attachmentId, Guid userId, CancellationToken ct)
    {
        var topicIds = await db.HubAttachments.AsNoTracking().Where(a => a.AttachmentId == attachmentId).Select(a => a.TopicId).ToListAsync(ct);
        if (topicIds.Count > 0)
            return await db.HubMembers.AsNoTracking().AnyAsync(m => topicIds.Contains(m.TopicId) && m.UserId == userId, ct);
        return await db.Attachments.AsNoTracking().AnyAsync(a => a.Id == attachmentId && a.DeletedAt == null && a.UploaderId == userId, ct);
    }
}

/// Nav badge: topics where someone is waiting for my answer.
public sealed class HubBadgeProvider(IHubService hub) : IBadgeProvider
{
    public string Key => HubEvents.BadgeKey;
    public Task<int> CountAsync(Guid userId, CancellationToken ct) => hub.WaitingCountAsync(userId, ct);
}

/// Every 30 minutes: day-2 reminders to the person, day-3 reminders to their head (without the topic text).
public sealed class HubReminderWorker(IServiceScopeFactory scopes, ILogger<HubReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetRequiredService<IHubService>() is HubService hub)
                    await hub.RunRemindersAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "HubReminderWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }
}
