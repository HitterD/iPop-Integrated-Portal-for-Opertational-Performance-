using System.Text.Json;
using IPop.Application.Email;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Email;

public sealed class DbEmailQueue(IPopDbContext db) : IEmailQueue
{
    public async Task EnqueueAsync(EmailMessage msg, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        db.EmailOutbox.Add(new EmailOutboxMessage
        {
            Id = Guid.NewGuid(), ToJson = JsonSerializer.Serialize(msg.To), Subject = msg.Subject,
            HtmlBody = msg.HtmlBody, TextBody = msg.TextBody, NextAttemptAt = now, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}

/// Drains the outbox. Retries after 1 then 5 minutes; after MaxAttempts the row stays
/// unsent with LastError so failures are visible.
public sealed class EmailOutboxProcessor(IPopDbContext db, IEmailSender sender, ILogger<EmailOutboxProcessor> log,
    EmailFailureAlerts? alerts = null)
{
    internal static TimeSpan Backoff(int attempts) =>
        attempts <= 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);

    public async Task<int> ProcessDueAsync(DateTime now, CancellationToken ct = default)
    {
        var due = await db.EmailOutbox
            .Where(m => m.SentAt == null && m.Attempts < EmailOutboxMessage.MaxAttempts && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt).Take(20).ToListAsync(ct);

        Exception? failure = null;
        foreach (var m in due)
        {
            // Stop between mails, never between "sent" and "recorded as sent".
            if (ct.IsCancellationRequested) break;
            m.Attempts++;
            try
            {
                var to = JsonSerializer.Deserialize<string[]>(m.ToJson) ?? [];
                await sender.SendAsync(new EmailMessage(to, m.Subject, m.HtmlBody, m.TextBody), ct);
                m.SentAt = now;
                m.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.LastError = ex.Message;
                m.NextAttemptAt = now + Backoff(m.Attempts);
                log.LogWarning(ex, "Email {Id} attempt {Attempt} failed", m.Id, m.Attempts);
                failure ??= ex;
            }
            // Saved per mail and not cancellable: the whole batch of 20 used to be saved once at the end, so a pod
            // stopping mid-batch (rollout) left already-sent mails unmarked and they went out again on restart.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        if (failure is not null && alerts is not null) await alerts.ReportAsync(failure, now, ct);
        return due.Count;
    }
}

public sealed class EmailOutboxWorker(IServiceScopeFactory scopes, ILogger<EmailOutboxWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EmailOutboxProcessor>()
                    .ProcessDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Email outbox tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
