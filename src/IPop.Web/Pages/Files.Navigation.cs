// Files page — loading a folder, server switching, back/forward/up history.
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
    private async Task SwitchServer(string id)
    {
        server = id; credServer = id; currentPath = "/"; search = "";
        shares = null;
        items = [];
        FileStateService.CurrentServer = id;
        FileStateService.CurrentPath = "/";
        backStack.Clear(); forwardStack.Clear();
        FileStateService.BackStack.Clear(); FileStateService.ForwardStack.Clear();
        _ = FileStateService.SaveLastSeenAsync(JS);
        await Reload(bypassCache: true);
    }

    private async Task Reload(bool bypassCache = false)
    {
        // ipop-42: navigation paths fire-and-forget Reload after mutating currentPath.
        // Capture at start and compare after EVERY await — a superseded continuation
        // must write nothing, or the listing can show folder A while currentPath is B
        // (and OnUpload then posts files into B while the user is looking at A).
        var pathAtStart = currentPath;
        var serverAtStart = server;
        loadError = null;

        bool hasCachedData = false;
        // Selalu pastikan shares di-resolve dari cache terlebih dahulu
        if (shares is null && FileStateService.TryGetCachedShares(server, out var shCache))
        {
            shares = shCache;
        }

        // The site root lists shares; the Recycle Bin at "/" is a real listing (every share's bin).
        var sharesOnly = currentPath == "/" && !isRecycleBin;
        if (!bypassCache)
        {
            if (sharesOnly)
            {
                if (shares is not null)
                {
                    hasCachedData = true;
                }
            }
            else
            {
                if (FileStateService.TryGetCachedItems(server, currentPath, out var iCached))
                {
                    items = iCached;
                    hasCachedData = true;
                }
            }
        }

        if (hasCachedData)
        {
            loading = false;
            isRevalidating = true;
            StateHasChanged();
        }
        else
        {
            loading = true;
            if (!sharesOnly) items = [];
        }

        try
        {
            // The credential check, share list and folder listing go out together: awaiting the
            // credential first put a whole round trip in front of every folder open. A missing
            // credential makes the listing 401, which the credential result below explains.
            var credTask = Auth.GetAsync<CredentialStatusDto>($"/api/files/credential?server={server}");

            Task<List<string>?>? sharesTask = null;
            if (shares is null)
            {
                sharesTask = Auth.GetAsync<List<string>>($"/api/files/shares?server={server}");
            }

            Task<List<SynoItemDto>?>? itemsTask = null;
            if (isRecycleBin)
            {
                itemsTask = Auth.GetAsync<List<SynoItemDto>>($"/api/files/recycle?server={server}&path={Uri.EscapeDataString(currentPath)}");
            }
            else if (currentPath != "/")
            {
                itemsTask = Auth.GetAsync<List<SynoItemDto>>($"/api/files?server={server}&path={Uri.EscapeDataString(currentPath)}");
            }

            var cred = await credTask;
            if (pathAtStart != currentPath || serverAtStart != server) return; // superseded by navigation
            credential = cred;
            Layout.SetSubtitle(cred is { HasCredential: true, IsInvalid: false } ? $"{serverLabel} · Connected as {cred.Username}" : $"{serverLabel} · Not connected");
            if (cred is null || !cred.HasCredential) { needsCredential = true; return; }
            if (cred.IsInvalid) { needsCredential = true; showCred = true; return; }
            needsCredential = false;

            // Volume usage barely moves: once per server, not on every folder click.
            if (storageInfo?.Server != server)
            {
                var storageServer = server;
                _ = Auth.GetAsync<SynoStorageInfo>($"/api/files/storage?server={storageServer}").ContinueWith(t => {
                    if (t.IsCompletedSuccessfully && t.Result is not null && storageServer == server) { storageInfo = t.Result; _ = InvokeAsync(StateHasChanged); }
                });
            }

            if (sharesTask is not null)
            {
                var s = await sharesTask;
                if (pathAtStart != currentPath || serverAtStart != server) return;
                if (s is not null)
                {
                    shares = s;
                    FileStateService.SetCachedShares(server, s);
                }
            }

            if (sharesOnly)
            {
                if (shares is null)
                {
                    if (!hasCachedData) loadError = "Could not reach the NAS.";
                }
            }
            else if (itemsTask is not null)
            {
                var list = await itemsTask;
                if (pathAtStart != currentPath || serverAtStart != server) return; // superseded
                if (list is null)
                {
                    if (!hasCachedData) loadError = "Could not load folder.";
                }
                else
                {
                    items = list;
                    FileStateService.SetCachedItems(server, currentPath, list);
                }
            }

            FileStateService.CurrentServer = server;
            FileStateService.CurrentPath = currentPath;
            FileStateService.BackStack = backStack.ToList();
            FileStateService.ForwardStack = forwardStack.ToList();
            _ = FileStateService.SaveLastSeenAsync(JS);
        }
        finally
        {
            // ipop-46 + ipop-42: guarantee the spinner clears on a throw, but a SUPERSEDED
            // continuation still writes nothing — the newer Reload owns `loading` now.
            if (pathAtStart == currentPath && serverAtStart == server)
            {
                loading = false;
                isRevalidating = false;
                StateHasChanged();
                await JS.InvokeVoidAsync("ipop.icons");
            }
        }
    }

    // Push the current path onto the back stack before navigating somewhere
    // new; forward gets cleared (a new branch invalidates the forward history).
    private void PushHistory()
    {
        if (backStack.Count == 0 || backStack[^1] != currentPath) backStack.Add(currentPath);
        forwardStack.Clear();
        FileStateService.BackStack = backStack.ToList();
        FileStateService.ForwardStack = forwardStack.ToList();
    }

    private void Enter(string share) { isRecycleBin = false; isStarredView = false; PushHistory(); currentPath = share; _ = Reload(); }
    private Task OnItemClick(SynoItemDto it)
    {
        if (it.IsDir) { PushHistory(); currentPath = it.Path; return Reload(); }
        TouchRecent(it);
        if (IsPreviewable(it.Name)) return Preview(it);
        return Download(it);
    }
    private void GoUp() { PushHistory(); var p = currentPath.TrimEnd('/'); var idx = p.LastIndexOf('/'); currentPath = idx <= 0 ? "/" : p[..idx]; _ = Reload(); }
    private async Task Navigate(string path)
    {
        if (path == currentPath) return; // clicking the current crumb is a no-op
        PushHistory(); currentPath = path; await Reload();
    }
    private void GoBack()
    {
        if (backStack.Count == 0) return;
        forwardStack.Add(currentPath);
        currentPath = backStack[^1];
        backStack.RemoveAt(backStack.Count - 1);
        FileStateService.BackStack = backStack.ToList();
        FileStateService.ForwardStack = forwardStack.ToList();
        FileStateService.CurrentPath = currentPath;
        _ = FileStateService.SaveLastSeenAsync(JS);
        _ = Reload();
    }
    private void GoForward()
    {
        if (forwardStack.Count == 0) return;
        backStack.Add(currentPath);
        currentPath = forwardStack[^1];
        forwardStack.RemoveAt(forwardStack.Count - 1);
        FileStateService.BackStack = backStack.ToList();
        FileStateService.ForwardStack = forwardStack.ToList();
        FileStateService.CurrentPath = currentPath;
        _ = FileStateService.SaveLastSeenAsync(JS);
        _ = Reload();
    }
}
