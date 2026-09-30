# Mail — Office-grade composer, Apple-style UI, list/reading-pane polish

Date: 2026-09-27 · Branch: v6-port (ipop-rescue worktree) · Status: approved in chat, pending spec review

## Goal

Make composing mail feel like editing in Word/Outlook (full formatting, tables, clean paste from
Office) while the Mail page and composer look like Apple Mail on macOS: clean, one compact toolbar,
rarely-used tools in popovers. Fix the functional bugs found in the survey.

## Decisions (from the user)

| Topic | Decision |
|---|---|
| Editor engine | Jodit (MIT), vendored under `wwwroot/lib/jodit/` — works offline on the intranet |
| Toolbar style | Apple design — single compact toolbar + popovers, not an Office ribbon |
| Composer window | Apple design — sheet-style popup, full-screen toggle, minimise to dock |
| Mail page scope | Bug fixes, list + reading pane, attachment preview, keyboard shortcuts |

## 1. Editor

- Vendor `jodit@4.x` `es2021/jodit.fat.min.js` + `jodit.min.css` into `wwwroot/lib/jodit/`
  (with its LICENSE). Loaded lazily by `ipop.js` the first time a composer opens; the rest of
  the app does not pay for it.
- `MailRteEditor.razor` keeps its public parameters (`Value`, `ValueChanged`, …) so
  `MailComposer`, quick reply and templates keep working. Internally it hosts a Jodit instance
  with Jodit's own toolbar **hidden**; our Razor toolbar calls a small JS bridge
  (`ipop.rte.exec(id, cmd, arg)`, `ipop.rte.state(id)`), and Jodit reports selection state back via
  `[JSInvokable] OnState(...)` (throttled) so buttons show active state like Word.
- Main toolbar (one row, wraps on narrow widths): undo/redo · font family · size in pt (8–72) ·
  B I U S · text colour · highlight · align left/centre/right/justify · bullets/numbers ·
  indent/outdent · link · table · image · "Aa" popover.
- "Aa" popover: paragraph style (Normal, Heading 1–3, Quote, Code) · line spacing (1.0/1.15/1.5/2.0)
  · sub/superscript · horizontal rule · format painter · clear formatting · find & replace.
- Tables: grid picker for size; Jodit's table plugin for add/remove row/column, merge/split,
  column resize. Rendered with visible 1px borders so they survive in recipients' view and PDF.
- Paste: Jodit's paste-from-Word cleaning, `askBeforePasteFromWord: false`, keep formatting
  (bold/italic/colour/tables/lists), strip Office-only XML/classes.
- Images: paste/drag inserts as `data:` URI (≤ 2 MB each after paste), resizable.
- Link and table dialogs are iPop popovers; no browser `prompt()`.
- `rteSetHtml` no longer wipes history on template/signature insertion (use Jodit's
  `s.insertHTML`).
- The placeholder parameter is honoured.

## 2. Sanitizer (server)

`MailHtml` allow-list additions:

- `data:` scheme for `img[src]` only, restricted to `image/png|jpeg|gif|webp`; per-image cap 2 MB,
  body cap enforced by existing request limits.
- `style` properties Jodit emits: `font-family`, `font-size`, `color`, `background-color`,
  `text-align`, `line-height`, `margin-left`, `padding-left`, `width`, `height`, `border*`,
  `vertical-align`, `text-decoration`.
- Table attributes `colspan`, `rowspan`; tags `sub`, `sup`, `hr`, `mark`.
- Everything else stays blocked (scripts, event handlers, `javascript:`, `data:` outside `img`).

## 3. Composer window (Apple Mail style)

- Sheet look: white surface (dark theme: elevated surface token), 12px radius, soft shadow,
  translucent header with `backdrop-filter: blur`.
- Header: title/subject echo · priority, label and meeting as small icon toggles with popovers ·
  window buttons (minimise, full-screen, close).
- Address block: borderless rows `To`, `Cc`, `Bcc` (Cc/Bcc revealed on demand), `Subject`,
  hairline separators.
- Footer: Send (split: schedule send) · Attach (split: from computer / from Synology) ·
  signature · "Saved hh:mm" draft indicator · Discard.
- States: popup (default, ~ 880×640), full-screen, minimised to dock.
- Ctrl/⌘+Enter sends; Esc minimises (confirm only if content would be lost on close).

## 4. Mail page

- **List column** widened to ~420px (min 360, resizable not required). Hover actions become an
  absolutely-positioned overlay at the row's right edge so they no longer reserve width.
  Row layout: sender (bold if unread) + date right · subject · 2-line snippet · icons
  (attachment, star, pin, labels).
- **Count**: "Showing N of Total" uses the server total, not the loaded page length.
- **Reading pane**: header with avatar, sender, collapsible recipients, date; icon actions
  Reply / Reply all / Forward / Archive / Delete / More. Body in a max-width readable column.
- **Attachments**: cards with thumbnail (existing `/attachments/{id}/thumbnail`) or file-type icon;
  image and PDF open in an in-app preview overlay (`?inline=true`) with download button.
- **Keyboard**: C new · R reply · A reply all · F forward · Del/Backspace delete · E archive ·
  J/K next/previous · Esc close · ? shortcut sheet. Ignored while focus is in an input, textarea,
  select or contenteditable, or while a modal is open.
- Remaining `prompt()` (reschedule) replaced with an iPop date-time popover.

## 5. Bug fixes

1. 'c' shortcut firing while typing → guarded (see keyboard).
2. `ipop.triggerFileInput` looks for `.composer-wrap` → fix selector so Upload from Computer works.
3. Labels chosen in the composer are dropped → `ComposeMailRequest` gains optional
   `IReadOnlyList<Guid>? LabelIds`; the service applies them to the sender's own copy (labels
   are per user; only labels owned by the sender are accepted).
4. `OpenNew` leaks meeting/label/priority state → full reset in one place.
5. Draft autosave keeps running after close → timer cancelled on close/discard/send; recipient,
   priority and subject changes also mark the draft dirty.
6. Forward drops original attachments → forward copies the original's attachment rows
   (server-side, owner check) and the composer lists them as removable chips.
7. "Move to Junk" moves to Trash while claiming Junk → there is no Junk folder; the item is
   removed from the More menu (no new folder in this scope).
8. Mark-read issues one request per message → new `POST /api/mail/bulk/read-ids`
   (`BulkIdsRequest`) used by the toolbar.

## Out of scope

Junk folder, external SMTP, collaborative editing, spell-check beyond the browser's, mail rules UI.

## Testing

- xUnit: sanitizer (data-image allowed/blocked MIME/oversize, Word paste markup, style
  allow-list, script stripped); labels applied on send only for owned labels; forward copies
  attachments; bulk read-ids.
- bUnit: shortcut guard in inputs; composer reset between opens.
- Full `dotnet test` (baseline 492 green) and API+Web build.
- Playwright (read-only; all mutating `/api/` calls blocked, no real send): 1920 and 1280,
  light and dark; list truncation, reading pane, attachment preview, composer formatting,
  table insert/edit, paste of Word HTML via clipboard event, full-screen/minimise, shortcuts,
  console errors.

## Safety

No commits. Back up every touched mail file to `.scratch/backup-mail/` first; `Mail.razor`,
`MailService.cs` and `css/v6/mail.css` already carry other sessions' uncommitted edits — edit in
place, never overwrite wholesale.
