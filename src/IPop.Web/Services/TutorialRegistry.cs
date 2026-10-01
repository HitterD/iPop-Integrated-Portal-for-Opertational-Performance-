namespace IPop.Web.Services;

/// One coach-mark. El = CSS selector on the real page; steps whose target isn't on screen
/// (empty inbox, role-gated button) are skipped. Limits = the role-aware "Good to know" card.
public record TourStep(string? El, string Title, string Body, bool Limits = false);

public record TutorialModule(
    string Key, string Title, string Icon, string Color,
    string InviteTitle, string InviteText,
    IReadOnlyList<TourStep> Steps,
    IReadOnlyList<(string Icon, string Title, string Text)> Feats,
    IReadOnlyDictionary<string, string[]> Limits,
    IReadOnlyList<(string Action, string Keys)> Keys,
    bool HasPractice = false)
{
    /// "admin" | "hr" | "emp" — falls back to the shared "all" list.
    public string[] LimitsFor(string role) =>
        Limits.TryGetValue(role, out var l) ? l : Limits.TryGetValue("all", out var a) ? a : [];
}

/// Tour content for every module, keyed by the first URL segment (/chat/… → "chat").
public static class TutorialRegistry
{
    private static TourStep S(string el, string title, string body) => new(el, title, body);
    private static readonly TourStep GoodToKnow = new(null, "Good to know", "", Limits: true);
    private const string Palette = "Ctrl + K";

