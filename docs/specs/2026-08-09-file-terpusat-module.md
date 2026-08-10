# File Terpusat Module

## Problem Statement

Employees have no file surface in the portal at all. The company's documents live on a Synology NAS where every employee already has their own account with their own permissions, and staff already reach it the way they always have — a mapped network drive in File Explorer. That works well on the LAN and not at all off it: someone at home, on the road, or on a phone has no route to their files without a VPN.

PRD §3 answers this by specifying MinIO in Distributed Clustered Mode plus a WPF agent doing Smart Sync — selective sync, on-demand placeholders, offline queue. That would stand up a second storage cluster beside the NAS the company already pays for, replace a workflow nobody is complaining about, and introduce sync conflicts as an entirely new class of bug. The user's requirement is the opposite: keep the File Explorer experience exactly as it is, and add a remote path — without a sync engine, "biar ga error."

## Solution

Synology becomes the only file store; MinIO is dropped from the product. On the LAN nothing changes — desktop users keep the mapped network drive, and the portal does not get between Windows and SMB. Off the LAN and on mobile, the portal's backend reaches the NAS over the FileStation HTTP API, **authenticating as the signed-in user with that user's own Synology account**, so the NAS enforces its own per-user permissions on every path the portal touches. A user enters their Synology credentials in the portal once; they are sealed at rest and replayed silently, so there is no repeated login.

Listings are pass-through — nothing about the NAS tree is mirrored into the portal's database. Application blobs (chat attachments, avatars, letter PDFs) are decided but not built here: they will live on a separate NAS share reached by a single service account, so they never touch anyone's personal share.

## User Stories

Browsing and transfer:

1. As an employee off the LAN, I want to browse the NAS from the portal, so that I can reach my files without a VPN or a mapped drive.
2. As an employee on a phone, I want to open the portal and reach the same files I see on my desktop, so that I am not cut off from work when away from my PC.
3. As an employee, I want to see exactly the shares my Synology account can see — my home folder and any team folders I belong to — so that the portal matches what File Explorer shows me.
4. As an employee, I want to open a folder and see its contents with name, size, type, and modified date, so that I can identify the file I need.
5. As an employee, I want a breadcrumb of where I am in the tree, so that I can navigate back up without guessing.
6. As an employee, I want to download a file from the portal, so that I can read it on whatever device I am using.
7. As an employee, I want to upload a file into a folder I have write access to, so that I can contribute from off-site.
8. As an employee, I want an upload into a folder I cannot write to be refused clearly, so that I do not believe a file was saved when it was not.
9. As an employee, I want the portal to refuse a folder my NAS account cannot read, so that permissions mean the same thing in the portal as they do in File Explorer.
10. As an employee, I want a folder with many files to load without stalling, so that browsing a large share stays usable.
11. As an employee, I want an empty folder to say it is empty, so that I can tell an empty folder from a failed load.
12. As an employee, I want a failed NAS call to tell me the NAS is unreachable rather than showing an empty folder, so that I do not mistake an outage for missing files.

Credentials:

13. As an employee, I want to enter my Synology username and password once in the portal, so that I am not logging in over and over.
14. As an employee, I want my credentials checked at the moment I enter them, so that I find out immediately if I typed them wrong.
15. As an employee, I want my stored Synology password kept encrypted at rest, so that a database leak does not hand over my NAS account.
16. As an employee, I want the portal to stop retrying and ask me again when my stored credential stops working, so that it never trips DSM's auto-block and locks me out of my own NAS.
17. As an employee who changed my NAS password, I want to update it in the portal, so that remote access keeps working.
18. As an employee, I want to clear my stored Synology credential from the portal, so that I can revoke that access myself at any time.
19. As an employee, I want my NAS session to be re-established silently when it expires mid-use, so that a long browsing session does not interrupt me.
20. As an employee, I want my NAS session dropped when I sign out of the portal, so that a shared machine does not leave my files reachable.

Desktop and agent:

21. As an employee on the LAN, I want the mapped network drive to keep working exactly as it does today, so that my existing habits and shortcuts are untouched.
22. As an employee, I want the portal to be an addition rather than a replacement for the mapped drive, so that I am not forced to relearn how to open a file at my desk.

