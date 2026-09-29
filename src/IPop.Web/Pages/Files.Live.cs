// Files page — live refresh on filesChanged and cross-server transfer progress.
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
    // ---- Live updates -------------------------------------------------------------------
    // filesChanged(scope): scope = "{server}:{folder}" (or "{server}:*"). Every Files page gets
    // every event and keeps only the ones about the folder it shows, then re-lists it through
    // this user's own NAS credential. Bursts (a 50-file upload) coalesce into one silent re-list
    // ~400ms after the last event: no spinner, items swapped in place (scroll stays), selection
    // pruned to paths that still exist.
    private IDisposable? _filesSub;
    private IDisposable? _opFinishedSub;
    private CancellationTokenSource? _liveCts;
    private bool _liveAncestorChanged;
    private bool _disposed;

    private sealed record TransferStarted(Guid OperationId);
    private sealed record TransferRow(Guid Id, string Kind, string DestServer, string Status, int TotalFiles, int CompletedFiles, int FailedFiles);
    private sealed class ActiveTransfer
    {
        public required string Kind { get; init; }
        public required string DestServer { get; init; }
        public int Total { get; set; }
        public int Completed { get; set; }
    }
    private readonly Dictionary<Guid, ActiveTransfer> activeTransfers = [];

    private static string NormDir(string? d)
    {
        d = (d ?? "").Trim().Replace('\\', '/');
        if (d.Length == 0) return "/";
        if (!d.StartsWith('/')) d = "/" + d;
        d = d.TrimEnd('/');
        return d.Length == 0 ? "/" : d;
    }

    private static bool IsUnder(string path, string ancestor) =>
        ancestor == "/" || path.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);

    private void OnFilesChanged(string scope) => _ = InvokeAsync(() =>
    {
        if (_disposed || isStarredView) return;
        var sep = scope.IndexOf(':');
        if (sep <= 0) return;
        if (!scope[..sep].Equals(server, StringComparison.OrdinalIgnoreCase)) return;
        var dir = scope[(sep + 1)..];
        var here = NormDir(currentPath);

        if (dir != "*") dir = NormDir(dir);
        // Keep the shared cache honest for folders we are not looking at, too.
        if (dir == "*") FileStateService.InvalidateServer(server);
        else FileStateService.InvalidateFolder(server, dir);

        bool relevant;
        if (dir == "*") relevant = true;
        else if (isRecycleBin && here == "/") relevant = dir.Contains("/#recycle", StringComparison.OrdinalIgnoreCase);
        else if (dir.Equals(here, StringComparison.OrdinalIgnoreCase)) relevant = true;
        else if (IsUnder(here, dir)) { relevant = true; _liveAncestorChanged = true; } // our folder may have been renamed/moved/deleted
        else relevant = false;
        if (!relevant) return;

        QueueLiveReload();
    });

    private void OnFileOperationFinished(Guid operationId, string status) => _ = InvokeAsync(() =>
    {
        if (_disposed) return;
        if (activeTransfers.Remove(operationId))
        {
            if (status == "Completed")
                Toasts.ShowDetailed("Transfer complete", "Cross-server transfer finished.", ToastKind.Success, "folder-check");
            else
                Toasts.Show(status == "Failed" ? "Transfer failed." : "Transfer finished with errors.", ToastKind.Error);
        }
        // The worker also sends filesChanged for the folders it touched; reload regardless so a
        // missed push never leaves the listing stale.
        FileStateService.InvalidateFolder(server, currentPath);
        QueueLiveReload();
    });

    private void OnHubReconnected() => _ = InvokeAsync(() => { if (!_disposed) QueueLiveReload(); });

    private void QueueLiveReload()
    {
        _liveCts?.Cancel();
        _liveCts?.Dispose();
        _liveCts = new CancellationTokenSource();
        _ = LiveReloadAsync(_liveCts.Token);
    }

    private async Task LiveReloadAsync(CancellationToken ct)
    {
        try { await Task.Delay(400, ct); }
        catch (OperationCanceledException) { return; }
        if (_disposed || ct.IsCancellationRequested || isStarredView || needsCredential || loading) return;
        var pathAtStart = currentPath;
        var serverAtStart = server;
        var recycleAtStart = isRecycleBin;
        var ancestorChanged = _liveAncestorChanged;
        _liveAncestorChanged = false;
        if (!recycleAtStart && currentPath == "/") return;
        try
        {
            var url = recycleAtStart
                ? $"/api/files/recycle?server={server}&path={Uri.EscapeDataString(currentPath)}"
                : $"/api/files?server={server}&path={Uri.EscapeDataString(currentPath)}";
            var res = await Auth.SendAsync(HttpMethod.Get, url);
            if (_disposed || pathAtStart != currentPath || serverAtStart != server || recycleAtStart != isRecycleBin) return; // superseded by navigation
            if (!res.IsSuccessStatusCode)
            {
                // The folder we are in went away (renamed, moved or deleted by someone else).
                if (ancestorChanged && !recycleAtStart && (int)res.StatusCode is 404 or 502)
                {
                    Toasts.Show("This folder was moved or deleted.", ToastKind.Info);
                    GoUp();
                }
                return;
            }
            var list = await res.Content.ReadFromJsonAsync<List<SynoItemDto>>();
            if (list is null || pathAtStart != currentPath || serverAtStart != server) return;
            items = list;
            if (!recycleAtStart) FileStateService.SetCachedItems(server, currentPath, list);
            var alive = list.Select(i => i.Path).ToHashSet();
            selectedPaths.RemoveWhere(p => !alive.Contains(p));
            if (infoItem is not null) infoItem = list.FirstOrDefault(i => i.Path == infoItem.Path);
            StateHasChanged();
            await JS.InvokeVoidAsync("ipop.icons");
        }
        catch { /* advisory: navigation, the refresh button or the next event resync */ }
    }

    /// Progress for a queued cross-server transfer. fileOperationProgress carries four
    /// arguments, one more than AuthState.On can bind, so poll the operation row while it runs;
    /// completion itself arrives live via fileOperationFinished.
    private void TrackTransfer(Guid id, string kind, string destServer, int total)
    {
        activeTransfers[id] = new ActiveTransfer { Kind = kind, DestServer = destServer, Total = total };
        _ = PollTransferAsync(id);
    }

    private async Task PollTransferAsync(Guid id)
    {
        while (!_disposed && activeTransfers.TryGetValue(id, out var t))
        {
            await Task.Delay(1500);
            if (_disposed || !activeTransfers.ContainsKey(id)) return;
            TransferRow? row = null;
            try { row = await Auth.GetAsync<TransferRow>($"/api/files/operations/{id}"); } catch { }
            if (row is null) continue;
            t.Total = row.TotalFiles;
            t.Completed = row.CompletedFiles + row.FailedFiles;
            if (row.Status is "Completed" or "CompletedWithErrors" or "Failed")
            {
                OnFileOperationFinished(id, row.Status); // in case the push was missed
                return;
            }
            await InvokeAsync(StateHasChanged);
        }
    }
}
