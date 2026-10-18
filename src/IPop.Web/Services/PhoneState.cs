using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// True while the phone layout applies (PHONE_QUERY in js/mobile.js: narrower than 768 px, or a
/// short touch landscape phone). Desktop-first: false until the browser answers, so a desktop
/// never renders phone markup, not even for one frame. Components that render different DOM on
/// phones read IsPhone and re-render on Changed (rotate / resize crosses the breakpoint).
public sealed class PhoneState(IJSRuntime js) : IDisposable
{
    private DotNetObjectReference<PhoneState>? _self;
    private Task? _start;
    public bool IsInitialized { get; private set; }

    public bool IsPhone { get; private set; }
    public event Action? Changed;

    /// Reads the query once and subscribes to its changes (ipop.mobile.watch).
    public Task StartAsync() => _start ??= DetectAsync();

    private async Task DetectAsync()
    {
        _self = DotNetObjectReference.Create(this);
        bool phone;
        try { phone = await js.InvokeAsync<bool>("ipop.mobile.watch", _self); }
        catch { phone = false; }
        OnPhoneChanged(phone);
    }

    [JSInvokable]
    public void OnPhoneChanged(bool isPhone)
    {
        IsInitialized = true;
        if (IsPhone == isPhone) return;
        IsPhone = isPhone;
        Changed?.Invoke();
    }

    public void Dispose() => _self?.Dispose();
}
