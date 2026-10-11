// Notes page — state, lifecycle, sidebar, page meta and the editor bridge.
//
// The document itself (TipTap + Yjs, real-time sync over /hubs/notes) lives in
// wwwroot/js/notes/notes-editor.js, built from tools/notes-editor. This class owns everything around
// it: sidebar tree, title/emoji/style (REST PATCH), comments panel, sharing and version history.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IPop.Application.Notes;
using IPop.Web.Components.Admin;
using IPop.Web.Models;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notes
{
    [Parameter] public Guid? Id { get; set; }

    private const string EditorModule = "./js/notes/notes-editor.js?v=20261011-mention-search";

    private enum View { Home, Page, New, Trash }
    private enum Pop { None, More, Style, Emoji, Share, History }

    private View view = View.Home;
    private Pop pop = Pop.None;
    private bool sideOpen, commentsOpen, showResolved, busy, creating, showTemplates;

    private NoteSidebarDto? sidebar;
    private readonly HashSet<Guid> expanded = [];
    private NotePageDto? note;
    private string? pageError;
    private string titleDraft = "";
    private CancellationTokenSource? titleCts;
    private string saveState = "saved";
    private List<PresenceUser> presence = [];

    private string searchQ = "";
    private List<NoteSearchHitDto>? hits;
    private CancellationTokenSource? searchCts;
    private List<NoteListItemDto>? trash;

    // New page view
    private Guid? newParent;
    private string newTitle = "";
    private string newAccent = "blue";

    private ElementReference editorRef, scrollRef, searchRef, newTitleRef, draftRef, previewRef;
    private IJSObjectReference? module, editor, hotkeys, previewer;
    [CascadingParameter] public IPop.Web.Layout.MainLayout? Layout { get; set; }
    private DotNetObjectReference<Notes>? selfRef;
    private Guid? mountedId, loadingId;
    private bool mountPending, focusNewTitle, focusDraft, focusSearch;
    private readonly List<IDisposable> subs = [];
    private bool disposed;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private bool CanEdit => note?.Access is "owner" or "edit";
    private bool CanComment => CanEdit || note?.Access == "comment";
    private bool IsOwner => note?.Access == "owner";
    private Guid? GuideId => sidebar?.Pages.FirstOrDefault(p => p.IsGuide && p.IsMine)?.Id;
    private string FontClass => note?.Font is "serif" or "mono" ? $"f-{note.Font}" : "";

    public record PresenceUser(string Name, string? Nik, string Color);

    // ── Lifecycle ────────────────────────────────────────────────────────────

    protected override async Task OnInitializedAsync()
    {
        selfRef = DotNetObjectReference.Create(this);
        subs.Add(Auth.On("notesChanged", () => _ = InvokeAsync(async () => { await LoadSidebarAsync(); if (view == View.Trash) await LoadTrashAsync(); StateHasChanged(); })));
        await LoadSidebarAsync();
    }

    private Guid? lastId;
    private bool paramsSeen;

    protected override async Task OnParametersSetAsync()
    {
        // The cascading MainLayout re-runs this on every layout render — only react to a real route change.
        if (paramsSeen && lastId == Id) return;
        paramsSeen = true;
        lastId = Id;
        pop = Pop.None;
        sideOpen = false;
        if (Id is { } id)
        {
            view = View.Page;
            if (loadingId == id || (note?.Id == id && mountedId == id)) return;
            await LoadPageAsync(id);
        }
        else if (view == View.Page)
        {
            await UnmountAsync();
            note = null;
            view = View.Home;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        if (firstRender)
        {
            Layout?.SetHeader("Notes", "Shared notes & docs");
            module = await JS.InvokeAsync<IJSObjectReference>("import", EditorModule);
            hotkeys = await module.InvokeAsync<IJSObjectReference>("hotkeys", selfRef);
            await SidebarFirstRenderAsync();
            await ImportFirstRenderAsync();
            if (note is not null) mountPending = true;
        }
        await JS.InvokeVoidAsync("ipop.icons");
        if (mountPending && module is not null && note is not null && view == View.Page)
        {
            mountPending = false;
            await MountAsync(note);
        }
        if (focusNewTitle) { focusNewTitle = false; await newTitleRef.FocusAsync(); }
        if (focusDraft) { focusDraft = false; try { await draftRef.FocusAsync(); } catch (JSException) { } }
        if (focusSearch) { focusSearch = false; await searchRef.FocusAsync(); }
        await SidebarAfterRenderAsync();
        await ImportAfterRenderAsync();
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        foreach (var s in subs) s.Dispose();
        titleCts?.Cancel();
        searchCts?.Cancel();
        try
        {
            await UnmountAsync();
            await DestroyPreviewAsync();
            await DisposeSidebarAsync();
            await DisposeTransferAsync();
            if (hotkeys is not null) { await hotkeys.InvokeVoidAsync("dispose"); await hotkeys.DisposeAsync(); }
            if (module is not null) await module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        selfRef?.Dispose();
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private async Task LoadSidebarAsync()
    {
        // Last-seen page tree renders at once; the fetch replaces it (memory only).
        sidebar ??= Cache.Peek<NoteSidebarDto>("notes.sidebar");
        if (sidebar is not null) StateHasChanged();
        var fresh = await Auth.GetAsync<NoteSidebarDto>("/api/notes/sidebar");
        if (fresh is not null) Cache.Put("notes.sidebar", fresh);
        sidebar = fresh ?? sidebar;
        if (note is not null) ExpandTo(note);
        if (editor is not null && sidebar is not null)
            await editor.InvokeVoidAsync("setPages", sidebar.Pages.Select(p => new { id = p.Id, title = DisplayTitle(p.Title), emoji = p.Emoji ?? "📄" }));
    }

    private async Task LoadPageAsync(Guid id)
    {
        loadingId = id;
        try { await LoadPageCoreAsync(id); }
        finally { if (loadingId == id) loadingId = null; }
    }

    private async Task LoadPageCoreAsync(Guid id)
    {
        await UnmountAsync();
        note = null; pageError = null; threads = []; draft = null; focusThread = null;
        presence = []; saveState = "saved"; showTemplates = false;
        StateHasChanged();
        var res = await Auth.SendAsync(HttpMethod.Get, $"/api/notes/{id}");
        if (Id != id) return; // navigated away meanwhile
        if (!res.IsSuccessStatusCode)
        {
            pageError = res.StatusCode == HttpStatusCode.NotFound ? "not_found" : "forbidden";
            return;
        }
        note = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
        if (note is null) { pageError = "not_found"; return; }
        _ = Notifs.MarkReadByTargetAsync("note", id);
        titleDraft = note.Title;
        ExpandTo(note);
        mountPending = true;
        _ = LoadThreadsAsync();
    }

    private void ExpandTo(NotePageDto p)
    {
        foreach (var c in p.Breadcrumb) expanded.Add(c.Id);
    }

    // ── Editor bridge ────────────────────────────────────────────────────────

    private async Task MountAsync(NotePageDto p)
    {
        await UnmountAsync();
        var me = Auth.Me;
        var opts = new
        {
            apiBase = Http.BaseAddress?.ToString() ?? "",
            pageId = p.Id,
            access = p.Access,
            user = new { name = me?.Name ?? "Me", nik = me?.Nik ?? "", color = AvatarPalette.For(me?.Nik) },
            accent = p.Accent,
            pages = (sidebar?.Pages ?? []).Select(x => new { id = x.Id, title = DisplayTitle(x.Title), emoji = x.Emoji ?? "📄" }),
            dotnet = selfRef,
        };
        try
        {
            mountedId = p.Id;
            var ed = await module!.InvokeAsync<IJSObjectReference>("mount", editorRef, opts);
            if (disposed || note?.Id != p.Id) { await ed.InvokeVoidAsync("destroy"); await ed.DisposeAsync(); return; }
            editor = ed;
            // #12: files from Windows dropped on the page show the shared drop target (the editor
            // already uploads dropped/pasted files and images itself).
            if (p.Access is "owner" or "edit")
                try { await JS.InvokeVoidAsync("ipopMicro.registerDrop", editorRef, "Drop to add to the page", "Images go inline, other files as attachments"); }
                catch (JSException) { }
            await PushThreadsAsync();
            await RefreshEmptyAsync();
            StateHasChanged();
        }
        catch (JSException ex)
        {
            mountedId = null;
            if (ex.Message.Contains("forbidden")) pageError = "forbidden";
            else Toasts.Show("Couldn't open the editor. Try reloading the page.", ToastKind.Error);
            StateHasChanged();
        }
    }

    private async Task UnmountAsync()
    {
        mountedId = null;
        var ed = editor;
        editor = null;
        if (ed is null) return;
        try { await ed.InvokeVoidAsync("destroy"); await ed.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private async Task RefreshEmptyAsync()
    {
        if (editor is null) return;
        showTemplates = CanEdit && await editor.InvokeAsync<bool>("isEmpty");
    }

    private async Task ApplyTemplate(string key)
    {
        if (editor is null) return;
        if (await editor.InvokeAsync<bool>("applyTemplate", key)) showTemplates = false;
    }

    [JSInvokable]
    public async Task<string?> GetToken()
    {
        if (TokenExpiresSoon(Auth.AccessToken)) await Auth.TryRestoreAsync();
        return Auth.AccessToken;
    }

    /// The access token is a JWT; refresh a minute early so the editor's own fetches never 401.
    private static bool TokenExpiresSoon(string? jwt)
    {
        try
        {
            var part = jwt?.Split('.') is { Length: 3 } s ? s[1] : null;
            if (part is null) return false;
            part = part.Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
            return doc.RootElement.TryGetProperty("exp", out var exp)
                && DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow.AddSeconds(60);
        }
        catch { return false; }
    }

    [JSInvokable]
    public void OnStatus(string s)
    {
        saveState = s;
        if (s == "saving" && showTemplates) showTemplates = false;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnPresence(List<PresenceUser> users)
    {
        presence = users.GroupBy(u => u.Nik ?? u.Name).Select(g => g.First()).ToList();
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnProgress(int done, int total)
    {
        if (note is null) return;
        note = note with { TodoDone = done, TodoTotal = total };
        if (sidebar is not null)
            sidebar = sidebar with { Pages = sidebar.Pages.Select(x => x.Id == note.Id ? x with { TodoDone = done, TodoTotal = total } : x).ToList() };
        await RefreshEmptyAsync();
        StateHasChanged();
    }

    [JSInvokable] public void OnOpenPage(string id) { if (Guid.TryParse(id, out var g)) Open(g); }
    [JSInvokable] public void OnToast(string msg) => Toasts.Show(msg, ToastKind.Info);

    [JSInvokable]
    public void OnHotkey(string key)
    {
        if (key == "new") _ = CreateBlank(null);
        StateHasChanged();
    }

    [JSInvokable]
    public async Task<object?> CreateSubPage()
    {
        if (note is null || !CanEdit) return null;
        var res = await Auth.SendAsync(HttpMethod.Post, "/api/notes", new CreateNoteRequest(note.Id, null, "📄", null, null, null));
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't create the sub-page", ToastKind.Error); return null; }
        var created = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
        if (created is null) return null;
        expanded.Add(note.Id);
        note = note with { Children = [.. note.Children, new NoteCrumbDto(created.Id, created.Title, created.Emoji)] };
        await LoadSidebarAsync();
        StateHasChanged();
        return new { id = created.Id, title = DisplayTitle(created.Title), emoji = created.Emoji ?? "📄" };
    }

    [JSInvokable]
    public async Task SetAccent(string k) => await PatchAsync(new UpdateNoteMetaRequest(null, null, null, k, null, null, null), fromEditor: true);

    [JSInvokable]
    public void OnMeta(NotePageDto m)
    {
        if (note?.Id != m.Id) return;
        // Access and favourite are per-user; the broadcast carries the editor's view of them.
        note = m with { Access = note.Access, IsFavorite = note.IsFavorite };
        if (titleCts is null) titleDraft = m.Title;
        _ = editor?.InvokeVoidAsync("setAccent", m.Accent);
        StateHasChanged();
    }

    // ── Meta ─────────────────────────────────────────────────────────────────

    private async Task PatchAsync(UpdateNoteMetaRequest req) => await PatchAsync(req, false);

    private async Task PatchAsync(UpdateNoteMetaRequest req, bool fromEditor)
    {
        if (note is null || !CanEdit) return;
        var id = note.Id;
        var res = await Auth.SendAsync(HttpMethod.Patch, $"/api/notes/{id}", req);
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't save your changes", ToastKind.Error); return; }
        var updated = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
        if (updated is null || note?.Id != id) return;
        note = updated with { IsFavorite = note.IsFavorite };
        if (req.Accent is not null && !fromEditor && editor is not null) await editor.InvokeVoidAsync("setAccent", req.Accent);
        if (req.Title is not null || req.Emoji is not null) await LoadSidebarAsync();
        StateHasChanged();
    }

    private Task SetAccentAsync(string k) => PatchAsync(new UpdateNoteMetaRequest(null, null, null, k, null, null, null));

    private async Task SetEmoji(string e)
    {
        pop = Pop.None;
        await PatchAsync(new UpdateNoteMetaRequest(null, e, null, null, null, null, null));
    }

    private void OnTitleInput(ChangeEventArgs e)
    {
        titleDraft = e.Value?.ToString() ?? "";
        titleCts?.Cancel();
        var cts = titleCts = new CancellationTokenSource();
        _ = Task.Delay(600, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _ = InvokeAsync(FlushTitle); }, TaskScheduler.Default);
    }

    private async Task FlushTitle()
    {
        titleCts?.Cancel();
        titleCts = null;
        if (note is null || !CanEdit || titleDraft == note.Title) return;
        await PatchAsync(new UpdateNoteMetaRequest(titleDraft, null, null, null, null, null, null));
    }

    private async Task OnTitleKey(KeyboardEventArgs e)
    {
        if (e.Key is "Enter" or "ArrowDown" && editor is not null)
        {
            await FlushTitle();
            await editor.InvokeVoidAsync("focus");
        }
    }

    private async Task ToggleFavorite()
    {
        if (note is null) return;
        var fav = !note.IsFavorite;
        note = note with { IsFavorite = fav };
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{note.Id}/favorite", new { favorite = fav });
        if (!res.IsSuccessStatusCode) { note = note with { IsFavorite = !fav }; Toasts.Show("Couldn't update favorites", ToastKind.Error); }
        await LoadSidebarAsync();
    }

    private async Task TrashPage()
    {
        if (note is not null) await TrashById(note.Id);
    }

    private async Task CopyLink()
    {
        if (note is null || module is null) return;
        var ok = await module.InvokeAsync<bool>("copyText", Nav.ToAbsoluteUri($"/notes/{note.Id}").ToString());
        Toasts.Show(ok ? "Link copied — only people with access can open it" : "Couldn't copy the link", ok ? ToastKind.Success : ToastKind.Error);
        if (pop == Pop.More) pop = Pop.None;
    }

    // ── Navigation / sidebar ─────────────────────────────────────────────────

    private void Open(Guid id)
    {
        sideOpen = false;
        if (Id == id && view == View.Page) return;
        Nav.NavigateTo($"/notes/{id}");
    }

    private void GoHome() => Nav.NavigateTo("/notes");

    private bool IsActive(Guid id) => view == View.Page && note?.Id == id;

    private void ToggleExpand(Guid id) { if (!expanded.Remove(id)) expanded.Add(id); }

    private async Task OpenTrash()
    {
        await UnmountAsync();
        note = null;
        view = View.Trash; sideOpen = false; trash = null;
        if (Id is not null) Nav.NavigateTo("/notes");
        view = View.Trash;
        await LoadTrashAsync();
    }

    private async Task LoadTrashAsync() => trash = await Auth.GetAsync<List<NoteListItemDto>>("/api/notes/trash") ?? [];

    private async Task RestorePage(Guid id)
    {
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{id}/restore");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't restore the page", ToastKind.Error); return; }
        Toasts.Show("Page restored", ToastKind.Success);
        await LoadSidebarAsync();
        await LoadTrashAsync();
    }

    private async Task PurgePage(NoteListItemDto t)
    {
        if (!await Confirm.AskAsync("Delete forever?", $"“{DisplayTitle(t.Title)}” and its sub-pages, comments and history will be deleted permanently.", "Delete forever")) return;
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notes/{t.Id}/purge");
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't delete", ToastKind.Error); return; }
        await LoadTrashAsync();
    }

    private void OpenNewPage(Guid? parent)
    {
        pop = Pop.None; sideOpen = false;
        newParent = parent; newTitle = ""; newAccent = "blue";
        if (Id is not null) Nav.NavigateTo("/notes");
        _ = UnmountAsync();
        note = null;
        view = View.New;
        focusNewTitle = true;
    }

    private async Task OnNewTitleKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await CreatePage(null);
        else if (e.Key == "Escape") view = View.Home;
    }

    private async Task CreatePage(string? template)
    {
        if (creating) return;
        creating = true;
        try
        {
            var t = Templates.FirstOrDefault(x => x.Key == template);
            var title = string.IsNullOrWhiteSpace(newTitle) ? t?.Name ?? "" : newTitle.Trim();
            var res = await Auth.SendAsync(HttpMethod.Post, "/api/notes",
                new CreateNoteRequest(newParent, title, t?.Emoji ?? "📄", template, t?.Accent, t?.Cover));
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't create the page", ToastKind.Error); return; }
            var created = await res.Content.ReadFromJsonAsync<NotePageDto>(Json);
            if (created is null) return;
            if (newParent is { } p) expanded.Add(p);
            await LoadSidebarAsync();
            Nav.NavigateTo($"/notes/{created.Id}");
        }
        finally { creating = false; }
    }

    private void OnSearchInput(ChangeEventArgs e)
    {
        searchQ = e.Value?.ToString() ?? "";
        searchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(searchQ)) { hits = null; return; }
        hits = null;
        var cts = searchCts = new CancellationTokenSource();
        var q = searchQ.Trim();
        _ = Task.Delay(250, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            var r = await Auth.GetAsync<List<NoteSearchHitDto>>($"/api/notes/search?q={Uri.EscapeDataString(q)}");
            if (cts.IsCancellationRequested) return;
            await InvokeAsync(() => { hits = r ?? []; StateHasChanged(); });
        }, TaskScheduler.Default);
    }

    private void OnSearchKey(KeyboardEventArgs e)
    {
        if (e.Key == "Escape") ClearSearch();
        else if (e.Key == "Enter" && hits is { Count: > 0 }) { var id = hits[0].Id; ClearSearch(); Open(id); }
    }

    private void ClearSearch() { searchQ = ""; hits = null; searchCts?.Cancel(); }

    private void ClosePop()
    {
        if (pop == Pop.History) _ = DestroyPreviewAsync();
        pop = Pop.None;
    }

    // ── Formatting helpers ───────────────────────────────────────────────────

    private static string DisplayTitle(string? t) => string.IsNullOrWhiteSpace(t) ? "Untitled" : t;
    /// Versions list contributors by name only; match them to a NIK from people already on screen.
    private string? ContributorNik(string name) =>
        presence.FirstOrDefault(u => u.Name == name)?.Nik
        ?? (shares is { } sh ? (sh.OwnerName == name ? sh.OwnerNik : sh.Shares.FirstOrDefault(s => s.UserName == name)?.UserNik) : null)
        ?? (Auth.Me?.Name == name ? Auth.Me?.Nik : null);
    private static string FirstName(string? name) => IPop.Application.PersonName.Short(name);
    private static string LevelLabel(string level) => level switch { "edit" => "Can edit", "comment" => "Can comment", _ => "Can view" };

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} hr ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} days ago";
        return ShortDate(utc);
    }

    // en-US month abbreviations, with "Sept" as used elsewhere in the English UI.
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sept", "Oct", "Nov", "Dec"];

    /// "Sept 21" this year, "Sept 21, 2025" otherwise (local time).
    private static string ShortDate(DateTime utc)
    {
        var l = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var s = $"{Months[l.Month - 1]} {l.Day}";
        return l.Year == DateTime.Now.Year ? s : $"{s}, {l.Year}";
    }

    /// "Sept 21, 2026, 14:05" — version history timestamps.
    private static string StampDate(DateTime utc)
    {
        var l = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return $"{Months[l.Month - 1]} {l.Day}, {l.Year}, {l:HH:mm}";
    }

    private static readonly Dictionary<string, string> Accents = new()
    {
        ["blue"] = "#0A84FF", ["indigo"] = "#5856D6", ["purple"] = "#AF52DE", ["pink"] = "#FF2D55", ["red"] = "#FF3B30",
        ["orange"] = "#FF9500", ["yellow"] = "#FFCC00", ["green"] = "#34C759", ["teal"] = "#30B0C7", ["gray"] = "#8E8E93",
    };
    private static string AccentHex(string? k) => Accents.GetValueOrDefault(k ?? "", "#0A84FF");

    private static readonly string[] Covers = ["ocean", "sunset", "lavender", "rose", "teal", "green", "sand", "graphite", "mesh", "aurora", "night"];
    private static string CoverCss(string? k) => $"var(--cv-{(Covers.Contains(k) ? k : "ocean")})";

    private record TemplateInfo(string Key, string Name, string Desc, string Emoji, string Color, string Accent, string? Cover);
    private static readonly TemplateInfo[] Templates =
    [
        new("meeting", "Meeting notes", "Agenda, notes, decisions, action items", "📅", "blue", "blue", "ocean"),
        new("todo", "To-do list", "Today, tomorrow, later", "✅", "green", "green", null),
        new("brief", "Project brief", "Goals, scope, timeline", "🎯", "purple", "purple", "lavender"),
        new("sop", "SOP", "Step-by-step procedure", "📖", "orange", "orange", "sand"),
        new("inspection", "Inspection checklist", "Checklist with photos, findings table", "🔍", "teal", "teal", "teal"),
        new("weekly", "Weekly report", "Highlights, KPI table, next week", "📊", "blue", "indigo", "graphite"),
        new("incident", "Incident report", "What happened, cause, actions", "🧯", "red", "red", "sunset"),
    ];

    private static readonly string[] Emojis =
    [
        "📄", "📝", "📒", "📓", "📔", "📕", "📗", "📘", "📙", "📚", "📖", "🗒️", "🗂️", "📁", "📋", "📌",
        "📎", "🔖", "🏷️", "✅", "☑️", "✔️", "🎯", "🚀", "⭐", "🌟", "✨", "💡", "🔥", "⚡", "🧠", "💬",
        "📅", "🗓️", "⏰", "⏳", "📈", "📊", "📉", "💰", "💼", "🏢", "🏭", "🚚", "📦", "🛒", "🧾", "🧮",
        "🔧", "🛠️", "⚙️", "🔒", "🔑", "🛡️", "⚠️", "⛔", "❗", "❓", "📣", "🔔", "🎉", "🏆", "🤝", "👥",
        "👋", "🙋", "🧑‍💻", "👷", "🩺", "🍽️", "☕", "🌱", "🌍", "🏠", "✈️", "🚗", "🎨", "🎵", "📷", "🖥️",
    ];
}
