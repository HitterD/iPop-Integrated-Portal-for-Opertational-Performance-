using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

public class ToastServiceTests
{
    private static ToastService Fast(int ms = 150) => new(_ => TimeSpan.FromMilliseconds(ms));

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Default_durations_keep_errors_longest()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), ToastService.DefaultDuration(ToastKind.Success));
        Assert.Equal(TimeSpan.FromSeconds(5), ToastService.DefaultDuration(ToastKind.Info));
        Assert.Equal(TimeSpan.FromSeconds(8), ToastService.DefaultDuration(ToastKind.Error));
        Assert.Equal(TimeSpan.FromSeconds(6), ToastService.DefaultDuration(ToastKind.Warning));
    }

    [Theory]
    [InlineData(ToastKind.Success, "check")]
    [InlineData(ToastKind.Error, "x")]
    [InlineData(ToastKind.Warning, "alert-triangle")]
    [InlineData(ToastKind.Info, "info")]
    public void Each_kind_has_its_own_icon(ToastKind kind, string icon)
        => Assert.Equal(icon, IPop.Web.Components.Chat.ToastHost.DefaultIcon(kind));

    [Fact]
    public void Newest_first_and_at_most_three_visible()
    {
        var s = new ToastService();
        for (var i = 1; i <= 5; i++) s.Show($"m{i}", ToastKind.Success);

        Assert.Equal(ToastService.MaxVisible, s.Active.Count);
        Assert.Equal(["m5", "m4", "m3"], s.Active.Select(t => t.Message));
    }

    [Fact]
    public void Repeated_message_replaces_instead_of_stacking()
    {
        var s = new ToastService();
        s.Show("Upload failed", ToastKind.Error);
        s.Show("Saved", ToastKind.Success);
        s.Show("Upload failed", ToastKind.Error);

        Assert.Equal(["Upload failed", "Saved"], s.Active.Select(t => t.Message));
    }

    [Fact]
    public async Task Toast_auto_dismisses_after_its_duration()
    {
        var s = Fast();
        s.Show("saved", ToastKind.Success);

        await WaitUntil(() => s.Active.Count == 0);
    }

    [Fact]
    public async Task Paused_toast_stays_until_resumed()
    {
        var s = Fast();
        s.Show("hover me", ToastKind.Info);
        var id = s.Active[0].Id;

        s.Pause(id);
        Assert.True(s.Active[0].IsPaused);
        await Task.Delay(400);
        Assert.Single(s.Active);
        Assert.False(s.Active[0].IsExiting);

        s.Resume(id);
        Assert.False(s.Active[0].IsPaused);
        await WaitUntil(() => s.Active.Count == 0, 5000);
    }

    [Fact]
    public async Task Dismiss_marks_exiting_then_removes()
    {
        var s = new ToastService();
        s.Show("bye", ToastKind.Error);
        var id = s.Active[0].Id;

        s.Dismiss(id);
        Assert.True(s.Active[0].IsExiting);
        await WaitUntil(() => s.Active.Count == 0);
    }

    [Fact]
    public async Task Undo_runs_action_and_removes_toast()
    {
        var s = new ToastService();
        var undone = false;
        var id = s.ShowUndo("Deleted", () => { undone = true; return Task.CompletedTask; });

        await s.RunUndoAsync(id);

        Assert.True(undone);
        Assert.Empty(s.Active);
    }

    [Fact]
    public void Only_toasts_with_a_notification_id_are_incoming()
    {
        var s = new ToastService();
        s.Show("Saved", ToastKind.Success);
        s.ShowDetailed("New mail", "Hi", ToastKind.Info, "mail", "/mail", Guid.NewGuid().ToString());

        Assert.True(s.Active[0].IsIncoming);
        Assert.False(s.Active[1].IsIncoming);
    }
}
