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
    private Guid? focusThread;
    private readonly Dictionary<Guid, string?> replies = [];
    private CommentDraft? draft;

    private sealed class CommentDraft(string quote, string anchor)
    {
        public string Quote { get; } = quote;
        public string Anchor { get; } = anchor;
        public string Body { get; set; } = "";
    }

    private async Task LoadThreadsAsync()
    {
        if (note is null) return;
        var id = note.Id;
        var list = await Auth.GetAsync<List<NoteThreadDto>>($"/api/notes/{id}/threads");
        if (note?.Id != id || list is null) return;
        threads = list;
        await PushThreadsAsync();
        await InvokeAsync(StateHasChanged);
    }

    private async Task PushThreadsAsync()
    {
        if (editor is null) return;
        try
        {
            await editor.InvokeVoidAsync("setThreads",
                threads.Select(t => new { id = t.Id, anchor = t.Anchor, resolvedAt = t.ResolvedAt }), focusThread);
        }
        catch (JSException) { }
    }

    [JSInvokable] public Task OnThreadsChanged() => LoadThreadsAsync();

    [JSInvokable]
    public void OnComment(string quote, string anchor)
    {
        if (!CanComment) return;
        draft = new CommentDraft(quote.Length > 300 ? quote[..300] + "…" : quote, anchor);
        commentsOpen = true;
        focusDraft = true;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnThreadClick(string id)
    {
        if (!Guid.TryParse(id, out var g)) return;
        commentsOpen = true;
        if (threads.FirstOrDefault(t => t.Id == g) is { ResolvedAt: not null }) showResolved = true;
        focusThread = g;
        await PushThreadsAsync();
        StateHasChanged();
    }

    private async Task FocusThread(Guid id)
    {
        focusThread = id;
        if (editor is not null) await editor.InvokeVoidAsync("focusThread", id);
    }

    private void ToggleComments() => commentsOpen = !commentsOpen;

    private async Task OnDraftKey(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && (e.CtrlKey || e.MetaKey)) await PostDraft();
        else if (e.Key == "Escape") draft = null;
    }

    private async Task PostDraft()
    {
        if (note is null || draft is null || string.IsNullOrWhiteSpace(draft.Body) || busy) return;
        busy = true;
        try
        {
            var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{note.Id}/threads",
                new CreateNoteThreadRequest(draft.Quote, draft.Anchor, draft.Body.Trim(), null));
            if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't post your comment", ToastKind.Error); return; }
            var t = await res.Content.ReadFromJsonAsync<NoteThreadDto>(Json);
            draft = null;
            if (t is not null)
            {
                threads = [t, .. threads.Where(x => x.Id != t.Id)];
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
        if (note is null) return;
        var body = replies.GetValueOrDefault(t.Id)?.Trim();
        if (string.IsNullOrEmpty(body)) return;
        replies[t.Id] = null;
        var res = await Auth.SendAsync(HttpMethod.Post, $"/api/notes/{note.Id}/threads/{t.Id}/replies", new NoteReplyRequest(body, null));
        if (!res.IsSuccessStatusCode) { replies[t.Id] = body; Toasts.Show("Couldn't send your reply", ToastKind.Error); return; }
        var updated = await res.Content.ReadFromJsonAsync<NoteThreadDto>(Json);
        if (updated is not null) threads = threads.Select(x => x.Id == updated.Id ? updated : x).ToList();
    }

    private async Task Resolve(NoteThreadDto t)
    {
        if (note is null) return;
        var resolve = t.ResolvedAt is null;
        var res = await Auth.SendAsync(HttpMethod.Put, $"/api/notes/{note.Id}/threads/{t.Id}/resolved", new { resolved = resolve });
        if (!res.IsSuccessStatusCode) { Toasts.Show("Couldn't update the comment", ToastKind.Error); return; }
        threads = threads.Select(x => x.Id == t.Id ? x with { ResolvedAt = resolve ? DateTime.UtcNow : null } : x).ToList();
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
        shares = await Auth.GetAsync<NoteSharesDto>($"/api/notes/{note.Id}/shares");
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
        pop = Pop.History; versions = null; selVersion = null; selVersionState = null;
        if (editor is not null) await editor.InvokeVoidAsync("flushNow");
        versions = await Auth.GetAsync<List<NoteVersionDto>>($"/api/notes/{note.Id}/versions") ?? [];
        if (versions.Count > 0) await SelectVersion(versions[0].Id);
    }

    private record VersionState(string State);

    private async Task SelectVersion(Guid vid)
    {
        if (note is null) return;
        selVersion = vid;
        var v = await Auth.GetAsync<VersionState>($"/api/notes/{note.Id}/versions/{vid}");
        if (selVersion != vid || v is null) return;
        selVersionState = v.State;
        StateHasChanged();
        await Task.Yield();
        await RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        await DestroyPreviewAsync();
        if (module is null || selVersionState is null || pop != Pop.History) return;
        try { previewer = await module.InvokeAsync<IJSObjectReference>("preview", previewRef, selVersionState); }
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
        if (editor is null || selVersionState is null || versions is null) return;
        var v = versions.First(x => x.Id == selVersion);
        if (!await Confirm.AskAsync("Restore this version?", $"The page content will be replaced with the version from {StampDate(v.CreatedAt)}. The current version stays in the history.", "Restore", danger: false)) return;
        busy = true;
        try
        {
            await editor.InvokeVoidAsync("restore", selVersionState);
            await DestroyPreviewAsync();
            pop = Pop.None;
            Toasts.Show("Version restored", ToastKind.Success);
        }
        finally { busy = false; }
    }
}
