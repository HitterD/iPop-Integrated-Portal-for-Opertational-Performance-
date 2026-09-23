# Outlook-Grade Mail Redesign & Scheduled Send Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform iPop's Mail module into a complete, Outlook-grade email client featuring scheduled send with background delivery, floating docked/maximized composer with rich text editing and file attachments (local & SynologyDrive), top action ribbon, All/Unread filters, pinned messages, and conversation thread reading pane.

**Architecture:** Extend backend data model (`Mail` and `MailRecipient`) with scheduling, priority, and pin flags; register `MailScheduledDeliveryWorker` for automated background dispatch; expose scheduling and pin endpoints; implement a floating multi-state composer (docked/minimized/maximized) with a WYSIWYG formatting toolbar and split Send button; overhaul `Mail.razor` with an Outlook top ribbon, message list quick hover actions, and collapsible conversation thread cards using iPop design tokens.

**Tech Stack:** ASP.NET Core (.NET 10), Entity Framework Core (PostgreSQL & SQLite in tests), Blazor WebAssembly, HTML5 contenteditable RTE, Lucide icons, CSS custom properties.

**Spec:** Agreed requirements from the Grilling session (Floating docked composer, RTE formatting, Scheduled folder & worker, Top ribbon, All/Unread tabs, Hover triage actions, Pin to top, Importance tags, SynologyDrive & computer attachments).

## Global Constraints
- Preserve theme tokens: Use existing iPop CSS variables (`var(--accent)`, `var(--surface)`, `var(--bg)`, `var(--line)`, `var(--ink)`, etc.) without introducing conflicting hardcoded palettes.
- Back up untracked files before editing (specifically `src/IPop.Web/wwwroot/css/modules/mail.css`).
- Build tests using `-o tests/IPop.Tests/bin/TestRun` to prevent DLL file locks against running `IPop.Api` processes.
- All scheduled send calculations must use UTC (`DateTime.UtcNow`).

---

### Task 1: Data Model & DTOs Extension for Scheduling, Priority, and Pinning

**Files:**
- Modify: `src/IPop.Core/Entities/Mail.cs:15-25`
- Modify: `src/IPop.Core/Entities/MailRecipient.cs:10-25`
- Modify: `src/IPop.Application/Mail/MailDtos.cs:1-55`
- Modify: `src/IPop.Web/Services/MailDtos.cs:1-55`

**Interfaces:**
- Produces:
  - `MailFolder.Scheduled`
  - `Mail.ScheduledAt` (`DateTime?`), `Mail.Importance` (`string`), `Mail.CancelledAt` (`DateTime?`)
  - `MailRecipient.IsPinned` (`bool`)
  - Extended `ComposeMailRequest`, `MailSendRequest`, `MailSummary`, `MailDetail`, and `MailCounts` with `ScheduledAt`, `Importance`, `IsPinned`, and `ScheduledTotal`.

- [ ] **Step 1: Update Core Entities (`Mail.cs` and `MailRecipient.cs`)**
Add `ScheduledAt`, `Importance`, `CancelledAt` to `Mail.cs`. Add `IsPinned` to `MailRecipient.cs`.

- [ ] **Step 2: Update Application DTOs (`src/IPop.Application/Mail/MailDtos.cs`)**
Add `MailFolder.Scheduled`, `ScheduledTotal` in `MailCounts`, `ScheduledAt` and `Importance` in `ComposeMailRequest`, `IsPinned`, `Importance`, `ScheduledAt` in `MailSummary` and `MailDetail`.

- [ ] **Step 3: Update Web DTOs (`src/IPop.Web/Services/MailDtos.cs`)**
Mirror the changes in `MailDtos.cs` for Web client serialization.

- [ ] **Step 4: Verify Compilation**
Run: `dotnet build src/IPop.Core/IPop.Core.csproj; dotnet build src/IPop.Application/IPop.Application.csproj`
Expected: Build succeeded with 0 errors.

- [ ] **Step 5: Commit**
```bash
git add src/IPop.Core/Entities/Mail.cs src/IPop.Core/Entities/MailRecipient.cs src/IPop.Application/Mail/MailDtos.cs src/IPop.Web/Services/MailDtos.cs
git commit -m "feat(mail): add scheduled send, importance, and pin fields to data models and dtos"
```

