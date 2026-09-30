namespace IPop.Web.Services;

public sealed record Toast(
    Guid Id, string Message, ToastKind Kind, DateTime ShownAt,
    string? Title = null, string? Icon = null, string? TargetUrl = null,
    bool IsExiting = false, string? NotificationId = null,
    Func<Task>? Undo = null, string? UndoLabel = null,
    bool IsPaused = false)
{
    /// Incoming notifications follow the user's notification preferences; action feedback always shows.
    public bool IsIncoming => !string.IsNullOrEmpty(NotificationId);
}

/// Warning = the user must fix their input (validation, limits); Error = the action failed.
public enum ToastKind { Success, Error, Info, Warning }

public sealed class ToastService
{
    public const int MaxVisible = 3;
    private const int ExitMs = 260;
    // After hover/focus leaves, give the reader a moment even if the timer had almost run out.
    private static readonly TimeSpan MinResume = TimeSpan.FromMilliseconds(1500);

    private readonly List<Toast> _active = [];
    private readonly Dictionary<Guid, Timer> _timers = [];
    private readonly Func<ToastKind, TimeSpan> _duration;

    public IReadOnlyList<Toast> Active => _active;
    public event Action? Changed;

    public ToastService() : this(DefaultDuration) { }

    internal ToastService(Func<ToastKind, TimeSpan> duration) => _duration = duration;

    /// Errors stay longest so they can be read; quick confirmations get out of the way.
    public static TimeSpan DefaultDuration(ToastKind kind) => kind switch
    {
        ToastKind.Success => TimeSpan.FromSeconds(4),
        ToastKind.Error => TimeSpan.FromSeconds(8),
        ToastKind.Warning => TimeSpan.FromSeconds(6),
        _ => TimeSpan.FromSeconds(5),
    };

    // No default kind: an implicit Error once painted every success message with the failure icon.
    public void Show(string message, ToastKind kind)
        => ShowDetailed(null, message, kind, null, null);

    /// Toast kaya (Fase 4): judul, ikon Lucide, dan target klik (ruang/surat/tugas).
    public void ShowDetailed(string? title, string message, ToastKind kind = ToastKind.Info,
        string? icon = null, string? targetUrl = null, string? notificationId = null)
    {
        var t = new Toast(Guid.NewGuid(),
            message.Length > 200 ? message[..200] : message,
            kind, DateTime.UtcNow, title, icon, targetUrl, false, notificationId);
        Add(t, _duration(kind));
    }

    /// v6 undo toast: shown for the undo window, and the action runs only if the user
    /// clicks Undo before it disappears. Returns the toast id.
    public Guid ShowUndo(string message, Func<Task> undo, string undoLabel = "Undo", int ms = 5000)
    {
        var t = new Toast(Guid.NewGuid(), message, ToastKind.Info, DateTime.UtcNow, Undo: undo, UndoLabel: undoLabel);
        Add(t, TimeSpan.FromMilliseconds(ms));
        return t.Id;
    }

    public async Task RunUndoAsync(Guid id)
    {
        var undo = _active.FirstOrDefault(x => x.Id == id)?.Undo;
        DismissImmediate(id);
        if (undo is not null) await undo();
    }

    /// Hover or keyboard focus holds the toast open until the pointer/focus leaves.
    public void Pause(Guid id)
    {
        var i = _active.FindIndex(x => x.Id == id);
        if (i < 0 || _active[i].IsPaused || _active[i].IsExiting) return;
        if (_timers.TryGetValue(id, out var timer)) timer.Pause();
        _active[i] = _active[i] with { IsPaused = true };
        Changed?.Invoke();
    }

    public void Resume(Guid id)
    {
        var i = _active.FindIndex(x => x.Id == id);
        if (i < 0 || !_active[i].IsPaused) return;
        _active[i] = _active[i] with { IsPaused = false };
        if (_timers.TryGetValue(id, out var timer)) timer.Start(Max(timer.Remaining, MinResume));
        Changed?.Invoke();
    }

    public void Dismiss(Guid id)
    {
        var i = _active.FindIndex(x => x.Id == id);
        if (i < 0 || _active[i].IsExiting) return;
        StopTimer(id);

        // Play the exit animation first, then remove.
        _active[i] = _active[i] with { IsExiting = true, IsPaused = false };
        Changed?.Invoke();
        _ = Task.Delay(ExitMs).ContinueWith(_ =>
        {
            var removed = _active.RemoveAll(x => x.Id == id) > 0;
            if (removed) Changed?.Invoke();
        });
    }

    public void DismissImmediate(Guid id)
    {
        StopTimer(id);
        var removed = _active.RemoveAll(x => x.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }

    private void Add(Toast t, TimeSpan duration)
    {
        // Repeating the same message replaces the old card (fresh timer) instead of stacking copies.
        if (t.Undo is null && !t.IsIncoming)
        {
            foreach (var dup in _active.Where(x => !x.IsExiting && x.Undo is null && !x.IsIncoming
                         && x.Kind == t.Kind && x.Title == t.Title && x.Message == t.Message).ToList())
            {
                StopTimer(dup.Id);
                _active.Remove(dup);
            }
        }
        _active.Insert(0, t);
        while (_active.Count(x => !x.IsExiting) > MaxVisible)
        {
            var oldest = _active.Last(x => !x.IsExiting);
            StopTimer(oldest.Id);
            _active.Remove(oldest);
        }
        var timer = new Timer(() => Dismiss(t.Id));
        _timers[t.Id] = timer;
        timer.Start(duration);
        Changed?.Invoke();
    }

    private void StopTimer(Guid id)
    {
        if (_timers.Remove(id, out var timer)) timer.Pause();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// Pausable countdown: remembers how much time was left when paused.
    private sealed class Timer(Action elapsed)
    {
        private CancellationTokenSource? _cts;
        private DateTime _startedAt;
        public TimeSpan Remaining { get; private set; }

        public void Start(TimeSpan duration)
        {
            _cts?.Cancel();
            Remaining = duration;
            _startedAt = DateTime.UtcNow;
            var cts = _cts = new CancellationTokenSource();
            _ = Task.Delay(duration, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) elapsed();
            }, TaskScheduler.Default);
        }

        public void Pause()
        {
            if (_cts is null) return;
            _cts.Cancel();
            _cts = null;
            var left = Remaining - (DateTime.UtcNow - _startedAt);
            Remaining = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}
