# Notes stage 3: automation

Decisions (grilled 2026-11-19, all recommendations accepted):

1. **To-do → Task.** A to-do is never turned into a task on its own. The to-do row has a "→ Task" action; the person picks the
   project (same pattern as "Make task" from a whiteboard sticky). Ticking the to-do completes the task, and completing the task
   ticks the to-do.
2. **To-do → Calendar.** A to-do with a person and a due date shows as an all-day item on that person's calendar, only while they
   can still open the note. People who only have free/busy access to someone's calendar never see it (it is not a meeting).
3. **Recurring pages.** On a template page: ⋯ → Repeat… (daily, weekly on chosen days, monthly on a date, workdays only). A page
   is created at 06:00 WIB on the day, under the parent the template page lives in, titled "Name — 24 Nov 2026", shared with the
   same people as the parent, who get a "New page" notification.
4. External-service triggers (Gmail, Slack, …) are out; iPop-module triggers are postponed.

## Facts this builds on (checked in code)

- `NoteTodoDue` already holds every to-do that has an assignee and a due date (rebuilt from each snapshot in
  `NoteService.Collab.SyncTodoDueAsync`). `NotesReminderWorker` reminds the person the day before and on the day.
- The calendar feed is `CalendarFeedService.BuildAsync` (`src/IPop.Infrastructure/Calendar`). Sources are blocks; a new block
  "notes" is added next to tickets. Items have `Source`, `Kind`, `AllDay`, `Url`.
- Projects: `POST /api/projects/{id}/tasks`, assignees at `/api/projects/tasks/{id}/assignees`. `ProjectTaskHooks.AfterStatusChangeAsync`
  is the single server-side place after a task changes status. `ProjectTask` has no origin column.
- Recurrence text is `TaskRecurrence` (RRULE subset `FREQ=DAILY|WEEKLY|MONTHLY;INTERVAL;BYDAY;BYMONTHDAY`). The Projects weekly
  worker (`ProjectAutomationWorker`) ticks every minute, takes a PostgreSQL advisory lock and keeps `LastRunAt`: copy that pattern.
- `CreateFromTemplateAsync` only works for the template's owner and checks the parent's access; a worker version has to run as the
  owner without those checks, and must still clone attachments.

## Parts (each shippable on its own)

### 3a. To-do on the Calendar (smallest, no migration)
- `CalendarFeedService`: block "notes": `NoteTodoDues` where assignee = me, not done, due in range, page not trashed, and I can
  open the page (same rule as the reminder worker). Item: `note:{pageId}:{itemKey}`, source `notes`, kind `note-todo`, all-day, `Url=/notes/{pageId}`.
- Calendar page: filter "Notes" and an icon. Not part of free/busy for others.

### 3b. To-do ↔ Task
- Migration: `NoteTodoDue.TaskId` (Guid?, nullable) and `ProjectTask.SourceNoteId`/`SourceNoteKey` (nullable) so the link survives either side
  being edited. (Text edits change the item key, so the editor also stores the task id on the to-do node: attr `taskId`.)
- API `POST /api/notes/{pageId}/todo-task` { itemKey, projectId } → creates the task (title = to-do text, due, assignee if a
  project member), returns `{ taskId, projectId, title }`; the editor writes `taskId` into the node.
- Done sync: to-do ticked → `PUT task status done` (server side on snapshot: `NoteTodoDue.Done` changed and `TaskId` set);
  task done → `ProjectTaskHooks` flips the to-do (editor receives it as a Yjs update written by a server-side Yjs client? NOT available:
  the server never edits the Yjs doc). So task → to-do is shown, not written: the to-do chip reads the task state live
  ("✓ Done in Projects") and the page's progress counts it. Ticking in the note still completes the task.
- Editor: "→ Task" in the to-do ⋯ menu; a chip with the project name that opens the task.

### 3c. Recurring pages
- Migration: `NoteRecurrence` (Id, PageId (template page), OwnerId, Rule, ParentId?, LastCreatedFor (DateOnly), Enabled, CreatedAt).
- Worker `NotesRecurrenceWorker`: every minute, advisory lock, for each enabled rule whose local day (Asia/Jakarta) matches and hour ≥ 6 and
  `LastCreatedFor` < today: create the page from the template page's current content (clone like Duplicate), title "Name — d MMM yyyy",
  parent = the template page's parent, share like the parent, notify people with access, set `LastCreatedFor`.
- UI: page ⋯ → "Repeat…" (small popover: Daily / Weekly (days) / Monthly (date) / Workdays), "Stop repeating". Owner or editor only.
- Safety: at most 1 page per rule per day; a deleted/trashed template stops its rule; max 20 rules per person.

## Order
3a → 3b → 3c, each: tests, browser QA on the throwaway DB, commit. Deploy together or one by one on request.
