using IPop.Application.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Mail;

public sealed class MailScheduledDeliveryWorker(IServiceScopeFactory scopes, ILogger<MailScheduledDeliveryWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            // Run initial check shortly after startup
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var mailSvc = scope.ServiceProvider.GetRequiredService<IMailService>();
                    var delivered = await mailSvc.DeliverDueScheduledMailsAsync(stoppingToken);
                    if (delivered > 0)
                    {
                        log.LogInformation("Delivered {Count} scheduled mail(s)", delivered);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to deliver scheduled mails; will retry next interval");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }
}