Admin and audit:

23. As an admin, I want file access gated by the portal's own permission system as well as the NAS, so that I can switch the module off for a person without touching the NAS.
24. As an admin, I want that permission to follow the existing role hierarchy, so that I grant file access the same way I grant everything else.
25. As an admin, I want every file read and write recorded in the audit trail, so that access is reviewable after the fact.
26. As an admin, I want an audit record to name the user, the path, and the action, so that a review answers who touched what.
27. As an admin, I want to see that a user's stored Synology credential has gone invalid, so that I can tell a NAS account problem from a portal problem when they ask for help.
28. As an admin, I want the portal never to create or modify Synology accounts, so that the NAS remains the single authority over its own users.

## Implementation Decisions

**MinIO is dropped entirely.** PRD §3's storage line is rewritten to Synology. No new container, no storage abstraction, no second backend to operate. The NAS already provides snapshots, RAID, and backup.

**Per-user FileStation sessions.** DSM's auth API is called with the user's own Synology credentials and returns a session identifier; every subsequent FileStation call carries it, so the NAS applies that user's ACLs to every listing, download, and upload. The session identifier is cached in memory keyed by portal user and is never persisted. Portal sign-out drops it.

**A Synology gateway interface, mirroring the HRIS adapter.** File access is split in two, following the pattern already established for HRIS:

- A gateway interface in the Application layer expressing raw DSM operations — authenticate, list shares, list folder, download, upload. The HTTP implementation lives in Infrastructure.
- A file-access service above it owning all the rules: decrypt the stored credential, obtain and cache the session, retry once on session expiry, and mark the credential invalid on an authentication failure.

This keeps every rule worth testing above the HTTP boundary, and puts the HTTP implementation in the same position `HttpHrisGateway` already occupies.

**Stored credentials, encrypted, fail-closed.** A new credential entity holds the portal user, the Synology username, the sealed password, an invalid flag, and a timestamp. The password is sealed with ASP.NET Data Protection under a dedicated purpose — a framework feature already present, so no new crypto code. Credentials are validated by an immediate authentication before they are ever stored.

**On any authentication failure the credential is marked invalid, replay stops, and the user is prompted to re-enter.** This is not a convenience: DSM auto-blocks an account after repeated failed logins, so a service that keeps retrying a stale password would lock a user out of their own NAS. Expiry of a live session is distinct from an authentication failure and triggers exactly one silent re-authentication, not invalidation.

**Data Protection keys persist to the database.** The deployment target is Docker, where the default key folder dies with the container — every stored credential would become permanently undecryptable on redeploy, forcing every user to re-enter their NAS password after each deployment. Keys are persisted to the existing database via Data Protection's EF Core integration, which is already backed up. One line of wiring, one package, one migration, and the deployment question stops mattering.

**Pass-through listing, no index.** Every browse hits the NAS live. No mirror table, no indexer, no staleness. This is a correctness decision rather than a shortcut: an index cannot know per-user NAS ACLs, so a cached listing would leak filenames across users — destroying the isolation that per-user accounts were chosen to obtain. The cost is that portal-side cross-share search is not available; the NAS's own search remains.

**API contract** — a new file group, all authenticated, all gated on a new `files.read` permission:

- List the shares visible to the calling user's NAS account.
- List the contents of a path.
- Download a file at a path, streamed.
- Upload a file to a target path.
- Set the calling user's Synology credential (username + password), validated before storage.
- Clear the calling user's Synology credential.

Rename, move, delete, and portal-side share links are deliberately absent from v1 — desktop users already perform all of those natively on the mapped drive, and remote use is dominated by read, fetch, and contribute.

**The permission evaluator gets its first real consumer.** The standing cleanup item — hierarchical RBAC exists but no endpoint uses it — is resolved here rather than as a separate chore. `files.read` is evaluated through the existing permission evaluator instead of a role check, turning that code from dead into the module's gate. It is seeded to the Employee role so the baseline is unchanged for everyone, and the check runs before any NAS call is made.

