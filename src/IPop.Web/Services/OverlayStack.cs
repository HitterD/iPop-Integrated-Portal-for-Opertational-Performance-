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
}
