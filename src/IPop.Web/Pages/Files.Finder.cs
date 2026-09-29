// Files page — the Finder layer: one row model for every view, selection, keyboard, context
// menus, drag & drop, NAS search, stars/recents and uploads with byte progress.
//
// Part of the Files partial class. _Imports.razor only reaches .razor files, so usings are
// restated in each part.
using System.Net.Http.Json;
using IPop.Web.Components;
using IPop.Web.Components.Files;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Files
{
    /// <summary>A listed item plus the site it lives on — Starred, Recent and search mix sites.</summary>
    private sealed record Row(string Server, SynoItemDto Item)
    {
        public string Key => Server + ":" + Item.Path;
    }

    /// <summary>A starred or recently opened item, stored per user in /api/files/prefs.</summary>
    private sealed record PlaceRef(string Server, string Path, string Name, bool IsDir, long At = 0);
    private sealed record FilesPrefs(List<PlaceRef>? Stars, List<PlaceRef>? Recent);
    private sealed record SearchStartDto(string? TaskId);
    private sealed record SearchPageDto(List<SynoItemDto>? Items, int Total, bool Finished);
    public sealed record UploadPlan(string Url, string Server, string Path);
    private sealed class UploadFile(string name, long size)
    {
        public string Name { get; } = name;
        public long Size { get; } = size;
        public long Sent { get; set; }
        public bool? Ok { get; set; }
        public string? Error { get; set; }
    }

    private const string ViewKey = "ipop.files.view";
    private const int RecentMax = 40;

    private IJSObjectReference? fx;
    private ElementReference rootRef;
    private bool isMobile, isRecentView, showInfoPane, sideOpen, recentSorted;
    private string? focusKey, anchorKey;
    private string? infoSite;
    private readonly HashSet<string> expandedSites = [];
    private readonly Dictionary<string, List<string>?> siteShares = [];
    private List<PlaceRef> stars = [];
    private List<PlaceRef> recent = [];

    // ── Rows ───────────────────────────────────────────────────────────────────

    private bool IsRoot => currentPath == "/" && !isRecycleBin && !isStarredView && !isRecentView;
    private bool SearchMode => search.Trim().Length >= 2 && !isStarredView && !isRecentView && !isRecycleBin;
    private bool ShowLocation => isStarredView || isRecentView || SearchMode;
    private bool CanAct(Row r) => r.Server == server && !IsRoot && !needsCredential;
    private bool IsStarred(Row r) => stars.Any(s => s.Server == r.Server && s.Path == r.Item.Path);

    private static string ShareName(string sharePath) => sharePath.Trim('/');
    private static string ParentOf(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }
    private static string SiteLabel(string id) => Servers.FirstOrDefault(s => s.Id == id)?.Label ?? id.ToUpperInvariant();
    private static string Where(Row r) => $"{SiteLabel(r.Server)} · {ParentOf(r.Item.Path)}";

    private List<Row> DisplayRows()
    {
        IEnumerable<Row> q;
        if (isStarredView) q = stars.Select(s => new Row(s.Server, new SynoItemDto(s.Name, s.Path, s.IsDir, 0, 0)));
        else if (isRecentView) q = recent.Select(s => new Row(s.Server, new SynoItemDto(s.Name, s.Path, s.IsDir, 0, s.At)));
        else if (IsRoot) q = (shares ?? []).Select(sh => new Row(server, new SynoItemDto(ShareName(sh), sh, true, 0, 0)));
        else q = items.Select(i => new Row(server, i));

        var term = search.Trim();
        if (term.Length > 0) q = q.Where(r => r.Item.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (SearchMode) q = q.Concat(searchHits).DistinctBy(r => r.Key);
        // Recents read newest-first until the user asks for another order.
        return isRecentView && !recentSorted ? q.ToList() : SortRows(q).ToList();
    }

    private IEnumerable<Row> SortRows(IEnumerable<Row> q)
    {
        var dirs = q.OrderBy(r => r.Item.IsDir ? 0 : 1);
        return sortBy switch
        {
            "name-desc" => dirs.ThenByDescending(r => r.Item.Name, StringComparer.OrdinalIgnoreCase),
            "modified" => dirs.ThenBy(r => r.Item.Mtime),
            "modified-desc" => dirs.ThenByDescending(r => r.Item.Mtime),
            "size" => dirs.ThenBy(r => r.Item.Size),
            "size-desc" => dirs.ThenByDescending(r => r.Item.Size),
            "kind" => dirs.ThenBy(r => FileKinds.Label(r.Item.Name, r.Item.IsDir)).ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase),
            "kind-desc" => dirs.ThenByDescending(r => FileKinds.Label(r.Item.Name, r.Item.IsDir)).ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase),
            _ => dirs.ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void SortBy(string key)
    {
        if (isRecentView) recentSorted = true;
        ToggleSort(key);
    }

    private string SortIcon(string key) => sortBy == key ? "chevron-up" : sortBy == key + "-desc" ? "chevron-down" : "";

    // ── Places: sites, shares, favourites ──────────────────────────────────────

    private List<string>? SharesFor(string site) => site == server ? shares : siteShares.GetValueOrDefault(site);

    private async Task ToggleSite(string site)
    {
        if (!expandedSites.Add(site)) { expandedSites.Remove(site); return; }
        await LoadSiteShares(site);
    }

    private async Task LoadSiteShares(string site)
    {
        if (site == server || siteShares.GetValueOrDefault(site) is not null) return;
        if (FileStateService.TryGetCachedShares(site, out var cached)) { siteShares[site] = cached; return; }
        if (SiteStatus(site).Css != "on") return;
        var list = await Auth.GetAsync<List<string>>($"/api/files/shares?server={site}");
        if (list is null) return;
        siteShares[site] = list;
        FileStateService.SetCachedShares(site, list);
        StateHasChanged();
    }

    /// <summary>Go to a folder on any site; the single entry point for sidebar, crumbs and "Show in folder".</summary>
    private async Task GoTo(string site, string path)
    {
        var fromSpecial = isStarredView || isRecentView || isRecycleBin;
        isStarredView = isRecentView = isRecycleBin = false;
        CancelSearch(clearText: true);
        sideOpen = false;
        if (site != server)
        {
            if (shares is not null) siteShares[server] = shares;
            server = site; credServer = site;
            shares = siteShares.GetValueOrDefault(site);
            items = [];
            backStack.Clear(); forwardStack.Clear();
            FileStateService.CurrentServer = site;
        }
        else if (path != currentPath || fromSpecial) PushHistory();
        expandedSites.Add(site);
        currentPath = path;
        selectedPaths.Clear();
        focusKey = anchorKey = null;
        FileStateService.CurrentPath = path;
        await Reload();
    }

    private void EnterRecentView()
    {
        isRecentView = true;
        isStarredView = isRecycleBin = false;
        CancelSearch(clearText: true);
        selectedPaths.Clear();
        focusKey = null;
        sideOpen = false;
    }

    private async Task ToggleStar(Row r)
    {
        var at = stars.FindIndex(s => s.Server == r.Server && s.Path == r.Item.Path);
        if (at >= 0) stars.RemoveAt(at);
        else stars.Add(new PlaceRef(r.Server, r.Item.Path, r.Item.Name, r.Item.IsDir, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        StateHasChanged();
        await SavePrefs();
    }

    private void TouchRecent(SynoItemDto it)
    {
        if (it.IsDir) return;
        recent.RemoveAll(r => r.Server == server && r.Path == it.Path);
        recent.Insert(0, new PlaceRef(server, it.Path, it.Name, false, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (recent.Count > RecentMax) recent.RemoveRange(RecentMax, recent.Count - RecentMax);
        _ = SavePrefs();
    }

    private async Task LoadPrefs()
    {
        try
        {
            var p = await Auth.GetAsync<FilesPrefs>("/api/files/prefs");
            stars = p?.Stars ?? [];
            recent = p?.Recent ?? [];
        }
        catch { /* favourites are a convenience; the page works without them */ }
    }

    /// <summary>
    /// Stars used to be bare paths in this browser's localStorage, with no site attached. They
    /// were only ever shown for the current folder, so the site they came from is unknowable;
    /// the current site is the best guess. Runs once, then the key is removed.
    /// </summary>
    private async Task MigrateLocalStars()
    {
        const string legacyKey = "ipop.files.starred";
        var raw = await JS.InvokeAsync<string?>("localStorage.getItem", legacyKey);
        if (string.IsNullOrEmpty(raw)) return;
        await LoadPrefs();
        foreach (var path in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (stars.Any(s => s.Path == path)) continue;
            var name = path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
            // Folders vs files can't be told apart from a path; an extension is the hint.
            stars.Add(new PlaceRef(server, path, name, !name.Contains('.'), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        }
        await SavePrefs();
        await JS.InvokeVoidAsync("localStorage.removeItem", legacyKey);
    }

    private async Task SavePrefs()
    {
        try { await Auth.SendAsync(HttpMethod.Put, "/api/files/prefs", new FilesPrefs(stars, recent)); }
        catch { Toasts.Show("Couldn't save your favourites.", ToastKind.Error); }
    }

    private async Task SetGridView(bool grid)
    {
        gridView = grid;
        await JS.InvokeVoidAsync("localStorage.setItem", ViewKey, grid ? "grid" : "list");
    }

    // ── Selection & opening ────────────────────────────────────────────────────

    private bool IsSelected(Row r) => CanAct(r) && selectedPaths.Contains(r.Item.Path);

    private async Task OnRowClick(Row r, MouseEventArgs e)
    {
        focusKey = r.Key;
        // Touch: a tap opens (there is no double-tap on a list), unless the user is picking items.
        if (isMobile)
        {
            if (selectedPaths.Count > 0 && CanAct(r)) { ToggleSelect(r.Item.Path); return; }
            await OpenRow(r);
            return;
        }
        if (!CanAct(r)) { selectedPaths.Clear(); anchorKey = r.Key; ShowInfoFor(r); return; }
        if (e.ShiftKey && anchorKey is not null) SelectRange(anchorKey, r.Key, additive: e.CtrlKey || e.MetaKey);
        else if (e.CtrlKey || e.MetaKey) { ToggleSelect(r.Item.Path); anchorKey = r.Key; }
        else { selectedPaths = [r.Item.Path]; anchorKey = r.Key; }
        ShowInfoFor(r);
    }

    private void SelectRange(string fromKey, string toKey, bool additive)
    {
        var rows = DisplayRows();
        var a = rows.FindIndex(x => x.Key == fromKey);
        var b = rows.FindIndex(x => x.Key == toKey);
        if (a < 0 || b < 0) return;
        if (!additive) selectedPaths.Clear();
        foreach (var x in rows.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1))
            if (CanAct(x)) selectedPaths.Add(x.Item.Path);
    }

    private void ShowInfoFor(Row r)
    {
        if (!showInfoPane) return;
        infoItem = r.Item;
        infoSite = r.Server;
    }

    private void ToggleInfoPane()
    {
        showInfoPane = !showInfoPane;
        if (!showInfoPane) { infoItem = null; return; }
        var f = DisplayRows().FirstOrDefault(r => r.Key == focusKey);
        infoItem = f?.Item;
        infoSite = f?.Server;
    }

    private void ClearSelection()
    {
        selectedPaths.Clear();
        focusKey = anchorKey = null;
        if (showInfoPane) infoItem = null;
    }

    /// <summary>Double-click / Enter: folders open in place, files Quick Look (or download).</summary>
    private async Task OpenRow(Row r)
    {
        if (r.Server != server || isStarredView || isRecentView || (SearchMode && !items.Any(i => i.Path == r.Item.Path)))
        {
            await Reveal(r, open: true);
            return;
        }
        if (r.Item.IsDir)
        {
            if (isRecycleBin) { PushHistory(); currentPath = r.Item.Path; selectedPaths.Clear(); await Reload(); return; }
            await GoTo(r.Server, r.Item.Path);
            return;
        }
        await OnItemClick(r.Item);
    }

    /// <summary>Take the user to where an item lives; optionally open it once there.</summary>
    private async Task Reveal(Row r, bool open)
    {
        var folder = r.Item.IsDir && open ? r.Item.Path : ParentOf(r.Item.Path);
        await GoTo(r.Server, folder);
        if (r.Item.IsDir && open) return;
        focusKey = anchorKey = r.Key;
        selectedPaths = [r.Item.Path];
        var fresh = items.FirstOrDefault(i => i.Path == r.Item.Path) ?? r.Item;
        if (open) await OnItemClick(fresh);
        await RevealFocus();
    }

    private async Task QuickLook(Row r)
    {
        if (r.Item.IsDir || !IsPreviewable(r.Item.Name)) return;
        if (r.Server != server) { await Reveal(r, open: true); return; }
        await Preview(r.Item);
    }

    private async Task RevealFocus()
    {
        StateHasChanged();
        if (fx is not null) try { await fx.InvokeVoidAsync("reveal"); } catch (JSException) { }
    }

    // ── Keyboard (keydown lives in js/files.js so it never fights text inputs) ─

    [JSInvokable]
    public async Task OnKey(string key, bool mod, bool shift, int cols)
    {
        if (showPreview) { if (key == " ") await ClosePreview(); return; }
        var rows = DisplayRows();
        var idx = focusKey is null ? -1 : rows.FindIndex(r => r.Key == focusKey);
        var focus = idx >= 0 ? rows[idx] : null;
        var step = 0;
        switch (key)
        {
            case "Escape":
                if (search.Length > 0) CancelSearch(clearText: true); else ClearSelection();
                break;
            case "Back": if (backStack.Count > 0) GoBack(); else if (!IsRoot && !isStarredView && !isRecentView) GoUp(); break;
            case "Forward": GoForward(); break;
            case "a" when mod:
                selectedPaths = rows.Where(CanAct).Select(r => r.Item.Path).ToHashSet();
                break;
            case "i" when mod: ToggleInfoPane(); break;
            case "o" when mod:
            case "Enter": if (focus is not null) await OpenRow(focus); break;
            case " ": if (focus is not null) await QuickLook(focus); break;
            case "F2": if (focus is not null && CanAct(focus) && !isRecycleBin) OpenRenameModal(focus.Item); break;
            case "Delete":
                if (selectedPaths.Count > 0) await (isRecycleBin ? DeleteSelectedPermanently() : DeleteSelected());
                else if (focus is not null && CanAct(focus)) await (isRecycleBin ? DeleteSinglePermanently(focus.Item) : DeleteSingle(focus.Item));
                break;
            case "ArrowDown": step = gridView ? cols : 1; break;
            case "ArrowUp": step = gridView ? -cols : -1; break;
            case "ArrowRight": step = gridView ? 1 : 0; break;
            case "ArrowLeft": step = gridView ? -1 : 0; break;
            case "Home": step = int.MinValue; break;
            case "End": step = int.MaxValue; break;
        }
        if (step != 0 && rows.Count > 0)
        {
            var next = step switch
            {
                int.MinValue => 0,
                int.MaxValue => rows.Count - 1,
                _ => idx < 0 ? (step > 0 ? 0 : rows.Count - 1) : Math.Clamp(idx + step, 0, rows.Count - 1),
            };
            var r = rows[next];
            focusKey = r.Key;
            if (shift && anchorKey is not null) SelectRange(anchorKey, r.Key, additive: false);
            else { anchorKey = r.Key; selectedPaths = CanAct(r) ? [r.Item.Path] : []; }
            ShowInfoFor(r);
        }
        StateHasChanged();
    }

    // ── Context menu ───────────────────────────────────────────────────────────

    private bool ctxOpen;
    private (double X, double Y)? ctxAt;
    private ContextMenu? ctxMenu;
    private IReadOnlyList<ContextMenu.Item> ctxItems = [];

    private static Func<Task> Do(Action a) => () => { a(); return Task.CompletedTask; };

    private void OpenItemMenu(Row r, MouseEventArgs e)
    {
        focusKey = r.Key;
        // Right-clicking outside the selection retargets it, like Finder.
        if (CanAct(r) && !selectedPaths.Contains(r.Item.Path)) { selectedPaths = [r.Item.Path]; anchorKey = r.Key; }
        ctxItems = selectedPaths.Count > 1 && CanAct(r) ? SelectionMenu() : ItemMenu(r);
        ShowMenu(e);
    }

    private void OpenBlankMenu(MouseEventArgs e)
    {
        if (needsCredential) return;
        ClearSelection();
        ctxItems = BlankMenu();
        ShowMenu(e);
    }

    private void OpenSortMenu(MouseEventArgs e)
    {
        ctxItems = SortMenu();
        ShowMenu(e);
    }

    private void ShowMenu(MouseEventArgs e)
    {
        ctxOpen = true;
        ctxAt = (e.ClientX, e.ClientY);
    }

    private List<ContextMenu.Item> ItemMenu(Row r)
    {
        var it = r.Item;
        var act = CanAct(r);
        var list = new List<ContextMenu.Item>
        {
            new(it.IsDir ? "Open" : IsPreviewable(it.Name) ? "Quick Look" : "Open", it.IsDir ? "folder-open" : "eye", () => OpenRow(r)),
        };
        if (!it.IsDir && !isRecycleBin && IsCoAuthorable(it.Name) && r.Server == server)
            list.Add(new("Edit together", "users", async () => { TouchRecent(it); await JS.InvokeVoidAsync("open", GetEditorUrl(it), "_blank"); }));
        if (r.Server == server && !isRecycleBin && !IsRoot)
            list.Add(new("Open in File Explorer", "external-link", () => OpenInExplorerFor(it)));
        if (ShowLocation) list.Add(new("Show in enclosing folder", "folder", () => Reveal(r, open: false)));

        if (isRecycleBin && act)
        {
            list.Add(ContextMenu.Item.Divider());
            list.Add(new("Put back", "undo-2", () => RestoreSingle(it)));
            list.Add(new("Delete immediately", "trash-2", () => DeleteSinglePermanently(it), Danger: true));
            return list;
        }

        list.Add(ContextMenu.Item.Divider());
        if (!it.IsDir && r.Server == server) list.Add(new("Download", "download", () => Download(it)));
        list.Add(new(IsStarred(r) ? "Remove from Starred" : "Add to Starred", "star", () => ToggleStar(r)));
        list.Add(new("Get Info", "info", Do(() => { showInfoPane = true; infoItem = it; infoSite = r.Server; })));
        list.Add(new("Copy path", "copy", () => CopyText(it.Path, "Path copied")));
        if (act)
        {
            list.Add(new("Share link…", "link", () => OpenShareModal(it)));
            if (!it.IsDir) list.Add(new("Send by mail", "mail", Do(() => ShareViaMail(it))));
            list.Add(ContextMenu.Item.Divider());
            list.Add(new("Rename…", "pencil", Do(() => OpenRenameModal(it))));
            list.Add(new("Move or copy…", "folder-input", Do(() => OpenMoveCopyModal(it))));
            list.Add(ContextMenu.Item.Divider());
            list.Add(new("Move to Recycle Bin", "trash-2", () => DeleteSingle(it), Danger: true));
        }
        return list;
    }

    private List<ContextMenu.Item> SelectionMenu()
    {
        var n = selectedPaths.Count;
        if (isRecycleBin)
            return
            [
                ContextMenu.Item.Header($"{n} items"),
                new("Put back", "undo-2", RestoreSelected),
                new("Delete immediately", "trash-2", DeleteSelectedPermanently, Danger: true),
            ];
        return
        [
            ContextMenu.Item.Header($"{n} items"),
            new("Download as ZIP", "download", DownloadSelectedZip),
            new("Move or copy…", "folder-input", Do(OpenMoveCopySelected)),
            ContextMenu.Item.Divider(),
            new("Move to Recycle Bin", "trash-2", DeleteSelected, Danger: true),
        ];
    }

    private List<ContextMenu.Item> BlankMenu()
    {
        var list = new List<ContextMenu.Item>();
        var inFolder = !IsRoot && !isRecycleBin && !isStarredView && !isRecentView;
        if (inFolder)
        {
            list.Add(new("New folder", "folder-plus", Do(OpenNewFolderModal)));
            list.Add(new("Upload files…", "upload", PickFiles));
            list.Add(ContextMenu.Item.Divider());
        }
        if (isRecycleBin) { list.Add(new("Empty Recycle Bin", "trash-2", EmptyRecycleBin, Danger: true)); list.Add(ContextMenu.Item.Divider()); }
        list.Add(new("as List", "list", () => SetGridView(false), Checked: !gridView));
        list.Add(new("as Icons", "layout-grid", () => SetGridView(true), Checked: gridView));
        list.Add(ContextMenu.Item.Divider());
        list.AddRange(SortMenu());
        if (inFolder)
        {
            list.Add(ContextMenu.Item.Divider());
            list.Add(new("Copy folder path", "copy", () => CopyText(currentPath, "Path copied")));
            list.Add(new("Refresh", "refresh-cw", ForceRefresh));
        }
        return list;
    }

    private List<ContextMenu.Item> SortMenu() =>
    [
        ContextMenu.Item.Header("Sort by"),
        new("Name", "a-large-small", Do(() => SortBy("name")), Checked: sortBy.StartsWith("name")),
        new("Kind", "shapes", Do(() => SortBy("kind")), Checked: sortBy.StartsWith("kind")),
        new("Date modified", "clock", Do(() => { SortBy("modified-desc"); if (sortBy == "modified-desc-desc") sortBy = "modified"; }), Checked: sortBy.StartsWith("modified")),
        new("Size", "hard-drive", Do(() => { sortBy = sortBy == "size-desc" ? "size" : "size-desc"; }), Checked: sortBy.StartsWith("size")),
    ];

    private async Task CopyText(string text, string toast)
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", text);
            Toasts.Show(toast, ToastKind.Success);
        }
        catch (JSException) { Toasts.Show("Clipboard is blocked by the browser.", ToastKind.Error); }
    }

    // ── Drag & drop (NAS-side move/copy) ───────────────────────────────────────

    [JSInvokable]
    public void OnDragStart(string key)
    {
        var path = key[(key.IndexOf(':') + 1)..];
        if (!selectedPaths.Contains(path)) { selectedPaths = [path]; focusKey = anchorKey = key; StateHasChanged(); }
    }

    [JSInvokable]
    public async Task OnDropItems(string key, string destServer, string destPath, bool copy)
    {
        var srcPath = key[(key.IndexOf(':') + 1)..];
        var paths = selectedPaths.Contains(srcPath) ? selectedPaths.ToList() : [srcPath];
        var site = string.IsNullOrEmpty(destServer) ? server : destServer;
        // Dropping a folder into itself (or where it already is) does nothing, like Finder.
        paths.RemoveAll(p => site == server && (p == destPath || IsUnder(destPath, p) || ParentOf(p) == destPath));
        if (paths.Count == 0) return;
        moveCopyPaths = paths;
        targetServer = site;
        targetDestDir = destPath;
        moveCopyMode = copy ? "copy" : "move";
        await ExecuteMoveCopy();
    }

    // ── NAS search ─────────────────────────────────────────────────────────────

    private string searchScope = "folder"; // folder | site | all
    private List<Row> searchHits = [];
    private CancellationTokenSource? searchCts;
    private bool searchRunning;
    private int searchSitesLeft;
    private const int SearchHitCap = 500;

    private void OnSearchInput(ChangeEventArgs e)
    {
        search = e.Value?.ToString() ?? "";
        QueueSearch();
    }

    private void SetSearchScope(string scope)
    {
        if (searchScope == scope) return;
        searchScope = scope;
        QueueSearch();
    }

    private void CancelSearch(bool clearText)
    {
        searchCts?.Cancel();
        searchCts?.Dispose();
        searchCts = null;
        searchRunning = false;
        searchHits = [];
        if (clearText) search = "";
    }

    private void QueueSearch()
    {
        CancelSearch(clearText: false);
        if (!SearchMode) return;
        searchCts = new CancellationTokenSource();
        _ = RunSearch(search.Trim(), searchCts.Token);
    }

    private async Task RunSearch(string term, CancellationToken ct)
    {
        try { await Task.Delay(450, ct); } catch (OperationCanceledException) { return; }
        var sites = searchScope == "all"
            ? Servers.Select(s => s.Id).Where(id => id == server ? !needsCredential : SiteStatus(id).Css == "on").ToList()
            : needsCredential ? [] : [server];
        if (sites.Count == 0) return;
        searchRunning = true;
        searchSitesLeft = sites.Count;
        StateHasChanged();
        await Task.WhenAll(sites.Select(s => SearchSite(s, term, ct)));
        if (ct.IsCancellationRequested) return;
        searchRunning = false;
        StateHasChanged();
        await JS.InvokeVoidAsync("ipop.icons");
    }

    private async Task SearchSite(string site, string term, CancellationToken ct)
    {
        string? taskId = null;
        try
        {
            string[]? paths = searchScope == "folder" && site == server && currentPath != "/" && !isRecycleBin ? [currentPath] : null;
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/files/search", new { server = site, paths, pattern = term }, ct);
            if (!res.IsSuccessStatusCode) return;
            taskId = (await res.Content.ReadFromJsonAsync<SearchStartDto>(ct))?.TaskId;
            if (taskId is null) return;
            var offset = 0;
            var started = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
            {
                var page = await Auth.GetAsync<SearchPageDto>($"/api/files/search?server={site}&taskId={Uri.EscapeDataString(taskId)}&offset={offset}", ct);
                if (page is null || ct.IsCancellationRequested) return;
                var got = page.Items ?? [];
                offset += got.Count;
                if (got.Count > 0)
                {
                    searchHits = [.. searchHits, .. got.Select(i => new Row(site, i))];
                    StateHasChanged();
                    _ = JS.InvokeVoidAsync("ipop.icons");
                }
                if (searchHits.Count >= SearchHitCap || (page.Finished && offset >= page.Total)) break;
                if (got.Count == 0) await Task.Delay(page.Finished ? 150 : 600, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* one site failing leaves the others' results standing */ }
        finally
        {
            searchSitesLeft--;
            if (taskId is not null) _ = Auth.SendAsync(HttpMethod.Delete, $"/api/files/search?server={site}&taskId={Uri.EscapeDataString(taskId)}");
        }
    }

    // ── Uploads (XHR in js/files.js for byte progress) ─────────────────────────

    private List<UploadFile> uploads = [];
    private string uploadSite = "", uploadDir = "";
    private bool uploadCancelled;

    private async Task PickFiles()
    {
        if (fx is not null) await fx.InvokeVoidAsync("pickFiles");
    }

    private long UploadTotal => uploads.Sum(u => u.Size);
    private long UploadSent => uploads.Sum(u => u.Ok == true ? u.Size : u.Sent);
    private int UploadPercent => UploadTotal == 0 ? 0 : (int)(UploadSent * 100 / UploadTotal);

    [JSInvokable]
    public UploadPlan? BeginUpload(string[] names, long[] sizes, string destServer, string destPath)
    {
        var site = string.IsNullOrEmpty(destServer) ? server : destServer;
        var dir = string.IsNullOrEmpty(destPath) ? currentPath : destPath;
        if (uploading) { Toasts.Show("An upload is already running.", ToastKind.Info); return null; }
        if (dir == "/" || (string.IsNullOrEmpty(destPath) && (isRecycleBin || isStarredView || isRecentView)) || (site == server && needsCredential))
        {
            Toasts.Show("Open a folder first, then upload into it.", ToastKind.Info);
            return null;
        }
        var tooBig = names.Where((_, i) => sizes[i] > MaxUploadBytes).ToList();
        if (tooBig.Count > 0)
        {
            Toasts.Show($"{tooBig[0]} is larger than 100 MB. Copy big files with File Explorer.", ToastKind.Error);
            return null;
        }
        uploads = names.Select((n, i) => new UploadFile(n, sizes[i])).ToList();
        uploadSite = site;
        uploadDir = dir;
        uploadCancelled = false;
        uploading = true;
        StateHasChanged();
        return new UploadPlan(new Uri(Http.BaseAddress!, "/api/files/upload").ToString(), site, dir);
    }

    private const long MaxUploadBytes = 100L * 1024 * 1024;

    [JSInvokable]
    public async Task<string?> UploadToken(bool refresh)
    {
        if (refresh) await Auth.TryRestoreAsync();
        return Auth.AccessToken;
    }

    [JSInvokable]
    public void UploadProgress(int index, long sent)
    {
        if (index < 0 || index >= uploads.Count) return;
        uploads[index].Sent = sent;
        StateHasChanged();
    }

    [JSInvokable]
    public void UploadFileDone(int index, bool ok, int status, string error)
    {
        if (index < 0 || index >= uploads.Count) return;
        var u = uploads[index];
        u.Ok = ok;
        if (ok) u.Sent = u.Size;
        else u.Error = status switch
        {
            0 => string.IsNullOrEmpty(error) ? "Network error" : error,
            401 => "Sign in to this site again",
            413 => "Too large",
            _ => string.IsNullOrEmpty(error) ? $"Failed ({status})" : error,
        };
        StateHasChanged();
    }

    [JSInvokable]
    public async Task EndUpload(bool cancelled)
    {
        uploadCancelled = cancelled;
        var ok = uploads.Count(u => u.Ok == true);
        var failed = uploads.Where(u => u.Ok == false).ToList();
        FileStateService.InvalidateFolder(uploadSite, uploadDir);
        if (failed.Count == 0 && !cancelled)
        {
            uploading = false;
            Toasts.ShowDetailed("Uploaded", ok == 1 ? $"{uploads[0].Name} is in {ShareName(uploadDir)}." : $"{ok} files uploaded to {ShareName(uploadDir)}.", ToastKind.Success, "check");
        }
        // Failures keep the panel open so the user can read which file and why.
        StateHasChanged();
        if (uploadSite == server && uploadDir == currentPath) await Reload(bypassCache: true);
    }

    private async Task CancelUpload()
    {
        if (fx is not null) await fx.InvokeVoidAsync("cancelUpload");
    }

    private void DismissUploads()
    {
        uploading = false;
        uploads = [];
    }
}
