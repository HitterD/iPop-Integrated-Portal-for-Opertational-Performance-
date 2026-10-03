// Files page — selection, recycle bin, new folder, rename, move/copy, share links, delete.
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
    private void ToggleSelect(string path)
    {
        if (!selectedPaths.Add(path)) selectedPaths.Remove(path);
    }

    private void ToggleSelectAll(ChangeEventArgs e)
    {
        if ((bool)(e.Value ?? false))
            selectedPaths = DisplayRows().Where(CanAct).Select(x => x.Item.Path).ToHashSet();
        else
            selectedPaths.Clear();
    }

    private void ToggleRecycleBin() => EnterRecycleBin();

    private void EnterRecycleBin()
    {
        isRecycleBin = true;
        isStarredView = false;
        currentPath = "/";
        selectedPaths.Clear();
        _ = Reload();
    }

    private void OpenNewFolderModal()
    {
        newFolderName = "";
        showNewFolderModal = true;
    }

    private async Task CreateFolder()
    {
        if (string.IsNullOrWhiteSpace(newFolderName)) return;
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/folder", new { server, path = currentPath, name = newFolderName.Trim() });
        showNewFolderModal = false;
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Folder Created", $"Folder \"{newFolderName}\" created.", ToastKind.Success, "folder-plus");
            await Reload();
        }
        else
        {
            Toasts.Show("Failed to create folder.", ToastKind.Error);
        }
    }

    private void OpenRenameModal(SynoItemDto it)
    {
        renameTargetItem = it;
        renameTargetName = it.Name;
        showRenameModal = true;
    }

    private async Task SaveRename()
    {
        if (renameTargetItem is null || string.IsNullOrWhiteSpace(renameTargetName)) return;
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/rename", new { server, path = renameTargetItem.Path, name = renameTargetName.Trim() });
        showRenameModal = false;
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Renamed", $"Item renamed to \"{renameTargetName}\".", ToastKind.Success, "edit-3");
            await Reload();
        }
        else
        {
            Toasts.Show("Failed to rename item.", ToastKind.Error);
        }
    }

    private void OpenMoveCopyModal(SynoItemDto it)
    {
        moveCopyPaths = [it.Path];
        targetServer = server;
        targetDestDir = currentPath;
        moveCopyMode = "copy";
        showMoveCopyModal = true;
    }

    private void OpenMoveCopySelected()
    {
        moveCopyPaths = selectedPaths.ToList();
        targetServer = server;
        targetDestDir = currentPath;
        moveCopyMode = "copy";
        showMoveCopyModal = true;
    }

    private async Task ExecuteMoveCopy()
    {
        showMoveCopyModal = false;
        if (moveCopyPaths.Count == 0 || string.IsNullOrWhiteSpace(targetDestDir)) return;

        if (targetServer.Equals(server, StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = moveCopyMode == "move" ? "/api/files/move" : "/api/files/copy";
            var res = await Auth.SendAsync(HttpMethod.Post, endpoint, new { server, paths = moveCopyPaths, destDir = targetDestDir, overwrite = true });
            if (res.IsSuccessStatusCode)
            {
                Toasts.ShowDetailed("Completed", $"Items {(moveCopyMode == "move" ? "moved" : "copied")} successfully.", ToastKind.Success, "folder-check");
                selectedPaths.Clear();
                await Reload();
            }
            else
            {
                Toasts.Show("Move/Copy operation failed.", ToastKind.Error);
            }
        }
        else
        {
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/transfer", new { sourceServer = server, paths = moveCopyPaths, destServer = targetServer, destDir = targetDestDir, mode = moveCopyMode });
            if (res.IsSuccessStatusCode)
            {
                Toasts.ShowDetailed("Transfer Started", "Cross-server file transfer queued.", ToastKind.Success, "upload-cloud");
                selectedPaths.Clear();
                try
                {
                    var started = await res.Content.ReadFromJsonAsync<TransferStarted>();
                    if (started is not null && started.OperationId != Guid.Empty)
                        TrackTransfer(started.OperationId, moveCopyMode, targetServer, moveCopyPaths.Count);
                }
                catch { /* progress is cosmetic; the finish event still reloads */ }
            }
            else
            {
                Toasts.Show("Could not initiate cross-server transfer.", ToastKind.Error);
            }
        }
    }

    private async Task OpenShareModal(SynoItemDto it)
    {
        shareTargetItem = it;
        existingShareLinks = [];
        showShareModal = true;
        try
        {
            var links = await Auth.GetAsync<List<SynoShareLink>>($"/api/files/sharing?server={server}&path={Uri.EscapeDataString(it.Path)}");
            existingShareLinks = links ?? [];
        }
        catch { }
    }

    private async Task CreateShareLink()
    {
        if (shareTargetItem is null) return;
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/sharing", new { server, path = shareTargetItem.Path, expiresInDays = 14 });
        if (res.IsSuccessStatusCode)
        {
            var l = await res.Content.ReadFromJsonAsync<SynoShareLink>();
            if (l != null) existingShareLinks = [l];
            Toasts.ShowDetailed("Link Created", "QuickConnect link generated.", ToastKind.Success, "link");
        }
    }

    private async Task CopyShareUrl(string url)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", url);
        Toasts.ShowDetailed("Copied", "Share link copied to clipboard.", ToastKind.Success, "copy");
    }

    private async Task DeleteShareLink(string shareId)
    {
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/files/sharing?server={server}&shareId={Uri.EscapeDataString(shareId)}");
        if (res.IsSuccessStatusCode)
        {
            existingShareLinks.RemoveAll(l => l.Id == shareId);
            Toasts.ShowDetailed("Removed", "Share link deleted.", ToastKind.Success, "trash-2");
        }
    }

    private async Task DeleteSingle(SynoItemDto it)
    {
        var ok = await Confirm.AskAsync("Move to Recycle Bin?", $"Move \"{it.Name}\" to Recycle Bin?", "Move to Bin");
        if (!ok) return;

        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/delete", new { server, paths = new[] { it.Path } });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Deleted", $"\"{it.Name}\" moved to Recycle Bin.", ToastKind.Success, "trash-2");
            await Reload();
        }
    }

    private async Task DeleteSelected()
    {
        if (selectedPaths.Count == 0) return;
        var ok = await Confirm.AskAsync("Move items to Recycle Bin?", $"Move {selectedPaths.Count} item(s) to Recycle Bin?", "Move to Bin");
        if (!ok) return;

        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/delete", new { server, paths = selectedPaths.ToArray() });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Deleted", $"{selectedPaths.Count} items moved to Recycle Bin.", ToastKind.Success, "trash-2");
            selectedPaths.Clear();
            await Reload();
        }
    }

    private async Task DeleteSinglePermanently(SynoItemDto it)
    {
        var ok = await Confirm.AskAsync("Delete permanently?", $"\"{it.Name}\" will be permanently removed.", "Delete forever", danger: true);
        if (!ok) return;

        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/delete", new { server, paths = new[] { it.Path } });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Deleted", $"\"{it.Name}\" permanently deleted.", ToastKind.Success, "trash-2");
            await Reload();
        }
    }

    private async Task DeleteSelectedPermanently()
    {
        if (selectedPaths.Count == 0) return;
        var ok = await Confirm.AskAsync("Delete permanently?", $"{selectedPaths.Count} item(s) will be permanently deleted.", "Delete forever", danger: true);
        if (!ok) return;

        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/delete", new { server, paths = selectedPaths.ToArray() });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Deleted", $"{selectedPaths.Count} items permanently deleted.", ToastKind.Success, "trash-2");
            selectedPaths.Clear();
            await Reload();
        }
    }

    private async Task RestoreSingle(SynoItemDto it)
    {
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/recycle/restore", new { server, paths = new[] { it.Path }, destDir = "" });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Restored", $"\"{it.Name}\" restored.", ToastKind.Success, "undo-2");
            await Reload();
        }
        else
        {
            Toasts.Show("Could not restore item.", ToastKind.Error);
        }
    }

    private async Task RestoreSelected()
    {
        if (selectedPaths.Count == 0) return;
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/recycle/restore", new { server, paths = selectedPaths.ToArray(), destDir = "" });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Restored", $"{selectedPaths.Count} items restored.", ToastKind.Success, "undo-2");
            selectedPaths.Clear();
            await Reload();
        }
        else
        {
            Toasts.Show("Could not restore selected items.", ToastKind.Error);
        }
    }

    private async Task EmptyRecycleBin()
    {
        var ok = await Confirm.AskAsync("Empty Recycle Bin?", "All items in the Recycle Bin will be permanently deleted.", "Empty Bin", danger: true);
        if (!ok) return;

        var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/recycle/empty", new { server });
        if (res.IsSuccessStatusCode)
        {
            Toasts.ShowDetailed("Emptied", "Recycle Bin emptied successfully.", ToastKind.Success, "trash-2");
            await Reload();
        }
        else
        {
            Toasts.Show(await ErrorTextAsync(res, "Could not empty Recycle Bin."), ToastKind.Error);
            await Reload();
        }
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage res, string fallback)
    {
        try
        {
            var json = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (json.TryGetProperty("message", out var m) && !string.IsNullOrWhiteSpace(m.GetString())) return m.GetString()!;
        }
        catch { }
        return fallback;
    }

    private async Task DownloadSelectedZip()
    {
        if (selectedPaths.Count == 0) return;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/bulk-download", new { server, paths = selectedPaths.ToArray() });
            if (res.IsSuccessStatusCode)
            {
                var bytes = await res.Content.ReadAsByteArrayAsync();
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
                await JS.InvokeVoidAsync("ipop.download", Convert.ToBase64String(bytes), $"files_{server}_{stamp}.zip");
                Toasts.ShowDetailed("Download Ready", "ZIP archive downloaded.", ToastKind.Success, "download");
            }
            else
            {
                Toasts.Show(await ErrorTextAsync(res, "Could not create the ZIP archive."), ToastKind.Error);
            }
        }
        catch { Toasts.Show("Could not create the ZIP archive.", ToastKind.Error); }
    }
}
