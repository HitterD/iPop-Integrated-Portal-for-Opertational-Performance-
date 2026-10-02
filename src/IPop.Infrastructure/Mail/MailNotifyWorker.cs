using System.Threading.Channels;
using IPop.Application.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Mail;

/// Wakes <see cref="MailNotifyWorker"/> as soon as a send queues a <see cref="IPop.Core.Entities.MailNotifyJob"/>,
/// so small mails still notify within milliseconds; the timer only covers retries and restarts.
public sealed class MailNotifySignal
{
    private readonly Channel<bool> _ch = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _ch.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _ch.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// Drains the durable "new mail" notification queue (badges, banners, push) with retry.
public sealed class MailNotifyWorker(IServiceScopeFactory scopes, MailNotifySignal signal, ILogger<MailNotifyWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    processed = await scope.ServiceProvider.GetRequiredService<IMailService>().ProcessNotifyJobsAsync(stoppingToken);
                } while (processed > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Mail notification queue failed; will retry");
            }
            try { await signal.WaitAsync(Idle, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
