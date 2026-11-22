using IPop.Application.Notifier;
using Xunit;

namespace IPop.Tests;

/// Audit 2026-11 (Notifier 1.4.8): a session that ended while the live hub was reconnecting hung the app for good.
/// The hub asks for a token; the refresh was rejected and signed out inline; signing out closes the hub and waits
/// for it, and the hub waits for its token. These pin the rules that keep that from happening again.
public class NotifierAuthGateTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_refresh_waiting_behind_another_gives_up_when_a_transition_asks()
    {
        var gate = new NotifierAuthGate();
        var first = await gate.EnterRefreshAsync();
        Assert.NotNull(first);
        var second = gate.EnterRefreshAsync();
        var transition = gate.EnterTransitionAsync();
        Assert.Null(await second.WaitAsync(Soon));   // gave up, did not queue behind the transition
        first!.Dispose();
        (await transition.WaitAsync(Soon)).Dispose();
    }

    [Fact]
    public async Task A_transition_that_closes_the_hub_is_never_blocked_by_the_hub_asking_for_a_token()
    {
        var gate = new NotifierAuthGate();
        var refreshDuringTeardown = Task.FromResult<IDisposable?>(null);
        var signOut = Task.Run(async () =>
        {
            using var _ = await gate.EnterTransitionAsync();
            // Closing the hub: SignalR waits for its pending AccessTokenProvider call, which refreshes.
            refreshDuringTeardown = gate.EnterRefreshAsync();
            Assert.Null(await refreshDuringTeardown.WaitAsync(Soon));
        });
        await signOut.WaitAsync(Soon);
    }

    [Fact]
    public async Task A_rejected_refresh_signs_out_after_it_lets_go_and_refreshes_stop_until_a_new_session()
    {
        var gate = new NotifierAuthGate();
        var signedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using (var refresh = await gate.EnterRefreshAsync())
        {
            Assert.NotNull(refresh);
            gate.EndSessionLater(async () =>
            {
                Interlocked.Increment(ref calls);
                using var _ = await gate.EnterTransitionAsync();   // would deadlock if run inline while held
                signedOut.TrySetResult();
            });
            gate.EndSessionLater(() => { Interlocked.Increment(ref calls); return Task.CompletedTask; });
            Assert.True(gate.Ending);
        }
        await signedOut.Task.WaitAsync(Soon);
        Assert.Equal(1, calls);
        Assert.Null(await gate.EnterRefreshAsync());

        using (await gate.EnterTransitionAsync()) gate.SessionStarted();
        using var again = await gate.EnterRefreshAsync();
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Refreshes_run_again_after_a_transition_ends()
    {
        var gate = new NotifierAuthGate();
        using (await gate.EnterTransitionAsync()) Assert.True(gate.Transitioning);
        Assert.False(gate.Transitioning);
        using var r = await gate.EnterRefreshAsync();
        Assert.NotNull(r);
    }
}
