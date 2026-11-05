using IPop.Application.Email;
using IPop.Core.Entities;
using IPop.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class EmailOutboxTests
{
    private sealed class FakeSender(bool fail) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage msg, CancellationToken ct = default)
        {
            if (fail) throw new InvalidOperationException("smtp down");
            Sent.Add(msg);
            return Task.CompletedTask;
        }
    }

    private static EmailOutboxProcessor Processor(IPop.Infrastructure.Persistence.IPopDbContext db, IEmailSender s) =>
        new(db, s, NullLogger<EmailOutboxProcessor>.Instance);

    [Fact]
    public async Task Enqueue_Persists_Pending()
    {
        using var t = new TestDb();
        await new DbEmailQueue(t.Db).EnqueueAsync(new EmailMessage(["a@t.co"], "Hi", "<p>x</p>"));

        var row = await t.Verify().EmailOutbox.SingleAsync();
        Assert.Null(row.SentAt);
        Assert.Equal(0, row.Attempts);
        Assert.Contains("a@t.co", row.ToJson);
    }

    [Fact]
    public async Task Process_Success_MarksSent()
    {
        using var t = new TestDb();
        await new DbEmailQueue(t.Db).EnqueueAsync(new EmailMessage(["a@t.co", "b@t.co"], "Hi", "<p>x</p>"));
        var sender = new FakeSender(fail: false);

        await Processor(t.Db, sender).ProcessDueAsync(DateTime.UtcNow.AddSeconds(1));

        Assert.Equal(["a@t.co", "b@t.co"], sender.Sent.Single().To);
        Assert.NotNull((await t.Verify().EmailOutbox.SingleAsync()).SentAt);
    }

    [Fact]
    public async Task Process_Failure_Retries_ThenStopsAtMax()
    {
        using var t = new TestDb();
        await new DbEmailQueue(t.Db).EnqueueAsync(new EmailMessage(["a@t.co"], "Hi", "<p>x</p>"));
        var p = Processor(t.Db, new FakeSender(fail: true));
        var now = DateTime.UtcNow.AddSeconds(1);

        await p.ProcessDueAsync(now);
        var row = await t.Verify().EmailOutbox.SingleAsync();
        Assert.Equal(1, row.Attempts);
        Assert.Equal("smtp down", row.LastError);
        Assert.True(row.NextAttemptAt > now);

        Assert.Equal(0, await p.ProcessDueAsync(now));   // backoff: not due yet

        await p.ProcessDueAsync(now.AddHours(1));
        await p.ProcessDueAsync(now.AddHours(2));
        Assert.Equal(0, await p.ProcessDueAsync(now.AddHours(3)));   // gave up
        var final = await t.Verify().EmailOutbox.SingleAsync();
        Assert.Equal(EmailOutboxMessage.MaxAttempts, final.Attempts);
        Assert.Null(final.SentAt);
    }

    [Fact]
    public void Options_EmptyHost_IsNotConfigured()
    {
        Assert.False(new SmtpOptions().IsConfigured);
        Assert.True(new SmtpOptions { Host = "smtp.sja.co.id" }.IsConfigured);
    }

    /// Stops (pod shutdown) after the third mail went out: those three stay marked sent, the rest stay queued.
    private sealed class StopAfterSender(int sends, CancellationTokenSource stop) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Task SendAsync(EmailMessage msg, CancellationToken ct = default)
        {
            Sent.Add(msg);
            if (Sent.Count == sends) stop.Cancel();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Shutdown_mid_batch_keeps_what_was_sent_marked_so_nothing_goes_out_twice()
    {
        using var t = new TestDb();
        var q = new DbEmailQueue(t.Db);
        for (var i = 0; i < 6; i++) await q.EnqueueAsync(new EmailMessage([$"u{i}@t.co"], $"Hi {i}", "<p>x</p>"));
        using var stop = new CancellationTokenSource();
        var sender = new StopAfterSender(3, stop);

        await Processor(t.Db, sender).ProcessDueAsync(DateTime.UtcNow.AddSeconds(1), stop.Token);

        Assert.Equal(3, sender.Sent.Count);
        var rows = await t.Verify().EmailOutbox.ToListAsync();
        Assert.Equal(3, rows.Count(r => r.SentAt != null));
        Assert.Equal(3, rows.Count(r => r.SentAt == null && r.Attempts == 0));

        var restart = new FakeSender(fail: false);
        await Processor(t.Verify(), restart).ProcessDueAsync(DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(3, restart.Sent.Count);   // only the three that never went out
    }
}
