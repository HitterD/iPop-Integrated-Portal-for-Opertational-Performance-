using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// Phone (BIGIS 12): swipe a message right = reply, left = Message info (chat-swipe.js), plus a
/// one-time tip. Long-press keeps opening the actions sheet (Chat.Phone.cs).
public partial class Chat
{
    private const string SwipeTipKey = "ipop.chat.swipeTip";
    private Guid? phoneInfoMsgId;
    private string? phoneSwipeBoundId;
    private bool swipeTipVisible;
    private bool swipeTipChecked;

    [JSInvokable]
    public Task OnPhoneSwipeReply(string id) => InvokeAsync(() =>
    {
        if (!Guid.TryParse(id, out var msgId) || !CanSwipe(msgId)) return;
        StartReply(msgId);
        DismissSwipeTip();
        StateHasChanged();
    });

    [JSInvokable]
    public Task OnPhoneSwipeInfo(string id) => InvokeAsync(() =>
    {
        if (!Guid.TryParse(id, out var msgId) || !CanSwipe(msgId)) return;
        mToolsMsgId = null;
        phoneActsMsgId = null;
        phoneInfoMsgId = msgId;
        DismissSwipeTip();
        StateHasChanged();
    });

    private bool CanSwipe(Guid msgId) =>
        ChatSvc.ActiveMessages.FirstOrDefault(x => x.Id == msgId) is { } m
        && m.Type is not ("system" or "emergency") && !m.IsDeleted && editingMsgId != msgId;

    /// After render: bind the swipe once per messages element and decide whether to show the tip.
    private async Task BindPhoneSwipeAsync()
    {
        if (!Phone.IsPhone || messagesRef.Id is not { } id || id == phoneSwipeBoundId) return;
        selfRef ??= DotNetObjectReference.Create(this);
        try
        {
            await JS.InvokeVoidAsync("ipop.chatSwipe.bind", messagesRef, "[data-lp]", selfRef,
                nameof(OnPhoneSwipeReply), nameof(OnPhoneSwipeInfo));
            phoneSwipeBoundId = id;
        }
        catch { return; /* older cached script: long-press still works */ }
        if (swipeTipChecked) return;
        swipeTipChecked = true;
        try
        {
            swipeTipVisible = await JS.InvokeAsync<string?>("localStorage.getItem", SwipeTipKey) is null;
            if (swipeTipVisible) StateHasChanged();
        }
        catch { }
    }

    private void DismissSwipeTip()
    {
        if (!swipeTipVisible) return;
        swipeTipVisible = false;
        _ = JS.InvokeVoidAsync("localStorage.setItem", SwipeTipKey, "1").AsTask().ContinueWith(_ => { });
    }
}
