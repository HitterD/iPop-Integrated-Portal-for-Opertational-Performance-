// Files page — state, lifecycle, server list, filtering and breadcrumbs.
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
    [CascadingParameter] public MainLayout Layout { get; set; } = default!;
    private string server = "spj";
    private string credServer = "spj";
    private string currentPath = "/";
    private readonly List<string> backStack = [];
    private readonly List<string> forwardStack = [];
    private string search = "";
    private string sortBy = "name";
    private bool loading, needsCredential, showCred;
    private string? loadError, credError;
    private string credUser = "", credPass = "";
    // Sign-in-once: the drawer defaults to trying one account on every plant NAS.
    private bool credAllSites = true, credBusy;
    private Dictionary<string, CredentialStatusDto> siteCreds = new();
    private Dictionary<string, SiteConnectResultDto> lastConnect = new();
    private List<SynoItemDto> items = [];
    private List<string>? shares;
    private CredentialStatusDto? credential;

    private bool showPreview;
    private SynoItemDto? previewItem;
    private string? previewUrl;
    private string? previewMime;
    private bool previewLoading;
    private string? previewError;
    private byte[]? previewSheetBytes;


    private bool isRecycleBin;
    private bool isStarredView;
    private bool uploading;
    private SynoStorageInfo? storageInfo;
    private HashSet<string> selectedPaths = [];
    private bool gridView;
    private SynoItemDto? infoItem;


    // Phone layout: the row "..." menu, and phone Back closing the full-screen preview.
    private string? mMenuPath;
    private DotNetObjectReference<Files>? mRef;

    [JSInvokable]
    public async Task CloseMobilePreview()
    {
        if (!showPreview) return;
        mPreviewPushed = false;
        await ClosePreview();
    }
    private bool mPreviewPushed;

    // Modals state
    private bool showNewFolderModal;
    private string newFolderName = "";

    private bool showRenameModal;
    private SynoItemDto? renameTargetItem;
    private string renameTargetName = "";

    private bool showMoveCopyModal;
    private List<string> moveCopyPaths = [];
    private string targetServer = "spj";
    private string targetDestDir = "/";
    private string moveCopyMode = "copy";

    private bool showShareModal;
    private SynoItemDto? shareTargetItem;
    private List<SynoShareLink> existingShareLinks = [];

    private record ServerInfo(string Id, string Label, string Host);
    private static readonly ServerInfo[] Servers = [
        new("spj", "SPJ", "192.168.2.16"),
        new("krw", "KRW", "10.12.2.6"),
        new("jtb", "JTB", "10.11.2.6"),
        new("smg", "SMG", "10.14.2.16"),
    ];
    private string serverLabel => Servers.FirstOrDefault(s => s.Id == server)?.Label ?? server.ToUpperInvariant();
    private void ToggleSort(string key)
    {
        if (sortBy == key) sortBy = key + "-desc";
        else if (sortBy == key + "-desc") sortBy = key;
        else sortBy = key;
        StateHasChanged();
    }
    private void ClearFilters()
    {
        search = ""; sortBy = "name";
        StateHasChanged();
    }

    private bool isRevalidating;
    private bool copiedPath;

    private record BreadcrumbSegment(string Name, string Path, bool IsLast);

    private List<BreadcrumbSegment> GetBreadcrumbs()
    {
        var list = new List<BreadcrumbSegment>();
        if (string.IsNullOrEmpty(currentPath) || currentPath == "/") return list;

        var parts = currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var acc = "";
        for (int i = 0; i < parts.Length; i++)
        {
            acc += "/" + parts[i];
            list.Add(new BreadcrumbSegment(parts[i], acc, i == parts.Length - 1));
        }
        return list;
    }

    private async Task CopyCurrentPath()
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", currentPath);
            copiedPath = true;
            StateHasChanged();
            await Task.Delay(1800);
            copiedPath = false;
            StateHasChanged();
        }
        catch
        {
            // fallback: no clipboard permission
        }
    }

    private async Task ForceRefresh()
    {
        FileStateService.Invalidate(server, currentPath);
        await Reload(bypassCache: true);
    }

    protected override void OnInitialized()
    {
        Layout.SetHeader("Files", "Files");
        // Pulihkan state server dan path dari in-memory state seketika (0ms)
        server = FileStateService.CurrentServer;
        credServer = server;
        currentPath = FileStateService.CurrentPath;

        backStack.Clear();
        backStack.AddRange(FileStateService.BackStack);
        forwardStack.Clear();
        forwardStack.AddRange(FileStateService.ForwardStack);

        // Pulihkan shares dari cache jika tersedia
        if (FileStateService.TryGetCachedShares(server, out var sCached))
        {
            shares = sCached;
        }

        if (currentPath == "/")
        {
            if (shares is not null) loading = false;
        }
        else
        {
            if (FileStateService.TryGetCachedItems(server, currentPath, out var iCached))
            {
                items = iCached;
                loading = false;
            }
        }
    }

    protected override async Task OnInitializedAsync()
    {
        gridView = await JS.InvokeAsync<string?>("localStorage.getItem", ViewKey) == "grid";

        // Pulihkan persistent last-seen dari localStorage (jika browser di-refresh / tab baru)
        var restored = await FileStateService.LoadLastSeenAsync(JS);
        if (restored.HasValue)
        {
            server = FileStateService.CurrentServer;
            credServer = server;
            currentPath = FileStateService.CurrentPath;
            if (FileStateService.TryGetCachedShares(server, out var sCached2))
            {
                shares = sCached2;
            }
            if (currentPath != "/" && FileStateService.TryGetCachedItems(server, currentPath, out var iCached))
            {
                items = iCached;
                loading = false;
                StateHasChanged();
            }
        }

        expandedSites.Add(server);
        await Task.WhenAll(Reload(), LoadSiteCredentials(), LoadPrefs());
        await MigrateLocalStars();

        _filesSub = Auth.On<string>(IPop.Application.Realtime.RealtimeEvents.FilesChanged, OnFilesChanged);
        _opFinishedSub = Auth.On<Guid, string>(IPop.Application.Realtime.RealtimeEvents.FileOperationFinished, OnFileOperationFinished);
        Auth.HubReconnected += OnHubReconnected;
    }


    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await JS.InvokeVoidAsync("ipop.icons");
        if (firstRender && !_disposed)
        {
            mRef ??= DotNetObjectReference.Create(this);
            fx = await JS.InvokeAsync<IJSObjectReference>("import", "./js/files.js?v=20261001-nik");
            if (_disposed) return;
            isMobile = await fx.InvokeAsync<bool>("attach", rootRef, mRef);
        }
        if (ctxAt is { } at && ctxMenu is not null)
        {
            ctxAt = null;
            await ctxMenu.OpenAsync(at.X, at.Y);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _filesSub?.Dispose();
        _opFinishedSub?.Dispose();
        Auth.HubReconnected -= OnHubReconnected;
        _liveCts?.Cancel();
        _liveCts?.Dispose();
        searchCts?.Cancel();
        if (fx is not null) _ = fx.InvokeVoidAsync("detach").AsTask().ContinueWith(_ => fx.DisposeAsync());
        _ = JS.InvokeVoidAsync("ipop.revokePreview");
        if (mRef is not null)
        {
            _ = JS.InvokeVoidAsync("ipop.mobile.clearDetail").AsTask().ContinueWith(_ => { });
            mRef.Dispose();
        }
    }
}
