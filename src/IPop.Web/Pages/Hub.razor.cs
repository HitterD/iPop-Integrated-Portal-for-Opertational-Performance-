// The Hub — confidential cross-department topics (docs/superpowers/specs/2026-11-21-the-hub-design.md,
// mockup docs/mockups/the-hub.html). List on the left (For me · New · Ongoing · Solved), the open topic
// on the right as a Threads-style thread. "hubChanged" (topicId) from the hub re-reads the list and, if it
// is the open topic, the thread.
using IPop.Application.Hub;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IPop.Web.Pages;

public partial class Hub : IDisposable
{
    [Parameter] public Guid? Id { get; set; }
    [CascadingParameter] public IPop.Web.Layout.MainLayout? Layout { get; set; }

    [Inject] private AuthState Auth { get; set; } = default!;
    [Inject] private HubApi Api { get; set; } = default!;
    [Inject] private BadgeState Badges { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private PageCache Cache { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    internal const string CacheKey = "hub.list";
    public const long MaxUpload = 50L * 1024 * 1024;

    // ── List ──
    private HubListDto? list;
    private string filter = "mine";
    private string q = "";
    private string kind = "";
    private string? listError;
    private bool howtoHidden;
    private readonly HashSet<Guid> freshRows = [];

    // ── Open topic ──
    private HubTopicDto? topic;
    private string? topicError;
    private Guid? loadedId;
    private bool loadingTopic;
    private string sort = "old";
    private bool detailsOpen;
    private Guid? flashPost;

    // ── Composer ──
    private string draft = "";
    private Guid? askTo;
    private readonly List<HubUploadDto> pending = [];
    private bool uploading, sending;
    private Guid? replyParent;
    private string replyDraft = "";
    private readonly HashSet<Guid> reminded = [];
    private Guid? remindBusy;
    private Guid? addPerson;

    // ── Post actions (edit / delete / noted / best) ──
    private Guid? editing;
    private string editDraft = "";
    private Guid? confirmDelete;
    private Guid? acting;
    private bool menuOpen, seenOpen;
    private HubFileDto? zoom;
    private bool zoomBig, zoomFocused;

    // ── Sheets ──
    private bool newOpen, solveOpen;
    private Sheet sheet = new();
    private Solve solve = new();

    private IDisposable? sub;
    private bool disposed;
    private Guid Me => Auth.Me?.UserId ?? Guid.Empty;

    private sealed class Sheet
    {
        public string Kind = HubKinds.Problem;
        public string Title = "";
        public string Need = "";
        public string Due = "1w";
        public List<HubPersonDto> People = [];
        public string Search = "";
        public List<HubPersonDto> Results = [];
        public List<HubPersonDto> Suggested = [];
        public bool Sending;
        public string? Error;
    }

    private sealed class Solve
    {
        public string Summary = "";
        public bool FollowUp;
        public List<SolveTask> Tasks = [];
        public List<HubProjectOptionDto> Projects = [];
        public bool Sending;
        public string? Error;
    }

    private sealed class SolveTask
    {
        public string Title = "";
        public Guid Assignee;
        public Guid Project;
        public string Due = "1w";
    }

    protected override async Task OnInitializedAsync()
    {
        Layout?.SetHeader("The Hub", "Ask other departments, decide in writing");
        list = Cache.Peek<HubListDto>(CacheKey);
        try { howtoHidden = await JS.InvokeAsync<string?>("localStorage.getItem", "hub.howto") == "off"; } catch { }
        sub = Auth.On<Guid>(HubEvents.Changed, id => _ = InvokeAsync(() => OnLiveAsync(id)));
        await LoadListAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Id is null) { topic = null; loadedId = null; topicError = null; return; }
        if (loadedId == Id) return;
        loadedId = Id;
        replyParent = null; draft = ""; askTo = null; pending.Clear(); flashPost = null;
        editing = null; confirmDelete = null; menuOpen = false; seenOpen = false; zoom = null;
        await LoadTopicAsync(Id.Value, markRead: true);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (disposed) return;
        try
        {
            await JS.InvokeVoidAsync("ipop.icons");
            await JS.InvokeVoidAsync("ipopHub.after", flashPost?.ToString());
            flashPost = null;
            // Pictures in replies and the zoom view: /api/attachments wants the bearer token, so they load as blob: URLs.
            if (topic?.Files.Any(IsImage) == true)
                await JS.InvokeVoidAsync("ipop.mailImages.hydrate", null, Http.BaseAddress?.ToString() ?? "", Auth.AccessToken);
            // Esc closes the picture: the dialog needs focus to hear it.
            if (zoom is not null && !zoomFocused) { zoomFocused = true; await JS.InvokeVoidAsync("ipopHub.focus", "hub-zoom"); }
            if (zoom is null) zoomFocused = false;
        }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
        catch (JSException) { }
    }

    public void Dispose() { disposed = true; sub?.Dispose(); }

    // ── Loading ──────────────────────────────────────────────────────────

    private async Task LoadListAsync()
    {
        var before = list?.Topics.Select(t => t.Id).ToHashSet() ?? [];
        var r = await Api.ListAsync("all", null);
        if (!r.Ok) { listError = list is null ? r.Message : null; return; }
        list = r.Value;
        listError = null;
        Cache.Put(CacheKey, list!);
        if (before.Count > 0)
            foreach (var t in list!.Topics.Where(t => !before.Contains(t.Id))) freshRows.Add(t.Id);
        if (Id is null && list!.Counts.Mine == 0 && filter == "mine" && list.Topics.Count > 0) filter = "ongoing";
    }

    private async Task LoadTopicAsync(Guid id, bool markRead)
    {
        loadingTopic = topic?.Id != id;
        var r = await Api.GetAsync(id, peek: !markRead);
        loadingTopic = false;
        if (!r.Ok) { topic = null; topicError = r.Message; return; }
        topic = r.Value;
        topicError = null;
        if (markRead && list is not null)
        {
            list = list with { Topics = list.Topics.Select(t => t.Id == id ? t with { Unread = false } : t).ToList() };
            await Badges.RefreshAsync();
        }
    }

    private async Task OnLiveAsync(Guid id)
    {
        if (disposed) return;
        await LoadListAsync();
        if (Id == id && !sending)
        {
            var known = topic?.Posts.Select(p => p.Id).ToHashSet() ?? [];
            await LoadTopicAsync(id, markRead: true);
            flashPost = topic?.Posts.LastOrDefault(p => !known.Contains(p.Id) && p.Author?.Id != Me)?.Id;
        }
        StateHasChanged();
    }

    // ── List helpers ─────────────────────────────────────────────────────

    private IEnumerable<HubTopicListItemDto> Visible
    {
        get
        {
            if (list is null) return [];
            IEnumerable<HubTopicListItemDto> rows = filter switch
            {
                "new" => list.Topics.Where(t => t.Status == HubStatus.New),
                "ongoing" => list.Topics.Where(t => t.Status == HubStatus.Ongoing),
                "solved" => list.Topics.Where(t => t.Status == HubStatus.Solved),
                _ => list.Topics.Where(t => (t.Status != HubStatus.Solved && (t.WaitingForMe || t.Unread || t.CreatedBy.Id == Me)) || t.Id == Id),
            };
            if (kind.Length > 0) rows = rows.Where(t => t.Kind == kind);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var w = q.Trim();
                rows = rows.Where(t => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || t.People.Any(p => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                    || (t.LastPreview ?? "").Contains(w, StringComparison.OrdinalIgnoreCase));
            }
            return rows;
        }
    }

    private int Count(string f) => list is null ? 0 : f switch
    {
        "mine" => list.Counts.Mine,
        "new" => list.Counts.New,
        "ongoing" => list.Counts.Ongoing,
        _ => list.Counts.Solved,
    };

    private static string FilterHelp(string f) => f switch
    {
        "new" => "Sent, but nobody has replied yet.",
        "ongoing" => "People are replying. Not solved yet.",
        "solved" => "Done. Each one has a written answer.",
        _ => "Topics that need you, or that you started.",
    };

    internal static (string Css, string Text) Say(HubTopicListItemDto t)
    {
        if (t.Status == HubStatus.Solved) return ("s-ok", $"Solved {t.SolvedAt?.ToLocalTime():d MMM}");
        if (t.WaitingForMe) return (t.MeLate ? "s-late" : "s-you", t.MeLate ? "Late: your answer is needed" : $"Your answer is needed{Due(t.DueDate)}");
        if (t.LateFor.Count > 0) return ("s-late", $"Late: {string.Join(", ", t.LateFor)}");
        if (t.WaitingFor.Count > 0) return ("s-wait", $"Waiting for {string.Join(", ", t.WaitingFor)}");
        if (t.Status == HubStatus.New) return ("s-new", "Sent · no reply yet");
        return ("s-on", $"Ongoing · {t.Replies} {(t.Replies == 1 ? "reply" : "replies")}");
    }

    private static string Due(DateTime? d) => d is { } x ? $" · by {x:d MMM}" : "";

    private async Task HideHowtoAsync()
    {
        howtoHidden = true;
        try { await JS.InvokeVoidAsync("localStorage.setItem", "hub.howto", "off"); } catch { }
    }

    private void Open(Guid id)
    {
        freshRows.Remove(id);
        Nav.NavigateTo($"/hub/{id}");
    }

    private void Back() => Nav.NavigateTo("/hub");

    // ── Topic helpers ────────────────────────────────────────────────────

    internal static string Initials(string? name) => IPop.Web.Models.AvatarPhotos.Initials(name);

    private bool CanWrite => topic is not null && topic.SolvedAt is null && topic.People.Any(p => p.Id == Me);

    private HubAskDto? MyAsk => topic?.Asks.FirstOrDefault(a => a.To.Id == Me && a.AnsweredAt is null);

    private IReadOnlyList<HubAskDto> OpenAsks => topic?.Asks.Where(a => a.AnsweredAt is null).ToList() ?? [];

    private int StageIndex => topic?.Status switch { HubStatus.Solved => 2, HubStatus.Ongoing => 1, _ => 0 };

    internal static string Short(string name) => IPop.Application.PersonName.Short(name);

    private sealed record Thread(HubPostDto Post, List<HubPostDto> Replies);

    private List<object> Items()
    {
        // Top posts with their replies, and system lines in between, oldest or newest first.
        if (topic is null) return [];
        var tops = new List<object>();
        var byParent = topic.Posts.Where(p => p.ParentId is not null).GroupBy(p => p.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var p in topic.Posts.Where(p => p.ParentId is null))
        {
            var kids = byParent.TryGetValue(p.Id, out var r) ? r.Where(k => !k.Deleted).ToList() : [];
            if (p.Kind == "system") tops.Add(p);
            else if (!p.Deleted || kids.Count > 0) tops.Add(new Thread(p, kids));
        }
        if (sort == "new") tops.Reverse();
        return tops;
    }

    private int ReplyCount => topic?.Posts.Count(p => p.Kind == "reply" && !p.Deleted) ?? 0;

    private bool IsMember => topic is not null && topic.People.Any(p => p.Id == Me);

    private HubPostDto? Best => topic?.BestPostId is { } b ? topic.Posts.FirstOrDefault(p => p.Id == b && !p.Deleted) : null;

    internal static string StatusLabel(string status) => status switch { HubStatus.Solved => "Solved", HubStatus.Ongoing => "Ongoing", _ => "New" };

    internal static bool IsImage(HubFileDto f) => f.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        && !f.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    /// "Abdul", "Abdul and Dedi", "Abdul, Dedi and 2 others".
    internal static string NotedText(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        3 => $"{names[0]}, {names[1]} and {names[2]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} others",
    };

    private static string Rel(DateTime utc) => Relative(utc, DateTime.UtcNow);

    /// Short, friendly time for a reply: "just now", "5 min ago", "2 hours ago", "yesterday 14:05", "Tue 09:12", "3 Nov".
    internal static string Relative(DateTime utc, DateTime nowUtc)
    {
        var ago = nowUtc - utc;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        var l = utc.ToLocalTime();
        var today = nowUtc.ToLocalTime().Date;
        if (l.Date == today) { var h = (int)ago.TotalHours; return h == 1 ? "1 hour ago" : $"{h} hours ago"; }
        if (l.Date == today.AddDays(-1)) return $"yesterday {l:HH:mm}";
        if (l.Date > today.AddDays(-7)) return $"{l:ddd HH:mm}";
        return l.Year == today.Year ? $"{l:d MMM}" : $"{l:d MMM yyyy}";
    }

    private static string Full(DateTime utc) => utc.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm");

    private static string Size(long b) => b >= 1024 * 1024 ? $"{b / 1024d / 1024:0.#} MB" : $"{Math.Max(1, b / 1024)} KB";

    private static string FileTag(string name, string type)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        if (type.StartsWith("image/")) return "IMG";
        return ext.Length is > 0 and <= 4 ? ext : "FILE";
    }

