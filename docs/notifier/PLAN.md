# iPop Notifier — build plan (approved mockups: docs/mockups/notifier/index.html v1 + v2.html)

Branch: `notifier` (from mobile-fixes `1699877`). Prod deploys only from the main session, per phase, on the user's go.

## Phase A — server side in iPop (API + Web)
A1. **Notifier device API** (`/api/notifier/*`)
   - `POST /api/notifier/devices/register` (JWT): machine name, Windows user, OS, app version, local IPs → `NotifierDevices`
     row (user, last seen, remote IP from the connection). Heartbeat `POST …/heartbeat`.
   - Device presence suppresses browser Web Push on the same PC (shared `deviceKey` cookie/localStorage set by the
     Notifier when it opens iPop; PushDispatcher skips browser subscriptions whose deviceKey has a live notifier).
   - `GET/PUT /api/notifier/settings` — per-user server-side settings (popup style v1|v2 default v2, popups on/off/until,
     hold during meetings, urgent breaks through, position, max visible, per-module Popup/Toast/Off, sound+volume,
     start with Windows).
   - ICT/Admin only: `GET /api/notifier/devices` (list for the Devices console: PC, IP, Windows user, OS, version, online).
A2. **Notification payload for desktop**: the NotificationHub already fans out; add a typed desktop envelope
   (module, title, sender, body text + sanitised HTML excerpt, first image URL (authorised, resized), attachments
   (incl. NAS UNC path), actions, deep link, priority). Actions endpoints reuse module APIs (mail send/reply, mark read,
   meeting join/decline, news ack, task done/comment, notes reply, iDesk ticket reply via TicketService).
A3. **Long-lived app session**: Notifier signs in with NIK+password (or Windows/AD later) → existing 90-day sliding
   refresh family tagged `client=notifier` (shows in Devices & sessions; admin revoke works).
A4. **"Sign in with iPop" (OpenID Connect provider)**: OpenIddict 7 in IPop.Api — authorization code + PKCE, password
   grant for confidential clients, RS256 signing key from config, claims `sub, nik, name, email, department, site`,
   client registry (iDesk now, more later) managed in Admin. Login UI = existing iPop /login (prompt only if no session).
   Integration doc for iDesk: docs/sso/idesk-sign-in-with-ipop.md.
A5. **Web: "Open in File Explorer"** — NAS file links get a menu (browser / File Explorer via `ipop-notifier://open?…`
   / copy UNC path); UNC built from the 5 configured NAS hosts only. Admin → Notifier: download + devices.

## Phase B — the Windows app (`src/IPop.Notifier`, .NET 10 WinForms host + WebView2)
B1. Tray icon (badge), single instance, autostart via HKCU Run, per-user install (Velopack) — **no admin**, self-update
   from `https://ipop.santos.co.id/download/notifier/`.
B2. Sign-in window (mockup 8, v1), token in DPAPI (CurrentUser), 90-day sliding refresh, SignalR client to
   `/hubs/notification` with reconnect + catch-up from `/api/notifications/v1/inbox`.
B3. Popup engine with **two styles** (setting, default v2):
   - v2: one top-level window per notification, cascaded, grouped under one taskbar button (shared AppUserModelID)
     → Windows shows the hover thumbnails natively; >8 visible → oldest minimise.
   - v1: stacked glass popups bottom-right, newest expanded, older collapsed, "+N more · Close all".
   - One shared WebView2 environment for every window (light on memory); no focus stealing (WS_EX_NOACTIVATE until
     clicked); hold during full-screen / Focus Assist / Busy; urgent breaks through; iPop sounds per module.
B4. Chat = Windows toast with iPop logo + inline reply (Microsoft.Toolkit/AppNotification), via AUMID.
B5. Tray Inbox (mockup 6) + Settings (mockup 7, incl. Popup style v1/v2) + Explorer protocol handler
   (`ipop-notifier://`, HKCU registration, allow-list of NAS hosts → `explorer /select,"\\host\share\path"`).
B6. ICT Devices console (mockup v2 §3) — visible only to Admin role / ICT department (server-enforced).

## Phase C — iDesk side (iDesk team, guided by the doc) and SJA Notifier switch-off after the pilot.
## Phase D — remote help (separate grilling session; needs AD read-only account etc.).

## QA
Copy-DB stack (see memory recipe) for API; Notifier tested against it on this PC (Windows), Playwright for web parts,
unit tests for settings/payload/allow-list/OIDC claims. Pilot: ICT group first.

## Status (2026-10-18, branch `notifier`, last commit 23832b6)
Done and QA'd on the copy DB (API :6391) with the real app on this PC:
- A1 device API + settings (ui-prefs "notifier"), ICT/Admin Devices list (non-ICT → 403, menu hidden).
- A2 realtime to the app: gateway sends SignalR when the Notifier is online (no iPop tab needed); chat messages
  reach the app as `desktopToast`; Windows browser Web Push is skipped for that user while the app is online.
- A4 OIDC provider (earlier commits) + iDesk doc.
- A5 Files "Open in File Explorer" (`ipop://open`) answered by the app; NAS links in mail popups get the menu.
- B1–B6: tray, single instance, sign-in (DPAPI, 90-day), feed + 1-day catch-up, popup style v2 (default, one window
  per notification, cascade, taskbar badge, oldest minimise) and v1 (stacked cards, +N more, hover expands),
  per-module actions (mail Reply/Reply all with attachment upload, Forward → iPop, Mark read; meeting Join/Snooze/
  Decline; memo Acknowledge; task comment / Mark done; note reply; iDesk Reply/Open in iDesk; call back), chat toast
  with inline reply, Inbox, Settings (style, popups, hold, urgent, position, visible, modules, sounds, autostart,
  account), ICT Devices console (+ Open Remote Desktop until phase D).
- Installer: Velopack per-user, no admin (install/uninstall verified), `release.ps1` → C:/ipop-ops/notifier/releases,
  served at /download/notifier/{file}; /app shows the download on a PC when the setup exists.

Open: "Open in iDesk" silent sign-in from the app (needs iDesk's OIDC client), Admin UI for OIDC clients (config for
now), hold-during-meeting verified by code/unit only (full-screen not simulated), toast reply (UIA could not reach the
toast host from this session — send path is the same chat API used by the popup).
MeetingKindTests (4) fail on the base commit too (unrelated).
