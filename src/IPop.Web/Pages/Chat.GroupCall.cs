using IPop.Application.Calls;
using IPop.Web.Services;

namespace IPop.Web.Pages;

/// Group calls from a chat group: header/drawer phone buttons and the live-call banner
/// ("Panggilan grup sedang berlangsung · N orang · Gabung"), kept current by hub broadcasts.
public partial class Chat
{
    private bool groupCallBusy;
    private bool groupCallSubscribed;

    private void EnsureGroupCallSubscription()
    {
        if (groupCallSubscribed) return;
        groupCallSubscribed = true;
        CallStateSvc.GroupCallChanged += OnGroupCallChanged;
        CallStateSvc.StateChanged += OnCallStateChangedForBanner;
    }

    private void UnsubscribeGroupCall()
    {
        if (!groupCallSubscribed) return;
        groupCallSubscribed = false;
        CallStateSvc.GroupCallChanged -= OnGroupCallChanged;
        CallStateSvc.StateChanged -= OnCallStateChangedForBanner;
    }

    private void OnGroupCallChanged(Guid chatRoomId)
    {
        if (ChatSvc.ActiveRoom?.Id == chatRoomId) _ = InvokeAsync(StateHasChanged);
    }

    private void OnCallStateChangedForBanner()
    {
        if (ChatSvc.ActiveRoom?.Type == "group") _ = InvokeAsync(StateHasChanged);
    }

    /// Called after a room opens: learn whether the group already has a live call.
    private async Task LoadGroupCallAsync(Guid roomId)
    {
        EnsureGroupCallSubscription();
        if (ChatSvc.ActiveRoom?.Type != "group") return;
        await CallStateSvc.FetchGroupCallAsync(roomId);
        if (ChatSvc.ActiveRoomId == roomId) await InvokeAsync(StateHasChanged);
    }

    /// The live call of the open group — null when none, or when I'm already in it.
    private CallRoomDto? ActiveGroupCallBanner
    {
        get
        {
            if (ChatSvc.ActiveRoom is not { Type: "group" } room) return null;
            var call = CallStateSvc.GroupCallFor(room.Id);
            if (call is null) return null;
            if (CallStateSvc.IsInGroupCall && CallStateSvc.Current?.Id == call.Id) return null;
            return call;
        }
    }

    private async Task StartGroupCall()
    {
        if (ChatSvc.ActiveRoom is not { Type: "group" } room || groupCallBusy) return;
        groupCallBusy = true;
        try
        {
            var call = await CallStateSvc.StartGroupAsync(room.Id);
            if (call is null)
                Toasts.Show(CallStateSvc.LastError ?? "Couldn't start the group call.", ToastKind.Error);
        }
        finally { groupCallBusy = false; }
    }

    private async Task JoinGroupCall(Guid callId)
    {
        if (groupCallBusy) return;
        groupCallBusy = true;
        try
        {
            var call = await CallStateSvc.JoinGroupAsync(callId);
            if (call is null)
                Toasts.Show(CallStateSvc.LastError ?? "Couldn't join the group call.", ToastKind.Error);
        }
        finally { groupCallBusy = false; }
    }
}
