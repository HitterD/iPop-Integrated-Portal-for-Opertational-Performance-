namespace IPop.Application.Approval;

/// Bumped whenever approver chains are written (import, edit, replace-everywhere), so caches built from them
/// (the Notifier's leaders, NotifierEndpoints) drop at once instead of waiting for their timeout.
public static class ApproverChainVersion
{
    private static long value;
    public static long Current => Interlocked.Read(ref value);
    public static void Bump() => Interlocked.Increment(ref value);
}
