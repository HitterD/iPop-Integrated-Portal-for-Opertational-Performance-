# The Hub — design (v1)

Mockup: `docs/mockups/the-hub.html` (approved direction, rounds 1–11, grilled 2026-11-10 … 2026-11-17).
Origin: GM case (packaging bought for 5 years, product discontinued next year) and Pak Sonny's ask for a
place where departments raise topics instead of meeting, to break silos.

## Goal

A confidential, cross-department topic forum. Anyone opens a topic, invites at least one person from
another department, talks it through in a Threads-style thread, and closes it with a written answer.
Follow-up work goes to Projects.

## End-to-end flow

1. **Start** — `New topic`: kind (Problem, Need clarity, Idea, Product change, Before you buy, Other),
   title, "What do you need from them?", answer-by date, people (≥ 1 from another department), files.
   Send stays off until the rule is met.
2. **Told** — every invited person gets an in-app notification + Web Push / Notifier / phone push.
   The nav badge counts topics waiting for *my* answer.
3. **Discuss** — replies (one level of nesting), files, "Ask" someone (a tracked question).
   Status moves **New → Ongoing** automatically at the first reply from someone other than the starter.
4. **Nudge** — `Remind` on a pending question (rate-limited, 1 per question per hour). A daily worker
   reminds after 2 working days and tells the person's department head after 3 (head gets a reminder
   with no topic text).
5. **Solve** — `Mark as solved`: the answer in 1–2 sentences + optional follow-up tasks (title, person,
   date, project the person can write to) → Projects tasks. Status **Solved**; everyone is told.
   `Reopen` brings it back to Ongoing.

## Rules

- **Confidential**: only members can read a topic, its replies and files. No GM/head override.
- Minimum: the starter + ≥ 1 member from a different department (Department of the user; a user with no
  department counts as their own "department").
- Anyone in the topic can reply, ask, remind, add people, mark solved and reopen.
- People added later see the whole history; everyone in it is told.
- Status is derived: `Solved` if `SolvedAt != null`, else `Ongoing` if any reply by a non-starter, else `New`.

## Data (Postgres, EF)

- `HubTopic` (Id, Kind, Title ≤200, Need ≤4000, DueDate, CreatedById, CreatedAt, UpdatedAt, LastActivityAt,
  SolvedAt?, SolvedById?, SolvedSummary? ≤2000, IsDeleted)
- `HubMember` (TopicId, UserId, AddedById, AddedAt, LastReadAt?) — PK (TopicId, UserId)
- `HubPost` (Id, TopicId, AuthorId?, ParentId?, Body ≤8000, Kind "reply"|"system", CreatedAt)
- `HubAsk` (Id, TopicId, PostId, ToUserId, AskedById, Question ≤500, CreatedAt, AnsweredAt?, AnsweredPostId?,
  LastRemindedAt?, HeadToldAt?)
- `HubAttachment` (TopicId, PostId?, AttachmentId) — files on the NAS through `IAttachmentStore` (area "hub")

## API (`/api/hub`, permission `hub.use`, Employee role gets it)

`GET /topics?filter=mine|new|ongoing|solved&q=` · `POST /topics` · `GET /topics/{id}` (marks read) ·
`POST /topics/{id}/posts` · `POST /topics/{id}/members` · `POST /topics/{id}/asks/{askId}/remind` ·
`POST /topics/{id}/solve` · `POST /topics/{id}/reopen` · `POST /topics/{id}/files` · `GET /people?q=`.
Realtime: `hubChanged` (topicId) to members; badge key `hub`.

## Web

`/hub` and `/hub/{id}`: topic list on the left (For me · New · Ongoing · Solved, search, kind filter),
Threads-style topic on the right (opening post, Next-step card, Your-turn box, composer, replies, Details).
Sheets for New topic and Mark as solved. Micro-interactions from the mockup (busy → tick buttons,
Undo after solve, rising rows, count bumps), all off under `prefers-reduced-motion`. Sidebar entry
"The Hub" in Workspace with a badge.

## Not in v1

Overview (dept heat map, answer times), "Prepared by iPop" from Oracle, Business Plan money tools,
phone-specific layout (the page is responsive), search provider for Ctrl+K, Notifier reply-from-toast.
