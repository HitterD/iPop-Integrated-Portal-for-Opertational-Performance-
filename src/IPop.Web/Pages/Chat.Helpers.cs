// Chat page — formatting/avatar helpers, message jump, scroll tracking, mention-chip navigation.
//
// Part of the Chat partial class. The class was split by responsibility out of a single
// ~1,500-line @code block; members were moved verbatim, never renamed or re-scoped.
// _Imports.razor only reaches .razor files, so usings are restated in each part.
using System.Net.Http.Json;
using IPop.Application.Chat.Mentions;
using IPop.Web.Services;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Chat
{
    // ---- Helpers ----

    private string GetCurrentUserNik() => Auth.Me?.Nik ?? "";

    private static string TruncateStr(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private string GetRoomName(IPop.Application.Chat.RoomListDto room)
        => room.Type == "dm"
            ? (!string.IsNullOrWhiteSpace(room.RecipientName) ? room.RecipientName : (!string.IsNullOrWhiteSpace(room.RecipientNik) ? room.RecipientNik : "Direct Message"))
            : (room.Name ?? "Group");

    private string GetActiveRoomName()
    {
        if (ChatSvc.ActiveRoom is null) return "";
        if (ChatSvc.ActiveRoom.Type == "dm")
        {
            var other = ChatSvc.ActiveRoom.Members.FirstOrDefault(m => m.Nik != GetCurrentUserNik());
            return other?.Name ?? "Direct Message";
        }
        return ChatSvc.ActiveRoom.Name ?? "Group";
    }

    private string GetRoomInitials(IPop.Application.Chat.RoomListDto room)
    {
        var name = room.Type == "dm" ? (room.RecipientName ?? room.RecipientNik ?? "?") : (room.Name ?? "G");
        return GetInitialsFromName(name);
    }

    private string GetActiveRoomInitials()
    {
        if (ChatSvc.ActiveRoom is null) return "?";
        return GetInitialsFromName(GetActiveRoomName());
    }

    private static string GetInitialsFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var clean = name.Trim().TrimStart('\'', '"', '`', '(', '[', '{');
        if (string.IsNullOrWhiteSpace(clean)) return "?";
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var p0 = parts[0].TrimStart('\'', '"', '`', '(', '[');
            var p1 = parts[1].TrimStart('\'', '"', '`', '(', '[');
            if (p0.Length > 0 && p1.Length > 0)
                return $"{p0[0]}{p1[0]}".ToUpperInvariant();
        }
        return clean.Length >= 2 ? clean[..2].ToUpperInvariant() : clean.ToUpperInvariant();
    }

    // ipop-33/33b: single source of truth in Models.AvatarPalette — same palette and
    // same hash for Chat, Directory, Admin and the member picker, so a person's colour
    // is consistent across modules and sessions.
    private static string ColorForHash(int hash)
        => IPop.Web.Models.AvatarPalette.ForHash(hash);

    private static string GetRoomColor(IPop.Application.Chat.RoomListDto room)
        => !string.IsNullOrWhiteSpace(room.AvatarColor) ? room.AvatarColor : ColorForHash(room.Id.GetHashCode());

    private string GetActiveRoomColor()
    {
        if (ChatSvc.ActiveRoom is null) return "var(--accent)";
        if (!string.IsNullOrWhiteSpace(ChatSvc.ActiveRoom.AvatarColor)) return ChatSvc.ActiveRoom.AvatarColor;
        return ColorForHash(ChatSvc.ActiveRoom.Id.GetHashCode());
    }

    /// Group photo URLs are API-relative; the WASM host is a different origin.
    private string PhotoUrl(string path) => new Uri(Http.BaseAddress!, path).ToString();

    private static string GetUserColor(string nik)
        => IPop.Web.Models.AvatarPalette.For(nik);

    private static string FormatTime(DateTime? dt)
    {
        if (dt is null) return "";
        var now = DateTime.Now;
        var d = dt.Value.ToLocalTime();
        if (d.Date == now.Date) return d.ToString("HH:mm");
        if (d.Date == now.Date.AddDays(-1)) return "Yesterday";
        if ((now - d).TotalDays < 7) return d.ToString("ddd");
        return d.ToString("MMM d");
    }

    private static string FormatDateSep(DateTime dt)
    {
        var d = dt.ToLocalTime();
        var now = DateTime.Now;
        if (d.Date == now.Date) return "Today";
        if (d.Date == now.Date.AddDays(-1)) return "Yesterday";
        if (d.Year != now.Year) return d.ToString("dddd, MMMM d, yyyy");
        return d.ToString("dddd, MMMM d");
    }

    private string FormatTypingLabel()
    {
        var users = ChatSvc.TypingUsers.ToList();
        return users.Count switch
        {
            1 => $"{users[0]} is typing...",
            2 => $"{users[0]} and {users[1]} are typing...",
            3 => $"{users[0]}, {users[1]}, and {users[2]} are typing...",
            _ => $"{users.Count} people are typing..."
        };
    }

    private static string PresenceLabel(string? status) => status switch
    {
        "on" => "Online",
        "away" => "Away",
        "busy" => "Busy",
        _ => "Offline"
    };

    private static readonly System.Text.RegularExpressions.Regex UrlRx = new(
        @"\b(https?://[^\s<>""]+|www\.[^\s<>""]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string FormatMessageContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        try
        {
            // Use the full mention parser + renderer so @project (p:GUID) and @task (t:GUID) render as chips,
            // and user mentions get proper active/inactive treatment. Legacy loose @word still get .legacy spans.
            var dir = new ChatDir(ChatSvc?.ActiveRoom);
            var isGroup = ChatSvc?.ActiveRoom?.Type == "group";
            var legacy = ChatSvc?.ActiveRoom?.MentionDirectory?.Select(d => (d.Nik, d.Name)).ToList();
            var html = MentionRenderer.ToHtml(content, dir, isGroup, Auth?.Me?.Nik, legacy);
            return html.Replace("\n", "<br />");
        }
        catch
        {
            // Fallback (should rarely hit)
            var safe = content
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
            safe = UrlRx.Replace(safe, m =>
            {
                var shown = m.Value;
                var href = shown.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + shown : shown;
                return $"<a class=\"msg-link\" href=\"{href}\" target=\"_blank\" rel=\"noopener noreferrer\">{shown}</a>";
            });
            safe = System.Text.RegularExpressions.Regex.Replace(safe, @"@(\w+)", "<span class=\"mention legacy\">@$1</span>");
            return safe.Replace("\n", "<br />");
        }
    }

    private sealed class ChatDir : IMentionDirectory
    {
        private readonly Dictionary<Guid, (string nik, string name, bool active)> _map;
        public ChatDir(IPop.Application.Chat.ChatRoomDto? room)
        {
            _map = (room?.MentionDirectory ?? Array.Empty<IPop.Application.Chat.MentionUserDto>()).ToDictionary(x => x.UserId, x => (x.Nik, x.Name, x.IsActive));
        }
        public bool TryResolve(Guid userId, out string nik, out string name, out bool isActive)
        {
            if (_map.TryGetValue(userId, out var v)) { nik = v.nik; name = v.name; isActive = v.active; return true; }
            nik = ""; name = ""; isActive = false; return false;
        }
    }

    // ---- Jump to a message (search results / pinned panel) ----

    private async Task JumpToMessage(Guid messageId)
    {
        // The target may live on an older page; pull history in until we find it.
        var guard = 0;
        while (ChatSvc.ActiveMessages.All(m => m.Id != messageId) && ChatSvc.HasOlderMessages && guard++ < 20)
            await ChatSvc.LoadOlderMessagesAsync();

        if (ChatSvc.ActiveMessages.All(m => m.Id != messageId)) return;
        searchOpen = false;
        pinnedPanelOpen = false;
        StateHasChanged();
        await Task.Yield();
        await JS.InvokeVoidAsync("ipop.scrollToMessage", messagesRef, messageId);
    }

    // ---- Scroll position ----

    private bool showJumpToLatest;
    private bool scrollWatchBound;

    /// Called from JS only when the list crosses the "far from bottom" threshold.
    [JSInvokable]
    public void OnScrollStateChanged(bool awayFromBottom)
    {
        if (awayFromBottom == showJumpToLatest) return;
        showJumpToLatest = awayFromBottom;
        _ = InvokeAsync(StateHasChanged);
    }

    private async Task JumpToLatest()
    {
        showJumpToLatest = false;
        await ScrollToBottom();
    }

    // ---- Attachment helpers ----

    private static bool IsImage(string mime) =>
        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string FormatFileSize(long bytes) => ChatAttachments.FormatSize(bytes);

    // ---- @user mention in a bubble → that person's profile (called from JS) ----
    [JSInvokable]
    public Task OnMentionUserClick(string nik) => OpenUserProfile(nik);

    // ---- ipop-22: Project/Task mention chip navigation (called from JS) ----
    [JSInvokable]
    public void OnProjectChipClick(string projectIdStr)
    {
        if (Guid.TryParse(projectIdStr, out var pid))
        {
            Nav.NavigateTo($"/projects/{pid}");
        }
    }

    [JSInvokable]
    public async Task OnTaskChipClick(string taskIdStr)
    {
        if (Guid.TryParse(taskIdStr, out var tid))
        {
            // ipop-28: resolve the task to its owning project and jump there with a task highlight,
            // instead of discarding the GUID on the bare list.
            try
            {
                var task = await Auth.GetAsync<IPop.Application.Projects.ProjectTaskDto>($"/api/projects/tasks/{tid}");
                if (task is not null)
                {
                    Nav.NavigateTo($"/projects/{task.ProjectId}?task={tid}");
                    return;
                }
            }
            catch { /* fall through to bounded fallback */ }

            // Not found / not permitted — land on the project list rather than nowhere.
            Nav.NavigateTo("/projects");
        }
    }
}
