// Files page — preview, open in Explorer, print, download and upload.
//
// Part of the Files partial class. The class was split by responsibility out of a single
// ~1,300-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Application.Files;
using IPop.Web.Components;
using IPop.Web.Layout;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Files
{
    private bool isSheetPreview => previewItem is not null && IsSheetPreviewable(previewItem.Name);

    private async Task Preview(SynoItemDto it)
    {
        previewItem = it;
        previewError = null;
        previewUrl = null;
        previewMime = GetMimeType(it.Name);
        previewLoading = true;
        previewSheetBytes = null;
        showPreview = true;
        mMenuPath = null;
        StateHasChanged();
        // #40: in icon view a photo grows out of the tile it was opened from.
        if (gridView && fx is not null && previewMime?.StartsWith("image/") == true)
            try { await fx.InvokeVoidAsync("zoomPreview", server + ":" + it.Path); } catch (JSException) { }
        mRef ??= DotNetObjectReference.Create(this);
        try { mPreviewPushed = await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", mRef, nameof(CloseMobilePreview)); } catch (JSException) { }
        if (IsSheetPreviewable(it.Name))
        {
            try
            {
                var res2 = await Auth.SendAsync(HttpMethod.Get, $"/api/files/download?server={server}&path={Uri.EscapeDataString(it.Path)}");
                if (!res2.IsSuccessStatusCode) { previewError = "Could not load preview."; return; }
                previewSheetBytes = await res2.Content.ReadAsByteArrayAsync();
                previewLoading = false;
                StateHasChanged(); await JS.InvokeVoidAsync("ipop.icons");
                return;
            }
            catch { previewError = "Preview failed."; }
            finally { previewLoading = false; StateHasChanged(); await JS.InvokeVoidAsync("ipop.icons"); }
            return;
        }
        if (GetFileExt(it.Name) == ".xls")
        {
            // Legacy binary Excel - rendered client-side by SheetJS (ipop.renderOffice).
            try
            {
                var res2 = await Auth.SendAsync(HttpMethod.Get, $"/api/files/download?server={server}&path={Uri.EscapeDataString(it.Path)}");
                if (!res2.IsSuccessStatusCode) { previewError = "Could not load preview."; return; }
                var bytes2 = await res2.Content.ReadAsByteArrayAsync();
                var b642 = Convert.ToBase64String(bytes2);
                previewLoading = false;
                StateHasChanged();
                await Task.Delay(50);
                var ok = await JS.InvokeAsync<bool>("ipop.renderOffice", b642, "xls");
                if (!ok) previewError = "Preview not available - use Download.";
                await JS.InvokeVoidAsync("ipop.icons");
                return;
            }
            catch { previewError = "Preview failed."; }
            finally { previewLoading = false; StateHasChanged(); await JS.InvokeVoidAsync("ipop.icons"); }
            return;
        }
        if (IsOfficePreviewable(it.Name))
        {
            try
            {
                var res2 = await Auth.SendAsync(HttpMethod.Get, $"/api/files/preview?server={server}&path={Uri.EscapeDataString(it.Path)}");
                if (!res2.IsSuccessStatusCode)
                {
                    var errText = "Document preview is unavailable.";
                    try
                    {
                        var errJson = await res2.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                        if (errJson.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString()))
                            errText = m.GetString()!;
                    }
                    catch { }
                    previewError = errText;
                    return;
                }
                var bytes2 = await res2.Content.ReadAsByteArrayAsync();
                previewUrl = await JS.InvokeAsync<string>("ipop.preview", Convert.ToBase64String(bytes2), "application/pdf", it.Name);
                previewMime = "application/pdf";
            }
            catch (Exception ex) { previewError = $"Failed to load preview: {ex.Message}"; }
            finally { previewLoading = false; StateHasChanged(); await JS.InvokeVoidAsync("ipop.icons"); }
            return;
        }
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Get, $"/api/files/download?server={server}&path={Uri.EscapeDataString(it.Path)}");
            if (!res.IsSuccessStatusCode) { previewError = "Could not load preview."; return; }
            var bytes = await res.Content.ReadAsByteArrayAsync();
            var b64 = Convert.ToBase64String(bytes);
            previewUrl = await JS.InvokeAsync<string>("ipop.preview", b64, previewMime, it.Name);
        }
        catch { previewError = "Preview failed."; }
        finally { previewLoading = false; StateHasChanged(); await JS.InvokeVoidAsync("ipop.icons"); }
    }
    private string BuildExplorerUrl(SynoItemDto it) => $"ipop://open?server={Uri.EscapeDataString(server)}&path={Uri.EscapeDataString(it.Path)}";
    private async Task OpenInExplorer()
    {
        if (previewItem is null) return;
        var url = BuildExplorerUrl(previewItem);
        await JS.InvokeVoidAsync("ipop.openExplorer", url);
        await JS.InvokeVoidAsync("ipop.icons");
    }
    private async Task OpenInExplorerFor(SynoItemDto it)
    {
        previewItem = it;
        await OpenInExplorer();
    }

    private async Task ClosePreview()
    {
        if (mPreviewPushed)
        {
            mPreviewPushed = false;
            try { await JS.InvokeVoidAsync("ipop.mobile.popDetail"); } catch (JSException) { }
        }
        showPreview = false;
        previewItem = null;
        var url = previewUrl;
        previewUrl = null;
        previewMime = null;
        previewError = null;
        previewSheetBytes = null;
        StateHasChanged();
        if (url is not null) await JS.InvokeVoidAsync("ipop.revokePreview");
        await JS.InvokeVoidAsync("ipop.icons");
    }

    private static bool IsCoAuthorable(string name) => GetFileExt(name) is ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt" or ".rtf";
    private string GetEditorUrl(SynoItemDto it) => $"/files/editor?server={server}&path={Uri.EscapeDataString(it.Path)}";

    private async Task PrintPreview()
    {
        if (previewUrl is null) return;
        await JS.InvokeVoidAsync("ipop.printUrl", previewUrl);
    }

    private async Task Download(SynoItemDto it)
    {
        var res = await Auth.SendAsync(HttpMethod.Get, $"/api/files/download?server={server}&path={Uri.EscapeDataString(it.Path)}");
        if (!res.IsSuccessStatusCode) { loadError = "Download failed."; return; }
        var bytes = await res.Content.ReadAsByteArrayAsync();
        var b64 = Convert.ToBase64String(bytes);
        await JS.InvokeVoidAsync("ipop.download", b64, it.Name);
    }

}
