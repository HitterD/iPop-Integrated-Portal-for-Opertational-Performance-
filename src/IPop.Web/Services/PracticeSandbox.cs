using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IPop.Web.Services;

/// Practice mode: while active, the module's API calls are answered in the browser with
/// sample data built for the signed-in user. GETs return canned JSON, writes succeed without
/// reaching the server (a few update the in-memory sample so the page reacts), and nothing
/// is ever sent. Everything is thrown away on exit or reset.
public sealed class PracticeSandbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AuthState _auth;
    private readonly ToastService _toasts;
    private Data? _d;

    public PracticeSandbox(AuthState auth, ToastService toasts, PracticeGate gate)
    {
        _auth = auth;
        _toasts = toasts;
        gate.Intercept = Handle;
    }

    public string? Module { get; private set; }
    public bool Active => Module is not null;

    private Func<string> _role = () => "emp";

    /// Called before the profile has loaded, so the sample (which names you) is built on first use.
    public void Start(string module, Func<string> role)
    {
        Module = module;
        _role = role;
        _d = null;
    }

    // Built on first use, after the profile is in: the sample needs your NIK to tell your
    // messages and tasks from theirs.
    private Data? Sample => Module is null ? null : _d ??= new Data(_auth.Me?.Nik ?? "me", _auth.Me?.Name ?? "You", _role());

    public void Stop() { Module = null; _d = null; }

    /// URL prefixes each practice module owns. Everything else (badges, presence, tutorials)
    /// still goes to the real API.
    private static readonly Dictionary<string, string[]> Owned = new()
    {
        ["chat"] = ["/api/chat/"],
        ["projects"] = ["/api/projects"],
        ["mail"] = ["/api/mail"],
        ["news"] = ["/api/news"],
        ["calendar"] = ["/api/calendar/", "/api/meetings"],
        ["calls"] = ["/api/calls/history", "/api/meetings", "/api/directory/favorites", "/api/directory/employees"],
        ["files"] = ["/api/files"],
    };

    private async Task<HttpResponseMessage?> Handle(HttpRequestMessage req)
    {
        if (Module is null || req.RequestUri is null) return null;
        var method = req.Method;
        var path = req.RequestUri.IsAbsoluteUri ? req.RequestUri.PathAndQuery : req.RequestUri.OriginalString;
        if (!Owned.TryGetValue(Module, out var prefixes) || !prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return null;
        if (_d is null && _auth.Me is null) await _auth.LoadMeAsync();

        if (method == HttpMethod.Get)
        {
            var result = Sample!.Get(Module, path);
            return result is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(result);
        }

        if (req.Content is MultipartFormDataContent || req.Content?.Headers.ContentType?.MediaType?.StartsWith("multipart/") == true)
        {
            InterceptUpload();
            return Ok(new { success = true });
        }
        JsonElement? el = null;
        if (req.Content is not null)
        {
            try { el = JsonDocument.Parse(await req.Content.ReadAsStringAsync()).RootElement; } catch { }
        }
        var (reply, title, text) = Sample!.Write(Module, method, path, el);
        _toasts.ShowDetailed(title ?? "Practice only — nothing was saved", text ?? "In real mode this would be saved and people would be notified.", ToastKind.Info, "flask-conical");
        return Ok(reply ?? new { success = true });
    }

    /// Placing a call is always simulated.
    public bool InterceptCall(string peerNik)
    {
        if (!Active) return false;
        var name = Sample?.PersonName(peerNik) ?? "them";
        _toasts.ShowDetailed($"In real mode this would ring {name}", "Practice mode never places calls.", ToastKind.Info, "phone");
        return true;
    }

    public bool InterceptUpload()
    {
        if (!Active) return false;
        _toasts.ShowDetailed("In real mode the file would upload", "Files go to the company NAS — skipped in practice.", ToastKind.Info, "upload");
        return true;
    }

    /// A chat message "sent" in practice: kept in the sample room, then the other side answers.
    public (IPop.Application.Chat.MessageDto Mine, Func<IPop.Application.Chat.MessageDto> Reply)? SendChat(Guid roomId, string text) =>
        Sample?.SendChat(roomId, text);

    private static HttpResponseMessage Ok(object value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value, value.GetType(), options: Json) };

    // ───────────────────────── Sample data ─────────────────────────

    public sealed record Person(string Nik, string Name, Guid Id, string Position, string Department);

    public sealed class Data
    {
        public readonly Person Me, Rina, Dimas, Maya;
        public readonly string Role;

        public static readonly Guid RoomRina = G("a", 1), RoomDimas = G("a", 2), RoomMaya = G("a", 3), RoomTeam = G("a", 4), RoomProj = G("a", 5);
        public static readonly Guid ProjectId = G("d", 1), GroupId = G("e", 1);

        private static Guid G(string p, int n) => Guid.Parse($"{p}0000000-0000-0000-0000-{n:D12}");
        private static DateTime Min(double m) => DateTime.UtcNow.AddMinutes(m);
        private static DateTime Day(int d) => DateTime.UtcNow.Date.AddDays(d);

        public Data(string myNik, string myName, string role)
        {
            Role = role;
            Me = new(myNik, myName, G("1", 99), "", "");
            Rina = new("S-0001", "Rina Putri", G("1", 1), "Procurement Officer", "Procurement");
            Dimas = new("S-0002", "Dimas Saputra", G("1", 2), "Network Engineer", "IT");
            Maya = new("S-0003", "Maya Wulandari", G("1", 3), "HR Generalist", "Human Resources");
            Tasks =
            [
                Task("Survey signal on floor 3", "todo", Day(0), "high", [Rina], comments: 2, tags: "site-visit"),
                Task("Order 12 access points", "todo", Day(2), null, [Me], subtasks: (3, 1)),
                Task("Compare vendor quotes", "doing", Day(-2), "high", [Me], attachments: 1),
                Task("Draft rollout schedule", "doing", Day(1), null, [Maya]),
                Task("Update network diagram", "review", Day(8), null, [Dimas], comments: 1),
                Task("Get budget approval", "done", Day(-3), null, [Rina, Me]),
            ];
            BuildMail();
            BuildNews();
            BuildCalendar();
        }

        public string? PersonName(string nik) => new[] { Rina, Dimas, Maya }.FirstOrDefault(p => p.Nik == nik)?.Name;

        public object? Get(string module, string url) => module switch
        {
            "chat" => GetChat(url),
            "projects" => GetProjects(url),
            "mail" => GetMail(url),
            "news" => GetNews(url),
            "calendar" => GetCalendar(url),
            "calls" => GetCalls(url),
            "files" => GetFiles(url),
            _ => null,
        };

        private static bool M(string url, string pattern) => Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase);
        private static Guid? IdIn(string url, string pattern)
        {
            var m = Regex.Match(url, pattern, RegexOptions.IgnoreCase);
            return m.Success && Guid.TryParse(m.Groups[1].Value, out var g) ? g : null;
        }
        private static object Paged<T>(IReadOnlyList<T> items) => new { items, totalCount = items.Count, page = 1, pageSize = Math.Max(items.Count, 20) };

        // ── Chat ──
        private object Room(Guid id, string type, string? name, Person? peer, string last, double lastMin, int unread = 0,
            string? presence = null, bool pinned = false, bool mention = false) => new
        {
            id, type, name, recipientNik = peer?.Nik, recipientName = peer?.Name, lastMessage = last, lastMessageAt = Min(lastMin),
            unreadCount = unread, isMuted = false, isArchived = false, isPinned = pinned, presenceStatus = presence,
            hasDraft = false, hasUnreadMention = mention, muteExpiresAt = (DateTime?)null, readReceiptsEnabled = true,
        };

        private List<object> Direct() =>
        [
            Room(RoomRina, "dm", null, Rina, $"Can you check it before the 10:00 meeting?", -18, 2, "on", pinned: true, mention: true),
            Room(RoomDimas, "dm", null, Dimas, "Thanks, noted 👍", -60 * 20, 0, "away"),
            Room(RoomMaya, "dm", null, Maya, "📎 Q3-budget.xlsx", -60 * 50, 0, "off"),
        ];

        private List<object> Groups() =>
        [
            Room(RoomTeam, "group", "IT Infra Team (sample)", null, "Dimas: Server patch tonight at 22:00", -95, 5),
            Room(RoomProj, "group", "Wi-Fi Upgrade Project (sample)", null, "Maya: Rollout draft is on the board", -60 * 26),
        ];

        public IPop.Application.Chat.MessageDto Msg(Guid room, Person who, string text, double min,
            IReadOnlyList<IPop.Application.Chat.AttachmentDto>? att = null, IReadOnlyList<IPop.Application.Chat.ReactionGroupDto>? reactions = null,
            IReadOnlyList<string>? mentions = null) =>
            new(Guid.NewGuid(), room, who.Id, who.Name, who.Nik, "text", text, Min(min), null, false, null,
                null, null, null, 0, null, att ?? [], reactions ?? [], mentions ?? [], false, Guid.NewGuid(),
                who == Me ? "read" : null);

        private readonly Dictionary<Guid, List<IPop.Application.Chat.MessageDto>> _rooms = [];
        private List<IPop.Application.Chat.MessageDto> Messages(Guid room) =>
            _rooms.TryGetValue(room, out var list) ? list : _rooms[room] = SeedMessages(room);

        public (IPop.Application.Chat.MessageDto, Func<IPop.Application.Chat.MessageDto>) SendChat(Guid room, string text)
        {
            var mine = Msg(room, Me, text, 0);
            Messages(room).Add(mine);
            return (mine, () =>
            {
                var who = room == RoomDimas ? Dimas : room == RoomMaya ? Maya : Rina;
                var reply = Msg(room, who, $"Got it, thanks! (sample reply from {who.Name.Split(' ')[0]})", 0);
                Messages(room).Add(reply);
                return reply;
            });
        }

        private List<IPop.Application.Chat.MessageDto> SeedMessages(Guid room)
        {
            if (room == RoomRina) return
            [
                Msg(room, Rina, "Morning! Did you get the vendor quote I sent yesterday?", -60 * 24 - 30),
                Msg(room, Me, "Not yet, could you send it again?", -60 * 24 - 25),
                Msg(room, Rina, "Here it is 👇", -24, att: [new(G("c", 1), "Vendor-quote-Oct.xlsx", 48200, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null)]),
                Msg(room, Rina, "It covers the 12 access points for floor 3.", -22, reactions: [new("👍", 1, true)]),
                Msg(room, Rina, $"Can you check it before the 10:00 meeting? @{Me.Name}", -18, mentions: [Me.Nik]),
            ];
            if (room == RoomDimas) return [Msg(room, Me, "The patch window is approved for tonight.", -60 * 21), Msg(room, Dimas, "Thanks, noted 👍", -60 * 20)];
            if (room == RoomMaya) return [Msg(room, Maya, "Here's the budget sheet you asked for.", -60 * 50, att: [new(G("c", 2), "Q3-budget.xlsx", 73100, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null)])];
            if (room == RoomTeam) return [Msg(room, Dimas, "Server patch tonight at 22:00", -95), Msg(room, Rina, "Noted, I'll warn the floor 3 team.", -90)];
            return [Msg(room, Maya, "Rollout draft is on the board", -60 * 26)];
        }

        private object? GetChat(string url)
        {
            if (M(url, @"/api/chat/rooms\?.*tab=direct")) return Paged(Direct());
            if (M(url, @"/api/chat/rooms\?.*tab=groups")) return Paged(Groups());
            if (M(url, @"/api/chat/rooms/archived")) return Paged(Array.Empty<object>());
            if (IdIn(url, @"/api/chat/rooms/([0-9a-f-]{36})/messages") is { } mr)
                return url.Contains("before=") ? Paged(Array.Empty<object>()) : Paged(Messages(mr));
            if (M(url, @"/api/chat/rooms/[^/]+/(pins|read-counts|files|media)")) return Array.Empty<object>();
            if (M(url, @"/api/chat/rooms/[^/]+/search")) return Paged(Array.Empty<object>());
            if (IdIn(url, @"/api/chat/rooms/([0-9a-f-]{36})($|\?)") is { } rid)
            {
                var people = rid == RoomRina ? new[] { Me, Rina } : rid == RoomDimas ? new[] { Me, Dimas } : rid == RoomMaya ? new[] { Me, Maya } : new[] { Me, Rina, Dimas, Maya };
                var isGroup = people.Length > 2;
                return new
                {
                    id = rid, type = isGroup ? "group" : "dm", name = isGroup ? (rid == RoomTeam ? "IT Infra Team (sample)" : "Wi-Fi Upgrade Project (sample)") : null,
                    description = (string?)null, memberCount = people.Length, onlineCount = people.Length - 1,
                    members = people.Select(p => new { userId = p.Id, nik = p.Nik, name = p.Name, role = p == Me && isGroup ? "admin" : "member", presenceStatus = "on" }),
                    currentUserRole = isGroup ? "admin" : "member",
                    mentionDirectory = people.Select(p => new { userId = p.Id, nik = p.Nik, name = p.Name, isActive = true }),
                    readReceiptsEnabled = true,
                };
            }
            if (M(url, @"/api/chat/users/(directory|search)")) return new[] { Rina, Dimas, Maya }.Select(p => new { userId = p.Id, nik = p.Nik, name = p.Name, position = p.Position, department = p.Department });
            if (url.Contains("/api/chat/users/S-", StringComparison.OrdinalIgnoreCase))
            {
                var p = new[] { Rina, Dimas, Maya }.FirstOrDefault(x => url.Contains(x.Nik)) ?? Rina;
                return Employee(p);
            }
            return null;
        }

        private static object Employee(Person p) => new
        {
            nik = p.Nik, name = p.Name, position = p.Position, department = p.Department, email = $"{p.Name.Split(' ')[0].ToLowerInvariant()}.sample@example.com",
            extension = "214", location = "Head office", status = "on",
        };

        // ── Projects ──
        private readonly List<Dictionary<string, object?>> Tasks;
        private int _t;

        private Dictionary<string, object?> Task(string title, string status, DateTime? due, string? priority, Person[] who,
            int comments = 0, int attachments = 0, string? tags = null, (int All, int Done) subtasks = default) => new()
        {
            ["id"] = G("f", ++_t), ["projectId"] = ProjectId, ["parentTaskId"] = null, ["title"] = title, ["description"] = null,
            ["status"] = status, ["priority"] = priority, ["startDate"] = null, ["dueDate"] = due, ["estimatedHours"] = null, ["actualHours"] = null,
            ["sortOrder"] = _t * 100, ["dependencies"] = null, ["isRecurring"] = false, ["recurrenceRule"] = null, ["isTemplate"] = false, ["tags"] = tags,
            ["createdById"] = Me.Id, ["createdByName"] = Me.Name,
            ["assignees"] = who.Select(p => new { userId = p.Id, nik = p.Nik, name = p.Name, role = "assignee", assignedAt = Day(-5) }).ToList(),
            ["subtaskCount"] = subtasks.All, ["subtaskDoneCount"] = subtasks.Done, ["createdAt"] = Day(-7), ["updatedAt"] = Day(-1),
            ["commentCount"] = comments, ["attachmentCount"] = attachments, ["hasReminder"] = false, ["groupId"] = GroupId,
            ["dependsOn"] = Array.Empty<object>(), ["isBlocked"] = false,
        };

        private List<Dictionary<string, object?>> By(string s) => Tasks.Where(t => (string?)t["status"] == s).ToList();
        private int Progress => Tasks.Count == 0 ? 0 : By("done").Count * 100 / Tasks.Count;

        private object ProjectDetail() => new
        {
            id = ProjectId, name = "Office Wi-Fi Upgrade (sample)", description = "Replace access points on floors 2–4", year = DateTime.UtcNow.Year,
            ownerId = Me.Id, ownerName = Me.Name, ownerNik = Me.Nik, startDate = Day(-14), deadline = Day(21), status = "active", progress = Progress,
            risk = (string?)null, obstacle = (string?)null, isActive = true, memberCount = 4, taskCount = Tasks.Count,
            todoCount = By("todo").Count, doingCount = By("doing").Count, reviewCount = By("review").Count, doneCount = By("done").Count,
            createdAt = Day(-14), updatedAt = Min(-30), color = "#0d9488",
        };

        private object? GetProjects(string url)
        {
            if (M(url, @"^/api/projects\?")) return Paged(new object[]
            {
                new { id = ProjectId, name = "Office Wi-Fi Upgrade (sample)", year = DateTime.UtcNow.Year, ownerName = Me.Name, status = "active", progress = Progress, deadline = Day(21), memberCount = 4, taskCount = Tasks.Count, isActive = true, color = "#0d9488" },
                new { id = G("d", 2), name = "Onboarding Handbook (sample)", year = DateTime.UtcNow.Year, ownerName = Maya.Name, status = "planning", progress = 10, deadline = Day(40), memberCount = 3, taskCount = 0, isActive = true, color = "#2563eb" },
            });
            if (M(url, @"/members")) return new[] { Me, Rina, Dimas, Maya }.Select((p, i) => new { userId = p.Id, nik = p.Nik, name = p.Name, role = i == 0 ? "owner" : "member", joinedAt = Day(-14) });
            if (M(url, @"/groups")) return new[] { new { id = GroupId, projectId = ProjectId, name = "Tasks", color = "#8E8E93", sortOrder = 0, taskCount = Tasks.Count, progress = Progress, overdueCount = 1 } };
            if (M(url, @"/kanban")) return new { projectId = ProjectId, todo = By("todo"), doing = By("doing"), done = By("done"), review = By("review") };
            if (M(url, @"/tasks/list")) return Paged(Tasks);
            if (M(url, @"/(fields|views|milestones|activity|comments|attachments|reminders|templates)")) return Array.Empty<object>();
            if (IdIn(url, @"/api/projects/tasks/([0-9a-f-]{36})($|\?)") is { } tid) return Tasks.FirstOrDefault(t => (Guid)t["id"]! == tid);
            if (M(url, @"/api/projects/[0-9a-f-]{36}($|\?)")) return ProjectDetail();
            return null;
        }

        // ── Mail ──
        private readonly List<Dictionary<string, object?>> _mail = [];

        private void AddMail(Person from, string subject, string body, double min, bool read, string folder = "inbox", bool star = false, bool attach = false, string importance = "normal")
        {
            _mail.Add(new()
            {
                ["id"] = G("7", _mail.Count + 1), ["senderId"] = from.Id, ["senderName"] = from.Name, ["senderNik"] = from.Nik, ["subject"] = subject,
                ["snippet"] = body.Length > 90 ? body[..90] : body, ["receivedAt"] = Min(min), ["isRead"] = read, ["isStarred"] = star, ["hasAttachment"] = attach,
                ["threadCount"] = 1, ["isThreadRoot"] = true, ["threadRootId"] = null, ["isPinned"] = false, ["importance"] = importance, ["labels"] = Array.Empty<string>(),
                ["_body"] = body, ["_folder"] = folder,
            });
        }

        private void BuildMail()
        {
            AddMail(Rina, "Vendor quote for floor 3 access points", "Hi, attached is the quote from the vendor for the 12 access points. Could you review it before Thursday? Thanks, Rina", -40, false, attach: true, importance: "high");
            AddMail(Dimas, "Server patch tonight 22:00", "Heads-up: the file server will be down for 20 minutes tonight at 22:00 for patching.", -60 * 5, false);
            AddMail(Maya, "Welcome to the Wi-Fi project", "Glad to have you on board. The rollout plan is on the Projects board.", -60 * 30, true, star: true);
            if (Role == "hr") AddMail(Rina, "Leave request — 3 days next week", "Hi HR, I'd like to request leave from Monday to Wednesday next week. My tasks are covered by Dimas.", -25, false);
            if (Role == "admin") AddMail(Dimas, "Can't sign in — account locked", "I typed my password wrong a few times and now my account is locked. Could you unlock it?", -12, false, importance: "high");
            AddMail(Me, "Re: Q3 budget", "Thanks Maya, looks good to me.", -60 * 48, true, folder: "sent");
        }

        private IEnumerable<Dictionary<string, object?>> Folder(string f) => f switch
        {
            "starred" => _mail.Where(m => (bool)m["isStarred"]!),
            _ => _mail.Where(m => (string)m["_folder"]! == f),
        };

        private static Dictionary<string, object?> Public(Dictionary<string, object?> m) => m.Where(kv => !kv.Key.StartsWith('_')).ToDictionary();

        private object? GetMail(string url)
        {
            if (M(url, @"/api/mail/counts")) return new
            {
                inboxUnread = Folder("inbox").Count(m => !(bool)m["isRead"]!), inboxTotal = Folder("inbox").Count(), sentTotal = Folder("sent").Count(),
                draftTotal = 0, starredTotal = Folder("starred").Count(), archiveTotal = Folder("archive").Count(), trashTotal = Folder("trash").Count(), scheduledTotal = 0,
            };
            if (M(url, @"/api/mail/labels")) return new[] { new { id = G("8", 1), name = "Wi-Fi project", color = "#0d9488", count = 2 } };
            var f = Regex.Match(url, @"/api/mail/folders/(\w+)");
            if (f.Success) return Paged(Folder(f.Groups[1].Value.ToLowerInvariant()).OrderByDescending(m => (DateTime)m["receivedAt"]!).Select(Public).ToList());
            if (IdIn(url, @"/api/mail/threads/([0-9a-f-]{36})") is { } th) return Paged(_mail.Where(m => (Guid)m["id"]! == th).Select(Public).ToList());
            if (IdIn(url, @"/api/mail/([0-9a-f-]{36})($|\?)") is { } id && _mail.FirstOrDefault(m => (Guid)m["id"]! == id) is { } mail)
            {
                mail["isRead"] = true;
                var from = new[] { Me, Rina, Dimas, Maya }.First(p => p.Nik == (string)mail["senderNik"]!);
                var to = from == Me ? Maya : Me;
                return new
                {
                    id, senderId = from.Id, senderName = from.Name, senderNik = from.Nik, subject = mail["subject"], body = $"<p>{WebUtility.HtmlEncode((string)mail["_body"]!)}</p>",
                    isHtml = true, signature = (string?)null, sentAt = mail["receivedAt"], inReplyToId = (Guid?)null, threadRootId = (Guid?)null, threadCount = 1,
                    isStarred = mail["isStarred"], isRead = true, folder = mail["_folder"],
                    recipients = new[] { new { userId = to.Id, name = to.Name, nik = to.Nik, email = "sample@example.com", recipientType = "to" } },
                    attachments = (bool)mail["hasAttachment"]! ? new[] { new { id = G("c", 9), fileName = "Vendor-quote-Oct.xlsx", fileSize = 48200L, mimeType = "application/vnd.ms-excel", thumbnailUrl = (string?)null } } : [],
                    deliveryState = Array.Empty<object>(), importance = mail["importance"],
                };
            }
            if (M(url, @"/api/mail/(rules|templates|signature|auto-reply|contacts|drafts)")) return null;
            return null;
        }

        // ── News ──
        private readonly List<Dictionary<string, object?>> _news = [];

        private void AddNews(string type, string title, string summary, string author, double min, bool pinned = false, bool ack = false, bool read = false, string status = "published")
        {
            _news.Add(new()
            {
                ["id"] = G("9", _news.Count + 1), ["type"] = type, ["title"] = title, ["status"] = status, ["authorName"] = author, ["authorNik"] = "S-0003",
                ["publishAt"] = null, ["publishedAt"] = status == "published" ? Min(min) : null, ["isPinned"] = pinned, ["pinnedAt"] = pinned ? Min(min) : null,
                ["requiresAcknowledgement"] = ack, ["targets"] = new[] { new { kind = "all", value = (string?)null } }, ["isAcknowledged"] = false,
                ["createdAt"] = Min(min), ["updatedAt"] = Min(min), ["summary"] = summary, ["isRead"] = read, ["isSaved"] = false, ["viewCount"] = 42,
                ["attachmentCount"] = 0, ["ackDueAt"] = ack ? Day(5) : null,
            });
        }

        private void BuildNews()
        {
            AddNews("memo", "New visitor policy from next Monday", "All visitors must now register at the lobby and wear a badge. Please read and acknowledge.", Maya.Name, -60 * 3, ack: true);
            AddNews("news", "Office Wi-Fi upgrade on floors 2–4", "New access points are being installed this month. Expect short outages on floor 3.", Dimas.Name, -60 * 26, pinned: true, read: true);
            AddNews("news", "Company outing — save the date", "Our annual outing is in three weeks. Details and sign-up coming soon.", Maya.Name, -60 * 72, read: true);
            if (Role is "hr" or "admin") AddNews("memo", "Updated leave procedure (draft)", "Draft of the new leave request flow. Not visible to employees until published.", Me.Name, -30, status: "draft");
        }

        private bool CanManage => Role is "hr" or "admin";

        private object? GetNews(string url)
        {
            var pub = _news.Where(n => (string)n["status"]! == "published").ToList();
            if (M(url, @"/api/news/counts")) return new
            {
                all = pub.Count, unread = pub.Count(n => !(bool)n["isRead"]!), needsAck = pub.Count(n => (bool)n["requiresAcknowledgement"]! && !(bool)n["isAcknowledged"]!),
                saved = pub.Count(n => (bool)n["isSaved"]!), pinned = pub.Count(n => (bool)n["isPinned"]!), scheduled = 0,
                drafts = _news.Count(n => (string)n["status"]! == "draft"), canManage = CanManage,
            };
            if (M(url, @"^/api/news($|\?)"))
            {
                IEnumerable<Dictionary<string, object?>> list = url.Contains("status=draft", StringComparison.OrdinalIgnoreCase) || url.Contains("view=drafts", StringComparison.OrdinalIgnoreCase)
                    ? _news.Where(n => (string)n["status"]! == "draft") : pub;
                if (url.Contains("needsAck=true", StringComparison.OrdinalIgnoreCase) || url.Contains("view=ack", StringComparison.OrdinalIgnoreCase))
                    list = list.Where(n => (bool)n["requiresAcknowledgement"]! && !(bool)n["isAcknowledged"]!);
                return Paged(list.OrderByDescending(n => (bool)n["isPinned"]!).ThenByDescending(n => (DateTime)n["createdAt"]!).ToList());
            }
            if (M(url, @"/audits")) return Array.Empty<object>();
            if (M(url, @"/discussion")) return new { likeCount = 3, isLiked = false, comments = new[] { new { id = G("9", 90), authorName = Rina.Name, body = "Thanks for the heads-up!", createdAt = Min(-50) } } };
            if (M(url, @"/acks")) return new
            {
                total = 3, acknowledged = 1,
                entries = new[] { Rina, Dimas, Maya }.Select((p, i) => new { userId = p.Id, nik = p.Nik, name = p.Name, department = p.Department, position = p.Position, acknowledgedAt = i == 0 ? Min(-20) : (DateTime?)null }),
            };
            if (IdIn(url, @"/api/news/([0-9a-f-]{36})($|\?)") is { } id && _news.FirstOrDefault(n => (Guid)n["id"]! == id) is { } n)
            {
                n["isRead"] = true;
                var d = new Dictionary<string, object?>(n)
                {
                    ["body"] = $"<p>{WebUtility.HtmlEncode((string)n["summary"]!)}</p><p>This is a sample post for practice mode.</p>",
                    ["authorId"] = Maya.Id, ["approverId"] = null, ["approverName"] = null, ["rejectionReason"] = null, ["audits"] = Array.Empty<object>(),
                    ["ackCount"] = 1, ["ackTotal"] = 3, ["acknowledgedAt"] = null, ["attachments"] = Array.Empty<object>(),
                    ["canManage"] = CanManage, ["canEdit"] = CanManage, ["canDelete"] = CanManage, ["canPublish"] = CanManage,
                };
                return d;
            }
            return null;
        }

        // ── Calendar & meetings ──
        private readonly List<Dictionary<string, object?>> _events = [];

        private void AddEvent(string source, string kind, string title, DateTime start, double hours, string? subtitle = null, string? url = null, string? location = null)
        {
            var id = G("5", _events.Count + 1);
            _events.Add(new()
            {
                ["id"] = id.ToString(), ["source"] = source, ["kind"] = kind, ["title"] = title, ["startAt"] = start, ["endAt"] = start.AddHours(hours),
                ["allDay"] = hours >= 24, ["url"] = url, ["subtitle"] = subtitle, ["eventId"] = source == "personal" ? id : null,
                ["meetingId"] = source == "meetings" ? id : null, ["rsvp"] = source == "meetings" ? "pending" : null, ["canEdit"] = source == "personal",
                ["location"] = location, ["link"] = null,
            });
        }

        private void BuildCalendar()
        {
            var today = DateTime.Today; // local midnight
            AddEvent("meetings", "meeting", "Wi-Fi rollout check-in (sample)", today.AddHours(10).ToUniversalTime(), 1, "Rina, Dimas, Maya", location: "Room 3A");
            AddEvent("personal", "event", "Focus time", today.AddHours(14).ToUniversalTime(), 2);
            AddEvent("projects", "deadline", "Order 12 access points", today.AddDays(2).ToUniversalTime(), 24, "Office Wi-Fi Upgrade");
            AddEvent("meetings", "meeting", "Vendor demo (sample)", today.AddDays(1).AddHours(13).ToUniversalTime(), 1.5, "Rina Putri");
            AddEvent("news", "ack", "Acknowledge: visitor policy", today.AddDays(5).ToUniversalTime(), 24);
            if (Role is "hr" or "admin") AddEvent("meetings", "meeting", "Monthly HR sync (sample)", today.AddDays(3).AddHours(9).ToUniversalTime(), 1, "Maya Wulandari");
        }

        private object Meeting(Dictionary<string, object?> e) => new
        {
            id = e["meetingId"] ?? Guid.Parse((string)e["id"]!), title = e["title"], startAt = e["startAt"], endAt = e["endAt"], timeZone = "Asia/Jakarta",
            location = e["location"], link = (string?)null, organizerId = Rina.Id, organizerName = Rina.Name, organizerNik = Rina.Nik, status = "scheduled",
            attendees = new[] { Me, Rina, Dimas }.Select(p => new { userId = p.Id, name = p.Name, nik = p.Nik, required = true, rsvpStatus = p == Rina ? "accepted" : "pending", note = (string?)null }),
            acceptCount = 1, maybeCount = 0, declineCount = 0, pendingCount = 2, myResponse = "pending", agenda = "Sample agenda: status, risks, next steps.", roomName = (string?)null,
        };

        private object? GetCalendar(string url)
        {
            if (M(url, @"/api/calendar/feed")) return _events;
            if (IdIn(url, @"/api/calendar/events/([0-9a-f-]{36})") is { } eid && _events.FirstOrDefault(e => (Guid?)e["eventId"] == eid) is { } ev) return new
            {
                id = eid, title = ev["title"], startAt = ev["startAt"], endAt = ev["endAt"], allDay = ev["allDay"], location = ev["location"], link = (string?)null,
                notes = "A sample event — edit or delete it, nothing is saved.", reminderMinutes = 10, roomId = (Guid?)null, roomName = (string?)null,
                meetingId = (Guid?)null, meetingStatus = (string?)null, ownerName = Me.Name, canEdit = true, attendees = Array.Empty<object>(),
            };
            if (M(url, @"/api/meetings/upcoming")) return _events.Where(e => (string)e["source"]! == "meetings").Select(Meeting).ToList();
            if (IdIn(url, @"/api/meetings/([0-9a-f-]{36})") is { } mid && _events.FirstOrDefault(e => (Guid?)e["meetingId"] == mid) is { } m) return Meeting(m);
            return null;
        }

        // ── Calls ──
        private object? GetCalls(string url)
        {
            if (M(url, @"/api/calls/history"))
            {
                object H(int n, string dir, string result, int secs, double min, Person p) => new
                {
                    callId = G("6", n), direction = dir, result, durationSeconds = secs, startedAt = Min(min), endedAt = Min(min).AddSeconds(secs), peerName = p.Name, peerNik = p.Nik,
                };
                var items = new[] { H(1, "incoming", "missed", 0, -35, Rina), H(2, "outgoing", "answered", 312, -60 * 4, Dimas), H(3, "incoming", "answered", 95, -60 * 27, Maya), H(4, "outgoing", "declined", 0, -60 * 50, Rina) };
                return new { items, total = items.Length, page = 1, pageSize = 50 };
            }
            if (M(url, @"/api/directory/favorites")) return new[] { Rina.Nik, Dimas.Nik };
            if (M(url, @"/api/directory/employees"))
            {
                var people = new[] { Rina, Dimas, Maya }.Where(p => !url.Contains("q=S-") || url.Contains(p.Nik)).ToArray();
                return new { total = people.Length, page = 1, pageSize = 10, items = people.Select(Employee) };
            }
            if (M(url, @"/api/meetings/upcoming")) return _events.Where(e => (string)e["source"]! == "meetings").Select(Meeting).ToList();
            if (IdIn(url, @"/api/meetings/([0-9a-f-]{36})") is { } mid && _events.FirstOrDefault(e => (Guid?)e["meetingId"] == mid) is { } m) return Meeting(m);
            return null;
        }

        // ── Files ──
        private object? GetFiles(string url)
        {
            var server = Regex.Match(url, @"server=(\w+)").Groups[1].Value is { Length: > 0 } s ? s : "spj";
            long T(int daysAgo) => DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeSeconds();
            if (M(url, @"/api/files/credentials")) return new[] { "spj", "krw", "jtb", "smg" }.Select(x => new { hasCredential = x == "spj", isInvalid = false, username = x == "spj" ? Me.Nik : null, server = x });
            if (M(url, @"/api/files/credential\?")) return new { hasCredential = true, isInvalid = false, username = Me.Nik, server };
            if (M(url, @"/api/files/shares")) return new[] { "Shared (sample)", "Projects (sample)" };
            if (M(url, @"/api/files/storage")) return new { server, usedBytes = 3_400_000_000_000L, totalBytes = 8_000_000_000_000L, freeBytes = 4_600_000_000_000L };
            if (M(url, @"/api/files/prefs")) return new { stars = new[] { new { server = "spj", path = "/Projects (sample)/Wi-Fi Upgrade", name = "Wi-Fi Upgrade", isDir = true, at = 0L } }, recent = Array.Empty<object>() };
            if (M(url, @"/api/files/(recycle|sharing)")) return Array.Empty<object>();
            if (M(url, @"^/api/files\?"))
            {
                var path = Uri.UnescapeDataString(Regex.Match(url, @"path=([^&]*)").Groups[1].Value);
                object D(string name) => new { name, path = $"{path.TrimEnd('/')}/{name}", isDir = true, size = 0L, mtime = T(3) };
                object F(string name, long size, int age) => new { name, path = $"{path.TrimEnd('/')}/{name}", isDir = false, size, mtime = T(age) };
                if (path is "" or "/") return new[] { D("Shared (sample)"), D("Projects (sample)") };
                if (path.EndsWith("Wi-Fi Upgrade")) return new[] { F("Vendor-quote-Oct.xlsx", 48_200, 1), F("Floor-3-signal-map.pdf", 2_300_000, 2), F("Rollout-plan.docx", 88_000, 4), F("AP-install-photo.jpg", 1_200_000, 6) };
                if (path.Contains("Projects")) return new[] { D("Wi-Fi Upgrade"), D("Onboarding Handbook"), F("Project-list.xlsx", 22_000, 9) };
                return new[] { D("Templates"), F("Company-holidays.pdf", 310_000, 20), F("Org-chart.pptx", 1_900_000, 12), F("Phone-extensions.xlsx", 36_000, 5) };
            }
            return null;
        }

        // ── Writes that the page should see reflected ──
        public (object? Reply, string? Title, string? Text) Write(string module, HttpMethod method, string url, JsonElement? body)
        {
            string? Str(string prop) => body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            if (module == "projects")
            {
                if (IdIn(url, @"/api/projects/tasks/([0-9a-f-]{36})/move") is { } tid && Tasks.FirstOrDefault(t => (Guid)t["id"]! == tid) is { } t)
                {
                    t["status"] = Str("status") ?? t["status"];
                    return (new { success = true }, "Moved — only in practice", "In real mode the assignees would see the new status.");
                }
                if (method == HttpMethod.Post && M(url, @"/api/projects/[0-9a-f-]{36}/tasks$"))
                {
                    var nt = Task(Str("title") ?? "My practice task", Str("status") ?? "todo", null, Str("priority"), [Me]);
                    Tasks.Add(nt);
                    return (nt, "Task added — only in practice", "In real mode the assignee would get a notification.");
                }
                if (method == HttpMethod.Delete && IdIn(url, @"/api/projects/tasks/([0-9a-f-]{36})$") is { } del)
                {
                    Tasks.RemoveAll(x => (Guid)x["id"]! == del);
                    return (new { success = true }, "Deleted — only in practice", null);
                }
            }
            if (module == "mail")
            {
                if (M(url, @"/api/mail/send"))
                {
                    AddMail(Me, Str("subject") ?? "(no subject)", Str("body") ?? "", 0, true, folder: "sent");
                    return (new { success = true, mailId = _mail[^1]["id"] }, "Not sent — practice mode", "In real mode your mail would be delivered now.");
                }
                if (IdIn(url, @"/api/mail/([0-9a-f-]{36})/(star|read|unread)") is { } mid && _mail.FirstOrDefault(m => (Guid)m["id"]! == mid) is { } mail)
                {
                    if (url.EndsWith("/star")) mail["isStarred"] = !(bool)mail["isStarred"]!;
                    else mail["isRead"] = url.EndsWith("/read");
                    return (new { success = true }, "Only in practice", "Changes to sample mail are thrown away when you exit.");
                }
                if (method == HttpMethod.Delete && IdIn(url, @"/api/mail/([0-9a-f-]{36})$") is { } dm && _mail.FirstOrDefault(m => (Guid)m["id"]! == dm) is { } dmail)
                {
                    dmail["_folder"] = "trash";
                    return (new { success = true }, "Moved to Trash — only in practice", null);
                }
            }
            if (module == "news" && IdIn(url, @"/api/news/([0-9a-f-]{36})/acknowledge") is { } nid && _news.FirstOrDefault(n => (Guid)n["id"]! == nid) is { } post)
            {
                post["isAcknowledged"] = true;
                return (new { success = true }, "Acknowledged — only in practice", "In real mode HR would see your acknowledgement with the time.");
            }
            if (module == "calendar" && method == HttpMethod.Post && M(url, @"/api/calendar/events$"))
            {
                var start = body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("startAt", out var s) && s.TryGetDateTime(out var st) ? st.ToUniversalTime() : DateTime.UtcNow.AddHours(1);
                AddEvent("personal", "event", Str("title") ?? "Practice event", start, 1);
                return (new { id = _events[^1]["eventId"] }, "Event added — only in practice", "It disappears when you exit practice mode.");
            }
            if (module is "calendar" or "calls" && M(url, @"/api/meetings(/|$)"))
                return (new { success = true }, "No invites sent — practice mode", "In real mode everyone invited would get it in their calendar.");
            return (null, null, null);
        }
    }
}
