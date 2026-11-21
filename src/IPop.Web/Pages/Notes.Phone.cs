using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notes
{
    private bool phoneMoreOpen, phoneDetailPushed, phoneChromeHidden, phoneMentionOpen;
    [JSInvokable] public Task OnPhoneMention(bool open) => InvokeAsync(() => { phoneMentionOpen = Phone.IsPhone && open; StateHasChanged(); });
    private async Task ClosePhoneMention()
    {
        phoneMentionOpen = false;
        if (editor is not null) await editor.InvokeVoidAsync("closeMobileMention");
    }

    private bool? sidebarPhoneMode;
    private void PhoneChanged() => _ = InvokeAsync(StateHasChanged);

    private async Task ReconcilePhoneSidebar()
    {
        if (sidebarPhoneMode == Phone.IsPhone || module is null) return;
        sidebarPhoneMode = Phone.IsPhone;
        // The conditional aside is a new DOM node after a breakpoint transition.
        if (sidebarJs is not null) { await DisposeSidebarAsync(); sidebarJs = null; }
        if (importJs is not null) await TryJsAsync(() => importJs.InvokeVoidAsync("dispose").AsTask());
        if (disposed || Phone.IsPhone) return;
        try
        {
            sidebarJs = await module.InvokeAsync<IJSObjectReference>("sidebar", sideRef, selfRef);
            if (!disposed && importJs is not null) await importJs.InvokeVoidAsync("bindDrop", sideRef, new { onlyImportable = true });
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
    }

    private async Task SyncPhoneNotes()
    {
        if (disposed) return;
        var detail = Phone.IsPhone && view != View.Home;
        if (detail != phoneChromeHidden)
        {
            phoneChromeHidden = detail;
            await TryJsAsync(() => JS.InvokeVoidAsync("ipop.mobile.hideTabbar", detail).AsTask());
        }
        if (disposed) return;
        if (detail && !phoneDetailPushed)
        {
            try { phoneDetailPushed = await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", selfRef, nameof(PhoneNotesHistoryBack)); }
            catch (JSException) { }
            catch (JSDisconnectedException) { }
        }
        else if (!detail && phoneDetailPushed)
        {
            phoneDetailPushed = false;
            await TryJsAsync(() => JS.InvokeVoidAsync("ipop.mobile.popDetail").AsTask());
        }
    }

    private async Task PhoneNotesBack()
    {
        try
        {
            if (phoneDetailPushed && await JS.InvokeAsync<bool>("ipop.mobile.backDetail")) return;
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
        if (phoneDetailPushed)
        {
            phoneDetailPushed = false;
            await TryJsAsync(() => JS.InvokeVoidAsync("ipop.mobile.clearDetail").AsTask());
        }
        await PhoneNotesHistoryBack();
    }

    [JSInvokable]
    public Task PhoneNotesHistoryBack() => InvokeAsync(() =>
    {
        if (disposed) return;
        phoneDetailPushed = false;
        phoneMoreOpen = commentsOpen = phoneMentionOpen = sideOpen = false;
        pop = Pop.None;
        Nav.NavigateTo("/notes", replace: true);
    });

    private async Task DisposePhoneNotes()
    {
        Phone.Changed -= PhoneChanged;
        if (phoneDetailPushed) { phoneDetailPushed = false; await TryJsAsync(() => JS.InvokeVoidAsync("ipop.mobile.clearDetail").AsTask()); }
        if (phoneChromeHidden) { phoneChromeHidden = false; await TryJsAsync(() => JS.InvokeVoidAsync("ipop.mobile.hideTabbar", false).AsTask()); }
    }

    private async Task PhoneEditorCommand(string command)
    {
        if (CanEdit && editor is not null) await TryJsAsync(() => editor.InvokeVoidAsync("mobileCommand", command).AsTask());
    }

    private async Task PhoneNoteAction(string action)
    {
        if (note is null) return;
        switch (action)
        {
            case "share" when IsOwner: await OpenShare(); break;
            case "favorite": await ToggleFavorite(); break;
            case "comments": commentsOpen = true; break;
            case "move" when IsOwner && !IsLocked:
                if (sidebar?.Pages.FirstOrDefault(p => p.Id == note.Id) is { } item) OpenMoveDialog(item);
                break;
            case "pdf": OpenExport(note.Id, "pdf"); break;
            case "md": OpenExport(note.Id, "md"); break;
            case "html": OpenExport(note.Id, "html"); break;
            case "history": await OpenHistory(); break;
            case "delete" when CanEdit: await TrashPage(); break;
            case "copy": await CopyLink(); break;
            case "style" when CanEdit && note.Kind != "board": pop = Pop.Style; break;
            case "subpage" when CanEdit: await CreateBlank(note.Id); break;
        }
    }
}