    public static readonly IReadOnlyList<TutorialModule> All =
    [
        new("dashboard", "Dashboard", "layout-grid", "#475569",
            "Welcome to iPop", "A 1-minute tour of your home page: what needs you today and where each widget leads.",
            [
                S(".dw-hero", "Your day at a glance", "<p>A greeting with what's waiting for you — unread chats, mail and memos that still need your acknowledgement.</p>"),
                S(".dw-qa", "Quick actions", "<p>Start the things you do most — a new mail, a chat, a meeting — without opening the module first.</p>"),
                S(".dw-kpis", "The numbers", "<p>Live counts from each module. Click one to jump straight to it.</p>"),
                S(".dw-card.m-calendar", "Today's schedule", "<p>Meetings and events from your calendar. Join an online meeting from here when it starts.</p>"),
                S(".dw-card.m-memo", "Memos to read", "<p>Company announcements. Some need you to press <b>Acknowledge</b> — HR can see who has.</p>"),
                S(".dw-card.m-team", "Your team", "<p>Who is online right now, so you know who to ping.</p>"),
                S(".dw-mini.m-admin", "Admin shortcuts", "<p>Only admins see this: sync status and user management at a glance.</p>"),
                GoodToKnow,
            ],
            [("layout-grid", "Widgets", "One card per module, in its own colour."), ("zap", "Quick actions", "New mail, chat or meeting in one click."), ("bell", "Live counts", "Update as things arrive.")],
            new Dictionary<string, string[]>
            {
                ["all"] = ["The dashboard only shows — every action opens the module it belongs to.", "Counts refresh live; if one looks stale, reload the page."],
                ["admin"] = ["Admin widgets are only visible to admins.", "The dashboard only shows — every action opens the module it belongs to."],
            },
            [("Search iPop", Palette), ("Toggle sidebar", "[")]),

        new("directory", "Directory", "users", "#0369a1",
            "Find anyone in the company", "See how to search people, browse by department and save favourites.",
            [
                S(".dir-toolbar .apple-segmented", "People or structure", "<p>Browse everyone as a list, or by department and site.</p>"),
                S(".dir-toolbar .v6-search", "Search", "<p>Type a name or email.</p>"),
                S(".view-seg", "Grid or list", "<p>Cards with photos, or a compact table for scanning.</p>"),
                S(".dir-sort", "Sort", "<p>By name, department or who is online.</p>"),
                S(".dir-structure", "Departments", "<p>Pick a department to see only its people.</p>"),
                S(".dir-people", "Profiles", "<p>Click someone to see their profile, then chat, mail or call them. Star them to keep them in <b>Favourites</b>.</p>"),
                GoodToKnow,
            ],
            [("search", "Search", "Name or email."), ("star", "Favourites", "Your go-to people, also used in Calls."), ("download", "Export", "Download the list as CSV."), ("user", "Profiles", "Chat, mail or call in one click.")],
            new Dictionary<string, string[]>
            {
                ["all"] = ["Employee data comes from HRIS — ask HR to correct a wrong position or department.", "You can edit your own photo, not other people's details."],
                ["admin"] = ["Employee data comes from HRIS and is synced; manual edits are overwritten by the next sync.", "Roles and account locks are managed under Administration."],
            },
            [("Search iPop", Palette)]),

        new("projects", "Projects", "kanban", "#0d9488",
            "New to Projects?", "See how boards and tasks work in a minute, or play with a sample project. Nobody gets assigned anything.",
            [
                S(".proj-sidebar .proj-list", "Your projects", "<p>Every project you belong to, with progress and deadline. Filter by year or search by name.</p>"),
                S(".proj-tabs", "Seven ways to look at it", "<p><b>Board</b> for stages, <b>List</b> for sorting, <b>Timeline</b> and <b>Calendar</b> for dates, <b>Workload</b> for who is busy, <b>Plans & Reports</b> for burndown.</p>"),
                S(".kanban-board-grid", "The board", "<p>Each column is a stage. <b>Drag a card</b> to move it along — try it with the sample tasks.</p>"),
                S(".kanban-task-card", "A task at a glance", "<p>Priority, due date (red when late), sub-tasks, comments and who it's assigned to. Click a card for details and the discussion.</p>"),
                S(".proj-add-task", "Add a task", "<p>Give it a title, an assignee and a due date. The assignee gets a notification.</p>"),
                S(".proj-head-actions", "Team & more", "<p>See who is on the project, add members, open the project chat, edit details or export.</p>"),
                GoodToKnow,
            ],
            [("kanban", "Kanban board", "Drag tasks between stages."), ("list-checks", "Tasks", "Assignee, due date, sub-tasks, comments and files."), ("calendar-range", "Timeline & calendar", "Plan deadlines across weeks."), ("users", "Workload", "See who has too much on their plate."), ("bell", "Notifications", "Assignees hear about new and overdue tasks.")],
            new Dictionary<string, string[]>
            {
                ["emp"] = ["You see only projects you're a member of.", "You can add and move tasks; creating projects and managing members is for project owners and admins."],
                ["hr"] = ["You see only projects you're a member of.", "You can add and move tasks; creating projects and managing members is for project owners and admins."],
                ["admin"] = ["As an admin you see every project and can manage its members.", "Deleting a project removes its tasks for everyone — set it to Completed instead if unsure."],
            },
            [("New task", "N"), ("Search tasks", "/"), ("Search iPop", Palette)], HasPractice: true),

        new("notes", "Notes", "notebook-pen", "#ca8a04",
            "New to Notes?", "A quick tour of pages, sharing and live editing. Your guide page has the details.",
            [
                S(".nt-side", "Your pages", "<p>Private pages, pages shared with you, and favourites. Pages can nest inside each other.</p>"),
                S(".nt-side-head .nt-icon-btn", "New page", "<p>Start a blank page or pick a template.</p>"),
                S(".nt-search", "Search", "<p>Find a page by title or text.</p>"),
                S(".nt-doc", "Write together", "<p>Type <kbd>/</kbd> for headings, lists, tables and more. People you share with see changes live.</p>"),
                S(".nt-side-foot", "Trash & guide", "<p>Deleted pages wait in Trash. The <b>guide page</b> explains every block.</p>"),
                GoodToKnow,
            ],
            [("file-text", "Pages", "Nested pages with rich blocks."), ("users", "Share", "Invite people to view, comment or edit."), ("radio", "Live", "See others typing in real time."), ("trash-2", "Trash", "Restore deleted pages.")],
            new Dictionary<string, string[]> { ["all"] = ["Pages are private until you share them.", "Only the owner can delete a page for everyone; shared editors can't."] },
            [("Search pages", "Ctrl + K"), ("New page", "Alt + N"), ("Blocks menu", "/")]),

        new("chat", "Chat", "message-circle", "#16a34a",
            "New to Chat?", "Take a 1-minute tour of the main features, or explore with sample conversations first. Nothing is sent to anyone.",
            [
                S(".room-pane .room-list", "Your conversations", "<p>Direct messages and groups, newest first. A green number means unread messages; pinned chats stay on top.</p>"),
                S(".room-segmented", "Direct or Groups", "<p>Switch between one-to-one chats and group rooms. The number shows how many have something unread.</p>"),
                S(".compose-btn", "Start a chat", "<p>Message anyone in the company, or create a group and pick who joins. Group admins can rename it and manage members.</p>"),
                S(".composer.v6-composer", "Write, attach, mention", "<p>Press <kbd>Enter</kbd> to send, <kbd>Shift</kbd>+<kbd>Enter</kbd> for a new line. Type <b>@</b> and a name to notify someone. <b>+</b> attaches files, stickers and more.</p>"),
                S(".conv-header .header-actions", "Call, search, pin", "<p>Start a call, search inside this conversation, pin it to the top, or see every file shared here.</p>"),
                S(".room-nav-links", "Find it later", "<p><b>Pinned</b> and <b>Saved</b> messages, search across all conversations, and every message where you were <b>@mentioned</b>.</p>"),
                GoodToKnow,
            ],
            [("send", "Direct & group messages", "One-to-one or with your team, in real time."), ("at-sign", "Mentions", "@name notifies that person, even if they muted the group."), ("paperclip", "Files & images", "Shared files are saved to the company NAS."), ("phone", "Voice & video", "Call straight from a conversation."), ("bookmark", "Saved & pinned", "Keep the messages and chats you need often.")],
            new Dictionary<string, string[]> { ["all"] = ["Only members of a conversation can read it.", "Company accounts only — no external contacts.", "Attachments are stored on the company NAS."] },
            [("Send", "Enter"), ("New line", "Shift + Enter"), ("Search iPop", Palette)], HasPractice: true),

        new("mail", "Mail", "mail", "#2563eb",
            "New to Mail?", "Internal mail with folders, labels and scheduling. Take the tour or practise with a sample inbox.",
            [
                S(".mail-page .toolbar .btn-dark", "Compose", "<p>Write to one or many colleagues, add Cc/Bcc, attach files, or <b>schedule</b> it for later.</p>"),
                S(".mail-search-wrap", "Search", "<p>Find mail by sender, subject or text.</p>"),
                S(".mail-folders", "Folders & labels", "<p>Inbox, Starred, Sent, Drafts, Scheduled, Archive and Trash — plus your own coloured labels.</p>"),
                S(".mail-list-tabs", "Filter the list", "<p>All, unread, starred or with attachments.</p>"),
                S(".mail-list-card", "Your mail", "<p>Bold = unread. Replies are grouped into one thread.</p>"),
                S(".mail-detail-card", "Read & reply", "<p>Reply, forward, archive, add a label, or link the mail to a project.</p>"),
                S(".mail-settings-btn", "Signature & rules", "<p>Set your signature and preferences.</p>"),
                GoodToKnow,
            ],
            [("send", "Compose", "To, Cc, Bcc and attachments."), ("clock", "Schedule", "Send at a later time."), ("tag", "Labels", "Colour-coded, your own."), ("check-check", "Read receipts", "See who has read what you sent."), ("folder-kanban", "Link to projects", "Keep mail next to the work.")],
            new Dictionary<string, string[]> { ["all"] = ["Mail is internal — it can't reach external addresses.", "Unsent drafts stay in Drafts; scheduled mail can be cancelled until it goes.", "Mail in Trash can still be restored until it's emptied."] },
            [("Compose", "C"), ("Reply / Reply all", "R / A"), ("Forward", "F"), ("Archive", "E"), ("Next / previous", "J / K"), ("All shortcuts", "?")], HasPractice: true),

        new("news", "News & Memos", "newspaper", "#ea580c",
            "Company news & memos", "See where announcements live and how to acknowledge a memo. Practise with sample posts.",
            [
                S(".news-v6x .toolbar .btn-primary", "New post", "<p>HR and admins publish announcements and memos — to everyone or to chosen departments, sites or people.</p>"),
                S(".news-search", "Search", "<p>Find a post by title or text.</p>"),
                S(".news-nav", "Views", "<p>All, unread, <b>needs acknowledgement</b>, saved and pinned. Managers also see drafts and scheduled posts.</p>"),
                S(".news-review", "Needs your acknowledgement", "<p>Some memos ask you to confirm you've read them. Open one and press <b>Acknowledge</b>.</p>"),
                S(".news-list", "The feed", "<p>Pinned posts first, then newest. Unread posts are marked.</p>"),
                S(".news-v6-reader", "Reading", "<p>Attachments, likes and comments — and for managers, who has acknowledged.</p>"),
                GoodToKnow,
            ],
            [("megaphone", "Announcements", "Company-wide or targeted."), ("badge-check", "Acknowledgements", "Confirm you've read a memo."), ("bookmark", "Save", "Keep posts to read later."), ("message-square", "Discussion", "Like and comment.")],
            new Dictionary<string, string[]>
            {
                ["emp"] = ["Only HR and admins can publish.", "An acknowledgement can't be taken back — HR sees it with the time."],
                ["hr"] = ["Targeted posts are only visible to the chosen people.", "Edits after publishing are recorded in the post's history.", "The acknowledgement report shows who hasn't read a memo yet."],
                ["admin"] = ["Targeted posts are only visible to the chosen people.", "Edits after publishing are recorded in the post's history.", "The acknowledgement report shows who hasn't read a memo yet."],
            },
            [("Search iPop", Palette)], HasPractice: true),

        new("calls", "Calls", "phone", "#dc2626",
            "Voice, video & meetings", "Learn how to call someone and schedule a meeting. Practice mode never rings anyone.",
            [
                S(".calls-tabs", "Calls or meetings", "<p>Your call history, and upcoming scheduled meetings.</p>"),
                S(".calls-search", "Find someone", "<p>Search anyone in the company and call them.</p>"),
                S(".calls-toolbar .btn-primary", "Schedule a meeting", "<p>Pick a time and people; they get an invite in their calendar.</p>"),
                S(".calls-favorites", "Favourites", "<p>Your starred people for one-tap calls.</p>"),
                S(".calls-history", "History", "<p>Missed calls are red. Call back or open the chat with one click.</p>"),
                GoodToKnow,
            ],
            [("phone", "Voice calls", "One-to-one, in the browser."), ("video", "Video & screen share", "Turn on camera or share your screen."), ("calendar-plus", "Meetings", "Scheduled calls with several people."), ("history", "History", "Every call, answered or missed.")],
            new Dictionary<string, string[]> { ["all"] = ["Calls need microphone access in your browser.", "Calls are not recorded.", "Company accounts only — no phone numbers."] },
            [("Search iPop", Palette)], HasPractice: true),

        new("files", "Files", "folder", "#65a30d",
            "Company files on the NAS", "A tour of the file browser: shares, search, preview and editing. Practise with a sample folder.",
            [
                S(".fx-connect-hero", "Connect first", "<p>Sign in once with your NAS account; iPop remembers it for every server.</p>"),
                S(".fx-side", "Places", "<p>NAS servers, shared folders, starred and recent files.</p>"),
                S(".fx-toolbar", "Toolbar", "<p>Back and forward, new folder, upload, and view options.</p>"),
                S(".fx-seg", "Views", "<p>Icons, list or columns — like Finder.</p>"),
                S(".fx-search", "Search", "<p>Search the current folder and below.</p>"),
                S(".fx-content", "Your files", "<p>Double-click to open. Office files open in the online editor; right-click for more.</p>"),
                GoodToKnow,
            ],
            [("hard-drive", "4 NAS servers", "One sign-in for all."), ("eye", "Preview", "Images, PDF and video."), ("file-pen", "Edit online", "Word, Excel and PowerPoint in the browser."), ("star", "Starred & recent", "Get back to files fast.")],
            new Dictionary<string, string[]> { ["all"] = ["You see only the folders your NAS account can access.", "Deleting here deletes on the NAS — check the NAS recycle bin if you need it back.", "Large uploads depend on your network speed."] },
            [("Search iPop", Palette)], HasPractice: true),

        new("calendar", "Calendar", "calendar", "#e11d48",
            "Your calendar", "Meetings, deadlines and your own events in one place. Practise with a sample week.",
            [
                S(".cal-create", "Create", "<p>Add a personal event or schedule a meeting.</p>"),
                S(".cal-mini", "Jump to a date", "<p>Pick any day from the mini calendar.</p>"),
                S(".cal-sources", "What's shown", "<p>Personal events, meetings, project deadlines, scheduled mail and memos. Turn sources on or off.</p>"),
                S(".cal-seg", "Day, week, month", "<p>Change how much you see at once.</p>"),
                S(".cal-main", "The calendar", "<p>Click an event for details, RSVP or the join link.</p>"),
                GoodToKnow,
            ],
            [("calendar-plus", "Events", "Personal, with reminders."), ("users", "Meetings", "Invites with RSVP."), ("layers", "Sources", "Projects, mail and news in one view.")],
            new Dictionary<string, string[]> { ["all"] = ["Personal events are private to you.", "Project and memo dates come from their module — change them there."] },
            [("Search iPop", Palette)], HasPractice: true),

        new("admin", "Administration", "sliders-horizontal", "#475569",
            "Administration", "A quick tour of users, roles, sync and the audit log.",
            [
                S(".adm-side", "Sections", "<p>Users, organisation, integrations and the audit log.</p>"),
                S(".adm-toolbar .adm-search", "Find a user", "<p>By name or NIK.</p>"),
                S(".adm-chips", "Filters", "<p>Active, locked, admins, and more.</p>"),
                S(".adm-grid-row", "User detail", "<p>Open a user to change roles, unlock, reset the password, or reset their tutorials.</p>"),
                GoodToKnow,
            ],
            [("users", "Users & roles", "Admin, HR and Employee."), ("lock", "Lock & unlock", "Block sign-in instantly."), ("refresh-cw", "HRIS sync", "Keep employee data current."), ("scroll-text", "Audit log", "Every admin action is recorded.")],
            new Dictionary<string, string[]> { ["all"] = ["Every admin action is written to the audit log with your name.", "Resetting a password sets it to the default and forces a change at next sign-in.", "Employee data is overwritten by the next HRIS sync."] },
            [("Search iPop", Palette)]),

        new("notifications", "Notifications", "bell", "#db2777",
            "All your notifications", "Where everything that pinged you lives, and how to quiet it down.",
            [
                S(".notifs-search", "Search", "<p>Find an old notification.</p>"),
                S(".notifs-nav", "Filter by module", "<p>Chat, mail, projects, memos and more.</p>"),
                S(".notifs-dnd", "Do not disturb", "<p>Pause pop-ups for a while; notifications still collect here.</p>"),
                S(".notifs-list", "The list", "<p>Click one to open what it's about.</p>"),
                S(".notifs-toolbar-actions", "Clean up", "<p>Mark all as read or clear them.</p>"),
                GoodToKnow,
            ],
            [("bell", "Inbox", "Everything in one place."), ("moon", "Quiet hours", "Set in notification settings."), ("filter", "Filters", "By module or unread.")],
            new Dictionary<string, string[]> { ["all"] = ["Muted modules and quiet hours stop pop-ups, not the list.", "Desktop notifications need https."] },
            [("Search iPop", Palette)]),
    ];

    private static readonly Dictionary<string, TutorialModule> ByKey = All.ToDictionary(m => m.Key);

    public static TutorialModule? Get(string? key) => key is not null && ByKey.TryGetValue(key, out var m) ? m : null;

    /// "chat/abc?x=1" → chat module.
    public static TutorialModule? ForPath(string relativePath)
    {
        var seg = relativePath.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Get(seg?.ToLowerInvariant());
    }
}
