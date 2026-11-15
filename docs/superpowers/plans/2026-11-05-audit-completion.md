# iPop Audit Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every accepted audit item with implementation and evidence, explicitly distinguishing partial fixes, disproven findings and external blockers.

**Architecture:** Work sequentially in the existing audit worktree. Correct regressions in previously shipped changes first, then complete module-specific slices. CI runs tests and cluster staging runs browser/runtime checks; production changes require a green verified batch and rollback artifacts.

**Tech Stack:** .NET 10, EF Core/PostgreSQL, Blazor WASM, SignalR, Synology, GitHub Actions, k3s/Argo.

**Spec:** User-approved full audit decisions in `C:/Users/IT12/.claude/projects/F--Program-Bagas-SynologyDrive-iPop-dotnet-10/memory/full-audit-2026-11-decisions.md`; accepted reports in this conversation. This document is the durable remaining-work ledger.

## Global Constraints
- Hall, Android and Notifier are excluded; meeting-room Calls remain included.
- No heavy local builds/tests; CI and cluster jobs run sequentially.
- Never delete a file still referenced by another attachment, including forwards.
- A commit, static inspection or green CI alone is not runtime proof.
- No paid dependency purchase, secret disclosure or DNS cutover inferred from general approval.
- Preserve rollback artifacts and verify executable path/PID before any production restart.

## Review Focus
- Deleted source messages with forwarded attachments: the forward must remain downloadable, including its thumbnail.
- Crash between message commit and notification dispatch: recipients eventually receive exactly one durable notification intent.
- Access revocation through a parent or room: every affected Notes connection loses future live content.
- Two tabs/retries crossing time boundaries: authentication and forwarding remain correct without minute-bucket identity.
- NAS outages/restarts: operations and uploads remain retryable without silent success or lost files.

---

## Closure ledger

Statuses start OPEN/PARTIAL, never DONE without linked evidence. This ledger supersedes earlier blanket completion claims.

| Area | Remaining deliverable | Status |
|---|---|---|
| Chat GC | Shared-file reference safety, bounded safe paths, retry semantics | OPEN |
| Chat delivery | Transactional message + durable notification intent; crash/replay proof | PARTIAL |
| Chat forward | Client-generated stable idempotency key and concurrent retry handling | DONE in CI: authz-before-replay 8a57dff (3318/3318), key 9174c6b (3319), conflict a7271b2 (3320), race b156754 (3321 GREEN 43662027984); staging runtime proof pending |
| Chat receipt | Batch actual new receipts, bounded broadcasts, load measurements | PARTIAL |
| Chat rendering | True windowing/virtualization; older/reply/forward jump and scroll parity | PARTIAL |
| Chat thumbnails | Correct keyed locking, no redundant full buffering, bounded cache | PARTIAL |
| Auth | Two-tab refresh, hub refresh after expiry, permanent-vs-transient outcomes | PARTIAL: two-tab RED 43666417283 (1 fail) -> 94eb9c4 GREEN 43666861498 (3323/3323); hub token RED 43667292128 (build) -> 8d8332d GREEN 43667585020 (3325/3325); transient-vs-permanent already covered by AuthRefreshTransientTests; browser two-tab proof on staging pending |
| Shell | Login throttling, profile single-flight, compression/bundles, photo invalidation, heartbeat | PARTIAL: LoginThrottle (bounded per-address counter) RED = missing type, GREEN 38797d3 CI 43668128476 (3328/3328); wired into /api/auth/login but OFF by default (Auth:LoginThrottle:Enabled=false) because behind NPM every user shares one remote address - enable only after the client-IP chain is verified (ClientAddress.Of already trusts X-Real-IP from NPM 192.168.2.3, but AuthEndpoints.Ip still uses the raw peer). Compression: static assets already Brotli on prod (blazor.webassembly.js 16.7 KB br, index gzip); API JSON not compressed by the app, proxy behaviour unverified (auth required) -> OPEN, needs an HTTP test host. Profile single-flight, photo invalidation, heartbeat: OPEN |
| Files | Restart recovery/atomic claims, picker route, event privacy, overwrite confirmation | OPEN |
| Mail | Body preview projection, upload timings, abandoned drafts/inline media, tab-close draft durability | PARTIAL: body projection - list and thread queries selected the whole Body (+tracked entities) for a 120-char snippet. Same test RED on pre-fix code 24a3170 (CI 43673584304, 2 fail) and GREEN on fix 1e42d22+57c75e3 (CI 43673621656, 3335/3335); snippet text for ordinary HTML unchanged (equality test). Real PostgreSQL bytes-per-page not yet measured. Drafts, abandoned inline media, tab-close durability OPEN |
| Mail cache | Size enforcement, shared/user access semantics, stream disposal and cleanup races | PARTIAL |
| Calls | True simultaneous crossing-call race, client media and failed-recording recovery | PARTIAL: CallConnections grace now deterministic (131f4a2/bc3a7db, CI 43665002940 3322/3322); crossing-call race + media + recording OPEN |
| Notes | PDF export injection repro/fix, ancestor/room revocation, update compaction and snapshot projections | PARTIAL: export/print HTML injection REPRODUCED (CI 43669185210: `<a href="javascript:alert(document.cookie)">` and bookmark kept) -> safeUrl allow-list 1c0d85b + rebuilt bundle c0e8d9e, GREEN 43671075395 (Notes editor tests, bundle-matches-sources, .NET 3329/3329); new CI job runs tools/notes-editor tests (never ran before) and fails on a stale bundle. Revocation: RED 43671721592 (2 fail; a tab on a sub-page kept live content after the parent share was removed) -> 47fd3e1 GREEN 43672236258 (3332/3332): eviction now re-checks every page with live connections and also runs after trash and move. Compaction, snapshot projections OPEN |
| Projects | Include projections/split queries, deleted-task timer stop, time-entry retention, due-reminder starvation | PARTIAL: deleted-task timer - RED 43674111547 (2 fail: timer still running after delete; restore resurrected it) -> 3456665 GREEN 43674487163 (3337/3337), fix is in MarkDeletedAsync so single and bulk delete both close it; due-reminder starvation already covered by A_due_reminder_is_sent_even_with_600_future_ones_queued. Include projections, time-entry retention OPEN |
| News | Revoked-video access, feed SQL, scheduled revision after pin runtime test | PARTIAL |
| Tickets | Durable webhook inbox/outbox, leases, retry/crash/idempotency proof | PARTIAL |
| Admin | User-facing safe undo, lookup cache correctness, paged queries, cancel/shutdown sync tests | PARTIAL |
| Infra | Real client-IP chain, SMB authentication diagnosis/restore proof, encrypted key rotation/migration, ImageSharp replacement/licensing | OPEN |
| QA | Complete module coverage, cold/warm p50/p95, sustained sessions, failure injection, before/after report | OPEN |

