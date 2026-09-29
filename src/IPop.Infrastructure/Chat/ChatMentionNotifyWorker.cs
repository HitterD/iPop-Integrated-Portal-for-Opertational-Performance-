using IPop.Application.Chat;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IPop.Infrastructure.Chat;

public sealed class ChatMentionNotifyWorker(IServiceScopeFactory scopeFactory) : BackgroundService
{
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
                        .Where(m => m.NotifiedAt == null && !m.SuppressNotification && m.NotifyAttempts < 3 && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
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
                    foreach (var mention in pending)
                    {
                        try
                        {
                            if (settingsByKey.TryGetValue((mention.UserId, mention.Message.RoomId), out var st)
                                && (st.IsArchived || (st.IsMuted && (st.MuteExpiresAt == null || st.MuteExpiresAt > now))))
                            {
                                // Tandai selesai agar tidak dicoba ulang terus-menerus.
                                mention.NotifiedAt = now;
                                continue;
                            }
                            var preview = IPop.Application.Chat.Mentions.MentionPlainText.Project(mention.Message.Content, 120);
                            var roomName = mention.Message.Room?.Name ?? "Chat";
                            var senderName = mention.Message.Sender?.Name ?? "Unknown";
                            await gateway.SendMentionNotificationAsync(mention.UserId, senderName, roomName, preview, true, stoppingToken, mention.MessageId);
                            mention.NotifiedAt = DateTime.UtcNow;
                        }
                        catch
                        {
                            mention.NotifyAttempts++;
                            mention.NextAttemptAt = DateTime.UtcNow.AddSeconds(5);
                        }
                    }
                    if (pending.Count > 0) await db.SaveChangesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch { /* swallow — retry next tick */ }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }
}
