using IPop.Application.Chat;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Chat;

public sealed class ChatMentionNotifyWorker(IServiceScopeFactory scopeFactory, ILogger<ChatMentionNotifyWorker>? log = null) : BackgroundService
{
    // Backoff 5s, 30s, 2m, 10m, 30m: a short DB/push outage must not cost anyone their mention.
    internal const int MaxAttempts = 6;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                    var gateway = scope.ServiceProvider.GetRequiredService<INotificationGateway>();
                    var now = DateTime.UtcNow;
                    var pending = await db.ChatMentions
                        .Where(m => m.NotifiedAt == null && !m.SuppressNotification && m.NotifyAttempts < MaxAttempts && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
                        .OrderBy(m => m.CreatedAt)
                        .Take(200)
                        .Include(m => m.Message).ThenInclude(msg => msg.Sender)
                        .Include(m => m.Message).ThenInclude(msg => msg.Room)
                        .ToListAsync(stoppingToken);
                    // Hormati bisu & arsip: ruang yang dibisukan (belum kedaluwarsa) atau
                    // diarsipkan pengguna tidak mengirim notifikasi mention — penanda
                    // HasUnreadMention tetap muncul di daftar ruang.
                    var settingsByKey = await db.ChatRoomUserSettings
                        .Where(s => pending.Select(m => m.UserId).Distinct().Contains(s.UserId))
                        .ToDictionaryAsync(s => (s.UserId, s.RoomId), stoppingToken);
                    // Someone who blocked the sender gets no "@you" from them either (the room toast already skips them).
                    var blocked = (await db.UserBlocks.AsNoTracking()
                        .Where(b => pending.Select(m => m.UserId).Contains(b.UserId))
                        .Select(b => new { b.UserId, b.BlockedUserId }).ToListAsync(stoppingToken))
                        .Select(b => (b.UserId, b.BlockedUserId)).ToHashSet();
                    foreach (var mention in pending)
                    {
                        try
                        {
                            if (blocked.Contains((mention.UserId, mention.Message.SenderId)))
                            {
                                mention.NotifiedAt = now;
                                continue;
                            }
                            if (settingsByKey.TryGetValue((mention.UserId, mention.Message.RoomId), out var st)
                                && (st.IsArchived || (st.IsMuted && (st.MuteExpiresAt == null || st.MuteExpiresAt > now))))
                            {
                                // Tandai selesai agar tidak dicoba ulang terus-menerus.
                                mention.NotifiedAt = now;
                                continue;
                            }
                            var preview = IPop.Application.Chat.Mentions.MentionPlainText.Project(mention.Message.Content, 120);
                            var senderName = mention.Message.Sender?.Name ?? "Unknown";
                            // A DM has no name: title it after the sender rather than a generic "Chat".
                            var roomName = mention.Message.Room?.Name ?? senderName;
                            await gateway.SendMentionNotificationAsync(mention.UserId, senderName, roomName, preview, true, stoppingToken,
                                mention.MessageId, mention.Message.RoomId);
                            mention.NotifiedAt = DateTime.UtcNow;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                        {
                            mention.NotifyAttempts++;
                            mention.NextAttemptAt = DateTime.UtcNow + Backoff[Math.Min(mention.NotifyAttempts - 1, Backoff.Length - 1)];
                            if (mention.NotifyAttempts >= MaxAttempts)
                                log?.LogError(ex, "Mention notification {MentionId} for user {UserId} gave up after {Attempts} attempts.", mention.Id, mention.UserId, mention.NotifyAttempts);
                            else
                                log?.LogWarning("Mention notification {MentionId} failed (attempt {Attempts}): {Error}", mention.Id, mention.NotifyAttempts, ex.GetType().Name);
                        }
                    }
                    if (pending.Count > 0) await db.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) { log?.LogWarning(ex, "Mention notify pass failed; retrying."); }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }
}
