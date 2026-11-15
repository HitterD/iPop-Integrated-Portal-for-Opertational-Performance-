# iPop Notifier audit, November 2026 (1.4.7 → 1.4.8)

Base: `github/main` b9808c8 (Notifier 1.4.7). Branch `IT18/notifier-audit`. Remote (AnyDesk-like) was not audited.
Method: code review of tray app + server Notifier endpoints, build, 176 Notifier tests, a harness that runs the real
`IPopClient` + `Feed` against a mock server, and the PC's own `app.log`.

## Bugs fixed

| # | Sev | Before | After | Proof |
|---|-----|--------|-------|-------|
| H1 | high | A session rejected by the server while the live connection reconnects hung the app for good: tray "connecting…", no popups, every call "Signed out." until restart. Seen on the build PC 2026-10-28 13:28–14:22 (89 log lines). | Refreshes never wait for sign-in/sign-out/switch; a rejected refresh schedules the sign-out. | Harness, same mock server, 1.4.7 code vs 1.4.8 code: **1.4.7 stays signed in after 60 s and a later SignOutAsync HANGS; 1.4.8 signs out and SignOutAsync returns.** + 4 unit tests (`NotifierAuthGateTests`). |
| H2 | high | "Snooze 5 min" on a meeting reminder behaved like Dismiss: the server hides it, nothing brings it back. | The app shows the same reminder again after 5 minutes (cleared on sign-out/switch). | Build + code path only. **Not tested live.** |
| M1 | med | A failed update check was retried every minute all day (40+ tries per hour in the log). | Back off 1 → 5 → 15 → 60 min; a release announcement or a different account checks at once. | Unit test (`RetryDelay`). |
| M2 | med | An unexpected error ended the update loop: no more update checks until restart. | The loop catches, logs and continues. | Code review only. |
| M3 | med | Popups always opened on the primary monitor. | They open on the screen the cursor is on, and a group stays together. | Build only. **This PC has one monitor, so not tested.** |
| M4 | low | Opening a notification from the Inbox forced "high priority", so it popped up even during a meeting/Busy/pause. | It opens as a window whatever those say; no sound; shown/missed state untouched. | Build only. |
| M5 | low | Settings saved through a throw-away HttpClient: no token renewal, no retry. | Uses the shared client (renews once, retries). | Build + tests. |
| M6 | low | Chat toast text was not clipped (other toasts clip at 200 characters). | Clipped the same way. | Build only. |

## Improvements

| # | Before | After |
|---|--------|-------|
| I1 | Tray said only "connecting…". | "reconnecting · last 14:02" and a **Reconnect now** menu item while offline. |
| I2 | A session ended by the server just reopened the sign-in window. | One balloon: "Your iPop session ended. Sign in again…" (not shown after a sign-out you chose). |
| I3 | No log line for a rejected session or hub drops. | `auth` / `hub` lines (lost, closed, reconnected, rejected with HTTP code). |

## Not found / not done

- No bug found on the iDesk side (toast text parsing, reply, read sync). Nothing to hand over.
- Live end-to-end run (QA build + copy DB: sign-in, offline backlog, reply, tab reuse, account switch) was **not** run.
  Only H1 was proven live (mock server).
- Pipeline (CI/MSI/publish scripts): not reviewed in this round beyond reading release.json handling.
- Not released. 1.4.8 is built and committed only.
