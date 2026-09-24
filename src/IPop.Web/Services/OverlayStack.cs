namespace IPop.Web.Services;

public sealed record OverlayLayer(string Id, string Kind, object? TriggerRef);

public sealed class OverlayStack
{
    private readonly List<OverlayLayer> _stack = [];
    public IReadOnlyList<OverlayLayer> Stack => _stack;
    public OverlayLayer? Top => _stack.Count > 0 ? _stack[^1] : null;
    public event Action? Changed;

    public void Push(OverlayLayer layer)
    {
        _stack.Add(layer);
        Changed?.Invoke();
    }

    public bool Pop(string id)
    {
        var idx = _stack.FindIndex(x => x.Id == id);
        if (idx < 0) return false;
        _stack.RemoveAt(idx);
        Changed?.Invoke();
        return true;
    }

    public OverlayLayer? PopTop()
    {
        if (_stack.Count == 0) return null;
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        Changed?.Invoke();
        return top;
    }

    public void Clear() { if (_stack.Count > 0) { _stack.Clear(); Changed?.Invoke(); } }

    /// Closer-style registration used by Modal, ContextMenu and CommandPalette: the layer
    /// carries its close action, so Escape can close whatever is on top. Dispose removes it.
    public IDisposable Push(Func<Task> close)
    {
        var layer = new OverlayLayer(Guid.NewGuid().ToString("N"), "closer", close);
        Push(layer);
        return new Registration(this, layer.Id);
    }

    /// Closes the top-most layer if it was pushed with a close action. False when nothing
    /// closable was open, so the caller can fall through to page-level Escape handling.
    public async Task<bool> CloseTopAsync()
    {
        if (Top?.TriggerRef is not Func<Task> close) return false;
        PopTop();
        await close();
        return true;
    }

    public bool Any => _stack.Count > 0;

    private sealed class Registration(OverlayStack stack, string id) : IDisposable
    {
        public void Dispose() => stack.Pop(id);
    }
}
