namespace IPop.Application.Notifier;

/// One gate for the Notifier's token refresh and its session transitions (sign-in, sign-out, account switch).
///
/// A transition tears the old session down (closes the live hub, ends remote sessions) while it holds the gate, and
/// the hub asks for a token while it reconnects. So nothing that asks for a token may ever wait for a transition:
///   • a transition, from the moment it asks for the gate, makes every refresh that is waiting (or arrives) give up
///     with "no token" instead of queueing behind it;
///   • a refresh that the server rejects never signs out inline (that would close the very hub that is asking):
///     it schedules the sign-out, which starts as its own transition once the refresh has let go of the gate.
/// Before 1.4.8 both happened inline and the app could hang for good: "connecting…", no popups, every call failing
/// with "Signed out.", until it was restarted (audit 2026-11, seen on a PC on 2026-10-28).
/// Shared by the app (linked into IPop.Notifier) and the tests.
public sealed class NotifierAuthGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource retiring = new();
    private int transitions;
    private int ending;

    /// A transition is waiting for, or holds, the gate: the current session is going away.
    public bool Transitioning => Volatile.Read(ref transitions) > 0;

    /// The server rejected this session; its sign-out is scheduled or running.
    public bool Ending => Volatile.Read(ref ending) == 1;

    /// For a token refresh. Null = give up without a token (a transition is under way, or the session has ended).
    public async Task<IDisposable?> EnterRefreshAsync(CancellationToken ct = default)
    {
        if (Transitioning || Ending) return null;
        var stop = Volatile.Read(ref retiring).Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        try { await gate.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        if (Transitioning || Ending) { gate.Release(); return null; }
        return new Release(this, transition: false);
    }

    /// For sign-in, sign-out and account switch: waits its turn; refreshes give up from the moment it asks.
    public async Task<IDisposable> EnterTransitionAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref transitions);
        try { Volatile.Read(ref retiring).Cancel(); } catch (ObjectDisposedException) { }
        try { await gate.WaitAsync(ct); }
        catch { Interlocked.Decrement(ref transitions); throw; }
        return new Release(this, transition: true);
    }

    /// The server rejected the refresh token: run <paramref name="signOut"/> once, off this call, as its own
    /// transition. Further refreshes get no token until a new session starts (<see cref="SessionStarted"/>).
    public void EndSessionLater(Func<Task> signOut)
    {
        if (Interlocked.Exchange(ref ending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await signOut(); }
            catch { /* the sign-out cleans up on its own; nothing to hand back */ }
        });
    }

    /// A new session is in place (inside its transition): refreshes may run again once the transition ends.
    public void SessionStarted() => Volatile.Write(ref ending, 0);

    private void Leave(bool transition)
    {
        if (transition && Interlocked.Decrement(ref transitions) == 0)
        {
            var old = Interlocked.Exchange(ref retiring, new CancellationTokenSource());
            old.Dispose();
        }
        gate.Release();
    }

    private sealed class Release(NotifierAuthGate owner, bool transition) : IDisposable
    {
        private int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) owner.Leave(transition); }
    }
}
