# Projects — work-management upgrade (monday.com-inspired, Apple-simple)

Status: draft for review · 2026-09-28 · branch v6-port

## Goal

Make the Projects page comprehensive enough to replace a monday.com board for
Santos teams, without monday's learning curve. Everything must feel like an
Apple app: calm, few controls on screen, progressive disclosure, one obvious
way to do each thing.

## Design principles (apply to every feature below)

1. **One place per action.** A task is edited only in the Task drawer. A view
   only changes *how* tasks are shown, never *what* you can do.
2. **Progressive disclosure.** Defaults show the minimum; power features sit
   behind a single "+" or "…" and appear only once used (e.g. custom columns
   are invisible until a project adds one).
3. **Plain language (Indonesian-friendly English labels already used in app).**
   No jargon like "recipe", "item", "board". We say *task*, *group*, *field*.
4. **Direct manipulation.** Drag to reorder/move/reschedule; click a cell to
   edit in place; Esc cancels, Enter saves. Every drag has a non-drag
   alternative (menu) for keyboard/touch.
5. **Visual language.** Existing v6 tokens only: SF-style type scale, 10–12px
   radii, hairline separators, soft grouped backgrounds, the 4 status colours
   (grey/blue/orange/green) with label+dot — never colour alone. Light & dark
   selected, not auto-flipped. Undo toast instead of confirm dialogs for
   reversible actions; confirm only for destructive, irreversible ones.
6. **Empty states teach.** Each new surface has a one-line hint and a single
   primary button (e.g. "Group tasks by phase — + Add group").

## Scope & phases

### Phase 1
**1a. Groups (sections)**
- A project has ordered groups (default one: "Tasks"). Every task belongs to
  exactly one group. Existing tasks migrate into the default group.
- List view: groups are collapsible headers showing name, colour dot, task
  count, progress %, overdue count. "+ Add group" at the bottom; rename by
  clicking the name; "…" menu: Rename, Colour, Move up/down, Delete (tasks move
  to the first group — undo toast).
- Drag tasks between groups (and menu "Move to group…").
- Board view keeps status lanes; a "Group" filter chip lets you focus one group.
- Timeline/Workload/Calendar respect the group filter.

**1b. Custom fields ("Fields")**
- Types (6): Text, Number (optional unit: Rp, %, jam…), Date, Dropdown
  (coloured options), Person, Link. Max 10 per project.
- Added from the List view header "+" → sheet with type picker and 3 presets
  (Biaya · Rp, Lokasi · Text, Vendor · Dropdown).
- Shown as List columns (resizable, hideable) and in the Task drawer under
  "Fields". Edit in place. Number fields show a sum in each group footer.
- Permission: project owner/manager add/rename/delete fields; all members
  edit values. Deleting a field requires confirm (data loss).

**1c. Portfolio dashboard ("Semua project")**
- New top-level view at the top of the project sidebar: "Overview semua project".
- Cards: active projects with progress & health (on track / at risk /
  overdue, derived from due dates), My tasks due this week, Overdue by person,
  Workload this week (tasks per person vs capacity 8), Upcoming deadlines.
- Every number is clickable → opens the filtered task list.
- Visibility: members see projects they belong to; global Admin / HR / super
  admin see all.

### Phase 2
**2a. Calendar view** — month/week grid of tasks by due date (start–due span
for multi-day), drag to reschedule, click → Task drawer, "+" on a day creates a
task due that day. Uses same filters.

**2b. Dependencies & recurring (UI for existing backend)**
- Drawer "Menunggu" row: pick blocking tasks (search within project); shows
  blocked badge on cards; Timeline draws link arrows. Cycles rejected.
- Drawer "Ulangi" row: Never / Daily / Weekly (weekday chips) / Monthly (day) /
  Custom (existing RecurrenceRule). Next instance created on completion.

**2c. Bulk actions & saved views**
- Checkbox on hover in List (and long-press on mobile) → bottom floating bar:
  Status, Assign, Due date, Group, Priority, Delete (undo). Shift-click range.
- Saved views: current filters + view + grouping saved as a named chip
  ("My overdue", "Site work"). Personal by default; owner can share with project.

### Phase 3
**3. Time tracking** — ▶/⏸ button in the drawer and on List rows; one running
timer per user (starting another stops the first); entries editable; totals
roll into Actual hours; weekly timesheet per person in Workload and Portfolio.

### Deferred
Automations (if wanted later: 3–4 ready-made rules toggled on/off, no builder).

## Data model (additive migrations)

- `ProjectGroup { Id, ProjectId, Name, Color, SortOrder }`; `ProjectTask.GroupId` (FK, required after backfill).
- `ProjectField { Id, ProjectId, Name, Type, Unit?, OptionsJson?, SortOrder }`;
  `ProjectTaskFieldValue { TaskId, FieldId, Text?, Number?, Date?, UserId?, OptionId? }` (one typed column filled).
- `ProjectTaskDependency { TaskId, DependsOnTaskId }` replacing the string
  `ProjectTask.Dependencies` (migrate existing values; keep column read-only one release).
- `ProjectSavedView { Id, ProjectId?, OwnerId, Name, IsShared, StateJson }`.
- `TaskTimeEntry { Id, TaskId, UserId, StartedAt, EndedAt?, Minutes, Note? }`.
All dates UTC via `UtcDates` helpers; all queries bounded; realtime
`project-changed` broadcast after mutations.

## Testing

Service tests per feature (permissions, migration backfill, cycles, sums,
single running timer, portfolio visibility); helper tests for calendar
layout; Playwright read-only visual checks at 1440/1280/390 px, light + dark,
with mutations blocked.

## Decisions taken as defaults (tell me if you disagree)

1. Custom fields are managed by project owner/manager; everyone edits values.
2. Portfolio: members see their projects; Admin/HR see all.
3. Phase order as above; each phase ships separately and is reviewed before the next.
