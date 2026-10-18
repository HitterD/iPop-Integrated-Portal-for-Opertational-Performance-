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