---

### Task 2: Service Layer Scheduling, Pinning, and Background Delivery Worker (TDD)

**Files:**
- Create: `src/IPop.Infrastructure/Mail/MailScheduledDeliveryWorker.cs`
- Modify: `src/IPop.Application/Mail/IMailService.cs`
- Modify: `src/IPop.Infrastructure/Mail/MailService.cs`
- Modify: `src/IPop.Api/Program.cs`
- Test: `tests/IPop.Tests/MailServiceTests.cs`

**Interfaces:**
- Consumes: Models & DTOs from Task 1
- Produces:
  - `IMailService.ScheduleAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct)`
  - `IMailService.CancelScheduleAsync(Guid userId, Guid mailId, CancellationToken ct)`
  - `IMailService.RescheduleAsync(Guid userId, Guid mailId, DateTime newTime, CancellationToken ct)`
  - `IMailService.SendScheduledImmediatelyAsync(Guid userId, Guid mailId, CancellationToken ct)`
  - `IMailService.TogglePinAsync(Guid userId, Guid mailId, CancellationToken ct)`
  - `IMailService.DeliverDueScheduledMailsAsync(CancellationToken ct)`
  - Background worker `MailScheduledDeliveryWorker` registered in `Program.cs`.

- [ ] **Step 1: Write failing unit tests in `MailServiceTests.cs`**
Add tests for:
1. `ScheduleAsync_creates_mail_in_scheduled_folder_without_delivering_to_recipient()`
2. `DeliverDueScheduledMails_delivers_ready_mails_and_moves_sender_to_sent()`
3. `CancelSchedule_reverts_to_draft_folder()`
4. `TogglePin_pins_and_unpins_recipient_mail()`
5. `GetFolder_orders_pinned_mails_first()`

- [ ] **Step 2: Run tests to verify failure**
Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~MailServiceTests" -o tests/IPop.Tests/bin/TestRun`
Expected: FAIL due to missing methods / implementation.

- [ ] **Step 3: Implement methods in `IMailService` and `MailService.cs`**
- In `SendAsync`: If `req.ScheduledAt` is in the future, route to `ScheduleAsync` logic (status="scheduled", sender folder="scheduled", `ScheduledAt = req.ScheduledAt`).
- Implement `CancelScheduleAsync`: verifies ownership, sets status="draft", sender folder="draft", clears `ScheduledAt`.
- Implement `RescheduleAsync`: verifies ownership, updates `ScheduledAt`.
- Implement `SendScheduledImmediatelyAsync`: sets `ScheduledAt = DateTime.UtcNow` and triggers delivery.
- Implement `TogglePinAsync`: toggles `IsPinned` on the user's `MailRecipient` record.
- Update `GetFolderAsync`:
  - When querying `MailFolder.Scheduled`, filter by sender recipient folder="scheduled".
  - Add ordering: `.OrderByDescending(r => r.IsPinned).ThenByDescending(r => r.Mail.SentAt ?? r.Mail.CreatedAt)`.
- Update `GetFolderCountsAsync`: compute `ScheduledTotal`.
- Implement `DeliverDueScheduledMailsAsync`: find all mails with `Status == "scheduled" && ScheduledAt <= DateTime.UtcNow`, deliver to each recipient, update sender's row to `sent`, set `SentAt = DateTime.UtcNow`.

- [ ] **Step 4: Implement `MailScheduledDeliveryWorker.cs`**
A `BackgroundService` that runs every 30 seconds (or configurable interval) and calls `DeliverDueScheduledMailsAsync`.

- [ ] **Step 5: Register worker in `src/IPop.Api/Program.cs`**
Add `builder.Services.AddHostedService<MailScheduledDeliveryWorker>();`.

- [ ] **Step 6: Run tests to verify they pass**
Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~MailServiceTests" -o tests/IPop.Tests/bin/TestRun`
Expected: PASS (all new and existing tests pass).

- [ ] **Step 7: Commit**
```bash
git add src/IPop.Application/Mail/IMailService.cs src/IPop.Infrastructure/Mail/MailService.cs src/IPop.Infrastructure/Mail/MailScheduledDeliveryWorker.cs src/IPop.Api/Program.cs tests/IPop.Tests/MailServiceTests.cs
git commit -m "feat(mail): implement scheduled delivery, pin toggle, and background delivery worker"
```

