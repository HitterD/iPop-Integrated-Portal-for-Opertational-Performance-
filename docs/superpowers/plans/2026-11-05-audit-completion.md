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
| Chat forward | Client-generated stable idempotency key and concurrent retry handling | PARTIAL |
| Chat receipt | Batch actual new receipts, bounded broadcasts, load measurements | PARTIAL |
| Chat rendering | True windowing/virtualization; older/reply/forward jump and scroll parity | PARTIAL |
| Chat thumbnails | Correct keyed locking, no redundant full buffering, bounded cache | PARTIAL |
| Auth | Two-tab refresh, hub refresh after expiry, permanent-vs-transient outcomes | OPEN |
| Shell | Login throttling, profile single-flight, compression/bundles, photo invalidation, heartbeat | OPEN |
| Files | Restart recovery/atomic claims, picker route, event privacy, overwrite confirmation | OPEN |
| Mail | Body preview projection, upload timings, abandoned drafts/inline media, tab-close draft durability | OPEN |
| Mail cache | Size enforcement, shared/user access semantics, stream disposal and cleanup races | PARTIAL |
| Calls | True simultaneous crossing-call race, client media and failed-recording recovery | PARTIAL |
| Notes | PDF export injection repro/fix, ancestor/room revocation, update compaction and snapshot projections | OPEN |
| Projects | Include projections/split queries, deleted-task timer stop, time-entry retention, due-reminder starvation | PARTIAL |
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
