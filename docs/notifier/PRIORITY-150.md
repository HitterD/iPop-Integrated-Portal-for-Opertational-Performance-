# Notifier 1.5.0 — Priority Inbox (BOD request, 2026-11-21)

Approved mockup (source of truth for look + motion): `docs/mockups/notifier/priority.html` (commit 64a28b2).
Branch `IT18/notifier-priority` (worktree `C:/Users/IT12/orca/workspaces/notifier-audit`), based on Notifier 1.4.8
(`IT18/notifier-audit`) merged with `github/main`. Version → **1.5.0** (`src/IPop.Notifier/IPop.Notifier.csproj`).

Goal: leaders get every kind of message. Classify every notification into 3 tiers so passing messages do not
interrupt, approvals and messages from leaders stand out, the Inbox is easy to scan (maximized = 3-pane like Mail),
and files can be previewed/downloaded. UI language English. Sources now: iPop + iDesk only (SPP comes later through
the Notification Gateway as `approval.*` events).

## Tiers

| Tier | Key | Behaviour |
|---|---|---|
| Needs action | `act` | Full popup window (existing NoticeWindow), stays, sound, breaks through Busy/full screen/paused when "Let Needs action break through" is on |
| Messages | `msg` | Small toast (existing native toast `Toasts.ShowNotice` / `Toasts.ShowChat`), soft sound, held during Busy/paused |
| For your info | `info` | No popup, no sound. Inbox + count only |

Always `act` regardless of settings: actor is a leader or VIP; `chat.emergency`; `memo.mandatory`; priority `high`.

### Kinds (Settings › Categories rows; default tier)

| Kind key | Label | Matches | Default |
|---|---|---|---|
| `approval` | Approvals | eventType `approval.*` | act |
| `chat` | Chat | `chat.*` + desktopToast chat | msg |
| `mail` | Mail | `mail.*` priority ≠ low | msg |
| `mailmass` | Mass mail & "All staff" | `mail.new` with priority `low` (mass mail is published with priority low) | info |
| `ticketassigned` | iDesk — assigned to me / reopened | `ticket.assigned`, `ticket.reopened`, technician events (see TicketService NotifyTechnicianAsync event names) | act |
| `ticketchat` | iDesk — replies | `ticket.message_created` | msg |
| `tickets` | iDesk — status updates | other `ticket.*` / `idesk.*` | msg |
| `meetingsoon` | Meeting starting soon | `meeting.reminder`, `calendar.reminder` | act |
| `calendar` | Invites & changes | other `meeting.*` / `calendar.*` | msg |
| `projects` | Comments, mentions & tasks for me | `project.task_assigned`, `project.task_due_reminder`, `*comment*`, `*mention*` under `project.`/`task.` | msg |
| `projectwatch` | Project updates you watch | other `project.*` / `task.*` | info |
| `notes` | Notes mentions | `note.*` | msg |
| `calls` | Calls | `call.*` / `calls.*` | msg |
| `news` | News & announcements | `news.*`, `memo.*` (except `memo.mandatory`) | info |
| `system` | System notices | everything else | info |

Implement the classifier ONCE in `src/IPop.Application/Notifier/NotifierSettings.cs` (linked into the app) as
`static string KindOf(string? eventType, string? priority)` and `string TierFor(kind, eventType, priority, bool fromLeaderOrVip)`;
mirror it in `src/IPop.Notifier/Ui/tiers.js` and add a node test (`tests/notifier-tiers.test.cjs`) that checks the JS
mapping against a table of cases identical to a C# test (`tests/IPop.Tests/NotifierTierTests.cs`).

### Settings (NotifierSettings, ui-prefs key `notifier`)
- `Tiers: Dictionary<kind, "act"|"msg"|"info">` normalised (unknown → default). Migration in `Normalized()`: when a
  stored settings JSON has no `tiers`, derive from the old `Modules` (module "off" → that module's kinds = info;
  otherwise default).
- `Vips: List<Guid>` (max 50, user ids).
- `HideMessageToasts: bool` (default false) — Messages tier shows no toast at all.
- `UrgentBreaksThrough` (existing) = "Let Needs action break through".
- Old `Modules` stays for parsing/back-compat, not shown in UI any more.

## Server (API)

1. **Actor on notifications.** Add nullable `ActorUserId` (Guid?) to `NotificationRequest` (EF migration
   `NotificationActor`, index not needed). Add to `INotificationGatewayService` a new member WITH a default interface
   implementation (so the ~10 test fakes keep compiling):
   `Task<NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message, NotificationTarget target, Guid? actorUserId, string? idempotencyKey, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)`
   — default implementation forwards to the existing overload (actor dropped); `NotificationGatewayService` overrides and stores it.
   Set the actor for: `mail.new` (mail.SenderId), `chat.mention` + `chat.emergency` (Chat/NotificationGateway.cs builds the
   request directly — set ActorUserId there), project comment/mention/assign notifications (ProjectService — actor = acting user),
   notes mentions, meeting/calendar invites (organiser) where the actor is at hand. Don't change titles/bodies.
