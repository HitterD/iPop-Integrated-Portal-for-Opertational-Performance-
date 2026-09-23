# Apple-style Chat & Mail redesign — decisions (2026-09-23)

Reference: `docs/mockups/ipop-apple/{chat,mail}.html` + `app.css`.
Backup of pre-change tree: `.scratch/backup/pre-apple-redesign-20260923-122635/`.

## Decisions

| # | Topic | Decision |
|---|-------|----------|
| 1 | Sidebar | Never auto-collapse on /chat or /mail. Remove `AutoCollapseForChatMail` + `.chat-mail-active` CSS. Manual collapse still works. |
| 2 | Scope of Apple look | Chat + Mail pages (and chat sub-pages) only. Shell/sidebar visuals unchanged. |
| 3 | Topbar | Becomes an Apple toolbar on Chat/Mail: large title + summary subtitle left, page actions + status/theme/bell right. Drop "Live" pill + crumb there. |
| 4 | Language | English everywhere — web UI (all pages, sidebar group labels) **and** API error message strings. |
| 5 | Profile card | Chat: clicking avatar/name opens the right detail panel (no overlay/blur). DM panel shows contact card (NIK, email, ext, location, org relation, favorite). Directory/Admin keep the sheet, restyled Apple (light dim, no blur). |
| 6 | Chat detail panel | Single scroll column: hero (avatar, name, status, 3 round actions Message/Mail/Call) → grouped inset lists: Contact info or Members, Pinned, Files & Media grid + "See all", Calendar, options (Mute / Read receipts / Archive / Leave). Stat cards removed. Header pin/paperclip icons scroll to their section. |
| 7 | Bubbles | iMessage: incoming light-gray no border, outgoing accent green, 18px radius, consecutive messages tightened, time on hover, "Read" under last outgoing only. |
| 8 | Mail actions | Per mock image 5: toolbar = segmented All/Unread/Starred + Archive/Delete/Mark read. Reading pane bar = Reply (primary), Forward, Archive, Delete, Star, Export PDF, Print, ⋯ menu (Pin, Mark unread, Move to Junk). Advanced → folder footer. Sync → refresh icon. Pager → "Load more". |
| 9 | Font | Inter, self-hosted woff2 (OFL), whole app. |
| 10 | Chat header | Topbar "Messages" + "N unread · M conversations"; room column has no title, just search + compose. |
| 11 | Narrow screens | <1440px: chat detail panel becomes overlay sheet. <1280px: mail folder column collapses to icons. Sidebar stays expanded. |

## Backend notes (report to user)
- Profile "Relasi Organisasi" manager avatar is hard-coded "M" + always-online dot — needs manager NIK/presence from API to be real.
- Ekstensi shows "—" when HRIS has no Extension; data issue, not UI.
- `ChatRoomDto` has no created date, so the mockup's "Created …" line in the group hero can't be shown without a backend field.

## Execution
- Foundation (done first): sidebar auto-collapse removed, Apple toolbar mode in `MainLayout` (`SetSubtitle` + `SectionOutlet "topbar-actions"`, `.apple-toolbar`/`.tb-actions`/`.tb-search` in `shell/topbar.css`), Inter font.
- Then three parallel tracks with disjoint file ownership: Chat (+ ProfileDrawer), Mail, English translation of the rest of the web app + API user-facing strings.
