namespace IPop.Notifier.Remote;

/// When to try again after the LiveKit connection drops, and when to give up (BIGIS #8 v3). Shared by the user's PC
/// (host) and, in its JS twin, the ICT viewer: <see cref="Attempts"/> tries spread over <see cref="Window"/>. The
/// session stays open meanwhile; only the server (heartbeat window) or the last try ends it. Pure: no timers here.
public sealed class RemoteRejoin(int attempts = 6, int seconds = 60)
{
    public int Attempts { get; } = Math.Clamp(attempts, 1, 20);
    public TimeSpan Window { get; } = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));

    /// When try <paramref name="n"/> (1-based) is due, counted from the drop.
    public TimeSpan DueAt(int n) => n <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Window.Ticks * (n - 1) / Attempts);

    /// Give up after the last try's slot is over (or the whole window).
    public bool GiveUp(int triesDone, TimeSpan sinceDrop) => triesDone >= Attempts && sinceDrop >= Window || sinceDrop >= Window + TimeSpan.FromSeconds(5);
}

/// The grace before the user's PC ends a session whose helper left without saying bye (BIGIS #8 v3): 30 s, and the
/// timer is dropped when the helper comes back (a reconnect is not a leave).
public sealed class HelperLeaveTimer(TimeSpan grace)
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);
    private int generation;

    /// The helper left at some point; returns the ticket to check when the grace is over.
    public int Left() => ++generation;

    /// The helper is back: any pending leave no longer counts.
    public void Back() => generation++;

    /// The grace for <paramref name="ticket"/> ran out with nobody back: end the session.
    public bool ShouldEnd(int ticket) => ticket == generation;

    public TimeSpan Grace { get; } = grace;
}
