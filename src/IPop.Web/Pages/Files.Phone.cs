using IPop.Web.Services;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Files
{
    private bool phoneLocations = true, phoneSelecting, phoneUpload, phoneInfo;
    private Row? phoneAction;
    private bool phoneAttached;
    private readonly Stack<(string Server, string Path, bool Recent, bool Starred, bool Bin)> phoneHistory = new();
    private void WatchPhone() => Phone.Changed += OnPhoneChanged;
    private void UnwatchPhone() => Phone.Changed -= OnPhoneChanged;
    private void OnPhoneChanged() => _ = InvokeAsync(async () =>
    {
        phoneAttached = false;
        phoneAction = null; phoneUpload = false;
        await JS.InvokeVoidAsync("ipop.mobile.clearDetail");
        phoneHistory.Clear();
        StateHasChanged();
    });
    private async Task PushPhoneFolder()
    {
        phoneHistory.Push((server, currentPath, isRecentView, isStarredView, isRecycleBin));
        mRef ??= DotNetObjectReference.Create(this);
        await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", mRef, nameof(BackPhoneFolder));
        phoneLocations = false;
    }
    [JSInvokable] public async Task BackPhoneFolder()
    {
        if (!phoneHistory.TryPop(out var previous)) { phoneLocations = true; await InvokeAsync(StateHasChanged); return; }
        phoneSelecting = false; selectedPaths.Clear(); CancelSearch(true);
        if (phoneHistory.Count == 0) phoneLocations = true;
        else
        {
            await GoTo(previous.Server, previous.Path);
            isRecentView = previous.Recent; isStarredView = previous.Starred; isRecycleBin = previous.Bin;
            if (isRecycleBin) await Reload();
        }
        await RestorePhoneDetail();
        await InvokeAsync(StateHasChanged);
    }
    private async Task RestorePhoneDetail()
    {
        if (!Phone.IsPhone || phoneLocations) return;
        mRef ??= DotNetObjectReference.Create(this);
        var callback = showPreview ? nameof(CloseMobilePreview) : nameof(BackPhoneFolder);
        await JS.InvokeAsync<bool>("ipop.mobile.pushDetail", mRef, callback);
    }
    private async Task PhoneBack()
    {
        await JS.InvokeVoidAsync("ipop.mobile.popDetail");
        await BackPhoneFolder();
    }
    private async Task PhoneSite(string site)
    {
        if (SiteStatus(site).Css != "on") { credServer = site; credAllSites = false; showCred = true; return; }
        await PushPhoneFolder(); await GoTo(site, "/");
    }
    private async Task PhoneFavorite(string place)
    {
        await PushPhoneFolder();
        selectedPaths.Clear(); CancelSearch(true);
        isStarredView = place == "starred"; isRecentView = place == "recent"; isRecycleBin = false;
        if (place == "bin") { isStarredView = isRecentView = false; EnterRecycleBin(); }
    }
    private async Task PhoneOpen(Row row)
    {
        if (phoneSelecting && CanAct(row)) { ToggleSelect(row.Item.Path); return; }
        if (row.Item.IsDir) await PushPhoneFolder();
        await OpenRow(row);
    }
    private async Task PhoneCrumb(string path)
    {
        if (path == currentPath && !isRecentView && !isStarredView) return;
        await PushPhoneFolder(); await GoTo(server, path);
    }
    private Task PhonePick(string source) => fx is null ? Task.CompletedTask : fx.InvokeVoidAsync("pickFiles", source).AsTask();
    private void PhoneSelect() { phoneSelecting = !phoneSelecting; selectedPaths.Clear(); }
    private void PhoneSort(string key) => SortBy(key);
    private async Task PhoneAction(string action)
    {
        var row = phoneAction; phoneAction = null;
        if (row is null) return;
        if (action is "share" or "move" or "rename" or "delete" or "restore" or "permanent" && !CanAct(row)) return;
        if (action == "download" && (row.Item.IsDir || row.Server != server)) return;
        if (showPreview) await ClosePreview();
        if (action == "star") { await ToggleStar(row); return; }
        if (action == "info") { infoItem = row.Item; infoSite = row.Server; phoneInfo = true; return; }
        // Mixed-site favorites/search must resolve through the existing NAS credential/permission path.
        if (row.Server != server || isRecentView || isStarredView) await Reveal(row, open: false);
        if (needsCredential) return;
        switch (action)
        {
            case "download": await Download(row.Item); break;
            case "share": await OpenShareModal(row.Item); break;
            case "move": OpenMoveCopyModal(row.Item); break;
            case "rename": OpenRenameModal(row.Item); break;
            case "delete": await DeleteSingle(row.Item); break;
            case "restore": await RestoreSingle(row.Item); break;
            case "permanent": await DeleteSinglePermanently(row.Item); break;
        }
    }
}
