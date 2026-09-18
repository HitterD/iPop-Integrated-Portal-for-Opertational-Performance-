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
        CheckMention();
        await GrowComposer();
    }

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

    private async Task OnComposerKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            await SendMessage();
        }
    }

    private async Task SendMessage()
    {
        var hasText = !string.IsNullOrWhiteSpace(messageText);
        var hasAttachments = pendingAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;
        var text = hasText ? messageText.Trim() : ""; // no sentinel; the bubble renders attachments directly
        // ipop-38b: snapshot the room up front with everything else — reading
        // ActiveRoomId after the awaits below can pick up a room the user
        // switched to mid-send and delete THEIR draft (silent data loss).
        var sendRoomId = ChatSvc.ActiveRoomId;
        messageText = "";
        uploadError = null;
        var ids = hasAttachments ? pendingAttachments.Select(a => a.Id).ToList() : null;
        // The optimistic bubble needs real attachment metadata to draw its tiles.
        var previews = hasAttachments ? pendingAttachments.Select(a => a.AsDto()).ToList() : null;
        var toRevoke = pendingAttachments.Where(p => !string.IsNullOrEmpty(p.PreviewUrl)).Select(p => p.PreviewUrl!).ToList();
        pendingAttachments = [];
        scrollToBottom = true;

        // Tell the room we stopped typing before the message lands, otherwise remote
        // clients keep the indicator up until their 8s local timeout expires.
        await ChatSvc.StopTypingAsync();
        await ChatSvc.SendMessageAsync(text, replyToMsg?.Id, null, ids, previews);
        foreach (var u in toRevoke) _ = JS.InvokeVoidAsync("ipop.revokeBlob", u);
        replyToMsg = null;
        await GrowComposer();
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

    private void CheckMention()
    {
        if (string.IsNullOrEmpty(messageText)) { CloseMentionPopup(false); return; }

        // Find whichever trigger is active nearer the caret.
        var atIdx = messageText.LastIndexOf('@');
        var hashIdx = messageText.LastIndexOf('#');
        var useTaskTrigger = hashIdx > atIdx && hashIdx >= 0;

        var idx = useTaskTrigger ? hashIdx : atIdx;
        if (idx < 0 || idx == messageText.Length - 1)
        {
            mentionFilter = "";   // reset stale filter on bare/absent trigger
            CloseMentionPopup(true);
            return;
        }
        mentionFilter = messageText[(idx + 1)..].ToLowerInvariant();
        if (mentionFilter.Contains(' ') && mentionFilter.Length > 20) { CloseMentionPopup(true); return; }
        mentionTrigger = useTaskTrigger ? '#' : '@';

        // '@' — user candidates first (group rooms only), then projects when no member matches
        if (!useTaskTrigger)
        {
            mentionSuggestions = ChatSvc.ActiveRoom?.Type == "group"
                ? ChatSvc.ActiveRoom.Members
                    .Where(m => m.Name.ToLowerInvariant().Contains(mentionFilter))
                    .Take(10).ToList()
                : [];
            mentionPopupOpen = mentionSuggestions.Any();
            mentionTaskProject = null;

            if (!mentionPopupOpen && !projectsLoadedOnce && Auth is not null && Auth.IsAuthenticated)
                _ = LoadProjectCandidatesIfNeeded().ContinueWith(_ => InvokeAsync(StateHasChanged));
        }
        else
        {
            // '#' — task candidates from a selected project; pick the project by which
            // one's name is nearest before '#', else fall back to the last project mention in text.
            ResolveTaskSourceProject(hashIdx);
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

    private void SelectMention(IPop.Application.Chat.MemberDto member)
    {
        var atIdx = messageText.LastIndexOf('@');
        if (atIdx >= 0) messageText = messageText[..atIdx] + $"@{member.Name} ";
        mentionPopupOpen = false;
    }

    private void HandleMentionPick(IPop.Web.Components.Chat.MentionPopup.PopupPick pick)
    {
        if (pick.IsAll)
        {
            // Insert @all directly; server will expand it.
            var atIdx = messageText.LastIndexOf('@');
            if (atIdx >= 0) messageText = messageText[..atIdx] + "@all ";
            mentionPopupOpen = false;
            return;
        }
        if (pick.Member is not null) SelectMention(pick.Member);
    }

    private void OpenMention()
    {
        if (ChatSvc.ActiveRoom?.Type != "group") return;
        // Seed with empty filter to show all candidates (or @all if ShowAll wiring is added later).
        mentionFilter = "";
        mentionTrigger = '@';
        mentionSuggestions = ChatSvc.ActiveRoom.Members
            .Where(m => m.Nik != GetCurrentUserNik())
            .Take(10).ToList();
        mentionPopupOpen = mentionSuggestions.Any();
        mentionTaskProject = null;
    }

    // ipop-30: insert a bounded p:GUID project mention token so it renders as a clickable chip
    private void SelectProjectMention(IPop.Application.Projects.ProjectListDto proj)
    {
        if (!string.IsNullOrEmpty(proj.Name))
        {
            var token = $"@[{proj.Name.Replace("[", "").Replace("]", "")}](p:{proj.Id:D})";
            var atIdx = messageText.LastIndexOf('@');
            if (atIdx >= 0 && messageText.Length - mentionFilter.Length - 1 + token.Length <= MentionSyntax.MaxContentLength)
                messageText = messageText[..atIdx] + token + " ";
        }
        mentionPopupOpen = false;
    }

    // ipop-30: insert a bounded t:GUID task mention token (#trigger path) so it becomes a clickable chip
    private void SelectTaskMention(IPop.Application.Projects.ProjectTaskDto task)
    {
        if (!string.IsNullOrEmpty(task.Title))
        {
            var token = $"@[{task.Title.Replace("[", "").Replace("]", "")}](t:{task.Id:D})";
            var hashIdx = messageText.LastIndexOf('#');
            if (hashIdx >= 0 && messageText.Length - mentionFilter.Length - 1 + token.Length <= MentionSyntax.MaxContentLength)
                messageText = messageText[..hashIdx] + token + " ";
        }
        mentionPopupOpen = false;
    }

    // ---- File Upload (Fix 3) ----

    // ---- Profile Drawer (avatar click) ----

    private async Task OpenUserProfile(string? nik)
    {
        if (string.IsNullOrWhiteSpace(nik)) return;
        profilePerson = null; // open drawer immediately with loading state
        try
        {
            var d = await Auth.GetAsync<IPop.Web.Services.DirectoryEmployeeDto>($"/api/chat/users/{Uri.EscapeDataString(nik)}");
            if (d is not null) profilePerson = MapEmployee(d);
        }
        catch { profilePerson = null; }
    }

    private Task CloseUserProfile() { profilePerson = null; return Task.CompletedTask; }

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
            pendingAttachments.Add(new PendingAtt(a.Id, a.FileName, a.FileSize, a.MimeType, preview));
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
