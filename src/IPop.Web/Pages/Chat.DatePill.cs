using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// Floating date while scrolling (BIGIS 12, owner revision: it must not cover a bubble). The pill
/// sits in a strip the list fades out under, and hides when scrolling stops (chat-datepill.js).
public partial class Chat
{
    private ElementReference datePillRef;
    private string? datePillBoundId;

    private async Task BindDatePillAsync()
    {
        if (messagesRef.Id is not { } id || id == datePillBoundId) return;
        try
        {
            await JS.InvokeVoidAsync("ipop.chatDatePill.bind", messagesRef, datePillRef);
            datePillBoundId = id;
        }
        catch { /* older cached script: separators still show in the list */ }
    }
}
