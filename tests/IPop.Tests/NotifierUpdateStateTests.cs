using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

public sealed class NotifierUpdateStateTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ipop-update-tests-" + Guid.NewGuid());
    private string StateFile => Path.Combine(folder, "update.json");
    private static readonly DateTime Ready = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Later_and_download_age_survive_restart_and_rechecking_the_same_version()
    {
        var state = new NotifierUpdateState(StateFile);
        state.MarkReady("1.3.0", Ready);
        state.MarkAsked(Ready.AddHours(1));

        var restarted = new NotifierUpdateState(StateFile);
        restarted.MarkReady("1.3.0", Ready.AddHours(3));

        Assert.Equal(Ready, restarted.ReadyAt);
        Assert.Equal(Ready.AddHours(1), restarted.AskedAt);
        Assert.Equal(NotifierUpdatePolicy.Step.Wait, NotifierUpdatePolicy.Next(
            Ready.AddHours(4), restarted.ReadyAt, restarted.AskedAt, false, false, false, TimeSpan.Zero));
        Assert.Equal(NotifierUpdatePolicy.Step.Ask, NotifierUpdatePolicy.Next(
            Ready.AddHours(5), restarted.ReadyAt, restarted.AskedAt, false, false, false, TimeSpan.Zero));
        Assert.Equal(NotifierUpdatePolicy.Step.InstallQuietly, NotifierUpdatePolicy.Next(
            Ready.AddHours(24), restarted.ReadyAt, restarted.AskedAt, false, false, false, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_different_version_starts_its_own_timers()
    {
        var state = new NotifierUpdateState(StateFile);
        state.MarkReady("1.3.0", Ready);
        state.MarkAsked(Ready.AddHours(1));
        state.MarkReady("1.3.1", Ready.AddHours(2));
        var restarted = new NotifierUpdateState(StateFile);
        Assert.Equal("1.3.1", restarted.Version);
        Assert.Equal(Ready.AddHours(2), restarted.ReadyAt);
        Assert.Null(restarted.AskedAt);
    }

    [Fact]
    public void Missing_or_corrupt_state_starts_a_new_clock_without_crashing()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(StateFile, "invalid json");
        var state = new NotifierUpdateState(StateFile);
        state.MarkReady("1.3.0", Ready);
        Assert.Equal(Ready, new NotifierUpdateState(StateFile).ReadyAt);
        Assert.Null(state.AskedAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
