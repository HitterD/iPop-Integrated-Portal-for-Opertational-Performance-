using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// Wave 2 phone chat (mockup v2 CH1–CH3). State lives here in the page; the Components/Phone/Chat
/// sheets are presentational. Nothing here subscribes to hubs.
public partial class Chat
{
    private Guid? phoneActsMsgId;
    private InputFile? phoneGalleryInput, phoneCameraInput;
    private string? phoneLpBoundId;
    private bool phoneSubscribed;

    private enum PhonePickKind { Gallery, Camera, Document }

    /// CH2 tiles: the same upload path as "File or photo", with a narrower picker.
    private async Task PhonePick(PhonePickKind kind)
    {
        plusMenuOpen = false;
        if (kind == PhonePickKind.Document) { await TriggerFileUpload(); return; }
        if (uploading) { uploadError = "Wait for the current upload to finish."; return; }
        if (pendingAttachments.Count >= IPop.Web.Services.ChatUploadPolicy.MaxFiles) { uploadError = "You can attach up to 5 files per message."; return; }
        var input = kind == PhonePickKind.Camera ? phoneCameraInput : phoneGalleryInput;
        if (input?.Element is { } el) await JS.InvokeVoidAsync("ipop.clickFileInput", el);
    }

    /// CH1 Call: a DM rings the other person, a group starts the group call.
    private Task PhoneInfoCall()
    {
        if (ChatSvc.ActiveRoom is null) return Task.CompletedTask;
        CloseDetail();
        if (ChatSvc.ActiveRoom.Type == "group") return StartGroupCall();
        var other = ChatSvc.ActiveRoom.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik());
        return other is null ? Task.CompletedTask : StartCallWith(other.Nik);
    }

    /// CH3: ph-kit.js long-press on a bubble (450 ms, no movement).
    [JSInvokable]
    public Task OnPhoneLongPress(string id) => InvokeAsync(() =>
    {
        if (!Guid.TryParse(id, out var msgId)) return;
        var m = ChatSvc.ActiveMessages.FirstOrDefault(x => x.Id == msgId);
        if (m is null || m.Type is "system" or "emergency") return;
        mToolsMsgId = null;
        ctxMsgId = null;
        phoneActsMsgId = msgId;
        StateHasChanged();
    });

    private void SubscribePhone()
    {
        if (phoneSubscribed) return;
        phoneSubscribed = true;
        Phone.Changed += OnPhoneChanged;
    }

    private void OnPhoneChanged() => _ = InvokeAsync(() =>
    {
        phoneActsMsgId = null;
        phoneLpBoundId = null;
        StateHasChanged();
    });

    /// After render: bind the long-press once per messages element (it is replaced on room switch).
    private async Task BindPhoneLongPressAsync()
    {
        SubscribePhone();
        if (!Phone.IsPhone || messagesRef.Id is not { } id || id == phoneLpBoundId) return;
        selfRef ??= DotNetObjectReference.Create(this);
        try
        {
            await JS.InvokeVoidAsync("ipop.phone.longPress", messagesRef, "[data-lp]", selfRef, nameof(OnPhoneLongPress));
            phoneLpBoundId = id;
        }
        catch { /* older cached ph-kit.js: the tap toolbar still works */ }
    }

    private void DisposePhone()
    {
        if (phoneSubscribed) Phone.Changed -= OnPhoneChanged;
    }
}
