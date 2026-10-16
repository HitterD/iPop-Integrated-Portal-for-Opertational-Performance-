// Notes page — sidebar editing: one-click new page, row menu (⋯ / right-click / long-press), inline
// rename, drag & drop (sidebar.js in the editor bundle), "Move to…", duplicate, collapsible sections
// and the Trash undo toast.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Notes;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notes
{
    private ElementReference sideRef, titleRef, renameRef, menuRef, moveRef;
    private IJSObjectReference? sidebarJs;

    private HashSet<string> collapsed = [];
    private bool guideHidden;

    private NoteListItemDto? menuFor;
    private bool newMenuOpen, placeMenu;
    private int menuX, menuY;

    private Guid? renamingId;
    private string renameDraft = "";
    private bool focusRename;

    private NoteListItemDto? moveFor;
    private string moveQ = "";
    private bool focusMove;

    /// Set by a one-click "New page": focus the title once that page has loaded.
    private Guid? focusTitleFor;

    private const string SectionsPref = "notes.sections", GuidePref = "notes.guideHidden";

    private async Task SidebarFirstRenderAsync()
    {
        try
        {
            if (await JS.InvokeAsync<string?>("ipop.prefGet", SectionsPref) is { } json)
                collapsed = JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
            guideHidden = await JS.InvokeAsync<string?>("ipop.prefGet", GuidePref) == "true";
        }
        catch (JsonException) { }
        if (!Phone.IsPhone) sidebarJs = await module!.InvokeAsync<IJSObjectReference>("sidebar", sideRef, selfRef);
        if (collapsed.Count > 0 || guideHidden) StateHasChanged();
    }

    private async Task SidebarAfterRenderAsync()
    {
        if (placeMenu && module is not null) { placeMenu = false; await module.InvokeVoidAsync("place", menuRef); }
        if (focusRename) { focusRename = false; try { if (module is not null) await module.InvokeVoidAsync("focusSelect", renameRef); } catch (JSException) { } }
        if (focusMove) { focusMove = false; try { await moveRef.FocusAsync(); } catch (JSException) { } }
        if (focusTitleFor is { } fid && note?.Id == fid && view == View.Page)
        {
            focusTitleFor = null;
            try { await titleRef.FocusAsync(); } catch (JSException) { }
        }
    }

    private async Task DisposeSidebarAsync()
    {
        if (sidebarJs is null) return;
        await sidebarJs.InvokeVoidAsync("dispose");
        await sidebarJs.DisposeAsync();
    }

    // ── Ordering (mirrors NoteService.Arrange) ───────────────────────────────

    private List<NoteListItemDto> Children(Guid id) =>
        sidebar?.Pages.Where(p => p.ParentId == id)
            .OrderBy(p => p.SortOrder <= 0).ThenBy(p => p.SortOrder)
            .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];

    /// Root rows: pages without a visible parent. Mine: new (unplaced) pages on top, newest first, then the
    /// manual order; shared with me: alphabetical.
    private List<NoteListItemDto> Roots(bool mine)
    {
        var roots = sidebar?.Pages.Where(p => p.ParentId is null && p.IsMine == mine) ?? [];
        return mine
            ? roots.OrderBy(p => p.SortOrder > 0).ThenByDescending(p => p.SortOrder > 0 ? DateTime.MinValue : p.UpdatedAt).ThenBy(p => p.SortOrder).ToList()
            : roots.OrderBy(p => DisplayTitle(p.Title), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ── Sections / guide ─────────────────────────────────────────────────────

    private async Task ToggleSection(string key)
    {
        if (!collapsed.Remove(key)) collapsed.Add(key);
        await JS.InvokeVoidAsync("ipop.prefSet", SectionsPref, JsonSerializer.Serialize(collapsed));
    }

    /// Hidden by choice, or automatically once every step is done; the Notes home still lists it.
    private bool GuideHidden(NoteListItemDto g) => guideHidden || (g.TodoTotal > 0 && g.TodoDone >= g.TodoTotal);

    private async Task HideGuide(bool hide)
    {
        menuFor = null;
        guideHidden = hide;
        await JS.InvokeVoidAsync("ipop.prefSet", GuidePref, hide ? "true" : "false");
        if (hide) Toasts.Show("“Start here” hidden — you can still open it from the Notes home", ToastKind.Info);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    /// One click: a blank page (under <paramref name="parent"/>), opened with the title focused.
    private async Task CreateBlank(Guid? parent)
    {
        if (creating) return;
        creating = true; newMenuOpen = false; menuFor = null; pop = Pop.None;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/notes", new CreateNoteRequest(parent, "", null, null, null, null));
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't create the page", ToastKind.Error); return; }
            var created = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
            if (created is null) return;
            if (parent is { } p) expanded.Add(p);
            await LoadSidebarAsync();
            sideOpen = false;
            focusTitleFor = created.Id;
            Nav.NavigateTo($"/notes/{created.Id}");
        }
        finally { creating = false; }
    }

    private void OpenNewMenu(MouseEventArgs e)
    {
        menuFor = null;
        newMenuOpen = !newMenuOpen;
        (menuX, menuY) = ((int)e.ClientX - 200, (int)e.ClientY + 14);
        placeMenu = newMenuOpen;
    }

    private async Task CreateFromTemplate(string key)
    {
        newMenuOpen = false;
        newParent = null; newTitle = "";
        await CreatePage(key);
    }

    // ── Row menu ─────────────────────────────────────────────────────────────

    private void OpenRowMenu(NoteListItemDto p, MouseEventArgs e)
    {
        newMenuOpen = false;
        if (menuFor?.Id == p.Id) { menuFor = null; return; }
        menuFor = p;
        (menuX, menuY) = ((int)e.ClientX - 8, (int)e.ClientY + 12);
        placeMenu = true;
    }

    // A long-press opens the menu while the finger is still down; that same touch then ends in a
    // click on the freshly rendered scrim, which closed the menu at once ("long-press does nothing").
    private DateTime rowMenuOpenedAt;
    private void CloseRowMenu() { if ((DateTime.UtcNow - rowMenuOpenedAt).TotalMilliseconds < 600) return; menuFor = null; }

    [JSInvokable]
    public void OnRowMenu(string id, int x, int y)
    {
        if (!Guid.TryParse(id, out var g) || sidebar?.Pages.FirstOrDefault(p => p.Id == g) is not { } p) return;
        newMenuOpen = false;
        menuFor = p;
        rowMenuOpenedAt = DateTime.UtcNow;
        (menuX, menuY) = (x, y);
        placeMenu = true;
        StateHasChanged();
    }

    private void OnRowKey(KeyboardEventArgs e, NoteListItemDto p)
    {
        if (e.Key == "F2") StartRename(p);
        else if (e.Key == "Enter") Open(p.Id);
    }

    private async Task SetFavorite(NoteListItemDto p, bool fav)
    {
        menuFor = null;
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{p.Id}/favorite", new { favorite = fav });
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't update favorites", ToastKind.Error); return; }
        if (note?.Id == p.Id) note = note with { IsFavorite = fav };
        await LoadSidebarAsync();
    }

    private async Task Duplicate(NoteListItemDto p)
    {
        menuFor = null;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{p.Id}/duplicate");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't duplicate the page", ToastKind.Error); return; }
        var copy = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
        if (copy is null) return;
        if (copy.ParentId is { } parent) expanded.Add(parent);
        await LoadSidebarAsync();
        Toasts.Show($"Duplicated as “{DisplayTitle(copy.Title)}”", ToastKind.Success);
        Open(copy.Id);
    }

    private async Task CopyLinkOf(Guid id)
    {
        menuFor = null;
        if (module is null) return;
        var ok = await module.InvokeAsync<bool>("copyText", Nav.ToAbsoluteUri($"/notes/{id}").ToString());
        Toasts.Show(ok ? "Link copied — only people with access can open it" : "Couldn't copy the link", ok ? ToastKind.Success : ToastKind.Error);
    }

    private async Task OpenInNewTab(Guid id)
    {
        menuFor = null;
        if (module is not null) await module.InvokeVoidAsync("openTab", Nav.ToAbsoluteUri($"/notes/{id}").ToString());
    }

    // ── Rename ───────────────────────────────────────────────────────────────

    private void StartRename(NoteListItemDto p)
    {
        menuFor = null;
        if (p.Access is not ("owner" or "edit") || p.IsGuide) return;
        renamingId = p.Id;
        renameDraft = p.Title;
        focusRename = true;
    }

    private async Task OnRenameKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await CommitRename();
        else if (e.Key == "Escape") renamingId = null;
    }

    private async Task CommitRename()
    {
        if (renamingId is not { } id) return;
        renamingId = null;
        var old = sidebar?.Pages.FirstOrDefault(p => p.Id == id);
        var title = renameDraft.Trim();
        if (old is null || title == old.Title.Trim()) return;
        // Show it right away; the PATCH and the live refresh confirm it.
        sidebar = sidebar! with { Pages = sidebar.Pages.Select(p => p.Id == id ? p with { Title = title } : p).ToList() };
        var res = await Auth.SendAsync(HttpMethod.Patch, $"/api/notes/{id}", new UpdateNoteMetaRequest(title, null, null, null, null, null, null));
        if (!res.IsSuccessStatusCode) Toasts.Show("Couldn't rename the page", ToastKind.Error);
        else if (note?.Id == id && await res.Content.ReadFromJsonAsync<NotePageDto>(Json) is { } updated)
        {
            note = updated with { IsFavorite = note.IsFavorite };
            if (titleCts is null) titleDraft = updated.Title;
        }
        await LoadSidebarAsync();
    }

    // ── Move / reorder ───────────────────────────────────────────────────────

    private void OpenMoveDialog(NoteListItemDto p)
    {
        menuFor = null;
        moveFor = p; moveQ = "";
        focusMove = true;
    }

    /// Pages the caller can edit, minus the page itself, its sub-pages and its current parent.
    private List<NoteListItemDto> MoveTargets(NoteListItemDto mv)
    {
        if (sidebar is null) return [];
        var below = new HashSet<Guid> { mv.Id };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var p in sidebar.Pages)
                if (p.ParentId is { } par && below.Contains(par) && below.Add(p.Id)) grew = true;
        }
        var q = moveQ.Trim();
        return sidebar.Pages
            .Where(p => p.Access is "owner" or "edit" && !below.Contains(p.Id) && p.Id != mv.ParentId)
            .Where(p => q.Length == 0 || DisplayTitle(p.Title).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(p => p.IsMine).ThenBy(p => DisplayTitle(p.Title), StringComparer.CurrentCultureIgnoreCase)
            .Take(50).ToList();
    }

    private async Task OnMoveKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") moveFor = null;
        else if (e.Key == "Enter" && moveFor is { } mv && MoveTargets(mv).FirstOrDefault() is { } t) await MoveTo(mv, t.Id, null);
    }

    /// Drop from sidebar.js: zone is "inside" (make it a sub-page), "before"/"after" (reorder) or "trash".
    [JSInvokable]
    public async Task OnRowDrop(string dragId, string? targetId, string zone)
    {
        if (sidebar is null || !Guid.TryParse(dragId, out var id) || sidebar.Pages.FirstOrDefault(p => p.Id == id) is not { } page) return;
        if (zone == "trash") { await TrashById(id); StateHasChanged(); return; }
        if (!Guid.TryParse(targetId, out var tid) || sidebar.Pages.FirstOrDefault(p => p.Id == tid) is not { } target) return;

        if (zone == "inside") await MoveTo(page, tid, null);
        else
        {
            var siblings = (target.ParentId is { } par ? Children(par) : Roots(target.IsMine)).Where(p => p.Id != id).ToList();
            var at = siblings.FindIndex(p => p.Id == tid);
            var before = zone == "before" ? tid : at + 1 < siblings.Count ? siblings[at + 1].Id : (Guid?)null;
            await MoveTo(page, target.ParentId, before);
        }
        StateHasChanged();
    }

    private async Task MoveTo(NoteListItemDto page, Guid? parentId, Guid? beforeId, bool confirm = false)
    {
        moveFor = null;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{page.Id}/move", new MoveNoteRequest(parentId, beforeId, confirm));
        if (!res.IsSuccessStatusCode)
        {
            var code = await ErrorCodeAsync(res);
            if (code == "confirm_audience")
            {
                var where = parentId is { } np ? $"“{DisplayTitle(sidebar?.Pages.FirstOrDefault(p => p.Id == np)?.Title)}”" : "My pages";
                if (await Confirm.AskAsync("Change who can see this page?",
                        $"“{DisplayTitle(page.Title)}” and its sub-pages will follow the sharing of {where}, so some people may gain or lose access.",
                        "Move", danger: false))
                    await MoveTo(page, parentId, beforeId, true);
                return;
            }
            Toasts.Show(code switch
            {
                "forbidden" when parentId != page.ParentId => "Only the page's owner can move it, into a page they can edit",
                "forbidden" => "You need edit access here to reorder pages",
                "invalid_move" => "A page can't go inside its own sub-page",
                _ => "Couldn't move the page",
            }, ToastKind.Error);
            return;
        }
        if (parentId != page.ParentId) await LeaveRowAsync(page.Id);
        if (parentId is { } parent) expanded.Add(parent);
        await LoadSidebarAsync();
        await RefreshNoteAsync();
    }

    /// Micro-interaction #9: the page's tree row (with its sub-pages) slides out before the sidebar reloads.
    private async Task LeaveRowAsync(Guid id)
    {
        try { await JS.InvokeVoidAsync("ipopMicro.leaveSelector", $".nt-side [data-nt-node=\"{id}\"]"); }
        catch (JSException) { /* motion is decoration */ }
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage res)
    {
        try { return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString(); }
        catch (Exception) { return res.StatusCode == HttpStatusCode.Forbidden ? "forbidden" : null; }
    }

    /// Breadcrumb and sub-page list of the open page may have changed; refetch it without remounting the editor.
    private async Task RefreshNoteAsync()
    {
        if (note is not { } cur) return;
        var fresh = await Auth.GetAsync<NotePageDto>($"/api/notes/{cur.Id}");
        if (fresh is not null && note?.Id == cur.Id) note = fresh;
    }

    // ── Trash ────────────────────────────────────────────────────────────────

    /// Moves a page (and its sub-pages) to Trash with an Undo toast; leaves it if it was open.
    private async Task TrashById(Guid id)
    {
        menuFor = null; pop = Pop.None;
        var page = sidebar?.Pages.FirstOrDefault(p => p.Id == id);
        var title = DisplayTitle(page?.Title ?? (note?.Id == id ? note.Title : null));
        var wasOpen = note is { } n && (n.Id == id || n.Breadcrumb.Any(c => c.Id == id));
        var openId = note?.Id;
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notes/{id}");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't delete the page", ToastKind.Error); return; }
        await LeaveRowAsync(id);
        await LoadSidebarAsync();
        if (wasOpen)
        {
            var parent = page?.ParentId ?? (note?.Id == id ? note.ParentId : null);
            Nav.NavigateTo(parent is { } pid && sidebar?.Pages.Any(x => x.Id == pid) == true ? $"/notes/{pid}" : "/notes");
        }
        else await RefreshNoteAsync();

        Toasts.ShowUndo($"“{title}” moved to Trash", async () =>
        {
            var r = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{id}/restore");
            if (!r.IsSuccessStatusCode) { Toasts.Show("Couldn't restore the page", ToastKind.Error); return; }
            await InvokeAsync(async () =>
            {
                await LoadSidebarAsync();
                if (view == View.Trash) await LoadTrashAsync();
                if (wasOpen && openId is { } back && Id is null) Nav.NavigateTo($"/notes/{back}");
                else await RefreshNoteAsync();
                StateHasChanged();
            });
        }, ms: 6000);
    }
}
