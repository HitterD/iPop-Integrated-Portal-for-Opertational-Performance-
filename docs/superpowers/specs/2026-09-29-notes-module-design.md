# Notes module — design (2026-09-29)

Notion-like pages with an Apple look. Approved mockup: `inspirasi/notes/iPop Notes mockup.html`.

## Decisions (grill)
- Editor: TipTap 3 + Yjs CRDT. Bundled with esbuild from `tools/notes-editor` into
  `wwwroot/js/notes/notes-editor.js` (ES module, imported via IJSRuntime). The bundle talks to
  `/hubs/notes` directly with the SignalR JS client (binary updates as base64).
- Persistence: `NotePage.YState` (compacted Yjs state) + `NoteUpdates` append log. Editors relay each
  update through the hub (server stores it, broadcasts to `note:{id}`). Clients snapshot the merged
  state every few seconds of idle (`POST /snapshot` with the highest contiguous update id they hold);
  the server swaps the state and drops the covered log rows. Snapshots older than the stored cursor
  are rejected, so a slow client cannot roll back another client's compaction.
- Versions: on snapshot, if the last version is older than 10 min a `NoteVersion` is recorded with the
  contributors since then. Restore is client-side: load the version into a temp doc and `setContent`
  so the restore is itself a collaborative edit.
- Sharing: `NoteShare` to a user or a chat room with level view|comment|edit. Room shares follow
  `ChatRoomMember.LeftAt == null`. Access = max over the page and every ancestor (owner of any
  ancestor = owner). A child can add access, never remove. Only owners share.
- Chat: `ChatMessage.Type = "note"`, `Content = "[note]" + json` (poll precedent). Rendered as a card.
- Comments: `comment` mark carries a thread id; threads live in `NoteComments`; @mentions notify via
  the notification gateway (`notes.mention`, deep link `/notes/{id}`).
- Images/files: attachment area `notes`, linked with `NoteAttachments`; readable by anyone who can view
  the page.
- Style per page: cover key, accent key, font (default|serif|mono), full width, small text.
  Text/highlight/block-background colours are named (Notion names, Apple hues) so dark mode adapts.
- Tutorial: every user gets a private page `IsGuide` with `SeedTemplate = "guide"`; the first client to
  open an empty seeded page inserts the template (templates are ProseMirror JSON in the bundle), then
  the seed flag clears. Progress = checked to-dos, reported with each snapshot (`TodoDone/TodoTotal`).
- Permissions: `notes.read`, `notes.write` — granted to Employee.
- Out of scope v1: databases/tables views, page lock, export, move-to UI, public links.
