using IPop.Infrastructure.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class SmtpSettingsTests
{
    private static readonly SmtpOptions File = new()
    {
        Host = "mail.file", Port = 465, User = "sys@t.co", Password = "old-pw", From = "sys@t.co", FromName = "iDesk Support",
    };

    private static SmtpSettingsStore Store(TestDb t) =>
        new(t.Db, File, new EphemeralDataProtectionProvider());

    [Fact]
    public async Task NothingSaved_UsesConfigFile()
    {
        using var t = new TestDb();
        var (o, fromAdmin) = await Store(t).EffectiveAsync();
        Assert.False(fromAdmin);
        Assert.Equal("old-pw", o.Password);
    }

    [Fact]
    public async Task Saved_Overrides_File_And_Password_Is_Encrypted()
    {
        using var t = new TestDb();
        var store = Store(t);
        await store.SaveAsync(new SmtpOptions { Host = "mail.new", Port = 587, User = "sys@t.co", Password = "new-pw", From = "sys@t.co", FromName = "iPop" }, "admin");

        var row = await t.Verify().AppSettings.SingleAsync(s => s.Key == SmtpSettingsStore.Key);
        Assert.DoesNotContain("new-pw", row.ValueJson);
        var audit = await t.Verify().AuditLogs.SingleAsync(a => a.Action == "settings.smtp_changed");
        Assert.DoesNotContain("new-pw", audit.DetailJson);

        var (o, fromAdmin) = await store.EffectiveAsync();
        Assert.True(fromAdmin);
        Assert.Equal(("mail.new", "new-pw", "iPop"), (o.Host, o.Password, o.FromName));
    }

    [Fact]
    public async Task BlankPassword_KeepsCurrent()
    {
        using var t = new TestDb();
        var store = Store(t);
        var merged = await store.MergeAsync(new SmtpOptions { Host = "mail.file", Port = 465, User = "sys@t.co", From = "sys@t.co", FromName = "iPop" });
        Assert.Equal("old-pw", merged.Password);
        await store.SaveAsync(merged, "admin");
        Assert.Equal("old-pw", (await store.EffectiveAsync()).Options.Password);
    }

    [Fact]
    public void AuthFailure_Is_Described_As_Wrong_Password()
    {
        var a = EmailFailureAlerts.Describe(new AuthenticationException("535: 5.7.8 Authentication failed"));
        Assert.Equal("smtp-auth", a.Key);
        Assert.Contains("Admin › Email", a.Message);
        Assert.Equal("smtp-other", EmailFailureAlerts.Describe(new InvalidOperationException("boom")).Key);
    }
}
