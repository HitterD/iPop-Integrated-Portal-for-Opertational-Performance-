using IPop.Web.Components.Mail;
using Microsoft.AspNetCore.Components;

namespace IPop.Web.Services;

/// The one mail composer of the session. MainLayout renders it, so a half-written message
/// survives leaving /mail (it tucks itself into a "Draft: …" pill). Mail.razor drives it
/// through <see cref="Instance"/> and listens to the events while it is on screen.
public sealed class MailComposerHost(NavigationManager nav)
{
    public MailComposer? Instance { get; set; }

    public event Action<bool>? OpenChanged;
    public event Func<Task>? Sent;
    public event Func<Task>? Discarded;
    public event Action? OpenTemplatesRequested;

    public void RaiseOpenChanged(bool open) => OpenChanged?.Invoke(open);
    public Task RaiseSent() => Sent?.Invoke() ?? Task.CompletedTask;
    public Task RaiseDiscarded() => Discarded?.Invoke() ?? Task.CompletedTask;

    /// Mail settings live on the Mail page; from anywhere else, go there.
    public void RaiseOpenTemplates()
    {
        if (OpenTemplatesRequested is { } handler) handler();
        else nav.NavigateTo("/mail?settings=templates");
    }
}
