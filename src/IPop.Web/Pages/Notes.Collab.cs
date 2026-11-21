// Notes page — comments panel, sharing (people + send to chat) and version history.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Application.Chat;
using IPop.Application.Notes;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Notes
{
    // ── Comments ─────────────────────────────────────────────────────────────

    private List<NoteThreadDto> threads = [];
    private List<NoteBacklinkDto> backlinks = [];

    private async Task LoadBacklinksAsync()
    {
        if (note is null) return;
        var id = note.Id;
        backlinks = [];
        var list = await Auth.GetAsync<List<NoteBacklinkDto>>($"/api/notes/{id}/backlinks");
        if (note?.Id != id || list is null) return;
        backlinks = list;
        await InvokeAsync(StateHasChanged);
    }
    private Guid? focusThread;

    // ── Margin comments (desktop) ────────────────────────────────────────────
    // Open threads anchored to text are cards in the right margin, level with their text. When the screen is too narrow (or the
    // page is full width) they become count bubbles beside the line, and a click opens the thread in a popover. The side drawer
    // is only for All / Resolved. The editor bundle measures positions (margin-comments.js); here we only render.
    private ElementReference pageWrapRef, pageRef, marginRef;
    private IJSObjectReference? marginJs;
    private bool marginMode;
    private Guid? bubbleThread;
    private int bubbleX, bubbleY;

    private bool MarginMode => marginMode && !Phone.IsPhone && note?.Kind != "board" && !(note?.FullWidth ?? false);

    /// Open threads pinned to text, in the order they appear on the page is decided in the browser; here: oldest first.
    private IEnumerable<NoteThreadDto> OpenAnchoredThreads =>
        threads.Where(t => t.ResolvedAt is null && t.Anchor is not null).OrderBy(t => t.CreatedAt);

    /// Called after every render: (re)creates the margin layout once the page is on screen, and re-measures.
    private async Task MarginAfterRenderAsync()
    {
        if (module is null || Phone.IsPhone || view != View.Page || note is null || note.Kind == "board" || editor is null)
        {
            if (marginJs is not null && (Phone.IsPhone || view != View.Page || note is null || note.Kind == "board")) await DisposeMarginAsync();
            return;
        }
        if (marginCreating) return;
        marginCreating = true;
        var pageId = note.Id;
        var ed = editor;
        try
        {
            if (marginJs is null || marginFor != pageId)
            {
                await DisposeMarginAsync();
                if (disposed || note?.Id != pageId || editor != ed || Phone.IsPhone || view != View.Page) return;
                var js = await ed.InvokeAsync<IJSObjectReference>("margin", scrollRef, pageRef, marginRef, selfRef);
                if (disposed || note?.Id != pageId || editor != ed || Phone.IsPhone || view != View.Page)
                {
                    await TryJsAsync(async () => { await js.InvokeVoidAsync("destroy"); await js.DisposeAsync(); });
                    return;
                }
                marginFor = pageId;
                marginJs = js;
            }
            if (marginJs is not null) await marginJs.InvokeVoidAsync("layout");
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
        finally { marginCreating = false; }
    }
    private Guid? marginFor;
    private bool marginCreating;

    private async Task DisposeMarginAsync()
    {
        var js = marginJs; marginJs = null; marginFor = null;
        if (js is null) return;
        try { await js.InvokeVoidAsync("destroy"); await js.DisposeAsync(); }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
    }

    [JSInvokable]
    public void OnMarginMode(string mode)
    {
        var on = mode == "margin";
        if (on == marginMode) return;
        marginMode = on;
        StateHasChanged();
    }

    private async Task OpenBubble(Guid id)
    {
        bubbleThread = bubbleThread == id ? null : id;
        focusThread = id;
        if (bubbleThread is not null && marginJs is not null)
        {
            try { var pos = await marginJs.InvokeAsync<int[]>("bubbleAt", id); bubbleX = pos[0]; bubbleY = pos[1]; } catch (JSException) { }
        }
        await PushThreadsAsync();
    }
    private readonly Dictionary<Guid, string?> replies = [];
    private CommentDraft? draft;
    private Guid? cmtMentionTarget;
    private List<NotePersonDto> cmtMentionHits = [];
    private CancellationTokenSource? cmtMentionCts;
    private readonly Dictionary<Guid, string> cmtMentions = [];

    private sealed class CommentDraft(string? quote, string? anchor)
    {
        /// Both null = a comment on the page as a whole (no selected text).
        public string? Quote { get; } = quote;
        public string? Anchor { get; } = anchor;
        public bool IsPageLevel => Anchor is null;
        public string Body { get; set; } = "";
    }

    private async Task LoadThreadsAsync()
    {
        if (note is null) return;
        var id = note.Id;
        var list = await Auth.GetAsync<List<NoteThreadDto>>($"/api/notes/{id}/threads");
        if (note?.Id != id || list is null) return;
        threads = list;
        note = note with { OpenThreads = threads.Count(t => t.ResolvedAt is null) };
        await PushThreadsAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task PushThreadsAsync()
    {
        if (editor is null) return;
        try
        {
            await editor.InvokeVoidAsync("setThreads",
                // the whiteboard lists comments per shape: first words, who wrote it and how many replies (no NIK)
                threads.Select(t => new
                {
                    id = t.Id, anchor = t.Anchor, resolvedAt = t.ResolvedAt, count = t.Comments.Count,
                    text = t.Comments.FirstOrDefault()?.Body is { } b ? (b.Length > 140 ? b[..140] + "…" : b) : null,
                    author = t.Comments.FirstOrDefault()?.AuthorName,
                }), focusThread);
        }
        catch (JSException) { }
    }

    [JSInvokable] public Task OnThreadsChanged() => LoadThreadsAsync();

    [JSInvokable]
    public void OnComment(string quote, string anchor)
    {
        if (!CanComment) return;
        draft = new CommentDraft(quote.Length > 300 ? quote[..300] + "…" : quote, anchor);
        commentsOpen = !(MarginMode && anchor is not null);   // wide screen + text selected: the box appears in the margin next to the text; otherwise the drawer opens
        focusDraft = true;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnThreadClick(string id)
    {
        if (!Guid.TryParse(id, out var g)) return;
        var resolved = threads.FirstOrDefault(t => t.Id == g) is { ResolvedAt: not null };
        // an open thread on a wide screen is already in the margin: just lift its card; everything else uses the drawer / popover
        if (!Phone.IsPhone && !resolved && MarginMode) { focusThread = g; await PushThreadsAsync(); StateHasChanged(); return; }
        if (!Phone.IsPhone && !resolved && note?.Kind != "board") { await OpenBubble(g); StateHasChanged(); return; }
        commentsOpen = true;
        if (resolved) showResolved = true;
        focusThread = g;
        await PushThreadsAsync();
        StateHasChanged();
    }

    private async Task FocusThread(Guid id)
    {
        focusThread = id;
        if (editor is not null) await TryJsAsync(() => editor.InvokeVoidAsync("focusThread", id).AsTask());
        if (marginJs is not null) await TryJsAsync(() => marginJs.InvokeVoidAsync("layout").AsTask());
    }

    private void ToggleComments() => commentsOpen = !commentsOpen;

    private void OnDraftInput(ChangeEventArgs e)
    {
        if (draft is null) return;
        draft.Body = e.Value?.ToString() ?? "";
        SearchCommentMentions(Guid.Empty, draft.Body);
    }

    private void OnReplyInput(ChangeEventArgs e, Guid threadId)
    {
        var text = e.Value?.ToString() ?? "";
        replies[threadId] = text;
        SearchCommentMentions(threadId, text);
    }

    private void SearchCommentMentions(Guid target, string? text)
    {
        cmtMentionCts?.Cancel();
        var at = text is null ? -1 : text.LastIndexOf('@');
        if (note is null || at < 0 || (at > 0 && !char.IsWhiteSpace(text![at - 1]))) { ClearCommentMention(); return; }
        var q = text![(at + 1)..];
        if (q.Length is < 2 or > 30 || q.Contains('\n')) { ClearCommentMention(); return; }
        var cts = cmtMentionCts = new CancellationTokenSource();
        var pageId = note.Id;
        _ = Task.Delay(180, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            var r = await Auth.GetAsync<List<NotePersonDto>>($"/api/notes/people?q={Uri.EscapeDataString(q)}&pageId={pageId}");
            if (cts.IsCancellationRequested) return;
            await InvokeAsync(() => { cmtMentionTarget = target; cmtMentionHits = r ?? []; StateHasChanged(); });
        }, TaskScheduler.Default);
    }

    private void PickCommentMention(Guid target, NotePersonDto p)
    {
        cmtMentions[p.Id] = p.Name;
        var cur = target == Guid.Empty ? draft?.Body : replies.GetValueOrDefault(target);
        if (!string.IsNullOrEmpty(cur))
        {
            var at = cur.LastIndexOf('@');
            var next = (at >= 0 ? cur[..at] : cur) + $"@{p.Name} ";
            if (target == Guid.Empty && draft is not null) draft.Body = next;
            else replies[target] = next;
        }
        ClearCommentMention();
    }

    private void ClearCommentMention() { cmtMentionCts?.Cancel(); cmtMentionTarget = null; cmtMentionHits = []; }

    private List<Guid>? ResolveCommentMentions(string body)
    {
        var list = cmtMentions.Where(kv => body.Contains("@" + kv.Value, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        return list.Count > 0 ? list : null;
    }

    private async Task OnDraftKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && (e.CtrlKey || e.MetaKey)) await PostDraft();
        else if (e.Key == "Escape") draft = null;
    }

    private async Task PostDraft()
    {
        if (note is null || !CanComment || draft is null || string.IsNullOrWhiteSpace(draft.Body) || busy) return;
        var pageId = note.Id;
        var curDraft = draft;
        busy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{pageId}/threads",
                new CreateNoteThreadRequest(curDraft.Quote, curDraft.Anchor, curDraft.Body.Trim(), ResolveCommentMentions(curDraft.Body)));
            if (disposed || note?.Id != pageId) return;
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't post your comment", ToastKind.Error); return; }
            var t = await res.Content.ReadFromJsonAsync<NoteThreadDto>(Json);
            if (disposed || note?.Id != pageId) return;
            draft = null;
            if (t is not null)
            {
                threads = [t, .. threads.Where(x => x.Id != t.Id)];
                note = note with { OpenThreads = threads.Count(x => x.ResolvedAt is null) };
                focusThread = t.Id;
                await PushThreadsAsync();
            }
        }
        finally { busy = false; }
    }

    private async Task OnReplyKey(KeyboardEventArgs e, NoteThreadDto t)
    {
        if (e.Key == "Enter" && !e.ShiftKey) await Reply(t);
    }

    private async Task Reply(NoteThreadDto t)
    {
        if (note is null || !CanComment) return;
        var pageId = note.Id;
        var body = replies.GetValueOrDefault(t.Id)?.Trim();
        if (string.IsNullOrEmpty(body)) return;
        replies[t.Id] = null;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{pageId}/threads/{t.Id}/replies", new NoteReplyRequest(body, ResolveCommentMentions(body)));
        if (disposed || note?.Id != pageId) return;
        if (!res.IsSuccessStatusCode) { replies[t.Id] = body; Toasts.Show("Couldn't send your reply", ToastKind.Error); return; }
        var updated = await res.Content.ReadFromJsonAsync<NoteThreadDto>(Json);
        if (!disposed && note?.Id == pageId && updated is not null) threads = threads.Select(x => x.Id == updated.Id ? updated : x).ToList();
    }

    // ── Edit / delete / react ────────────────────────────────────────────────

    private Guid? editingComment;
    private string editText = "";
    private Guid? reactionPickerFor;
    private static readonly string[] ReactionChoices = ["👍", "❤️", "🎉", "😂", "🙏", "👀"];

    /// A comment on the whole page: opens the comment panel with an empty draft that has no anchor.
    private void StartPageComment()
    {
        if (!CanComment) return;
        commentsOpen = true;
        draft = new CommentDraft(null, null);
        focusDraft = true;
    }

    private async Task OnEditKeyAsync(KeyboardEventArgs e, NoteThreadDto t, NoteCommentDto c)
    {
        if (e.Key == "Escape") CancelEdit();
        else if (e.Key == "Enter" && !e.ShiftKey) await SaveEditAsync(t, c);
    }

    private void BeginEdit(NoteCommentDto c) { editingComment = c.Id; editText = c.Body; reactionPickerFor = null; }
    private void CancelEdit() { editingComment = null; editText = ""; }

    private async Task SaveEditAsync(NoteThreadDto t, NoteCommentDto c)
    {
        if (note is null || string.IsNullOrWhiteSpace(editText)) return;
        var pageId = note.Id;
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{pageId}/comments/{c.Id}", new { body = editText.Trim() });
        if (disposed || note?.Id != pageId) return;
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't save your edit", ToastKind.Error); return; }
        var updated = await res.Content.ReadFromJsonAsync<NoteThreadDto>(Json);
        if (disposed || note?.Id != pageId) return;
        if (updated is not null) threads = threads.Select(x => x.Id == updated.Id ? updated : x).ToList();
        CancelEdit();
    }

    private async Task DeleteCommentAsync(NoteThreadDto t, NoteCommentDto c)
    {
        if (note is null) return;
        var pageId = note.Id;
        var detail = t.Comments.Count == 1 ? "It is the only comment in this thread, so the thread goes with it." : "This can't be undone.";
        if (!await Confirm.AskAsync("Delete this comment?", detail, "Delete")) return;
        if (disposed || note?.Id != pageId) return;
        var res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notes/{pageId}/comments/{c.Id}");
        if (disposed || note?.Id != pageId) return;
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't delete the comment", ToastKind.Error); return; }
        await LoadThreadsAsync();
    }

    private async Task ReactAsync(NoteThreadDto t, NoteCommentDto c, string emoji)
    {
        if (note is null || !CanComment) return;
        var pageId = note.Id;
        reactionPickerFor = null;
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{pageId}/comments/{c.Id}/reactions", new { emoji });
        if (disposed || note?.Id != pageId) return;
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't add your reaction", ToastKind.Error); return; }
        var updated = await res.Content.ReadFromJsonAsync<NoteThreadDto>(Json);
        if (!disposed && note?.Id == pageId && updated is not null) threads = threads.Select(x => x.Id == updated.Id ? updated : x).ToList();
    }

    private async Task Resolve(NoteThreadDto t)
    {
        if (note is null) return;
        var pageId = note.Id;
        var resolve = t.ResolvedAt is null;
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{pageId}/threads/{t.Id}/resolved", new { resolved = resolve });
        if (disposed || note?.Id != pageId) return;
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't update the comment", ToastKind.Error); return; }
        threads = threads.Select(x => x.Id == t.Id ? x with { ResolvedAt = resolve ? DateTime.UtcNow : null } : x).ToList();
        note = note with { OpenThreads = threads.Count(x => x.ResolvedAt is null) };
        if (resolve && focusThread == t.Id) focusThread = null;
        await PushThreadsAsync();
    }

    // ── Sharing ──────────────────────────────────────────────────────────────

    private int shareTab;
    private NoteSharesDto? shares;
    private string inviteQ = "", inviteLevel = "edit";
    private List<NotePersonDto> inviteHits = [];
    private CancellationTokenSource? inviteCts;
    private List<RoomListDto>? rooms;
    private string roomQ = "", sendLevel = "edit", sendMsg = "";
    private Guid? sendRoom;

    private async Task OpenShare()
    {
        if (note is null) return;
        pop = Pop.Share; shareTab = 0; shares = null; inviteQ = ""; inviteHits = [];
        await LoadSharesAsync();
    }

    private async Task LoadSharesAsync()
    {
        if (note is null) return;
        var pageId = note.Id;
        var loaded = await Auth.GetAsync<NoteSharesDto>($"/api/notes/{pageId}/shares");
        if (disposed || note?.Id != pageId) return;
        shares = loaded;
    }

    private async Task OpenSendTab()
    {
        shareTab = 1; sendRoom = null; sendMsg = ""; roomQ = "";
        if (rooms is not null) return;
        var direct = Auth.GetAsync<PagedResult<RoomListDto>>("/api/chat/rooms?tab=direct&page=1&pageSize=100");
        var groups = Auth.GetAsync<PagedResult<RoomListDto>>("/api/chat/rooms?tab=groups&page=1&pageSize=100");
        await Task.WhenAll(direct, groups);
        rooms = [.. (direct.Result?.Items ?? []).Concat(groups.Result?.Items ?? [])
            .Where(r => !r.IsArchived)
            .OrderByDescending(r => r.IsPinned).ThenByDescending(r => r.LastMessageAt ?? DateTime.MinValue)];
    }

    private static bool IsDirect(string? type) => type is "dm" or "direct";
    private static string RoomName(RoomListDto r) => IsDirect(r.Type) ? r.RecipientName ?? r.Name ?? "Chat" : r.Name ?? "Group";

    private void OnInviteInput(ChangeEventArgs e)
    {
        inviteQ = e.Value?.ToString() ?? "";
        inviteCts?.Cancel();
        if (inviteQ.Trim().Length < 2) { inviteHits = []; return; }
        var cts = inviteCts = new CancellationTokenSource();
        var q = inviteQ.Trim();
        _ = Task.Delay(200, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            var r = await Auth.GetAsync<List<NotePersonDto>>($"/api/notes/people?q={Uri.EscapeDataString(q)}");
            if (cts.IsCancellationRequested) return;
            var taken = shares?.Shares.Where(s => s.UserId is not null && s.InheritedFromId is null).Select(s => s.UserId!.Value).ToHashSet() ?? [];
            await InvokeAsync(() =>
            {
                inviteHits = (r ?? []).Where(p => !taken.Contains(p.Id) && !p.Nik.Equals(shares?.OwnerNik, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
                StateHasChanged();
            });
        }, TaskScheduler.Default);
    }

    private async Task Invite(NotePersonDto p)
    {
        if (note is null) return;
        inviteHits = []; inviteQ = "";
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{note.Id}/shares", new AddNoteShareRequest(p.Id, null, inviteLevel));
        if (!res.IsSuccessStatusCode) { Toasts.Show($"Couldn't share with {p.Name}", ToastKind.Error); return; }
        Toasts.Show($"{FirstName(p.Name)} can now {LevelVerb(inviteLevel)} this note", ToastKind.Success);
        await LoadSharesAsync();
        await ReloadPageMetaAsync();
        await SharesChangedInEditorAsync();
    }

    /// "Share (can edit)" on the editor's prompt after tagging someone without access. Owner only;
    /// the editor then sends the mention notification itself.
    [JSInvokable]
    public async Task<bool> ShareFromMention(string userId, string name)
    {
        if (note is null || !IsOwner || !Guid.TryParse(userId, out var uid)) return false;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{note.Id}/shares", new AddNoteShareRequest(uid, null, "edit"));
        if (!res.IsSuccessStatusCode) { Toasts.Show($"Couldn't share with {name}", ToastKind.Error); return false; }
        Toasts.Show($"{FirstName(name)} can now edit this note", ToastKind.Success);
        if (shares is not null) await LoadSharesAsync();
        await ReloadPageMetaAsync();
        return true;
    }

    /// Greyed tags in the page follow share changes made in the dialog.
    private async Task SharesChangedInEditorAsync()
    {
        if (editor is null) return;
        try { await editor.InvokeVoidAsync("sharesChanged"); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private async Task ChangeShare(NoteShareDto s, string? level)
    {
        if (note is null || level is null) return;
        HttpResponseMessage res;
        if (level == "remove")
        {
            res = await Auth.SendAsync(HttpMethod.Delete, $"/api/notes/{note.Id}/shares/{s.Id}");
        }
        else
        {
            res = await Auth.SendAsync(HttpMethod.Patch, $"/api/notes/{note.Id}/shares/{s.Id}", new { level });
        }
        if (!res.IsSuccessStatusCode) Toasts.Show("Couldn't change access", ToastKind.Error);
        await LoadSharesAsync();
        await ReloadPageMetaAsync();
        await SharesChangedInEditorAsync();
    }

    private async Task SendToChat()
    {
        if (note is null || sendRoom is not { } roomId || busy) return;
        busy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{note.Id}/send",
                new { roomId, level = sendLevel, message = string.IsNullOrWhiteSpace(sendMsg) ? null : sendMsg.Trim() });
            if (!res.IsSuccessStatusCode)
            {
                Toasts.Show(res.StatusCode == System.Net.HttpStatusCode.Forbidden ? "You're not a member of that chat" : "Couldn't send to chat", ToastKind.Error);
                return;
            }
            var name = rooms?.FirstOrDefault(r => r.Id == roomId) is { } r ? RoomName(r) : "chat";
            Toasts.Show($"Sent to {name}", ToastKind.Success);
            pop = Pop.None;
            await ReloadPageMetaAsync();
            await SharesChangedInEditorAsync();
        }
        finally { busy = false; }
    }

    private static string LevelVerb(string level) => level switch { "edit" => "edit", "comment" => "comment on", _ => "view" };

    /// Sharing changes the "Shared"/rooms props on the page header.
    private async Task ReloadPageMetaAsync()
    {
        if (note is null) return;
        var fresh = await Auth.GetAsync<NotePageDto>($"/api/notes/{note.Id}");
        if (fresh is not null && note?.Id == fresh.Id) note = fresh;
        await LoadSidebarAsync();
    }

    // ── Version history ──────────────────────────────────────────────────────

    private List<NoteVersionDto>? versions;
    private Guid? selVersion;
    private string? selVersionState;

    private async Task OpenHistory()
    {
        if (note is null) return;
        var pageId = note.Id;
        pop = Pop.History; versions = null; selVersion = null; selVersionState = null;
        showDiff = false; diffLines = null; namingVersion = false; renameVersionText = ""; newVersionName = "";
        if (editor is not null) await TryJsAsync(() => editor.InvokeVoidAsync("flushNow").AsTask());
        var loaded = await Auth.GetAsync<List<NoteVersionDto>>($"/api/notes/{pageId}/versions") ?? [];
        if (disposed || note?.Id != pageId || pop != Pop.History) return;
        versions = loaded;
        if (versions.Count > 0) await SelectVersion(versions[0].Id);
    }

    /// The whiteboard's version slider: "Restore this version" opens the page's History on that version
    /// (same preview, same confirmation, the whole page is restored as it always is).
    [JSInvokable]
    public async Task OnRestoreVersionId(string versionId)
    {
        if (note is null || !Guid.TryParse(versionId, out var vid)) return;
        await OpenHistory();
        if (versions?.Any(v => v.Id == vid) == true) await SelectVersion(vid);
        if (!disposed) await InvokeAsync(StateHasChanged);
    }

    /// "Send to Chat" from the board's menu: the page's Share dialog, on the chat tab.
    [JSInvokable]
    public async Task OnShareFromBoard()
    {
        if (!IsOwner) return;
        await OpenShare();
        await OpenSendTab();
        if (!disposed) await InvokeAsync(StateHasChanged);
    }

    private record VersionState(string State);

    private async Task SelectVersion(Guid vid)
    {
        if (note is null) return;
        var pageId = note.Id;
        selVersion = vid;
        showDiff = false; diffLines = null; namingVersion = false;
        var v = await Auth.GetAsync<VersionState>($"/api/notes/{pageId}/versions/{vid}");
        if (disposed || note?.Id != pageId || selVersion != vid || v is null) return;
        selVersionState = v.State;
        StateHasChanged();
        await Task.Yield();
        if (disposed || note?.Id != pageId || selVersion != vid) return;
        await RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        await DestroyPreviewAsync();
        if (module is null || selVersionState is null || pop != Pop.History) return;
        try { previewer = await module.InvokeAsync<IJSObjectReference>("preview", previewRef, selVersionState, new { apiBase = Http.BaseAddress?.ToString() ?? "", dotnet = selfRef }); }
        catch (JSException) { Toasts.Show("This version can't be displayed", ToastKind.Error); }
    }

    private async Task DestroyPreviewAsync()
    {
        var p = previewer;
        previewer = null;
        if (p is null) return;
        try { await p.InvokeVoidAsync("destroy"); await p.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
    }

    private async Task RestoreVersion()
    {
        if (note is null || !CanEdit || editor is null || selVersionState is null || versions is null) return;
        var pageId = note.Id;
        var state = selVersionState;
        var v = versions.FirstOrDefault(x => x.Id == selVersion);
        if (v is null) return;
        if (!await Confirm.AskAsync("Restore this version?", $"The page content will be replaced with the version from {StampDate(v.CreatedAt)}. The current version stays in the history.", "Restore", danger: false)) return;
        if (disposed || note?.Id != pageId || !CanEdit || editor is null) return;
        busy = true;
        try
        {
            await editor.InvokeVoidAsync("restore", state);
            await DestroyPreviewAsync();
            pop = Pop.None;
            Toasts.Show("Version restored", ToastKind.Success);
        }
        catch (JSException) { Toasts.Show("Couldn't restore this version", ToastKind.Error); }
        catch (JSDisconnectedException) { }
        finally { busy = false; }
    }
}
