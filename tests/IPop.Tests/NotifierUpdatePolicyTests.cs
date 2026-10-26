using IPop.Application.Notifier;
using Xunit;
using Step = IPop.Application.Notifier.NotifierUpdatePolicy.Step;

namespace IPop.Tests;

/// Notifier 1.3.0: announced updates — ask, ask again after Later, install anyway after a day when idle,
/// mandatory at once, never during remote help or a call.
public class NotifierUpdatePolicyTests
{
    private static readonly DateTime Ready = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Active = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    [Fact]
    public void A_fresh_download_asks_once()
    {
        Assert.Equal(Step.Ask, NotifierUpdatePolicy.Next(Ready.AddMinutes(1), Ready, null, false, false, false, Active));
        Assert.Equal(Step.Wait, NotifierUpdatePolicy.Next(Ready.AddMinutes(1), Ready, null, false, asking: true, false, Active));
    }

    [Fact]
    public void Later_asks_again_after_four_hours()
    {
        var asked = Ready.AddMinutes(1);
        Assert.Equal(Step.Wait, NotifierUpdatePolicy.Next(asked.AddHours(3.9), Ready, asked, false, false, false, Active));
        Assert.Equal(Step.Ask, NotifierUpdatePolicy.Next(asked.AddHours(4), Ready, asked, false, false, false, Active));
    }

    [Fact]
    public void After_a_day_it_installs_only_once_the_pc_is_idle()
    {
        var asked = Ready.AddHours(23);
        Assert.Equal(Step.Wait, NotifierUpdatePolicy.Next(Ready.AddHours(24), Ready, asked, false, false, false, Active));
        Assert.Equal(Step.InstallQuietly, NotifierUpdatePolicy.Next(Ready.AddHours(24), Ready, asked, false, false, false, Idle));
        // Even with the prompt still open (nobody at the PC).
        Assert.Equal(Step.InstallQuietly, NotifierUpdatePolicy.Next(Ready.AddHours(30), Ready, asked, false, asking: true, false, Idle));
    }

    [Fact]
    public void Mandatory_installs_at_once()
        => Assert.Equal(Step.InstallNow, NotifierUpdatePolicy.Next(Ready, Ready, null, mandatory: true, false, false, Active));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remote_help_or_a_call_always_waits(bool mandatory)
        => Assert.Equal(Step.Wait, NotifierUpdatePolicy.Next(Ready.AddDays(3), Ready, null, mandatory, false, busy: true, Idle));
}