    // ── Post actions ─────────────────────────────────────────────────────

    private void StartEdit(HubPostDto p) { editing = p.Id; editDraft = p.Body; confirmDelete = null; }
    private void CancelEdit() { editing = null; editDraft = ""; }

    private async Task SaveEditAsync()
    {
        if (topic is null || editing is not { } id || acting is not null) return;
        if (string.IsNullOrWhiteSpace(editDraft)) { Toasts.Show("Write something, or delete the reply instead.", ToastKind.Info); return; }
        acting = id;
        var r = await Api.EditPostAsync(topic.Id, id, editDraft);
        acting = null;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Not saved. Try again.", ToastKind.Error); return; }
        topic = r.Value;
        CancelEdit();
        flashPost = id;
        Toasts.Show("Saved. Everyone sees it marked as edited.", ToastKind.Success);
    }

    private async Task OnEditKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && (e.CtrlKey || e.MetaKey)) await SaveEditAsync();
        else if (e.Key == "Escape") CancelEdit();
    }

    private async Task DeleteAsync(HubPostDto p)
    {
        if (topic is null || acting is not null) return;
        acting = p.Id;
        var r = await Api.DeletePostAsync(topic.Id, p.Id);
        acting = null;
        confirmDelete = null;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Not deleted. Try again.", ToastKind.Error); return; }
        topic = r.Value;
        Toasts.Show("Reply deleted.", ToastKind.Success);
        await LoadListAsync();
        await Badges.RefreshAsync();
    }

    private async Task NoteAsync(HubPostDto p)
    {
        if (topic is null || acting is not null) return;
        acting = p.Id;
        var r = await Api.NoteAsync(topic.Id, p.Id);
        acting = null;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Couldn't save that. Try again.", ToastKind.Error); return; }
        topic = r.Value;
    }

    private async Task BestAsync(HubPostDto p)
    {
        if (topic is null || acting is not null) return;
        acting = p.Id;
        var r = await Api.BestAsync(topic.Id, p.Best ? null : p.Id);
        acting = null;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Couldn't save that. Try again.", ToastKind.Error); return; }
        topic = r.Value;
        Toasts.Show(p.Best ? "Best answer removed." : "Marked as the best answer. It's pinned under the topic.", ToastKind.Success);
        await LoadListAsync();
    }

    private void JumpTo(Guid postId) => flashPost = postId;

    private void ToggleSeen() { seenOpen = !seenOpen; menuOpen = false; }

    private void OpenZoom(HubFileDto f) { zoom = f; zoomBig = false; }

    private void OnZoomKey(KeyboardEventArgs e) { if (e.Key == "Escape") zoom = null; }

    /// Files need the sign-in token, so they are fetched and handed to the browser instead of linked.
    private async Task OpenFileAsync(HubFileDto f)
    {
        try { await JS.InvokeVoidAsync("ipopHub.open", Http.BaseAddress?.ToString() ?? "", Auth.AccessToken, f.Id.ToString(), f.FileName, IsImage(f) || f.ContentType == "application/pdf"); }
        catch (JSException) { Toasts.Show("The file couldn't be opened. Try again.", ToastKind.Error); }
    }

    // ── Composer ─────────────────────────────────────────────────────────

    private async Task OnFilesAsync(InputFileChangeEventArgs e)
    {
        if (topic is null) return;
        uploading = true;
        foreach (var f in e.GetMultipleFiles(5))
        {
            if (f.Size > MaxUpload) { Toasts.Show($"{f.Name} is larger than 50 MB.", ToastKind.Error); continue; }
            await using var s = f.OpenReadStream(MaxUpload);
            var r = await Api.UploadAsync(topic.Id, f.Name, f.ContentType, s);
            if (r.Ok && r.Value is { } up) pending.Add(up);
            else Toasts.Show(r.Message ?? $"{f.Name} couldn't be uploaded.", ToastKind.Error);
        }
        uploading = false;
    }

    private void RemovePending(HubUploadDto f) => pending.Remove(f);

    private async Task SendAsync()
    {
        if (topic is null || sending) return;
        if (string.IsNullOrWhiteSpace(draft) && pending.Count == 0) { Toasts.Show("Write something first.", ToastKind.Info); return; }
        sending = true;
        var wasAsked = MyAsk is not null;
        var r = await Api.PostAsync(topic.Id, new CreateHubPostRequest(draft, MyAsk?.PostId, askTo, pending.Select(p => p.Id).ToList()));
        sending = false;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Not sent. Try again.", ToastKind.Error); return; }
        var to = askTo is { } a ? topic.People.FirstOrDefault(p => p.Id == a)?.Name : null;
        topic = r.Value;
        flashPost = topic!.Posts.LastOrDefault(p => p.Author?.Id == Me)?.Id;
        draft = ""; askTo = null; pending.Clear();
        Toasts.Show(wasAsked ? "Answer sent. Everyone in the topic can see it." : to is not null ? $"Posted. {Short(to)} was asked to answer." : (topic.People.Count - 1 == 1 ? "Posted. 1 person was told." : $"Posted. {topic.People.Count - 1} people were told."), ToastKind.Success);
        await LoadListAsync();
        await Badges.RefreshAsync();
    }

    private async Task OnComposerKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && (e.CtrlKey || e.MetaKey)) await SendAsync();
    }

    private void StartReply(HubPostDto p) { replyParent = p.ParentId ?? p.Id; replyDraft = ""; replyTo = p; }
    private HubPostDto? replyTo;
    private void CancelReply() { replyParent = null; replyTo = null; replyDraft = ""; }

    private async Task SendReplyAsync()
    {
        if (topic is null || replyTo is null || sending) return;
        if (string.IsNullOrWhiteSpace(replyDraft)) { Toasts.Show("Write your reply first.", ToastKind.Info); return; }
        sending = true;
        var r = await Api.PostAsync(topic.Id, new CreateHubPostRequest(replyDraft, replyTo.Id, null, null));
        sending = false;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Not sent. Try again.", ToastKind.Error); return; }
        var name = replyTo.Author?.Name;
        topic = r.Value;
        flashPost = topic!.Posts.LastOrDefault(p => p.Author?.Id == Me)?.Id;
        CancelReply();
        Toasts.Show($"Replied to {Short(name ?? "them")}.", ToastKind.Success);
        await LoadListAsync();
    }

    private async Task OnReplyKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey) await SendReplyAsync();
        else if (e.Key == "Escape") CancelReply();
    }

    private async Task RemindAsync(HubAskDto a)
    {
        if (topic is null || remindBusy is not null) return;
        remindBusy = a.Id;
        var r = await Api.RemindAsync(topic.Id, a.Id);
        remindBusy = null;
        if (r.Ok || r.Code == "rate_limited") reminded.Add(a.Id);
        Toasts.Show(r.Ok ? $"Reminder sent to {Short(a.To.Name)} on PC and phone." : r.Message ?? "Couldn't send the reminder.", r.Ok ? ToastKind.Success : ToastKind.Info);
    }

    private async Task RemindLateAsync()
    {
        foreach (var a in OpenAsks.Where(a => a.Late && !reminded.Contains(a.Id)).ToList()) await RemindAsync(a);
    }

    private async Task RemindAllAsync()
    {
        foreach (var a in OpenAsks.Where(a => !reminded.Contains(a.Id)).ToList()) await RemindAsync(a);
    }

    private async Task NeedMoreTimeAsync()
    {
        if (topic is null || MyAsk is not { } a) return;
        var r = await Api.PostAsync(topic.Id, new CreateHubPostRequest("I need a bit more time. I'll answer as soon as I can.", a.PostId, null, null));
        if (!r.Ok) { Toasts.Show(r.Message ?? "Not sent.", ToastKind.Error); return; }
        topic = r.Value;
        Toasts.Show("Everyone knows you need more time.", ToastKind.Success);
        await LoadListAsync();
        await Badges.RefreshAsync();
    }

    private List<HubPersonDto> addResults = [];
    private string addSearch = "";
    private async Task SearchAddAsync(ChangeEventArgs e)
    {
        addSearch = e.Value?.ToString() ?? "";
        if (addSearch.Trim().Length < 2) { addResults = []; return; }
        var r = await Api.SearchPeopleAsync(addSearch.Trim());
        addResults = r.Ok ? r.Value!.Where(p => topic?.People.All(x => x.Id != p.Id) != false).ToList() : [];
    }

    private async Task AddPersonAsync(HubPersonDto p)
    {
        if (topic is null) return;
        addPerson = p.Id;
        var r = await Api.AddMembersAsync(topic.Id, [p.Id]);
        addPerson = null;
        if (!r.Ok) { Toasts.Show(r.Message ?? "Couldn't add them.", ToastKind.Error); return; }
        topic = r.Value;
        addResults = []; addSearch = "";
        Toasts.Show($"{p.Name} was added and told.", ToastKind.Success);
    }

    // ── New topic sheet ──────────────────────────────────────────────────

    private async Task OpenNewAsync()
    {
        sheet = new Sheet();
        newOpen = true;
        var r = await Api.SuggestedPeopleAsync();
        if (r.Ok) sheet.Suggested = r.Value!;
    }

    private void CloseNew() => newOpen = false;

    private async Task SheetSearchAsync(ChangeEventArgs e)
    {
        sheet.Search = e.Value?.ToString() ?? "";
        if (sheet.Search.Trim().Length < 2) { sheet.Results = []; return; }
        var r = await Api.SearchPeopleAsync(sheet.Search.Trim());
        sheet.Results = r.Ok ? r.Value!.Where(p => sheet.People.All(x => x.Id != p.Id)).ToList() : [];
    }

    private void Pick(HubPersonDto p)
    {
        if (sheet.People.All(x => x.Id != p.Id)) sheet.People.Add(p);
        sheet.Results = sheet.Results.Where(x => x.Id != p.Id).ToList();
        sheet.Search = "";
    }

    private void Unpick(HubPersonDto p) => sheet.People.RemoveAll(x => x.Id == p.Id);

    private string? MyDept => Auth.Me?.Department;

    private bool OtherDeptPicked => sheet.People.Any(p => p.Department is null || MyDept is null || p.Department != MyDept);
    private bool TextOk => sheet.Title.Trim().Length >= 3 && sheet.Need.Trim().Length >= 3;
    private bool SheetOk => OtherDeptPicked && TextOk;

    private string SheetWhy => sheet.People.Count == 0 ? "Add one person to send."
        : !OtherDeptPicked ? "Add someone from another department to send."
        : !TextOk ? "Write a title and what you need." : "";

    internal static DateTime? DueFrom(string code) => code switch
    {
        "2d" => AddWorkingDays(DateTime.Today, 2),
        "1w" => DateTime.Today.AddDays(7),
        "2w" => DateTime.Today.AddDays(14),
        _ => null,
    };

    internal static DateTime AddWorkingDays(DateTime from, int n)
    {
        var d = from;
        while (n > 0) { d = d.AddDays(1); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n--; }
        return d;
    }

    private async Task SendNewAsync()
    {
        if (!SheetOk || sheet.Sending) return;
        sheet.Sending = true; sheet.Error = null;
        var due = DueFrom(sheet.Due);
        var r = await Api.CreateAsync(new CreateHubTopicRequest(sheet.Kind, sheet.Title, sheet.Need,
            due is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null, sheet.People.Select(p => p.Id).ToList()));
        sheet.Sending = false;
        if (!r.Ok) { sheet.Error = r.Message; return; }
        newOpen = false;
        var n = sheet.People.Count;
        Toasts.Show($"Sent to {n} {(n == 1 ? "person" : "people")}. They got a message on PC and phone.", ToastKind.Success);
        filter = "mine";
        await LoadListAsync();
        freshRows.Add(r.Value!.Id);
        Nav.NavigateTo($"/hub/{r.Value.Id}");
    }

    // ── Mark as solved sheet ─────────────────────────────────────────────

    private async Task OpenSolveAsync()
    {
        solve = new Solve { Summary = Best is { } b ? Trim(b.Body, 600) : "" };
        solveOpen = true;
        menuOpen = false;
        var r = await Api.ProjectsAsync();
        if (r.Ok) solve.Projects = r.Value!;
    }

    private void CloseSolve() => solveOpen = false;

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    private void SetFollowUp(bool on)
    {
        solve.FollowUp = on;
        if (on && solve.Tasks.Count == 0) AddSolveTask();
    }

    private void AddSolveTask() => solve.Tasks.Add(new SolveTask
    {
        Assignee = topic?.People.FirstOrDefault(p => p.Id != Me)?.Id ?? Me,
        Project = solve.Projects.FirstOrDefault()?.Id ?? Guid.Empty,
    });

    private int SolveTaskCount => solve.FollowUp ? solve.Tasks.Count(t => !string.IsNullOrWhiteSpace(t.Title)) : 0;

    private async Task SendSolveAsync()
    {
        if (topic is null || solve.Sending) return;
        if (solve.Summary.Trim().Length < 3) { solve.Error = "Write how it was solved, in one or two sentences."; return; }
        if (SolveTaskCount > 0 && solve.Tasks.Any(t => !string.IsNullOrWhiteSpace(t.Title) && t.Project == Guid.Empty))
        { solve.Error = "Pick a project for each task."; return; }
        solve.Sending = true; solve.Error = null;
        var tasks = solve.FollowUp
            ? solve.Tasks.Where(t => !string.IsNullOrWhiteSpace(t.Title)).Select(t => new HubFollowUpTask(t.Title, t.Assignee, t.Project,
                DueFrom(t.Due) is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null)).ToList()
            : [];
        var r = await Api.SolveAsync(topic.Id, new SolveHubTopicRequest(solve.Summary, tasks));
        solve.Sending = false;
        if (!r.Ok) { solve.Error = r.Message; return; }
        solveOpen = false;
        topic = r.Value;
        var id = topic!.Id;
        Toasts.ShowUndo(tasks.Count > 0 ? $"Solved. {tasks.Count} task{(tasks.Count > 1 ? "s" : "")} added to Projects." : "Marked as solved. Everyone was told.",
            async () =>
            {
                var back = await Api.ReopenAsync(id);
                if (back.Ok && Id == id) { topic = back.Value; await LoadListAsync(); StateHasChanged(); }
            }, "Undo", 6000);
        await LoadListAsync();
        await Badges.RefreshAsync();
    }

    private async Task ReopenAsync()
    {
        if (topic is null) return;
        var r = await Api.ReopenAsync(topic.Id);
        if (!r.Ok) { Toasts.Show(r.Message ?? "Couldn't reopen.", ToastKind.Error); return; }
        topic = r.Value;
        Toasts.Show("Reopened. The topic is ongoing again.", ToastKind.Success);
        await LoadListAsync();
    }
}
