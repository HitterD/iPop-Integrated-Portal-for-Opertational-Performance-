# iPop audit 2026-11: before / after

Source of every number: GitHub Actions runs on branch `IT18/audit-2026-11` (RED = same test on the old behaviour,
GREEN = same test on the fix). Nothing here is a production measurement. The batch is NOT deployed to either prod target.

| Area | Before (RED evidence) | After (GREEN evidence) | Commit |
|---|---|---|---|
| Chat attachment GC | forwarded file deleted with its source | file kept until last reference ages out | CI 3310/3310 |
| Chat send / forward / thread | message could commit with no durable push intent | intent saved in the same SaveChanges | 3313 / 3315 |
| Chat replay | retry after a crash never re-broadcast | replay re-offers realtime; thread events deduplicated client side | 3316 |
| Chat forward | replay returned content after membership was revoked; key derived from the minute | authorization first; explicit client key; conflict on a different source; concurrent retry returns the winner | 3318 to 3321 |
| Calls grace timer | 4 tests relied on 100-800 ms real sleeps | injectable clock, no sleeps | 3322 |
| Auth two tabs | 1 test failed: stale token wiped the live one | tab adopts the rotated token | 3323 |
| Auth hub reconnect | hubs sent the cached (expired) token | token renewed when under a minute remains | 3325 |
| Login throttle | per-account lockout only | per-address counter (bounded); OFF by default | 3328 |
| Client IP in auth | raw peer = NPM for the whole office | forwarded browser address via trusted proxy | 3329 |
| Notes export | `javascript:` href survived in links and bookmarks | URL allow-list; CI now runs the Notes tests and checks the bundle is not stale | Notes job |
| Notes revoke | 2 failed: sub-page tab kept live content | eviction re-checks every live page; also after trash/move | 3332 |
| Mail lists | 2 failed: whole Body column selected per row | bounded `Substring(0, 4000)` prefix; snippet text unchanged | 3335 |
| Projects timers | 2 failed: timer kept running on a deleted task | closed on delete (same rules as Stop) | 3337 |
| News video | 2 failed: 6 h ticket was the whole permission | each stream/poster re-checks the viewer | 3339 |
| Tickets webhook | 1 failed: failed send consumed the receipt, notification lost | receipt released on failure; iDesk redelivers | 3340 |

Test count: 3,309 passed on the first CI run of this phase's chain (Task 1) to 3,340 on the last run; every count in the table is the Passed/Total line of the cited CI run. The last run is fully green.

## Not measured, not done (do not read this report as completion)
- No cold/warm p50/p95, no load test, no payload-bytes measurement of the Mail change on real PostgreSQL.
- No staging runtime or crash/replay proof for any row above; CI (SQLite, in-process) only.
- Open rows: Shell (profile single-flight, photo invalidation, API JSON compression, heartbeat), Files, Mail drafts/inline media,
  Notes compaction/snapshots, Projects includes/retention, News feed SQL, Tickets durable inbox/leases, Admin, Calls crossing race,
  Infra (SMB, key rotation, ImageSharp licence).
- Per-address login throttle ships disabled (`Auth:LoginThrottle:Enabled=false`) until the client-IP chain is verified on prod.
- One test (`MeetingRoomsFinalFixTests.Worker_ends_an_overrun_after_a_missed_tick`) failed once near office midnight on an
  unrelated run; it passed on every rerun. Treated as a clock-sensitive flake, not investigated further.
