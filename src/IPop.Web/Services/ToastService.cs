namespace IPop.Web.Services;

public sealed record Toast(
    Guid Id, string Message, ToastKind Kind, DateTime ShownAt,
    string? Title = null, string? Icon = null, string? TargetUrl = null,
    bool IsExiting = false, string? NotificationId = null);

public enum ToastKind { Success, Error, Info }

public sealed class ToastService
{
    private readonly List<Toast> _active = [];
    public IReadOnlyList<Toast> Active => _active;
    public event Action? Changed;

    public void Show(string message, ToastKind kind = ToastKind.Error)
        => ShowDetailed(null, message, kind, null, null);

    /// Toast kaya (Fase 4): judul, ikon Lucide, dan target klik (ruang/surat/tugas).
    public void ShowDetailed(string? title, string message, ToastKind kind = ToastKind.Info,
        string? icon = null, string? targetUrl = null, string? notificationId = null)
    {
        var t = new Toast(Guid.NewGuid(),
            message.Length > 200 ? message[..200] : message,
            kind, DateTime.UtcNow, title, icon, targetUrl, false, notificationId);
        _active.Insert(0, t);
        while (_active.Count > 4) _active.RemoveAt(_active.Count - 1);
        Changed?.Invoke();
        _ = Task.Delay(6000).ContinueWith(_ => Dismiss(t.Id));
    }

    public void Dismiss(Guid id)
    {
        var toast = _active.FirstOrDefault(x => x.Id == id);
        if (toast is null) return;

        // Trigger exit animation first
        var index = _active.IndexOf(toast);
        _active[index] = toast with { IsExiting = true };
        Changed?.Invoke();

        // Remove after animation completes
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            var removed = _active.RemoveAll(x => x.Id == id) > 0;
            if (removed) Changed?.Invoke();
        });
    }

    public void DismissImmediate(Guid id)
    {
        var removed = _active.RemoveAll(x => x.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }
}
