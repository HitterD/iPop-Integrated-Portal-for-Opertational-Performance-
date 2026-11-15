namespace IPop.Infrastructure.Auth;

/// Caps how many Argon2 hashes run at once. Each one allocates 64 MB and keeps two cores busy, so a burst of logins
/// (everyone signing in at 08:00, or every Notifier PC after a forced sign-out) used to push the API past its memory
/// limit: the k3s load test hit OutOfMemoryException inside Argon2 at ~30 concurrent logins and took every other
/// request down with it. Waiting callers queue (async, no thread blocked); past the wait limit they get "busy".
public sealed class HashGate(int concurrency, TimeSpan maxWait)
{
    private readonly SemaphoreSlim slots = new(concurrency, concurrency);

    /// Half the cores (min 2): 3 slots on a 6-core node = at most ~192 MB in Argon2 at any moment.
    public static HashGate Shared { get; } = new(Math.Max(2, Environment.ProcessorCount / 2), TimeSpan.FromSeconds(20));

    public int Concurrency { get; } = concurrency;

    /// Runs the CPU-bound hash on the thread pool once a slot is free. Returns (false, default) when no slot freed
    /// up within the wait limit — callers answer 503 "busy, try again" instead of piling up more memory.
    public async Task<(bool Ran, T? Result)> RunAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        if (!await slots.WaitAsync(maxWait, ct)) return (false, default);
        try { return (true, await Task.Run(work, ct)); }
        finally { slots.Release(); }
    }
}
