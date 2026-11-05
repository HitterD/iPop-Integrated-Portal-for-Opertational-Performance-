// Chat page — composer, emoji/sticker picker, mention autocomplete, file upload, profile drawer.
//
// Part of the Chat partial class. The class was split by responsibility out of a single
// ~1,500-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Application.Chat.Mentions;
using IPop.Web.Models;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    // ---- Composer ----

    private async Task OnComposerInput()
    {
        ScheduleComposerLink();
        StageComposerDraft();
        await ChatSvc.SendTypingAsync();
        await CheckMentionAsync();
        await GrowComposer();
    }

    // ---- Composer link preview ----
    // The first web link in the composer gets a preview card above the input ~500ms after typing
    // stops. ✕ turns it off for that URL only (sent with linkPreviewOff); a different first link
    // brings the card back, and every send starts over.
    private string? composerLinkUrl;      // debounced first link the card is showing
    private string? composerLinkOff;      // URL whose preview the sender removed
    private CancellationTokenSource? composerLinkCts;
    private string? _firstLinkFor;
    private string? _firstLink;

    private string? ComposerFirstLink()
    {
        if (!ReferenceEquals(_firstLinkFor, messageText))
            (_firstLinkFor, _firstLink) = (messageText, MentionRenderer.FirstWebLink(messageText));
        return _firstLink;
    }

    /// The card is shown only while the debounced link is still the composer's first link, so
    /// clearing or editing the text hides a stale card immediately.
    private string? ComposerPreviewUrl =>
        composerLinkUrl is { } u && u != composerLinkOff && ComposerFirstLink() == u ? u : null;

    private void ScheduleComposerLink()
    {
        var first = ComposerFirstLink();
        if (first == composerLinkUrl) return;
        composerLinkCts?.Cancel();
        if (first is null) { composerLinkUrl = null; return; }
        var cts = composerLinkCts = new CancellationTokenSource();
        _ = DebounceComposerLinkAsync(first, cts.Token);
    }

    private async Task DebounceComposerLinkAsync(string url, CancellationToken ct)
    {
        try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || _disposed || ComposerFirstLink() != url) return;
        composerLinkUrl = url;
        await InvokeAsync(StateHasChanged);
    }

    private void RemoveComposerPreview(string url)
    {
        composerLinkOff = url;
        focusComposerPending = true;
    }

    private void ResetComposerLink()
    {
        composerLinkCts?.Cancel();
        composerLinkCts = null;
        composerLinkUrl = null;
        composerLinkOff = null;
    }

    /// Picks from the @-popup, kept beside the plain-text composer ("@Full Name" in the
    /// textarea). Converted into @[Name](u:GUID) tokens only when the message is sent.
    private List<MentionSegment> pickedMentions = [];
    /// The "@query" (or "#query") under the caret while the popup is serving it.
    private ActiveMentionToken? activeMention;
    private IPop.Web.Components.Chat.MentionPopup? mentionPopupRef;
    private bool mentionShowAll;
    /// Keyboard selection inside the project/task fallback list.
    private int mentionAltIndex;

    private int MentionAllCount => Math.Max(0, (ChatSvc.ActiveRoom?.Members.Count ?? 1) - 1);

    /// Wire content for drafts: keeps picked mentions as real tokens across room switches.
    private string ComposerWireText() => ComposerMentionModel.ToWire(messageText, ComposerMentionModel.Prune(messageText, pickedMentions));

    private void LoadComposerFromWire(string? wire)
    {
        (messageText, pickedMentions) = ComposerMentionModel.FromWire(wire);
        ResetComposerLink();
        ScheduleComposerLink();
        activeMention = null;
        CloseMentionPopup(true);
    }

    /// Keys the JS composer handler routes here while a suggestion list is on screen, so
    /// Enter/Tab pick instead of sending and Esc closes only the list.
    [Microsoft.JSInterop.JSInvokable]
    public async Task OnJsMentionKey(string key)
    {
        if (mentionPopupOpen && mentionPopupRef is not null)
        {
            switch (key)
            {
                case "ArrowDown": mentionPopupRef.MoveDown(); return;
                case "ArrowUp": mentionPopupRef.MoveUp(); return;
                case "Enter" or "Tab": await mentionPopupRef.PickSelected(); return;
                case "Escape": CloseMentionPopup(true); StateHasChanged(); return;
            }
            return;
        }

        // Project (@) / task (#) fallback list.
        var projects = mentionTrigger == '@' ? CurrentProjectMatches() : [];
        var tasks = mentionTrigger == '#' && mentionTaskProject is not null ? CurrentTaskMatches() : [];
        var count = mentionTrigger == '#' ? tasks.Count : projects.Count;
        switch (key)
        {
            case "ArrowDown" when count > 0: mentionAltIndex = (mentionAltIndex + 1) % count; break;
            case "ArrowUp" when count > 0: mentionAltIndex = (mentionAltIndex - 1 + count) % count; break;
            case "Enter" or "Tab" when count > 0:
                var i = Math.Clamp(mentionAltIndex, 0, count - 1);
                if (mentionTrigger == '#') await SelectTaskMention(tasks[i]); else await SelectProjectMention(projects[i]);
                break;
            case "Escape":
                activeMention = null; // hides the list until the next keystroke re-detects
                break;
        }
        StateHasChanged();
    }

    /// Caret moved without typing (click, arrows, Home/End): re-evaluate the token under it.
    [Microsoft.JSInterop.JSInvokable]
    public void OnJsComposerCaret(int caret)
    {
        var wasOpen = MentionListVisible;
        EvaluateMention(Math.Clamp(caret, 0, messageText.Length));
        if (wasOpen || MentionListVisible) StateHasChanged();
    }

    /// Any suggestion list (people, projects or tasks) is on screen. The JS key handler
    /// reads this through data-mention-open so Enter picks instead of sending.
    private bool MentionListVisible =>
        activeMention is not null &&
        (mentionPopupOpen || CurrentProjectMatches().Count > 0 || (mentionTrigger == '#' && mentionTaskProject is not null && CurrentTaskMatches().Count > 0));

    private List<IPop.Application.Projects.ProjectListDto> CurrentProjectMatches()
        => activeMention is null || mentionPopupOpen ? [] :
           projectMentionCandidates.Where(p => p.Name.ToLowerInvariant().Contains(mentionFilter)).Take(5).ToList();

    private List<IPop.Application.Projects.ProjectTaskDto> CurrentTaskMatches()
        => activeMention is null ? [] :
           taskMentionCandidates.Where(t => t.Title.ToLowerInvariant().Contains(mentionFilter)).Take(8).ToList();

    /// Guarded: a browser holding a cached older ipop.js would otherwise throw on
    /// every keystroke and surface Blazor's unhandled-error bar.
    private async Task GrowComposer()
    {
        try { await JS.InvokeVoidAsync("ipop.autoGrow", composerRef); } catch { }
    }

    /// Leaving the composer should clear the remote typing indicator right away instead of
    /// waiting for the other client's 8s timeout.
    private async Task OnComposerBlur()
    {
        if (string.IsNullOrWhiteSpace(messageText))
            await ChatSvc.StopTypingAsync();
    }

    [Microsoft.JSInterop.JSInvokable]
    public async Task OnJsEnter()
    {
        // Belt and braces: if the list opened after the DOM attribute was last rendered,
        // Enter still picks instead of sending.
        if (MentionListVisible) { await OnJsMentionKey("Enter"); return; }
        await SendMessage();
    }

    [Microsoft.JSInterop.JSInvokable]
    public void OnJsEscape()
    {
        // The document-level shortcut in MainLayout also fires for this same press; the
        // shared guard keeps one keystroke from peeling two layers.
        if (!TryBeginChatEscape()) return;
        HandleChatEscape();
        StateHasChanged();
    }

    /// Raised (synchronously, before any await) once a composer send is handed to ChatState.
    /// Chat.Drafts.cs implements it to remember which draft version this send consumed; the
    /// matching ChatSvc.SendSettled (same RoomId + IdempotencyKey) says whether it went out.
    partial void OnComposerSendStarted(ComposerSendSnapshot snapshot);

    /// What a composer send consumed: the wire text (mentions as tokens) and the exact
    /// composer text it came from, so a later edit is never mistaken for the sent version.
    internal sealed record ComposerSendSnapshot(Guid RoomId, Guid IdempotencyKey, string WireText, string ComposerText,
        Guid? ReplyToId, IReadOnlyList<IPop.Web.Models.PendingAttachmentItem> Attachments);

    private async Task SendMessage()
    {
        var hasText = !string.IsNullOrWhiteSpace(messageText);
        var hasAttachments = pendingAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;
        // Attachments still uploading would be dropped from this message: wait for them.
        if (uploading) { uploadError = "Wait for the upload to finish before sending."; return; }
        var sendRoomId = ChatSvc.ActiveRoomId;
        if (sendRoomId is null) return;
        // no sentinel; the bubble renders attachments directly. Picked "@Name" text becomes
        // @[Name](u:GUID) tokens here so the server records mentions and notifies.
        var text = hasText ? ComposerMentionModel.ToWire(messageText.Trim(), ComposerMentionModel.Prune(messageText, pickedMentions)) : "";
        // Refuse before clearing anything: the text stays in the composer to be shortened.
        if (text.Length > IPop.Web.Services.ChatState.MaxMessageLength)
        {
            Toasts.Show($"Message is too long (max {IPop.Web.Services.ChatState.MaxMessageLength:N0} characters).", ToastKind.Error);
            return;
        }

        // Capture everything this send consumes before the first await, so text typed (or a
        // room switched to) while it is in flight is never cleared or attributed to it.
        var key = Guid.NewGuid();
        var composerText = messageText;
        var replyId = replyToMsg?.Id;
        var sentAttachments = pendingAttachments;
        var ids = hasAttachments ? sentAttachments.Select(a => a.Id).ToList() : null;
        // The optimistic bubble needs real attachment metadata to draw its tiles.
        var previews = hasAttachments ? sentAttachments.Select(a => a.AsDto()).ToList() : null;
        var blobUrls = sentAttachments.Where(p => !string.IsNullOrEmpty(p.PreviewUrl)).Select(p => p.PreviewUrl!).ToList();
        OnComposerSendStarted(new ComposerSendSnapshot(sendRoomId.Value, key, text, composerText, replyId, sentAttachments));
        var linkOff = composerLinkOff is { } off && MentionRenderer.FirstWebLink(text) == off;
        ResetComposerLink();

        pickedMentions = [];
        activeMention = null;
        CloseMentionPopup(true);
        _ = FlySendArrowAsync();
        messageText = "";
        uploadError = null;
        pendingAttachments = [];
        replyToMsg = null;
        scrollToBottom = true;
        // Clicking Send moves focus to the button; hand it back so typing can continue.
        focusComposerPending = true;

        // ChatState adds the bubble before its first await, so the message is in the outbox
        // (and retryable) even if anything below throws.
        var sendTask = ChatSvc.SendMessageAsync(text, replyId, null, ids, previews, key, blobUrls, linkOff);
        try { await JS.InvokeVoidAsync("ipop.resetComposer", composerRef); } catch { }
        // Tell the room we stopped typing, otherwise remote clients keep the indicator up
        // until their 8s local timeout expires.
        try { await ChatSvc.StopTypingAsync(); } catch { }
        var outcome = await sendTask;
        // Local previews are only needed to put a failed message back into the composer.
        if (outcome?.State == IPop.Web.Services.SendState.Confirmed)
            foreach (var u in blobUrls) _ = JS.InvokeVoidAsync("ipop.revokeBlob", u);
    }

    // Micro #15: the send button stays for one beat after sending so its arrow can fly out.
    private Microsoft.AspNetCore.Components.ElementReference sendBtnRef;
    private bool sendFlying;
    private async Task FlySendArrowAsync()
    {
        sendFlying = true;
        await Task.Delay(460);
        sendFlying = false;
        if (!_disposed) StateHasChanged();
    }

    private async Task ScrollToBottom()
    {
        try
        {
            await JS.InvokeAsync<bool>("ipop.scrollBottom", messagesRef, false);
        }
        catch { /* Element might not exist yet */ }
    }

    /// Follow new messages only if the reader is at the bottom; otherwise count them on the
    /// "new messages" pill and leave the scroll position alone.
    private async Task FollowNewMessages()
    {
        var count = pendingIncoming;
        pendingIncoming = 0;
        bool scrolled;
        try { scrolled = await JS.InvokeAsync<bool>("ipop.scrollBottom", messagesRef, true); }
        catch { return; }
        if (!scrolled && count > 0)
        {
            newBelowCount += count;
            StateHasChanged();
        }
    }

    // ---- Emoji/Sticker Picker (Task 16.1) ----

    private async Task ToggleEmojiPicker()
    {
        await JS.InvokeVoidAsync("ipop.emojiSaveCaret", composerRef);
        pickerOpen = !pickerOpen;
        plusMenuOpen = false;
        stickerOpen = false;
    }

    private Task CloseEmojiPicker()
    {
        pickerOpen = false;
        focusComposerPending = true;
        return Task.CompletedTask;
    }

    // Outside click: just close; focus goes wherever the user clicked.
    private void DismissEmojiPicker() => pickerOpen = false;

    private async Task InsertEmoji(string emoji)
    {
        messageText = await JS.InvokeAsync<string>("ipop.emojiInsert", composerRef, emoji);
        pickerOpen = false;
        focusComposerPending = true;
        await OnComposerInput();
    }
    // ---- Stickers ----

    private async Task OpenStickers()
    {
        stickerOpen = true;
        stickerError = null;
        await ChatSvc.LoadStickersAsync();
    }

    /// Any sticker key: "custom:{id}", "fluent:{slug}" or a legacy Lucide name.
    private async Task SendStickerKey(string key)
    {
        stickerOpen = false;
        pickerOpen = false;
        await ChatSvc.SendMessageAsync(key, null, "sticker");
        ChatSvc.NoteStickerUsed(key);
        scrollToBottom = true;
        focusComposerPending = true;
    }

    /// "+ Create" tile: the picker closes and the editor opens empty (file or paste).
    private void OpenStickerEditor()
    {
        stickerOpen = false;
        stickerEditor?.Open();
    }

    private async Task OnStickerEditorSaved((Guid Id, bool Send) saved)
    {
        ChatSvc.StickerTab = "mine";
        if (saved.Send) await SendStickerKey(IPop.Application.Chat.StickerLimits.Content(saved.Id));
        else Toasts.Show("Added to My stickers.", ToastKind.Success);
    }

    private async Task RemoveSticker(Guid id)
    {
        var err = await ChatSvc.StickerActionAsync(() => Auth.SendAsync(HttpMethod.Delete, $"/api/chat/stickers/{id}"));
        if (err is not null) stickerError = StickerErrorText(err);
    }

    private async Task SaveStickerFromMessage(Guid id)
    {
        ctxMsgId = null;
        var err = await ChatSvc.StickerActionAsync(() => Auth.SendAsync(HttpMethod.Post, $"/api/chat/stickers/{id}/save"));
        if (err is null) Toasts.Show("Added to My stickers.", ToastKind.Success);
        else Toasts.Show(StickerErrorText(err), ToastKind.Error);
    }

    private async Task ToggleStickerFavorite(string key, bool favorite)
    {
        ctxMsgId = null;
        await ChatSvc.LoadStickersAsync();
        if (await ChatSvc.SetStickerFavoriteAsync(key, favorite))
            Toasts.Show(favorite ? "Added to favorites." : "Removed from favorites.", ToastKind.Success);
        else Toasts.Show("Couldn't update favorites. Try again.", ToastKind.Error);
    }

    /// "Make sticker" on a chat image: still images open in the editor; animated ones are detected
    /// there and saved through the server's from-attachment conversion so the frames survive.
    private void MakeStickerFromAttachment(IPop.Application.Chat.AttachmentDto a)
    {
        ctxMsgId = null;
        stickerEditor?.OpenAttachment(a.Id);
    }

    private async Task SetStickerOfficial(Guid id, bool official)
    {
        ctxMsgId = null;
        var err = await ChatSvc.StickerActionAsync(() => Auth.SendAsync(HttpMethod.Put, $"/api/chat/stickers/{id}/official", new { official }));
        if (err is null) Toasts.Show(official ? "Added to the official pack." : "Removed from the official pack.", ToastKind.Success);
        else Toasts.Show(StickerErrorText(err), ToastKind.Error);
    }

    private static bool IsStickerableImage(IPop.Application.Chat.AttachmentDto a) =>
        a.MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    private static string StickerErrorText(string code) => code switch
    {
        "too_large" => "Image is too large (max 10 MB).",
        "not_an_image" or "invalid_image" => "The file must be a PNG, JPG, GIF or WebP image.",
        "limit_reached" => "My stickers is full (max 200). Remove a few first.",
        "not_found" => "Sticker or image not found.",
        _ => "Couldn't process the sticker. Try again.",
    };

    // ---- Mention Autocomplete (Task 16.2 + ipop-30 @project + ipop-31 #task) ----

    private List<IPop.Application.Projects.ProjectListDto> projectMentionCandidates = [];
    private bool projectsLoadedOnce;
    private IPop.Application.Projects.ProjectTaskDto[] taskMentionCandidates = [];
    private string tasksLoadedForProject = "";
    // ipop-31: which trigger the popup is currently serving — '@' = user/project, '#' = task
    private char mentionTrigger;
    private IPop.Application.Projects.ProjectListDto? mentionTaskProject;

    private async Task LoadProjectCandidatesIfNeeded()
    {
        // ipop-30: lazy-load the member-visible project list once for the composer @project picker.
        if (projectsLoadedOnce || Auth is null || !Auth.IsAuthenticated) return;
        try
        {
            var res = await Auth.GetAsync<IPop.Application.Projects.PagedResult<IPop.Application.Projects.ProjectListDto>>("/api/projects?page=1&pageSize=100");
            projectMentionCandidates = res?.Items.ToList() ?? [];
            projectsLoadedOnce = true;
            StateHasChanged(); // repaint so first-@ users see results without typing again
        }
        catch { /* bounded: picker simply stays user-only if load fails */ }
    }

    private sealed record ComposerCaret(int Start, int End);

    private async Task<int> ComposerCaretAsync()
    {
        try
        {
            var c = await JS.InvokeAsync<ComposerCaret>("ipop.caretOf", composerRef);
            if (c is not null && c.Start >= 0 && c.Start <= messageText.Length) return c.Start;
        }
        catch { /* fall back to end of text */ }
        return messageText.Length;
    }

    /// Caret-aware: the '@' (people/projects) or '#' (tasks) token under the caret drives
    /// the popup, only when the trigger starts a word so e-mail addresses never match.
    private async Task CheckMentionAsync()
    {
        pickedMentions = ComposerMentionModel.Prune(messageText, pickedMentions);
        if (string.IsNullOrEmpty(messageText)) { activeMention = null; CloseMentionPopup(false); return; }
        var caret = await ComposerCaretAsync();
        EvaluateMention(caret);
    }

    private void EvaluateMention(int caret)
    {
        var at = ComposerMentionModel.DetectActiveAt(messageText, caret, '@');
        var hash = ComposerMentionModel.DetectActiveAt(messageText, caret, '#');
        // A finished pick ("@Ann Lee" or "@Ann Lee more words") is not an open query.
        if (at is { } a && pickedMentions.Any(p => p is MentionSegment.User u
                ? a.Query == u.Name || a.Query.StartsWith(u.Name + " ", StringComparison.Ordinal)
                : a.Query == MentionSyntax.AllLabel || a.Query.StartsWith(MentionSyntax.AllLabel + " ", StringComparison.Ordinal)))
            at = null;
        var useTaskTrigger = hash is not null && (at is null || hash.Value.AtIndex > at.Value.AtIndex);
        var active = useTaskTrigger ? hash : at;
        if (active is null)
        {
            activeMention = null;
            mentionFilter = "";
            CloseMentionPopup(true);
            return;
        }

        var query = active.Value.Query;
        if (activeMention?.AtIndex != active.Value.AtIndex || activeMention?.Query != query) mentionAltIndex = 0;
        activeMention = active;
        mentionFilter = query.ToLowerInvariant();
        mentionTrigger = useTaskTrigger ? '#' : '@';

        if (!useTaskTrigger)
        {
            // Group rooms: the members; DMs: the other person. Never yourself.
            var me = GetCurrentUserNik();
            var members = (ChatSvc.ActiveRoom?.Members ?? []).Where(m => m.Nik != me).ToList();
            mentionSuggestions = ComposerMentionModel.Filter(members, query, 10).ToList();
            mentionShowAll = IsGroupAdmin;
            var allMatches = mentionShowAll && "all".StartsWith(mentionFilter, StringComparison.Ordinal);
            mentionPopupOpen = mentionSuggestions.Count > 0 || allMatches;
            mentionTaskProject = null;

            if (!mentionPopupOpen && query.Length > 0 && !projectsLoadedOnce && Auth is not null && Auth.IsAuthenticated)
                _ = LoadProjectCandidatesIfNeeded().ContinueWith(_ => InvokeAsync(StateHasChanged));
        }
        else
        {
            // '#' — task candidates from the project already mentioned in this message.
            ResolveTaskSourceProject(active.Value.AtIndex);
            mentionSuggestions = [];
            mentionPopupOpen = false;
        }
    }

    private void CloseMentionPopup(bool keepProjectCandidates)
    {
        mentionPopupOpen = false;
        mentionSuggestions = [];
        if (!keepProjectCandidates) { /* no-op placeholder for symmetry */ }
    }

    /// ipop-31: work out which project's tasks to offer after a '#'. Prefer an explicit
    /// p:GUID token already in this message — resolved straight from the parsed GUID,
    /// so paste/edit paths work without ever having triggered the '@' picker.
    private void ResolveTaskSourceProject(int hashIdx)
    {
        mentionTaskProject = null;
        try
        {
            var segs = MentionParser.Parse(messageText);
            foreach (var s in segs.Reverse())
                if (s is MentionSegment.Project p)
                {
                    // Carry the GUID directly; EnsureTaskCandidatesAsync fetches by Id,
                    // so no dependency on projectMentionCandidates being loaded.
                    mentionTaskProject = projectMentionCandidates.FirstOrDefault(x => x.Id == p.ProjectId)
                        ?? new IPop.Application.Projects.ProjectListDto(p.ProjectId, "", 0, "", "", 0, null, 0, 0, false);
                    break;
                }
        }
        catch { /* token scan failure just means no source project */ }
    }

    /// ipop-31: load tasks for the chosen project once per project per composer session.
    public async Task EnsureTaskCandidatesAsync()
    {
        if (mentionTaskProject is null || Auth is null || !Auth.IsAuthenticated) return;
        var key = mentionTaskProject.Id.ToString("N");
        if (tasksLoadedForProject == key) return;
        try
        {
            var res = await Auth.GetAsync<IPop.Application.Projects.PagedResult<IPop.Application.Projects.ProjectTaskDto>>(
                $"/api/projects/{mentionTaskProject.Id}/tasks/list?page=1&pageSize=100");
            taskMentionCandidates = res?.Items.ToArray() ?? [];
            tasksLoadedForProject = key;
            StateHasChanged();
        }
        catch { /* bounded: picker stays empty if load fails */ }
    }

    /// Replace the "@query" under the caret with display text, remember the pick, and put
    /// the caret right after the inserted name.
    private async Task ReplaceActiveMention(string display, MentionSegment? picked, char trigger = '@')
    {
        var active = activeMention
            ?? ComposerMentionModel.DetectActiveAt(messageText, messageText.Length, trigger)
            ?? new ActiveMentionToken(messageText.Length, "");
        if (active.AtIndex >= messageText.Length || messageText[active.AtIndex] != trigger)
        {
            // Opened from the "+" menu with no '@' typed yet: append one.
            var sep = messageText.Length == 0 || char.IsWhiteSpace(messageText[^1]) ? "" : " ";
            messageText += sep + trigger;
            active = new ActiveMentionToken(messageText.Length - 1, "");
        }
        var (text, caret) = ComposerMentionModel.InsertDisplay(messageText, active, display);
        if (text.Length > MentionSyntax.MaxContentLength) { CloseMentionPopup(true); return; }
        messageText = text;
        if (picked is not null && !pickedMentions.Contains(picked)) pickedMentions.Add(picked);
        StageComposerDraft();
        activeMention = null;
        CloseMentionPopup(true);
        StateHasChanged();
        await Task.Yield(); // let the textarea receive the new value before placing the caret
        try { await JS.InvokeVoidAsync("ipop.setCaret", composerRef, caret, caret); } catch { }
        await GrowComposer();
    }

    private Task SelectMention(IPop.Application.Chat.MemberDto member)
    {
        var name = ComposerMentionModel.SafeName(member.Name);
        if (name.Length == 0 || name.Length > MentionSyntax.MaxNameLength) return Task.CompletedTask;
        return ReplaceActiveMention(name, new MentionSegment.User(member.UserId, name));
    }

    private async Task HandleMentionPick(IPop.Web.Components.Chat.MentionPopup.PopupPick pick)
    {
        if (pick.IsAll)
        {
            // Owner/admin only (the server enforces the same rule).
            if (IsGroupAdmin) await ReplaceActiveMention(MentionSyntax.AllLabel, new MentionSegment.All());
            else CloseMentionPopup(true);
            return;
        }
        if (pick.Member is not null) await SelectMention(pick.Member);
    }

    /// "+" menu → Mention: insert an '@' at the caret and open the list.
    private async Task OpenMention()
    {
        if (ChatSvc.ActiveRoom is null) return;
        var caret = await ComposerCaretAsync();
        var needsSpace = caret > 0 && !char.IsWhiteSpace(messageText[caret - 1]);
        var insert = (needsSpace ? " " : "") + "@";
        messageText = messageText[..caret] + insert + messageText[caret..];
        caret += insert.Length;
        EvaluateMention(caret);
        StateHasChanged();
        await Task.Yield();
        try { await JS.InvokeVoidAsync("ipop.setCaret", composerRef, caret, caret); } catch { }
    }

    // ipop-30: insert a bounded p:GUID project mention token so it renders as a clickable chip
    private async Task SelectProjectMention(IPop.Application.Projects.ProjectListDto proj)
    {
        var name = ComposerMentionModel.SafeName(proj.Name ?? "");
        if (name.Length == 0) { CloseMentionPopup(true); return; }
        // Token text is kept verbatim in the textarea ("@" is stripped by InsertDisplay's prefix).
        await ReplaceActiveMention($"[{name}](p:{proj.Id:D})", null);
    }

    // ipop-30: insert a bounded t:GUID task mention token (#trigger path) so it becomes a clickable chip
    private async Task SelectTaskMention(IPop.Application.Projects.ProjectTaskDto task)
    {
        var title = ComposerMentionModel.SafeName(task.Title ?? "");
        if (title.Length == 0) { CloseMentionPopup(true); return; }
        var active = activeMention ?? ComposerMentionModel.DetectActiveAt(messageText, messageText.Length, '#');
        if (active is null) { CloseMentionPopup(true); return; }
        // '#query' → '@[Title](t:GUID) '
        var token = $"@[{title}](t:{task.Id:D}) ";
        var end = Math.Min(messageText.Length, active.Value.AtIndex + 1 + active.Value.Query.Length);
        var text = messageText[..active.Value.AtIndex] + token + messageText[end..];
        if (text.Length > MentionSyntax.MaxContentLength) { CloseMentionPopup(true); return; }
        var caret = active.Value.AtIndex + token.Length;
        messageText = text;
        StageComposerDraft();
        activeMention = null;
        CloseMentionPopup(true);
        StateHasChanged();
        await Task.Yield();
        try { await JS.InvokeVoidAsync("ipop.setCaret", composerRef, caret, caret); } catch { }
    }

    // ---- File Upload (Fix 3) ----

    // ---- Profile Drawer (avatar click) ----

    private async Task OpenUserProfile(string? nik)
    {
        if (string.IsNullOrWhiteSpace(nik)) return;
        // The DM partner's card IS the DM's info page — show that instead of a second copy.
        var dmOther = ChatSvc.ActiveRoom?.Type == "dm"
            ? ChatSvc.ActiveRoom.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik())
            : null;
        if (dmOther is not null && string.Equals(dmOther.Nik, nik, StringComparison.OrdinalIgnoreCase))
        {
            if (drawerOpen) NavigateDetail(DetailPanelView.Main);
            else OpenDetailView(DetailPanelView.Main);
            return;
        }
        // From inside the drawer (member rows) push so Back returns there; otherwise fresh.
        if (drawerOpen) NavigateDetail(DetailPanelView.Person);
        else OpenDetailView(DetailPanelView.Person);
        profilePerson = null;
        personFromRoom = ChatSvc.ActiveRoom != null;
        StateHasChanged();
        try
        {
            var d = await Auth.GetAsync<IPop.Web.Services.DirectoryEmployeeDto>($"/api/chat/users/{Uri.EscapeDataString(nik)}");
            // Ignore a late answer if the user already moved on.
            if (d is not null && panelView == DetailPanelView.Person) profilePerson = MapEmployee(d);
        }
        catch { profilePerson = null; }
        StateHasChanged();
    }

    private Task CloseUserProfile()
    {
        GoBackDetail();
        return Task.CompletedTask;
    }

    private static IPop.Web.Models.Employee MapEmployee(IPop.Web.Services.DirectoryEmployeeDto d)
        => new(d.Name, d.Position ?? "—", d.Nik, d.Extension ?? "—", d.Location ?? "—",
               d.Department ?? "—", d.DepartmentCode, d.Site ?? "—", d.SiteCode,
               d.Manager ?? "—", d.Email, d.Status, IPop.Web.Models.AvatarPalette.For(d.Nik), d.PhotoPath, d.ManagerNik);

    private const long MaxUploadBytes = ChatUploadPolicy.MaxBytes;
    private int uploadingFiles;
    private CancellationTokenSource? composerUploadCts;

    private void CancelComposerUpload()
    {
        composerUploadCts?.Cancel();
        composerUploadCts = null;
        uploading = false;
        uploadingFiles = 0;
    }

    private async Task TriggerFileUpload()
    {
        if (uploading) { uploadError = "Wait for the current upload to finish."; return; }
        if (pendingAttachments.Count >= ChatUploadPolicy.MaxFiles)
        {
            uploadError = "You can attach up to 5 files per message.";
            return;
        }
        if (fileInput?.Element is { } input)
            await JS.InvokeVoidAsync("ipop.clickFileInput", input);
    }

    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        if (ChatSvc.ActiveRoomId is null) return;
        if (uploading) { uploadError = "Wait for the current upload to finish."; return; }
        failedUpload = null;
        await UploadFilesAsync(e.GetMultipleFiles(e.FileCount));
    }

    // Micro #13: a failed upload keeps its files so the error banner can offer Retry
    // (the input still holds them until the next pick, paste or drop).
    private IReadOnlyList<IBrowserFile>? failedUpload;

    private async Task RetryFailedUpload()
    {
        if (failedUpload is not { } files || uploading) return;
        failedUpload = null;
        uploadError = null;
        await UploadFilesAsync(files);
    }

    private async Task UploadFilesAsync(IReadOnlyList<IBrowserFile> selected)
    {
        if (ChatSvc.ActiveRoomId is not { } roomId) return;
        var composerId = composerWrapRef.Id;
        var plan = ChatUploadPolicy.Plan(selected.Select(f => new ChatUploadPolicy.File(f.Name, f.Size)).ToList(), pendingAttachments.Count);
        uploadError = plan.Notice;
        if (plan.Accepted.Count == 0) return;
        var failed = false;
        var cts = composerUploadCts = new CancellationTokenSource();
        uploading = true;
        uploadingFiles = plan.Accepted.Count;
        StateHasChanged();
        try
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(roomId.ToString()), "roomId");
            foreach (var i in plan.Accepted)
            {
                var file = selected[i];
                var part = new StreamContent(file.OpenReadStream(MaxUploadBytes, cts.Token));
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ChatAttachments.Mime(file.ContentType, file.Name));
                content.Add(part, "file", file.Name);
            }
            using var resp = await Auth.SendMultipartAsync($"/api/chat/attachments/upload?roomId={roomId}", content, cts.Token);
            if (ChatSvc.ActiveRoomId != roomId || composerWrapRef.Id != composerId || _disposed) return;
            if (resp.IsSuccessStatusCode)
            {
                var uploaded = await resp.Content.ReadFromJsonAsync<List<IPop.Application.Chat.AttachmentDto>>();
                if (uploaded is { Count: > 0 }) await AddPendingWithThumbs(uploaded, roomId, composerId);
                else uploadError = "No files were uploaded. Select the files and try again.";
            }
            else { uploadError = await ReadUploadError(resp); failed = (int)resp.StatusCode >= 500 || resp.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.BadGateway; }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception) { if (ChatSvc.ActiveRoomId == roomId && composerWrapRef.Id == composerId && !_disposed) { uploadError = "Couldn't upload. Check your connection, then try again."; failed = true; } }
        finally
        {
            if (failed && ChatSvc.ActiveRoomId == roomId && !_disposed) failedUpload = plan.Accepted.Select(i => selected[i]).ToList();
            if (composerUploadCts == cts)
            {
                composerUploadCts = null;
                uploading = false;
                uploadingFiles = 0;
            }
            cts.Dispose();
            if (!_disposed) StateHasChanged();
        }
    }

    private async Task AddPendingWithThumbs(IEnumerable<IPop.Application.Chat.AttachmentDto> uploaded, Guid roomId, string? composerId)
    {
        foreach (var a in uploaded)
        {
            if (ChatSvc.ActiveRoomId != roomId || composerWrapRef.Id != composerId || _disposed) return;
            string? preview = null;
            if (ChatAttachments.IsImage(a))
            {
                try
                {
                    var url = new Uri(Http.BaseAddress!, $"/api/chat/attachments/{a.Id}/thumbnail").ToString();
                    preview = await JS.InvokeAsync<string?>("ipop.authBlob", url, Auth.AccessToken);
                }
                catch { /* keep the typed-card fallback */ }
            }
            if (ChatSvc.ActiveRoomId != roomId || composerWrapRef.Id != composerId || _disposed)
            {
                if (preview is not null) await JS.InvokeVoidAsync("ipop.revokeBlob", preview);
                return;
            }
            pendingAttachments.Add(new PendingAttachmentItem(a.Id, a.FileName, a.FileSize, a.MimeType, preview));
        }
        focusComposerPending = true;
    }

    private void RemovePending(Guid id)
    {
        var idx = pendingAttachments.FindIndex(x => x.Id == id);
        if (idx < 0) return;
        var url = pendingAttachments[idx].PreviewUrl;
        if (!string.IsNullOrEmpty(url))
            _ = JS.InvokeVoidAsync("ipop.revokeBlob", url);
        pendingAttachments.RemoveAt(idx);
    }

    [JSInvokable]
    public Task OnPasteUploadNotice(string message) => InvokeAsync(() =>
    {
        uploadError = message;
        StateHasChanged();
    });

    private static async Task<string> ReadUploadError(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (body is not null && body.TryGetValue("code", out var code))
                return code switch
                {
                    "file_too_large" => "Files can be up to 50 MB each.",
                    "too_many_attachments" => "You can attach up to 5 files per message.",
                    "room_not_found" => "This conversation is no longer available.",
                    "file_required" => "No files were uploaded. Select the files and try again.",
                    "image_too_large" => body.TryGetValue("message", out var m) ? m : "That picture is too large. Send a smaller one.",
                    "storage_unavailable" => "The file server is not reachable right now. Try again in a moment.",
                    _ => "Couldn't upload. Select the files and try again."
                };
        }
        catch { /* non-JSON proxy response */ }
        if (resp.StatusCode is System.Net.HttpStatusCode.TooManyRequests) return "Too many uploads at once. Wait a minute, then try again.";
        return resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
            ? "You don't have permission to upload to this conversation."
            : "Couldn't upload. Check your connection, then select the files and try again.";
    }
}
