namespace IPop.Web.Services;

public record ConfirmRequest(string Title, string Body, string ConfirmLabel, bool Danger);

/// Single-slot confirm dialog. Esc / cancel / click-away all resolve false.
public class ConfirmService
{
    private TaskCompletionSource<bool>? _tcs;
    public ConfirmRequest? Current { get; private set; }
    public event Action? Changed;

    public Task<bool> AskAsync(string title, string body, string confirmLabel = "Delete", bool danger = true)
    {
        // A second ask replaces the first; the first resolves false so no caller hangs.
        _tcs?.TrySetResult(false);
        _tcs = new TaskCompletionSource<bool>();
        Current = new ConfirmRequest(title, body, confirmLabel, danger);
        Changed?.Invoke();
        return _tcs.Task;
    }

    public void Resolve(bool ok)
    {
        var tcs = _tcs;
        _tcs = null;
        Current = null;
        Changed?.Invoke();
        tcs?.TrySetResult(ok);
    }
}