2. **Mass mail priority.** In `MailService.NotifyDeliveredAsync`: `priority = mail.IsMass ? "low" : (mail.Importance == "high" ? "high" : "normal")`.
   Check the web `NotificationState` / push code doesn't treat "low" specially in a bad way (gateway already accepts low).
3. **Expose actor.** `NotificationDeliveryState` gets `Guid? ActorId = null` (last optional param) filled where states are
   built (NotificationGatewayService + Chat/NotificationGateway.cs). `InboxItemRow` gets `Guid? ActorId = null, string? ActorName = null`
   (name from Users join — name only, never NIK/position). Desktop chat toast: `PushPayload` gets optional `Guid? SenderId = null`
   set in the chat audience (PushDispatcher), so the app can tell a leader's chat.
4. **`GET /api/notifier/leaders`** (signed-in): returns
   `{ leaders: [{ id, name, department, reason }], vips: [{ id, name, department }] }` where
   leaders = every distinct user in the caller's own `ApproverChain` slots (reason = slot label, e.g. "Manager",
   "General Manager", "Director" — ApproverSlots.Labels) ∪ users whose `ApproverChain.SourceLevel` is
   `GENERAL MANAGER` or `DIRECTOR` ∪ distinct users in Slot8/Slot9 of any chain (reason "BOD / GM level"), excluding the
   caller; vips = the caller's `NotifierSettings.Vips` resolved to active users. Match chain NIKs to `Users.Nik`.
   Never return NIK or Position. Cache per user 10 min in memory is fine.
5. **`GET /api/notifier/people?q=`** (signed-in, q ≥ 2): up to 12 active users `{ id, name, department }` by name/email
   ILIKE — VIP picker. No NIK.
6. **`POST /api/notifier/convert-preview`** (signed-in): request body = raw file bytes (≤ 60 MB, else 413),
   query `name=`; Word (.doc/.docx/.rtf/.odt), PowerPoint (.pptx), Excel (.xlsx/.xls/.csv) → PDF via
   `IWordDocumentService` / `IPresentationService` / `ISpreadsheetService` `.ConvertToPdfAsync`; max 2 concurrent
   (SemaphoreSlim), 415 for other types. The app already holds the original bytes (downloaded with the user's own
   access), so no extra access check is needed; nothing is stored server-side.
7. Tests (xUnit, tests/IPop.Tests): leaders endpoint (chain slots + GM/Director + VIP, excludes self, no NIK in JSON),
   actor stored + exposed in inbox, mass mail priority low, convert-preview rejects wrong type/too large.

## App (src/IPop.Notifier)

- `Core/Models.cs`: `Delivery`/`InboxItem` gain `ActorId`/`ActorName`; `Notice` gains `ActorId`, `ActorName`, `Kind`, `Tier`
  (tier computed by the app with current settings + leader set). `ToastPayload` gains `SenderId`.
- `Core/Leaders.cs`: loads `/api/notifier/leaders` after sign-in and every 30 min / on settings change; `IsLeaderOrVip(Guid?)`.
- `Windows/PopupManager.cs`: decide by tier: act → window (as today, sound, breaks through when allowed); msg → toast
  unless `HideMessageToasts` (held while Busy/paused, as today); info → nothing (only `CountChanged`). Backlog keeps working
  but only act items open windows one by one; msg items in the backlog collapse to toasts as today; info items are skipped.
  `Held()` uses tier act instead of `Urgent`.
- `NotifierApp.OnChatToast`: when `SenderId` is a leader/VIP or the toast is an emergency → open a popup window
  (Notice module "chat", eventType "chat.message", targetUrl = toast url, title = sender) instead of a toast.
- Tray: tooltip "N need action · M unread"; tray icon keeps the dot; taskbar badge = act count.
- `ApiBridge` allow-list additions: `GET api/notifier/leaders`, `GET api/notifier/people?q=`, `GET api/chat/attachments/{G}/download(?inline=true)?`,
  `GET api/mail/attachments/{G}/download(?inline=true)?`, ticket attachment routes if iPop exposes them (check TicketEndpoints / message DTO),
  `PUT api/me/ui-prefs/notifier` only if Settings doesn't already go through the app.
- **Downloads**: bridge message `download { path, name }` → app streams it with the token to the user's Downloads folder
  (unique name "x (1).pdf"), reports progress (`downloadProgress {id, p}`) and the result (`downloadDone {id, file}`);
  `reveal {file}` → `explorer /select,`; `openFile {file}` → shell open. Only allow-listed download paths.