---

### Task 3: EF Core Database Migration

**Files:**
- Create: EF Core migration for `AddMailSchedulingAndPinning`
- Modify: `src/IPop.Infrastructure/Persistence/IPopDbContext.cs`

**Interfaces:**
- Consumes: Entity properties from Task 1

- [ ] **Step 1: Configure entity mappings in `IPopDbContext.cs` if needed**
Ensure `ScheduledAt`, `Importance`, `CancelledAt` on `Mail` and `IsPinned` on `MailRecipient` are properly indexed (e.g. index on `(Status, ScheduledAt)` for fast worker sweep).

- [ ] **Step 2: Generate EF Core Migration**
Run: `dotnet ef migrations add AddMailSchedulingAndPinning --project src/IPop.Infrastructure --startup-project src/IPop.Api`
Expected: Migration generated in `src/IPop.Infrastructure/Persistence/Migrations/`.

- [ ] **Step 3: Verify build and migration generation**
Run: `dotnet build src/IPop.Infrastructure/IPop.Infrastructure.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**
```bash
git add src/IPop.Infrastructure/Persistence/IPopDbContext.cs src/IPop.Infrastructure/Persistence/Migrations/*AddMailSchedulingAndPinning*
git commit -m "feat(mail): add ef core migration for mail scheduling and pinning"
```

---

### Task 4: Mail Endpoints & Web Client Service Integration

**Files:**
- Modify: `src/IPop.Api/Endpoints/MailEndpoints.cs`
- Modify: `src/IPop.Web/Services/MailService.cs`

**Interfaces:**
- Consumes: Task 2 service methods
- Produces:
  - POST `/api/mail/{id}/cancel-schedule`
  - POST `/api/mail/{id}/reschedule` (body: `{ scheduledAt: DateTime }`)
  - POST `/api/mail/{id}/send-now`
  - POST `/api/mail/{id}/pin`
  - Web `MailService` client methods for each endpoint.

- [ ] **Step 1: Add new endpoints to `MailEndpoints.cs`**
Expose `cancel-schedule`, `reschedule`, `send-now`, `pin` with RBAC `mail.read` and `mail.send` checks.

- [ ] **Step 2: Update `src/IPop.Web/Services/MailService.cs`**
Add:
- `CancelScheduleAsync(Guid id, CancellationToken ct = default)`
- `RescheduleAsync(Guid id, DateTime scheduledAt, CancellationToken ct = default)`
- `SendScheduledNowAsync(Guid id, CancellationToken ct = default)`
- `TogglePinAsync(Guid id, CancellationToken ct = default)`
- Update `F(MailFolder f)` to handle `MailFolder.Scheduled => "scheduled"`.

- [ ] **Step 3: Verify Build**
Run: `dotnet build src/IPop.Api/IPop.Api.csproj; dotnet build src/IPop.Web/IPop.Web.csproj`
Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Commit**
```bash
git add src/IPop.Api/Endpoints/MailEndpoints.cs src/IPop.Web/Services/MailService.cs
git commit -m "feat(mail): wire scheduled send, reschedule, cancel, and pin endpoints"
```

---

### Task 5: CSS & Design System Styling for Outlook Experience

**Files:**
- Backup: `src/IPop.Web/wwwroot/css/modules/mail.css` -> `.scratch/backup/mail.css.bak`
- Modify: `src/IPop.Web/wwwroot/css/modules/mail.css`
- Modify: `src/IPop.Web/wwwroot/css/ipop.css` (if global tokens needed)

**Interfaces:**
- Produces styles for:
  - `.mail-ribbon` (top action bar with button groups, icons, labels, separators)
  - `.mail-tabs` (All / Unread segmented filter)
  - `.mail-row-actions` (quick hover actions: Delete, Pin, Flag, Mark Read)
  - `.mail-row.pinned` and `.pin-badge`
  - `.importance-tag` (High / Low)
  - `.outlook-composer-dock` (floating bottom-right dock, minimize, maximize, normal)
  - `.rte-toolbar`, `.rte-btn`, `.rte-dropdown`, `.rte-editor`
  - `.scheduled-banner` and `.scheduled-actions` in reading pane
  - `.conversation-cards` (collapsible thread cards)
  - `.inline-quick-reply` (docked at the bottom of the reading pane)

- [ ] **Step 1: Backup untracked `mail.css`**
Save a pristine copy in `.scratch/backup/mail.css.bak`.

- [ ] **Step 2: Write comprehensive Outlook-grade CSS rules**
Ensure light and dark mode compliance using iPop tokens (`var(--surface)`, `var(--bg)`, `var(--line)`, `var(--accent)`, `var(--danger)`).

- [ ] **Step 3: Verify CSS build / bundling**
Verify that `dotnet build src/IPop.Web/IPop.Web.csproj` succeeds and no broken selectors are introduced.

- [ ] **Step 4: Commit**
```bash
git add src/IPop.Web/wwwroot/css/modules/mail.css
git commit -m "feat(mail): add outlook-grade styles for ribbon, floating composer, rte, and thread cards"
```

---

### Task 6: Blazor Component - Rich Text Editor Toolbar & Floating Docked Composer

**Files:**
- Create: `src/IPop.Web/Components/Mail/MailComposer.razor`
- Create: `src/IPop.Web/Components/Mail/MailRteEditor.razor`
- Modify: `src/IPop.Web/wwwroot/js/ipop.js` (for contenteditable execCommand / selection helpers and paste image handling)

**Interfaces:**
- Produces:
  - `MailRteEditor.razor`: WYSIWYG editor with Bold, Italic, Underline, Strike, Font size, Colors, Alignment, Lists, Links, Tables, Clear Formatting, paste image interception.
  - `MailComposer.razor`: Multi-state window (Docked normal: 600x550px, Minimized bar, Maximized centered modal: 960px). Split-button Send ([ Send | ▾ ]) with "Send now" and "Schedule send...".

- [ ] **Step 1: Implement JS interop helpers in `ipop.js`**
Add rich text helpers: `ipop.rteFormat(element, command, value)`, `ipop.rteInsertHtml(element, html)`, `ipop.rteInsertTable(element, rows, cols)`, and paste image event handler.

- [ ] **Step 2: Build `MailRteEditor.razor`**
Toolbar containing icon buttons with Lucide icons, dropdowns for font size and headings, color pickers, and contenteditable area bound to HTML body.

- [ ] **Step 3: Build `MailComposer.razor`**
- Window header with Minimize, Maximize/Restore, Close.
- Recipient inputs: To with `AppMemberPicker`, toggleable CC / BCC chips.
- Subject input and Importance dropdown (High !, Normal, Low ↓).
- Attachments list with file drop zone and delete button.
- Rich text editor component.
- Footer with Split Send button:
  - Main button triggers immediate send or scheduled send if scheduled.
  - Dropdown menu triggers "Schedule send" with presets (Tomorrow morning 08:00, Tomorrow afternoon 13:00, Custom date-time).
- Auto-save timer (saves draft every 15s when modified).

- [ ] **Step 4: Verify build**
Run: `dotnet build src/IPop.Web/IPop.Web.csproj`
Expected: Build succeeded with 0 errors.

- [ ] **Step 5: Commit**
```bash
git add src/IPop.Web/Components/Mail/MailComposer.razor src/IPop.Web/Components/Mail/MailRteEditor.razor src/IPop.Web/wwwroot/js/ipop.js
git commit -m "feat(mail): create floating docked composer and rich text editor component"
```

---

### Task 7: File Attachment: Local Upload & iPop SynologyDrive File Picker

**Files:**
- Create: `src/IPop.Web/Components/Mail/SynologyFilePickerDialog.razor`
- Modify: `src/IPop.Web/Components/Mail/MailComposer.razor`

**Interfaces:**
- Consumes: `IFileAccessService` / `FileEndpoints` from iPop Files
- Produces:
  - Attachment menu: "Upload from computer" and "Browse iPop Files".
  - `SynologyFilePickerDialog`: modal allowing the user to browse their SynologyDrive folders and select files to attach to the email.

- [ ] **Step 1: Build `SynologyFilePickerDialog.razor`**
List directories and files using the existing `FileEndpoints` (`/api/files/list`), with breadcrumb navigation, search, and selection checkboxes.

- [ ] **Step 2: Connect Dialog to `MailComposer.razor`**
When user selects "Browse iPop Files", open picker, select file(s), and convert/attach to `composerAtts`.

- [ ] **Step 3: Verify build**
Run: `dotnet build src/IPop.Web/IPop.Web.csproj`
Expected: Build succeeded with 0 errors.

- [ ] **Step 4: Commit**
```bash
git add src/IPop.Web/Components/Mail/SynologyFilePickerDialog.razor src/IPop.Web/Components/Mail/MailComposer.razor
git commit -m "feat(mail): add synology file picker dialog for email attachments"
```

---

### Task 8: Blazor Page - Mail.razor Overhaul (Outlook Layout, Ribbon, All/Unread, Pinned, Thread Cards, Inline Reply)

**Files:**
- Modify: `src/IPop.Web/Pages/Mail.razor`

**Interfaces:**
- Consumes: Components from Tasks 5, 6, 7 and service methods from Task 4

- [ ] **Step 1: Add Top Action Ribbon**
Add `.mail-ribbon` above the 3-pane layout containing Outlook action buttons: New mail, Delete, Archive, Junk, Pin/Unpin, Star/Flag, Mark Read/Unread, Move to.

- [ ] **Step 2: Add Scheduled Folder to Sidebar**
Add `MailFolder.Scheduled` with icon `calendar-clock` and badge `counts?.ScheduledTotal`.

- [ ] **Step 3: Add All / Unread Filter Tabs & Hover Quick Actions in List Pane**
- Segmented control `All` and `Unread`.
- Quick hover action buttons on each mail row (Delete, Pin, Star, Mark Read) with `@onclick:stopPropagation`.
- Display pinned items with pin indicator at top of the list.
- Display high importance red exclamation badge.

- [ ] **Step 4: Overhaul Reading Pane (Detail)**
- For scheduled mails: render alert card *"Scheduled for [Date] at [Time]"* with buttons [ Send Now ], [ Reschedule ], [ Cancel & Edit as Draft ], [ Delete ].
- Conversation history: Render thread messages as expandable conversation cards with sender avatar, timestamp, and body snippet.
- Inline Quick Reply at bottom of reading pane: text box with [ Reply ], [ Reply All ], [ Forward ], and [ Pop-out to window ] icon button.

- [ ] **Step 5: Integrate Floating `MailComposer.razor`**
Replace the old side-drawer composer with `<MailComposer @ref="composer" ... />`.

- [ ] **Step 6: Verify build**
Run: `dotnet build src/IPop.Web/IPop.Web.csproj`
Expected: Build succeeded with 0 errors.

- [ ] **Step 7: Commit**
```bash
git add src/IPop.Web/Pages/Mail.razor
git commit -m "feat(mail): overhaul mail.razor with outlook layout, ribbon, thread cards, and inline reply"
```

---

### Task 9: End-to-End Verification & Browser Testing

**Files:**
- Test: `tests/IPop.Tests/MailServiceTests.cs`
- Verification script / Playwright test: `tests/IPop.Tests/MailOutlookUiVerification.cs` (or browser automation)

- [ ] **Step 1: Run full automated test suite**
Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~Mail" -o tests/IPop.Tests/bin/TestRun`
Expected: All tests PASS.

- [ ] **Step 2: Launch browser / Playwright to test UI in real running app**
- Navigate to `http://localhost:5103/mail` (or LAN IP).
- Verify Outlook ribbon displays properly.
- Open composer: verify docked window at bottom right, test Minimize and Maximize.
- Test formatting toolbar (Bold, Italic, lists, etc.).
- Schedule an email for tomorrow: verify it lands in "Scheduled" folder with badge.
- Open the scheduled email: test "Cancel & Edit" or "Send now".
- Verify Pinned emails stay at top and importance badges render.
- Capture screenshot to visually verify Outlook parity.

- [ ] **Step 3: Commit any final polish and verification notes**
```bash
git add .
git commit -m "test(mail): verify outlook layout, scheduled send, and composer features"
```
