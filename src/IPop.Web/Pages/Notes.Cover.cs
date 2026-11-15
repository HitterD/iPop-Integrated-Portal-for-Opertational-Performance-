using IPop.Application.Notes;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

/// Photo covers: upload, pick an image already in the page, drag to reposition. The picture itself is painted by
/// covers.js (the file is bearer-protected, so it is fetched with the token and shown from an object URL).
public partial class Notes
{
    private enum CoverTab { Colours, Upload, ThisPage }

    private static readonly (string Key, string Label)[] BoardTemplates = [("retro", "Retro"), ("flow", "Flowchart"), ("brainstorm", "Brainstorm")];

    private async Task ApplyBoardTemplateAsync(string key)
    {
        if (editor is null || !CanEdit) return;
        var n = await editor.InvokeAsync<int>("boardTemplate", key);
        if (n == 0) Toasts.Show("The whiteboard is still loading. Try again in a moment.", ToastKind.Info);
    }

    private IJSObjectReference? coverJs;
    private ElementReference coverRef;
    private CoverTab coverTab = CoverTab.Colours;
    private bool coverUploading, coverRepositioning;
    private int coverProgress;
    private string? coverError;
    private int coverDraftPos = 50;
    private int coverSavedPos = 50;
    private List<NoteImageDto>? pageImages;
    private Guid? paintedCover;
    private int paintedPos = -1;
    private bool coverPaintPending;

    private bool HasCover => note is not null && (note.Cover is not null || note.CoverImageId is not null);
    private bool HasPhotoCover => note?.CoverImageId is not null;

    /// Gradient presets use the CSS variable; a photo sets --img / --y from JS.
    private string CoverStyle => note?.CoverImageId is not null ? $"--y:{note.CoverPosY}%" : $"--cover:{CoverCss(note?.Cover)}";

    private async Task<IJSObjectReference> CoverModuleAsync()
    {
        if (coverJs is not null) return coverJs;
        coverJs = await module!.InvokeAsync<IJSObjectReference>("covers", new { apiBase = Http.BaseAddress?.ToString() ?? "", dotnet = selfRef });
        return coverJs;
    }

    /// Called after every render: paints the photo once per cover change.
    private async Task CoverAfterRenderAsync()
    {
        if (module is null || note is null || view != View.Page) return;
        if (note.CoverImageId is not { } id) { paintedCover = null; paintedPos = -1; return; }
        if (coverRepositioning) return;   // the drag owns --y while it is active
        if (paintedCover == id && paintedPos == note.CoverPosY && !coverPaintPending) return;
        paintedCover = id; paintedPos = note.CoverPosY; coverPaintPending = false;
        try
        {
            var js = await CoverModuleAsync();
            var ok = await js.InvokeAsync<bool>("paint", coverRef, id, note.CoverPosY);
            if (!ok) Toasts.Show("Couldn't load the cover image.", ToastKind.Warning);
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
    }

    private async Task DisposeCoverAsync()
    {
        if (coverJs is null) return;
        try { await coverJs.InvokeVoidAsync("dispose"); await coverJs.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        coverJs = null; paintedCover = null; paintedPos = -1;
    }

    private async Task SetCoverTab(CoverTab tab)
    {
        coverTab = tab;
        coverError = null;
        if (tab == CoverTab.ThisPage && note is not null)
        {
            pageImages = (await Auth.GetAsync<List<NoteImageDto>>($"/api/notes/{note.Id}/images")) ?? [];
            StateHasChanged();
            if (pageImages.Count > 0)
            {
                await Task.Yield();
                try { await (await CoverModuleAsync()).InvokeVoidAsync("thumbs", coverThumbsRef, pageImages.Select(i => i.Id)); }
                catch (JSException) { }
            }
        }
    }

    private ElementReference coverThumbsRef;

    private async Task ChooseCoverFile()
    {
        if (note is null || !CanEdit || coverUploading) return;
        var js = await CoverModuleAsync();
        // The file never passes through Blazor: the browser picks it and covers.js uploads it with progress.
        await js.InvokeAsync<bool>("pickAndUpload", note.Id);
    }

    [JSInvokable]
    public void OnCoverProgress(int percent)
    {
        coverProgress = percent;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnCoverDragged(int pos) => coverDraftPos = pos;

    /// Starts/ends an upload from JS (so the progress bar and the cover band reflect it immediately).
    [JSInvokable]
    public void OnCoverUploadState(bool uploading, string? error)
    {
        coverUploading = uploading;
        coverError = error;
        if (uploading) { coverProgress = 0; pop = Pop.None; }
        else if (error is not null)
        {
            // The picker closed when the upload started: bring it back on the Upload tab with the reason, and toast it too
            // so the failure is never silent.
            pop = Pop.Style; coverTab = CoverTab.Upload;
            Toasts.Show(error, ToastKind.Warning);
        }
        StateHasChanged();
    }

    /// The server already set the new photo as the cover and broadcast the page; reload it so this tab shows it at once.
    [JSInvokable]
    public async Task OnCoverUploaded()
    {
        if (note is null) return;
        var fresh = await Auth.GetAsync<NotePageDto>($"/api/notes/{note.Id}");
        if (fresh is null || note?.Id != fresh.Id) return;
        note = fresh with { IsFavorite = note.IsFavorite };
        coverRepositioning = false;
        coverPaintPending = true;
        StateHasChanged();
        await StartRepositionAsync();   // a fresh photo opens straight in reposition mode, like the mockup
    }

    private async Task PickCoverImage(Guid attachmentId)
    {
        if (note is null || !CanEdit) return;
        await PatchAsync(new UpdateNoteMetaRequest(null, null, null, null, null, null, null, attachmentId));
        pop = Pop.None;
        coverRepositioning = false;
        coverPaintPending = true;
    }

    private async Task StartRepositionAsync()
    {
        if (note?.CoverImageId is not { } id || !CanEdit) return;
        pop = Pop.None;
        coverRepositioning = true;
        coverSavedPos = coverDraftPos = note.CoverPosY;
        StateHasChanged();
        await Task.Yield();
        try { await (await CoverModuleAsync()).InvokeVoidAsync("startReposition", coverRef, id, note.CoverPosY); }
        catch (JSException) { }
    }

    private async Task SaveRepositionAsync()
    {
        await StopRepositionAsync();
        await PatchAsync(new UpdateNoteMetaRequest(null, null, null, null, null, null, null, null, coverDraftPos));
    }

    private async Task CancelRepositionAsync()
    {
        await StopRepositionAsync();
        if (coverJs is not null && note?.CoverImageId is { } id)
            try { await coverJs.InvokeAsync<bool>("paint", coverRef, id, coverSavedPos); } catch (JSException) { }
    }

    private async Task StopRepositionAsync()
    {
        coverRepositioning = false;
        if (coverJs is not null) try { await coverJs.InvokeVoidAsync("stopReposition"); } catch (JSException) { }
    }

    private async Task RemoveCoverAsync()
    {
        pop = Pop.None;
        await StopRepositionAsync();
        await PatchAsync(new UpdateNoteMetaRequest(null, null, "", null, null, null, null));
    }

    private async Task PickGradient(string key)
    {
        await StopRepositionAsync();
        await PatchAsync(new UpdateNoteMetaRequest(null, null, key, null, null, null, null));
    }
}