- **Preview**: new `Windows/ViewerWindow.cs` + `Ui/viewer.html/js/css` (dark viewer per mockup screen 5): receives
  `{ files:[{name, path, size}], index }`. The app downloads each file into its media cache (WebHost `media.notifier.ipop`
  mapping already exists) and hands back the https URL. PDF → `<iframe>` (WebView2's PDF viewer); images → `<img>`;
  video/audio → tags; Word/PowerPoint/Excel → app posts the bytes to `/api/notifier/convert-preview` once, caches the
  PDF by content hash, shows a shimmering skeleton + "Preparing preview — converting … on iPop" meanwhile; zip/.ppt/others
  → "Download only". Toolbar: name, type/size, n of N, zoom (images), Open in app (download + shell open), Download;
  ← → keys and buttons; file list on the right; slide/fade transitions; prefers-reduced-motion respected.
  Excel shows as PDF pages (sheet grid viewer is out of scope — note it).

## UI (src/IPop.Notifier/Ui)

Follow the mockup's tokens, spacing, chips and motion (sliding indicators, row enter, read-dot shrink, collapse on
snooze, Undo snackbars, count tick, empty state check-draw, file card hover overlay, download progress ring → tick).

- **Inbox small** (`inbox.html/js`, 480×720): header (logo, "Notifications", "First · N unread", search that expands,
  Mark all read, Settings, □), summary line ("N approvals · N from leaders · N messages", each a link to its chip),
  chips All / Needs action / Messages / Info with counts (sliding thumb), list: "Needs action" pinned box first, then
  day groups; Info rows smaller/muted; rows grouped per thread (same targetUrl for chat/tickets/project task → one row,
  "N updates" chip expands the older ones); chips on rows: Approval / Your leader / Starts soon / 📎n / ticket no.;
  hover quick actions ✓ read/unread and ⏰ snooze 1 h (POST …/snooze {hours:1}) with Undo; click → opens the popup
  window (existing `openNotice`); footer Popups switch (existing) + Open iPop. Data: `/api/notifications/v1/inbox`
  (pageSize 100, then "Show older"), tiers computed with `tiers.js` + context (settings tiers, leader ids) sent by the app.
- **Inbox maximized** (`html.max`): 3 panes. Sidebar: Needs action, From leaders, Approvals, Messages, For your info,
  All (badges), Modules list with counts, Popups switch. List pane: search, List/Table segmented, title + counts,
  Mark all read, list (same rows), keyboard ↑↓ / E / S hint. Reading pane: module chip, tier chip, time, title,
  "Waiting for your approval" bar for approvals, person card (name + leader relation from leaders list), body:
  mail → `GET api/mail/{id}` plain body + attachments; tickets → messages; chat → last messages; others → notification body;
  files section with cards (Preview → viewer, Download); action bar: Reply inline for chat (POST chat message) and iDesk
  (POST ticket reply) with Enter-to-send and bubble append; mail → "Reply" opens the popup window (full mail reply lives there);
  Open in iPop; Mark as read/unread; Snooze. Auto mark-read after the item is shown ~1 s. Table view: Priority / From /
  Subject / Module / 📎 / Time / Status, grouped by tier, collapsible, sortable by time, hover quick actions.
- **Popups** (`popup.js/css`): header chip "Approval" / "Your leader"; approvals get the orange "Waiting for you" strip;
  act windows' icon rings once on entrance; file cards (type badge, name, size; Preview / Download buttons) for mail
  attachments (all, not only images) and ticket/chat attachments where available; chat loader (last messages + inline
  reply) used for leader chats; Snooze opens a small menu (15 min / 1 h / tomorrow 08:00) where snooze applies.
- **Settings** (`settings.html/js`): new "Categories" page replacing the per-module Popup/Toast/Off list: 3 tier cards
  (with live "N kinds" counts), Leaders (auto, from /leaders, with reason chips), Your VIPs (search /people, add/remove,
  max 50), By module rows with a 3-way segmented control (sliding thumb) + Reset to defaults, Busy box (Let Needs action
  break through, Hide Messages toasts entirely). Every change saves immediately ("Saved" pill).

## Verification
- `dotnet build` + full `dotnet test` (one at a time, `-m:1`), node tests incl. new tiers test, existing notifier tests.
- Copy-DB QA: local API against a copy DB, QA Notifier build (`IPOP_NOTIFIER_QA_DIR`, CDP) — inbox small/max, popup
  tiers, viewer, settings; screenshots light/dark.
- Release: Notifier 1.5.0 → pilot via `publish-notifier.ps1`, then promote (user said "kerjakan sampai tuntas lalu deploy").
  API/Web deploy to both prods (cluster via ship.ps1, Windows prod swap) because of the migration + new endpoints.
