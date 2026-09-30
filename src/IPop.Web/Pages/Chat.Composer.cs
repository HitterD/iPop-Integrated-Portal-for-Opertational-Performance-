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
        await ChatSvc.SendTypingAsync();
        await CheckMentionAsync();
        await GrowComposer();
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

    private async Task SendMessage()
    {
        var hasText = !string.IsNullOrWhiteSpace(messageText);
        var hasAttachments = pendingAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;
        // no sentinel; the bubble renders attachments directly. Picked "@Name" text becomes
        // @[Name](u:GUID) tokens here so the server records mentions and notifies.
        var text = hasText ? ComposerMentionModel.ToWire(messageText.Trim(), ComposerMentionModel.Prune(messageText, pickedMentions)) : "";
        pickedMentions = [];
        activeMention = null;
        CloseMentionPopup(true);
        // ipop-38b: snapshot the room up front with everything else — reading
        // ActiveRoomId after the awaits below can pick up a room the user
        // switched to mid-send and delete THEIR draft (silent data loss).
        var sendRoomId = ChatSvc.ActiveRoomId;
        messageText = "";
        uploadError = null;
        try { await JS.InvokeVoidAsync("ipop.resetComposer", composerRef); } catch { }
        var ids = hasAttachments ? pendingAttachments.Select(a => a.Id).ToList() : null;
        // The optimistic bubble needs real attachment metadata to draw its tiles.
        var previews = hasAttachments ? pendingAttachments.Select(a => a.AsDto()).ToList() : null;
        var toRevoke = pendingAttachments.Where(p => !string.IsNullOrEmpty(p.PreviewUrl)).Select(p => p.PreviewUrl!).ToList();
        pendingAttachments = [];
        scrollToBottom = true;
        // Clicking Send moves focus to the button; hand it back so typing can continue.
        focusComposerPending = true;

        // Tell the room we stopped typing before the message lands, otherwise remote
        // clients keep the indicator up until their 8s local timeout expires.
        await ChatSvc.StopTypingAsync();
        await ChatSvc.SendMessageAsync(text, replyToMsg?.Id, null, ids, previews);
        foreach (var u in toRevoke) _ = JS.InvokeVoidAsync("ipop.revokeBlob", u);
        replyToMsg = null;
        try { await JS.InvokeVoidAsync("ipop.resetComposer", composerRef); } catch { }
        // ipop-38: the sent draft is consumed — drop it from the per-room draft store
        // so returning to this room doesn't restore an already-sent message.
        if (sendRoomId is not null)
            _ = Drafts.RemoveAsync(sendRoomId.Value);
    }

    private async Task ScrollToBottom()
    {
        try
        {
            await JS.InvokeVoidAsync("ipop.scrollBottom", messagesRef);
        }
        catch { /* Element might not exist yet */ }
    }

    // ---- Emoji/Sticker Picker (Task 16.1) ----

    private void InsertEmoji(string emoji) { messageText += emoji; pickerOpen = false; }
    private async Task SendSticker(string iconName) { pickerOpen = false; await ChatSvc.SendMessageAsync(iconName, null, "sticker"); scrollToBottom = true; }

    // ---- Custom stickers ----

    private async Task OpenStickers()
    {
        stickerOpen = true;
        stickerError = null;
        await ChatSvc.LoadStickersAsync();
        // Nothing of my own yet but an official pack exists: open on that instead of an empty tab.
        if (stickerTab == "mine" && ChatSvc.Stickers.Mine.Count == 0 && ChatSvc.Stickers.Official.Count > 0) stickerTab = "official";
    }

    private async Task SendCustomSticker(Guid id)
    {
        pickerOpen = false;
        await ChatSvc.SendMessageAsync(IPop.Application.Chat.StickerLimits.Content(id), null, "sticker");
        scrollToBottom = true;
    }

    private async Task PickStickerFile()
    {
        stickerError = null;
        if (stickerInput?.Element is { } input) await JS.InvokeVoidAsync("ipop.clickFileInput", input);
    }

    private async Task OnStickerFileSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file.Size > IPop.Application.Chat.StickerLimits.MaxUploadBytes) { stickerError = "Gambar terlalu besar (maks 512 KB)."; return; }
        stickerUploading = true;
        stickerError = null;
        StateHasChanged();
        try
        {
            var err = await ChatSvc.StickerActionAsync(async () =>
            {
                using var form = new MultipartFormDataContent();
                var part = new StreamContent(file.OpenReadStream(IPop.Application.Chat.StickerLimits.MaxUploadBytes));
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ChatAttachments.Mime(file.ContentType, file.Name));
                form.Add(part, "file", file.Name);
                return await Auth.SendMultipartAsync("/api/chat/stickers", form);
            });
            if (err is null) stickerTab = "mine";
            else stickerError = StickerErrorText(err);
        }
        finally
        {
            stickerUploading = false;
            StateHasChanged();
        }
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
        if (err is null) Toasts.Show("Stiker disimpan ke Stiker saya.", ToastKind.Success);
        else Toasts.Show(StickerErrorText(err), ToastKind.Error);
    }

    private async Task MakeStickerFromAttachment(Guid attachmentId)
    {
        ctxMsgId = null;
        Toasts.Show("Membuat stiker…", ToastKind.Info);
        var err = await ChatSvc.StickerActionAsync(() => Auth.SendAsync(HttpMethod.Post, $"/api/chat/stickers/from-attachment/{attachmentId}"));
        if (err is null) { stickerTab = "mine"; Toasts.Show("Stiker dibuat. Cari di Stiker saya.", ToastKind.Success); }
        else Toasts.Show(StickerErrorText(err), ToastKind.Error);
    }

    private async Task SetStickerOfficial(Guid id, bool official)
    {
        ctxMsgId = null;
        var err = await ChatSvc.StickerActionAsync(() => Auth.SendAsync(HttpMethod.Put, $"/api/chat/stickers/{id}/official", new { official }));
        if (err is null) Toasts.Show(official ? "Stiker masuk paket resmi." : "Stiker dikeluarkan dari paket resmi.", ToastKind.Success);
        else Toasts.Show(StickerErrorText(err), ToastKind.Error);
    }

    private static bool IsStickerableImage(IPop.Application.Chat.AttachmentDto a) =>
        a.MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    private static string StickerErrorText(string code) => code switch
    {
        "too_large" => "Gambar terlalu besar (maks 512 KB).",
        "not_an_image" or "invalid_image" => "File harus gambar PNG, JPG, GIF, atau WebP.",
        "limit_reached" => "Stiker saya sudah penuh (maks 200). Hapus beberapa dulu.",
        "not_found" => "Stiker atau gambar tidak ditemukan.",
        _ => "Stiker gagal diproses. Coba lagi.",
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
               d.Manager ?? "—", d.Email, d.Status, IPop.Web.Models.AvatarPalette.For(d.Nik), d.PhotoPath);

    private const long MaxUploadBytes = 50L * 1024 * 1024;

    private async Task TriggerFileUpload()
    {
        if (pendingAttachments.Count >= 5)
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
        uploadError = null;
        uploading = true;
        StateHasChanged();

        try
        {
            var remaining = Math.Max(0, 5 - pendingAttachments.Count);
            if (remaining == 0)
            {
                uploadError = "You can attach up to 5 files per message.";
                return;
            }

            // GetMultipleFiles throws above its cap, so ask for the browser's full
            // selection and trim here — that lets us explain what was dropped.
            var selected = e.GetMultipleFiles(e.FileCount);
            var skippedTooBig = new List<string>();
            var accepted = 0;

            var content = new MultipartFormDataContent();
            content.Add(new StringContent(ChatSvc.ActiveRoomId.Value.ToString()), "roomId");

            foreach (var file in selected)
            {
                if (accepted >= remaining) break;
                if (file.Size > MaxUploadBytes) { skippedTooBig.Add(file.Name); continue; }

                var part = new StreamContent(file.OpenReadStream(MaxUploadBytes));
                // Without an explicit Content-Type the server sees "" and cannot tell an
                // image from a spreadsheet — this is what broke every attachment preview.
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    ChatAttachments.Mime(file.ContentType, file.Name));
                content.Add(part, "file", file.Name);
                accepted++;
            }

            var notes = new List<string>();
            if (skippedTooBig.Count > 0)
                notes.Add($"{string.Join(", ", skippedTooBig)} exceeded the 50 MB limit.");
            if (selected.Count - skippedTooBig.Count > remaining)
                notes.Add($"Only {remaining} more file(s) can be attached to this message.");

            if (accepted == 0)
            {
                uploadError = notes.Count > 0 ? string.Join(" ", notes) : "No files were uploaded.";
                return;
            }

            var resp = await Auth.SendMultipartAsync("/api/chat/attachments/upload", content);
            if (resp.IsSuccessStatusCode)
            {
                var uploaded = await resp.Content.ReadFromJsonAsync<List<IPop.Application.Chat.AttachmentDto>>();
                if (uploaded is not null)
                    await AddPendingWithThumbs(uploaded);
                if (notes.Count > 0) uploadError = string.Join(" ", notes);
            }
            else
            {
                uploadError = await ReadUploadError(resp);
            }
        }
        catch (Exception ex)
        {
            uploadError = ex.Message;
        }
        finally
        {
            uploading = false;
            StateHasChanged();
        }
    }

    private async Task AddPendingWithThumbs(IEnumerable<IPop.Application.Chat.AttachmentDto> uploaded)
    {
        foreach (var a in uploaded)
        {
            string? preview = null;
            if (ChatAttachments.IsImage(a))
            {
                try
                {
                    var thumbPath = $"/api/chat/attachments/{a.Id}/thumbnail";
                    var url = new Uri(Http.BaseAddress!, thumbPath).ToString();
                    preview = await JS.InvokeAsync<string?>("ipop.authBlob", url, Auth.AccessToken);
                }
                catch { /* keep the typed-card fallback */ }
            }
            pendingAttachments.Add(new PendingAttachmentItem(a.Id, a.FileName, a.FileSize, a.MimeType, preview));
        }
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

    public record PastedFileDto(string FileName, string ContentType, string Base64, long FileSize);

    [JSInvokable]
    public async Task OnPasteFiles(PastedFileDto[] files)
    {
        if (ChatSvc.ActiveRoomId is null || files is null || files.Length == 0) return;
        // Respect 5-file staging limit
        var remaining = Math.Max(0, 5 - pendingAttachments.Count);
        if (remaining == 0) return;
        var toUpload = files.Take(remaining).ToArray();

        uploadError = null;
        StateHasChanged();
        try
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(ChatSvc.ActiveRoomId.Value.ToString()), "roomId");
            foreach (var f in toUpload)
            {
                if (string.IsNullOrWhiteSpace(f.Base64)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(f.Base64); } catch { continue; }
                if (bytes.Length > 50 * 1024 * 1024) continue;
                var bc = new ByteArrayContent(bytes);
                bc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                    string.IsNullOrWhiteSpace(f.ContentType) ? "application/octet-stream" : f.ContentType);
                content.Add(bc, "file", string.IsNullOrWhiteSpace(f.FileName) ? "pasted-image.png" : f.FileName);
            }

            var resp = await Auth.SendMultipartAsync("/api/chat/attachments/upload", content);
            if (resp.IsSuccessStatusCode)
            {
                var uploaded = await resp.Content.ReadFromJsonAsync<List<IPop.Application.Chat.AttachmentDto>>();
                if (uploaded is not null)
                    await AddPendingWithThumbs(uploaded);
                StateHasChanged();
            }
            else
            {
                uploadError = await ReadUploadError(resp);
            }
        }
        catch (Exception ex)
        {
            uploadError = ex.Message;
        }
        finally
        {
            StateHasChanged();
        }
    }

    private static async Task<string> ReadUploadError(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (body is not null && body.TryGetValue("message", out var msg) && !string.IsNullOrWhiteSpace(msg))
                return msg;
        }
        catch { /* non-JSON body */ }
        return $"Upload failed ({(int)resp.StatusCode}).";
    }
}