### Task 1: Shared Chat attachment lifetime

**Files:** `src/IPop.Infrastructure/Chat/ChatAttachmentGcWorker.cs`, `tests/IPop.Tests/ChatAttachmentGcTests.cs`.

**Interfaces:** preserve `RunOnceAsync(IPopDbContext, IFileAccessService?, string?, DateTime, ILogger, CancellationToken) -> Task<int>`.

- [ ] Add regression test: deleted original and live forward share original/thumbnail paths; GC removes original row, preserves both files and the forward row; after last reference ages out, both files are removed.
- [ ] Run regression on CI before implementation; require FAIL on preserved file assertion.
- [ ] Add per-server/path reference checks against original and thumbnail columns before file deletion; safely remove obsolete row when file remains referenced. Save each successful row removal so the last reference can be collected in the same pass. Guard local paths with a directory boundary.
- [ ] Run CI regression + complete tests; require PASS. Verify staging on synthetic local attachment data, not production files.
- [ ] Commit code, tests and evidence together; mark ledger only after stage verification.

### Task 2: Atomic durable Chat delivery

**Files to inspect:** `ChatService.Messages.cs`, `ChatNotifier.cs`, `PushQueue.cs`, existing push job/entity mappings and tests.
- [ ] Trace all send/thread/forward/replay paths and record transaction/cancellation boundaries.
- [ ] Add crash/replay tests that fail when a persisted message has no durable intent or creates duplicates.
- [ ] Put intent into the same SaveChanges/transaction as the message, with a durable uniqueness constraint; separate best-effort realtime from durable dispatch and avoid per-call concrete service casts.
- [ ] Verify migration on copy DB and CI, then simulate reconnect/crash in staging and record received intent counts.
- [ ] Commit the independently verified slice and update the ledger.

### Task 3: Complete remaining modules sequentially

Each ledger row becomes its own detailed implementation slice only after inspecting current interfaces (do not invent signatures from an earlier report).
- [ ] For each row, record exact files, current behavior, reproducible input, expected result and current evidence.
- [ ] Write a failing regression first; run remotely, implement the smallest fix and run the same regression again.
- [ ] Run full CI, an independent focused review and staging runtime proof before closing the row.
- [ ] Treat disproven findings as DISPROVEN with evidence; do not implement unnecessary changes.
- [ ] Keep unresolved environment/license items BLOCKED with a concrete safe fallback, never silently DONE.

### Task 4: Release and final evidence report
- [ ] Verify all ledger rows DONE/DISPROVEN or explicitly BLOCKED; report blocked rows plainly.
- [ ] Run sequential cold/warm and bounded-load cluster tests with p50/p95, errors, payloads and resource usage.
- [ ] Recheck Windows/cluster deployment source parity and migrations; preserve backups and approved release process.
- [ ] Deploy only the verified batch to both authorized targets; verify functional authenticated flows, not login-only smoke tests.
- [ ] Final report links commit, regression, runtime evidence and deployment revision for every item; no blanket completion without coverage.

## Execution
User reaffirmed on 2026-11-05: "yauda selesaikan dan benar benar kamu cek" and "sisanya sesuai rekommu". Execute sequentially in this session using the existing worktree, with no repeated product-choice questionnaire. Production remains unchanged while regressions are established on CI/staging.
