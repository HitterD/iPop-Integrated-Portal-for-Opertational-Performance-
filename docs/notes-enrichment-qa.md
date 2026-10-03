# Notes enrichment verification — 2026-10-03

Branch: `sweep-mcn`. Not deployed to production. No database migration.

## Implemented

- Task items support photos/files/sub-items, assignee and due date, completion date, and list progress.
- Images support percent width, alignment, inline captions, view/download/delete, galleries and lightbox navigation.
- Tables support size picker, row/column operations, header toggle, cell colour, column resizing and horizontal overflow. Cells contain paragraphs rather than arbitrary blocks.
- Bookmark and video embed slash commands; empty-line URL paste menu. YouTube uses click-loaded youtube-nocookie frames; direct HTTPS mp4/webm supported.
- Inspection checklist, Weekly report and Incident report templates; server validation allows their keys.
- Internal Notes bookmark resolution uses Notes access checks; inaccessible notes expose no title/id.
- Cache scoped to editor host with expiry; collaborative task picker targets map through transactions; native upload anchors are captured before the picker opens; galleries preserve attributes and all items; owned lightboxes/resize handlers are cleaned up.

## Verification

- WebHost build: 0 errors, 0 warnings in final recorded build.
- .NET regression: 1546 passed, 0 failed, 0 skipped.
- Existing root JavaScript tests: 7 passed.
- Persistent Notes editor tests: 14 passed (`npm --prefix tools/notes-editor test`). All eight templates are schema-valid; galleries >60, attribute roundtrip, paragraph-only cells and position mapping are covered.
- Browser QA on copied PostgreSQL DB and separate Redis: checklist photo/due/completion-date toggle, table row addition, bookmark insertion, reload persistence; image width/alignment/caption; table grid/colour; people assignment; history preview; gallery/lightbox navigation; click-loaded YouTube; all new templates.
- Desktop 1440px and mobile 390px screenshots retained in `.scratch/qa/`; no browser page errors in successful runs.
- Failed intermediate runs exposed an empty-text template bug (fixed), incorrect test selectors and QA CORS/static-asset-manifest setup. Later runs passed.

## Limits / follow-up

- Native file URLs still fall back to generic internal cards; no new NAS file metadata resolver was introduced.
- Direct mp4/webm playback, paste-menu interactions, drag-resize gestures and two-client live collaboration were not end-to-end browser tested. Position mapping and restricted schema have automated coverage, not a claim of full multiplayer QA.
- Browser emitted existing Tailwind CDN warnings and a TipTap TextSelection warning while opening report templates; no pageerror or failed rendering. These are not silently represented as warning-free browser runs.
- No production deploy, secrets/config changes or schema migration. Owned QA API/WebHost, DB and Redis were removed; temporary API launch/client config removed. Screenshots/scripts/logs remain untracked under `.scratch` and must not be committed.
