# Mail read receipts, recipients header, EPop reading layout — design

Grilled with the user 2026-10-14 (Q1–Q13; Q2 overridden, rest "sesuai rekomendasi"). Approved mockup:
`docs/mockups/mail-read-receipts-epop.html` ("ok sih sudahan lanjut"). Base: prod b7c7083 + mail hotfix (Reply, Quick Reply
emoji placement, duplicated Cc). UI text English. NIK never shown.

## 1. Read receipts (chat style)
- Row under every message header: `✓✓ Read by 12 of 41` + avatar stack of readers → popover/sheet "Read receipts" with tabs
  **Read by · N** / **Not yet · M**, search (name/department), people rows (avatar, name, department · site, "Read · Wed 14 Oct, 09:12"),
  groups expandable ("ICT · SJA Karawang — 3 of 9 read"). Footer: "Everyone on this mail sees the same list. Bcc recipients are never shown."
- Single internal recipient: `✓✓ Read 09:12` / `✓ Delivered`. External-only address: "Read status not available".
- Collapsed thread items: small ✓✓ (all read) / ✓ icon only.
- Phone: bottom sheet.

## 2. Who sees it
- **All To/Cc recipients and the sender** see the same receipts (user override of sender-only).
- **Bcc**: Bcc recipients' read state visible to the sender only; excluded from counts/names for everyone else. A Bcc recipient sees
  the To/Cc receipts like any recipient.
- **Mass mail**: non-senders see only the count "Read by 412 of 1000"; names only for the sender (existing mass panel).

## 3. What counts as Read (strict)
- A receipt is recorded only when the message is opened in the reading pane (thread item expanded/visible counts for that
  message), the tab is visible AND focused, for ≥ 2 s continuously. Recorded once (first time).
- Mark as read / Mark all read / notification click-through without opening / global search / sync from another device clear only the
  user's own unread badge (existing IsRead) and never record a receipt.
- Stored separately from the personal unread flag: new `MailRecipient.SeenAt` (receipt) vs existing `IsRead/ReadAt` (badge).
- Old data: receipts shown only from the new field; mails sent before the feature date show "Read status not tracked before 14 Oct"
  (cut-off = a stored feature start timestamp, not hard-coded date text).

## 4. Recipients header
- One line each for To and Cc: "you" first, then names, then group chips, then `+N`. Fix the duplicate Cc (hotfix).
- `+N` → panel "Recipients · 42 · To 39 · Cc 3" with search, **People** (two columns, To/Cc tag) and **Groups & departments** grouped
  by site with counts.

## 5. EPop archive mail reading layout (and any rich HTML mail)
- Body in a centred reading column (max ≈ 760 px). Normalise font-size/line-height (≈15 px / 1.6) and font-family to iPop; keep
  colours, bold/italic, links. Headings capped to sensible sizes.
- Images scale to the column width; click opens full size.
- Quoted older mail (blockquote / Outlook "From:… Sent:…" divider / thick coloured left borders) → iPop quote style, collapsed under
  "Show quoted text".
- Toggle "Original formatting" (per message, remembered for the session) shows the sanitized original styles.
- Applied as scoped CSS + render-time transform; the stored body is never rewritten.

## 6. List
- Remove the "Arsip EPop" badge from list rows; show a small archive-box icon next to the date with tooltip "Imported from EPop".
  Sidebar label stays.

## 7. QA scope (after build)
- Full Mail QA on a copy DB: list, thread, Reply/Reply all/Forward/Quick Reply, compose + attachments + emoji, drafts, schedule,
  search, labels, archive/trash, mass mail, phone. Fix bugs; collect other UI ideas as a list for the user.
- Mockup approved ("ok sih sudahan lanjut"). The hotfix may ship as soon as it passes QA; the feature deploys after QA when the
  user says OK.
