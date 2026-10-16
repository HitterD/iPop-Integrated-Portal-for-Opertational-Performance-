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
        // The conditional aside is a new DOM node after a breakpoint transition.
        if (sidebarJs is not null) { await DisposeSidebarAsync(); sidebarJs = null; }
        if (importJs is not null) await importJs.InvokeVoidAsync("dispose");
        sidebarPhoneMode = Phone.IsPhone;
        if (!Phone.IsPhone)
        {
            sidebarJs = await module.InvokeAsync<IJSObjectReference>("sidebar", sideRef, selfRef);
            if (importJs is not null) await importJs.InvokeVoidAsync("bindDrop", sideRef, new { onlyImportable = true });
        }
    }

    private async Task SyncPhoneNotes()
    {
        var detail = Phone.IsPhone && view != View.Home;
        if (detail != phoneChromeHidden)
        {
            phoneChromeHidden = detail;
            await JS.InvokeVoidAsync("ipop.mobile.hideTabbar", detail);
        }
        if (detail && !phoneDetailPushed)
            phoneDetailPushed = await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", selfRef, nameof(PhoneNotesHistoryBack));
        else if (!detail && phoneDetailPushed)
        {
            phoneDetailPushed = false;
            await JS.InvokeVoidAsync("ipop.mobile.popDetail");
        }
    }

    private async Task PhoneNotesBack()
    {
        if (phoneDetailPushed)
        {
            phoneDetailPushed = false;
            await JS.InvokeVoidAsync("ipop.mobile.popDetail");
        }
        await PhoneNotesHistoryBack();
    }

    [JSInvokable]
    public Task PhoneNotesHistoryBack() => InvokeAsync(() =>
    {
        phoneDetailPushed = false;
        phoneMoreOpen = commentsOpen = false;
        pop = Pop.None;
        Nav.NavigateTo("/notes", replace: true);
    });

    private async Task DisposePhoneNotes()
    {
        Phone.Changed -= PhoneChanged;
        if (phoneDetailPushed) await JS.InvokeVoidAsync("ipop.mobile.clearDetail");
        if (phoneChromeHidden) await JS.InvokeVoidAsync("ipop.mobile.hideTabbar", false);
    }

    private async Task PhoneEditorCommand(string command)
    {
        if (CanEdit && editor is not null) await editor.InvokeVoidAsync("mobileCommand", command);
    }

    private async Task PhoneNoteAction(string action)
    {
        if (note is null) return;
        switch (action)
        {
            case "share" when IsOwner: await OpenShare(); break;
            case "favorite": await ToggleFavorite(); break;
            case "comments": commentsOpen = true; break;
            case "move" when CanEdit:
                if (sidebar?.Pages.FirstOrDefault(p => p.Id == note.Id) is { } item) OpenMoveDialog(item);
                break;
            case "pdf": OpenExport(note.Id, "pdf"); break;
            case "md": OpenExport(note.Id, "md"); break;
            case "html": OpenExport(note.Id, "html"); break;
            case "history": await OpenHistory(); break;
            case "delete" when CanEdit: await TrashPage(); break;
            case "copy": await CopyLink(); break;
            case "style" when CanEdit: pop = Pop.Style; break;
            case "subpage" when CanEdit: await CreateBlank(note.Id); break;
        }
    }
}
