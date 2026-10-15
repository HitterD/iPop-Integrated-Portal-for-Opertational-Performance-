using Microsoft.JSInterop;

namespace IPop.Web.Services;

public sealed record OverlayLayer(string Id, string Kind, object? TriggerRef);

/// Overlays that close on Escape (MainLayout.OnEscape → CloseTopAsync) and, on phones, on the
/// system Back button: each closer layer also gets a history entry through ipop.mobile.overlayOpen,
/// and popstate calls back into <see cref="OnBack"/> to close it. Desktop adds no history entries.
public sealed class OverlayStack
{
    private readonly List<OverlayLayer> _stack = [];
    private IJSRuntime? _js;
    private DotNetObjectReference<OverlayStack>? _self;

    public IReadOnlyList<OverlayLayer> Stack => _stack;
    public OverlayLayer? Top => _stack.Count > 0 ? _stack[^1] : null;
    public event Action? Changed;
    /// Raised after a layer was closed by the phone Back button, so the layout can re-render.
    public event Action? ClosedByBack;

    /// MainLayout wires the browser once it has rendered; without it the stack is Escape-only (tests).
    public void AttachBrowser(IJSRuntime js) { _js = js; _self ??= DotNetObjectReference.Create(this); }

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
    public IDisposable Push(Func<Task> close) => Push(close, history: true);

    /// history:false = Escape only (the page already gives this overlay its own phone Back entry).
    public IDisposable Push(Func<Task> close, bool history)
    {
        var layer = new OverlayLayer(Guid.NewGuid().ToString("N"), "closer", close);
        Push(layer);
        if (history) Browser("ipop.mobile.overlayOpen", _self, layer.Id);
        return new Registration(this, layer.Id);
    }

    /// Closes the top-most layer if it was pushed with a close action. False when nothing
    /// closable was open, so the caller can fall through to page-level Escape handling.
    public async Task<bool> CloseTopAsync()
    {
        if (Top?.TriggerRef is not Func<Task> close) return false;
        var top = PopTop()!;
        Browser("ipop.mobile.overlayClose", top.Id);
        await close();
        return true;
    }

    /// Phone Back popped the top layer's history entry: close it without touching history again.
    [JSInvokable]
    public async Task OnBack()
    {
        if (Top?.TriggerRef is not Func<Task> close) return;
        PopTop();
        await close();
        ClosedByBack?.Invoke();
    }

    public bool Any => _stack.Count > 0;

    private void Remove(string id)
    {
        if (Pop(id)) Browser("ipop.mobile.overlayClose", id);
    }

    private void Browser(string fn, params object?[] args)
    {
        if (_js is null || _self is null) return;
        try { _ = _js.InvokeVoidAsync(fn, args).AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default); }
        catch (Exception) { /* disconnected */ }
    }

    private sealed class Registration(OverlayStack stack, string id) : IDisposable
    {
        public void Dispose() => stack.Remove(id);
    }
}