**Audit.** File reads and writes append to the existing audit log through the existing pattern, giving the module the passive read-trail the PRD calls for without new infrastructure.

**Application blobs: decided now, built later.** Chat attachments, avatars, and letter PDFs will live on a dedicated application share reached by **one service account**, structurally separate from personal shares and never touched by per-user credentials — visibility decided by portal RBAC and chat or group membership, since only the people a thing is shared with should see it. Nothing consumes this yet: there is no avatar field on the user, and Chat does not exist. No code is written for it in this module. Storage APIs designed without a consumer get redesigned when the consumer arrives; the decision needed locking, and it is locked.

**Web — Files page.** Breadcrumb plus listing, reusing the existing tray, core, and input styling, and the existing client auth wrapper for bearer tokens and silent refresh. A first-visit prompt for Synology credentials and a re-prompt when the stored credential has been marked invalid. No new client plumbing.

**Windows tray agent — Phase 2, not this module.** A thin tray application: mount the drive at login, native toast, `ipop://` deep link, credentials held by Windows Credential Manager. **No sync engine.** Consequently PRD §3's selective sync, offline queue, and Smart Sync placeholders are dropped rather than deferred — a mapped drive is live, so those features have nothing left to describe. This trade is the deliberate point of choosing SMB over a sync client.

## Testing Decisions

A good test here asserts external behavior — what a caller observes and what ends up persisted — not how the service reaches it. Tests must not assert on cache internals, call sequencing, or the shape of DSM's payloads; they assert that a denied user gets denied, that an invalid credential is never replayed, and that a stored credential round-trips.

**One seam, already proven.** The existing SQLite-in-memory xUnit suite driving services directly, with a stub gateway substituted for the real one. Prior art is exact: `HrisSyncServiceTests` defines a private `StubGateway` implementing `IHrisGateway` and drives `HrisSyncService` against a real database via `TestDb`. The file-access service is tested the same way, with a stub Synology gateway that can be told to fail authentication or report an expired session. No new seam, no HTTP interception, no test host.

Modules under test — the file-access service and the permission gate:

- A stored credential round-trips through Data Protection and authenticates successfully.
- **An authentication failure marks the credential invalid, and the next call does not replay it.** This is the DSM-lockout guard — the one rule whose failure actively harms a user — and it is the most important test in the module.
- An expired session triggers exactly one re-authentication and retry, not a loop.
- A credential is not stored when the validating authentication fails.
- Clearing a credential removes it, and a subsequent call reports that credentials are required.
- A user denied `files.read` is refused before any gateway call occurs — asserted by the stub gateway recording no interaction.

`TestDb` needs no change; `AuthorizationPolicyTests` remains the prior art for asserting the permission gate independently of the HTTP surface.

## Out of Scope

- MinIO, in any form.
- Rename, move, delete, and portal-side share links — the mapped drive covers these natively on desktop.
- Indexing, mirroring, or cross-share search over the NAS tree.
- Provisioning or modifying Synology accounts from the portal; the NAS owns its own accounts and the portal only holds the credential a user chooses to give it.
- Smart Sync, selective sync, offline queue, and on-demand placeholders — dropped along with the sync engine.
- The Windows tray agent (Phase 2) and the Android client.
- The application blob store implementation, which ships with Chat.
- Malware scanning on upload (PRD §3) — deferred until there is an integration target.

## Further Notes

- The mapped drive remains the primary desktop experience. The portal's file surface is the remote and mobile path, not a replacement, which is why v1 is deliberately browse, download, and upload and nothing more.
- Per-user Synology accounts make the NAS the authority on file permissions. The portal's `files.read` is an on/off switch for the module, not a second ACL system that would have to be kept in sync with the NAS.
- Storing NAS passwords is a real trust boundary the portal did not previously have. It is accepted because the alternative — re-authenticating every session — was explicitly rejected as too disruptive. The fail-closed invalidation rule and encryption at rest are what make it acceptable, and neither is optional.
- Dropping MinIO removes a PRD requirement rather than postponing one. PRD §3 should be amended so the document and the system agree.
