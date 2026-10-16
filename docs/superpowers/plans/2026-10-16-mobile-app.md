# Mobile App (Stage 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or superpowers:executing-plans inline). Every task is TDD: write the listed tests first, run them RED, implement, run GREEN, then the full suite, then commit. Steps use checkbox (`- [ ]`) syntax. One wave = one branch off the previous wave's prod commit = one deploy.

**Goal:** Turn the phone layout into one consistent app (large collapsing titles, list → detail with ‹ back, bottom sheets with grab handle, per-page FAB, ≥44 px targets, top safe area), with a user-editable tab bar, an installable PWA + Android APK (TWA), and an `/app` install page. Desktop (≥1025 px) and the tablet compact desktop (768–1024 px, stage 1) stay visually unchanged.

**Mockups (binding visuals + copy):** `docs/mockups/mobile-app-v1.html` (frames a–r) · `docs/mockups/mobile-app-v2-modules.html` (D1–D5, P1–P4, T1–T3, N1–N3, NF1–NF2, F1–F4, C1–C3, CA1–CA2, CH1–CH3, M1–M2, S1–S3, AD1–AD2, L1–L2). Decisions: memory *mobile-app-decisions*. Stage 1 hotfix (live 36cc2b5): `.scratch/backup-mobile/CONTRACT.md`, `tests/mobile-breakpoints.test.cjs`, `tests/overlay-history.test.cjs`, `tests/IPop.Tests/MobileHotfixTests.cs`.

**Tech stack:** Blazor WASM (.NET 10), minimal API, EF Core (SQLite `TestDb` in tests), bUnit 2.9, xunit, node `--test`, Playwright (QA only), Bubblewrap CLI (TWA).

## Global constraints

- Worktree `C:/Users/IT12/orca/workspaces/mobile-fixes`. Each wave on its own branch `mobile-w<N>`; prod only on an explicit "deploy ke prod". After each deploy run `deploy/prune-deploy-artifacts.ps1`. Never kill processes by image name.
- **One breakpoint.** Phone = `PHONE_QUERY` from `wwwroot/js/mobile.js` (`(max-width: 767px), (max-height: 500px) and (max-width: 932px) and (pointer: coarse)`). Every new stylesheet uses exactly that string (the existing `mobile-breakpoints.test.cjs` walks all CSS and enforces it).
- **CSS scoping (desktop must not move a pixel):**
  - New phone-only classes use the prefix `ph-` (kit) or `m-<module>-` (module screens). Never edit or add rules for an existing desktop selector outside the phone media query.
  - Outside the media query the only allowed rule is the guard `display:none` for phone-only elements (as `.m-tabbar` does today).
  - New files: `wwwroot/css/mobile/phone-kit.css` (Wave 1) and per module `css/mobile/<module>.css` (existing files are extended, not replaced). Linked in `index.html` after the existing mobile block; bump `?v=` on every change.
  - Phone markup that differs structurally is rendered **only when `PhoneState.IsPhone`** (see W1.2), so the desktop DOM is byte-identical; small tweaks stay CSS-only.
- **NIK is never shown to non-admins** (memory *hide-nik-position-decisions*): no new DTO field, no markup. Only AD2 (Admin users) shows and searches NIK. Each wave has a bUnit NIK test for its screens.
- **Copy:** English, exactly as the mockups. Lucide icons only. Reuse v6 tokens (`--surface`, `--ink`, `--ink-3`, `--sep`, `--fill`, `--accent`), forest green `#123528` for app chrome.
- **Back:** every phone detail/sheet uses the stage-1 history model — page details via `ipop.mobile.pushDetail/popDetail`, overlays via `<OverlayHook>` (`OverlayStack`). No new history code paths.
- **Tests:** `dotnet test tests/IPop.Tests/IPop.Tests.csproj` + `node --test tests/*.test.cjs tests/*.test.mjs`. API migrations: `dotnet build src/IPop.Api -o .scratch/efbuild` then `dotnet ef migrations add <Name> --project src/IPop.Infrastructure --startup-project src/IPop.Api --no-build -- --contentRoot src/IPop.Api`; never `database update` against the prod Postgres (127.0.0.1:5433); QA on a throwaway copy DB (memory *dev-db-writes-allowed-for-tests*).
- **Backups:** `.scratch/` and other untracked files have no undo — back them up before large refactors (memory *backup-untracked-files-before-refactor*).

## Shared QA protocol (every wave)

Harness `tools/mobile-qa/` (committed: `package.json` with `playwright`, `qa.mjs`, `routes.json`; `node_modules/` gitignored). `node tools/mobile-qa/qa.mjs --wave <N> --base https://localhost:7103` signs in as the demo users (memory *ipop-two-browser-call-test*), then for every route of the wave:

| Viewport | Pointer | Checks |
|---|---|---|
| 360×780, 390×844, 430×932 | coarse, `isMobile:true, hasTouch:true` | screenshot light+dark; `document.scrollingElement.scrollWidth <= innerWidth` and no element with `getBoundingClientRect().right > innerWidth+1` (overflow); every visible `a,button,[role=button],input,select,[role=tab]` ≥ 44×44 (hit-box incl. padding; report offenders); large title collapses on scroll; FAB not covering last list row |
| 844×390 | coarse | phone layout (tab bar visible, sidebar hidden), no overflow, sheet max-height fits |
| 768×1024 | coarse | compact desktop unchanged vs baseline |
| 1440×900 | fine | **pixel diff vs baseline** taken from the previous prod commit (`pixelmatch`, mask clocks/avatars/badges); 0 changed px outside masks = pass |

Plus per wave: **Back** (`page.goBack()` closes top sheet → detail → list, never leaves the app at the first level), **keyboard** (simulate: `page.setViewportSize` height −300 and dispatch `visualViewport` resize via `window.visualViewport` override; assert focused field and composer/toolbar are above `vv.offsetTop+vv.height`), **dark mode** via `emulateMedia({colorScheme})` and the in-app theme toggle, **safe area** (inject `--m-safe-t: 47px` / `env()` polyfill style and check nothing sits under it). Output `tools/mobile-qa/out/<wave>/report.json` + PNGs (gitignored).

---

## Wave 1 — Shell & app

### W1.1 Phone kit components (`src/IPop.Web/Components/Phone/`)

- [ ] **Tests first** `tests/IPop.Tests/PhoneKitTests.cs` (bUnit): each component renders its root class, slots, `aria-*`; `BottomSheet` renders nothing when `Open=false`, includes `<OverlayHook>` when open, grab handle has `aria-label="Close"`-free decorative role; `SegmentedControl` sets `aria-selected="true"` as the string (memory *directory-aria-checked-bool-bug*) and fires `ValueChanged`; `Fab` hidden when `Visible=false`; `ListRow` renders `<a>` when `Href` set else `<button>`.
- [ ] `PhoneScreen.razor` — `[Parameter] string Title`, `RenderFragment? Leading` (‹ back slot), `RenderFragment? Trailing` (icon buttons), `RenderFragment? Below` (search/chips under the large title), `RenderFragment ChildContent`, `bool LargeTitle = true`, `string? BackLabel`, `EventCallback OnBack`. Markup: `.ph-screen > .ph-nav (.ph-nav-title small) + .ph-scroll > .ph-large-title + content`. Collapse: `ph-kit.js` `ipop.phone.collapse(scrollEl)` IntersectionObserver on the large title toggles `.ph-nav.is-collapsed` (no scroll listeners per frame).
- [ ] `BottomSheet.razor` — `bool Open`, `EventCallback<bool> OpenChanged`, `string? Title`, `RenderFragment? Leading/Trailing` (Cancel / Save of frame n), `SheetSize Size = Auto|Half|Tall|Full`, `bool ScaleBack` (page scales back, frames n/k2). Grab handle + drag-to-dismiss in `ph-kit.js` (`pointerdown` on handle only, threshold 30 % height or velocity > 0.6 px/ms), scrim tap closes, `<OverlayHook OnClose>` for Esc/Back. Replaces the ad-hoc `.m-sheet` markup (MainLayout More sheet moves to it).
- [ ] `Fab.razor` — `string Icon`, `string Label` (aria-label + extended label), `EventCallback OnClick`, `bool Visible`; positioned above tab bar + `--m-safe-b`, hides while keyboard open (`body.ph-kb-open`, set by mobile.js).
- [ ] `ListRow.razor` — `Leading` (avatar/icon), `Title`, `Subtitle`, `Meta` (time/badge), `Trailing`, `Href|OnClick`, `bool Chevron`, `bool Unread`; min-height 56 px; `ListSection.razor` (`Header`, inset grouped style).
- [ ] `SegmentedControl.razor` — `IReadOnlyList<(string Value, string Label, int? Count)> Items`, `string Value`, `EventCallback<string> ValueChanged`; `role="tablist"`.
- [ ] `PhoneOnly.razor` / `DesktopOnly.razor` — render `ChildContent` from `PhoneState.IsPhone`.
- [ ] CSS `wwwroot/css/mobile/phone-kit.css` (all `.ph-*` rules inside the phone query; guards outside), JS `wwwroot/js/ph-kit.js` (collapse, sheet drag, keyboard flag). Node test `tests/ph-kit.test.cjs` for the pure parts (`dismissDecision(dy, height, velocity)`, `collapseState`).

### W1.2 PhoneState service

- [ ] Tests: `PhoneStateTests.cs` — `Changed` fires once per transition; default `false` before JS answers (desktop-first render, so desktop never flashes phone markup).
- [ ] `Services/PhoneState.cs` (scoped): `bool IsPhone`, `event Action Changed`, `[JSInvokable] OnPhoneChanged(bool)`. `mobile.js` gains `ipop.mobile.watch(ref)` → `mq.addEventListener('change')`. MainLayout replaces its one-shot `isPhone` with this service. Register in `Program.cs`.

### W1.3 Tab bar + Edit tabs + More sheet (frames g, h)

- [ ] API tests `UiPrefsEndpointTests.cs`: GET `/api/me/ui-prefs/tabs` → default `["/chat","/mail","/calls","/calendar"]`; PUT validates exactly 4 distinct hrefs from the whitelist (all NavGroups hrefs; `/admin` only for admins), 400 otherwise; per-user isolation.
- [ ] Entity `UserUiPref { UserId, Key (≤32), Json (≤2 KB), UpdatedAt }` (unique `UserId+Key`) in `IPop.Core`, `DbSet` in `IPopDbContext`, migration `AddUserUiPrefs`; endpoints in `MeEndpoints.cs` (`GET/PUT /api/me/ui-prefs/{key}`, keys whitelisted: `tabs`, `notif-card`, `dash-order`). Mirror to `user-store.js` (per-account localStorage) so the bar renders before the API answers.
- [ ] bUnit `MainLayoutTabsTests.cs`: default tabs = Chat·Mail·Calls·Calendar + More (replaces Dashboard·Chat·Mail·Calendar·Projects — update the stage-1 test that pinned the old list); saved prefs re-order; More grid lists every non-pinned module, Admin settings only when `IsAdmin`; badges carry over; `MoreBadgeTotal` counts only non-pinned.
- [ ] `Components/Phone/TabBar.razor` (extract from MainLayout lines 301–350; `Tabs` from `TabPrefsState`), `MoreSheet.razor` (frame g: profile header → `/profile`, module grid, rows Edit tabs / Settings / Sign out), `EditTabsSheet.razor` (frame h: live preview bar on top, list with drag handles, pin/unpin; drag via `ph-kit.js` `sortable(listEl, ref)` using pointer events, `aria-grabbed` alternatives: ↑/↓ buttons for accessibility). `Services/TabPrefsState.cs` (load/save, whitelist, default).
- [ ] Dashboard is no longer a tab by default → reachable from More; login still redirects to `/dashboard` (unchanged).

### W1.4 Notification card (frame o) + top safe area

- [ ] Tests: `NotifCardTests.cs` — phone + permission `default` + `notif-card` pref not `dismissed` → card; "Not now" PUTs `dismissed` and never shows again; desktop keeps the existing `.push-banner` (assert by `PhoneState=false`).
- [ ] `Components/Phone/NotificationCard.razor` (bottom-sheet-style card, Turn on → existing `ipopPush` enable flow; insecure context (http LAN) → card text explains, memory *ipop-http-blocks-secure-browser-apis*). MainLayout: `push-banner` wrapped in `<DesktopOnly>`.
- [ ] Safe area: `.ph-nav` and `.v6-topbar` use `padding-top: var(--m-safe-t)`; green status bar in standalone via `@media (display-mode: standalone)` inside the phone query only.

### W1.5 PWA manifest, icons, splash (frames q, r, r2)

- [ ] Tests `tests/pwa-manifest.test.cjs`: manifest has `id:"/"`, `start_url:"/dashboard?source=pwa"`, `scope:"/"`, `display:"standalone"`, `theme_color:"#123528"`, `background_color:"#123528"`, icons 192/512 `purpose:"any"` + 192/512 `purpose:"maskable"`, `shortcuts` (Chat, Mail, Calendar), every icon file exists with the stated pixel size (read PNG IHDR).
- [ ] `tools/brand/make-icons.py` (Pillow, run with `python -I`): from `img/brand/ipop-mark.png` → `icon-maskable-192/512.png` (mark on white, 80 % safe zone), `splash-*.png` for iOS `apple-touch-startup-image` (390×844@3, 430×932@3, 360×780@3), `android/twa` splash 512. `.gitignore`: add `!src/IPop.Web/wwwroot/img/brand/*.png`.
- [ ] `index.html`: keep `<meta name="theme-color" content="#F5F5F7">` for desktop and add `<meta name="theme-color" content="#123528" media="(max-width: 767px)">` (Safari macOS tints the tab strip — desktop must stay grey); iOS `apple-mobile-web-app-capable`, `-status-bar-style: black-translucent`, startup images with media queries. Loading screen (`#app` "Loading IPop…") gets a phone-only splash style (forest green, batik texture `batik-kopi-oval.svg`, mark tile, Kapal Api Group logo) in `phone-kit.css`.

### W1.6 Service worker app-shell caching (`wwwroot/sw.js`)

Rules (keep push code untouched; add one `fetch` handler):
- Only same-origin `GET`, no `Range` header, `request.cache !== 'no-store'` (version-check.js uses `no-store` for `index.html` and `HEAD _framework/dotnet.js` → always network, so auto version reload keeps working).
- **Never handle:** `/api/`, `/hubs/`, `/livekit`, `/onlyoffice`, `/meet/` media, `/download/`, `/.well-known/`, `sw.js`, anything with `Authorization`, any cross-origin (API base host, CDNs).
- **Navigations / `index.html`:** network-first with 4 s timeout → cached `index.html` → `offline.html` (new, tiny, "You're offline — iPop needs a connection", Retry).
- **`_framework/*` with a fingerprint in the name** (`/\.[a-z0-9]{8,}\.(js|wasm|dat|json)$/` — .NET 10 fingerprints by default; verify in `bin/.../wwwroot/_framework` before coding): cache-first, immutable. Non-fingerprinted framework files (`dotnet.js`, `blazor.boot.json`/`dotnet.boot.js`): network-first.
- **`css/`, `js/`, `fonts/`, `img/` with `?v=`:** stale-while-revalidate, cache key includes the query.
- Caches `ipop-shell-<SW_VERSION>`, `ipop-fw-<SW_VERSION>`; `activate` deletes other `ipop-shell-*`/`ipop-fw-*` (never `ipop-push-state-v1`), LRU cap 400 entries for `ipop-fw`. Kill switch: if `GET /sw-kill` (static JSON in wwwroot, `{"off":true}`) is seen on activation, clear caches and `registration.unregister()`.
- Registration: `index.html` registers `sw.js` on secure contexts at load (same URL/scope as `push-lifecycle.js`, so one registration).
- [ ] Tests first `tests/sw-cache.test.cjs` (load `sw.js` in a `vm` sandbox with fake `caches`/`fetch`, extend `push-worker.test.cjs` helpers): routing table for 20 URLs (api, hubs, no-store index, HEAD dotnet.js, fingerprinted wasm, `?v=` css, cross-origin, Range); navigation falls back to cache then offline page; activate keeps push cache; existing `push-worker.test.cjs` still green.
- [ ] Manual QA: deploy twice with a changed `IPop.Web.dll` → version bar/reload still appears; DevTools Offline → offline page; Lighthouse "installable" passes.

### W1.7 `/app` install page (frames p1, p2, p3)

- [ ] bUnit `AppInstallPageTests.cs`: platform from injected UA (`android` → APK button first + Chrome install steps; `iphone|ipad` → Safari Share → Add to Home Screen steps; else → QR of `https://ipop.santos.co.id/app` + both instructions collapsed); page is anonymous (`@layout EmptyLayout`, no auth redirect).
- [ ] `Pages/AppInstall.razor` `@page "/app"`; `beforeinstallprompt` captured in `ph-kit.js` (`ipop.phone.canInstall()/install()`); QR via vendored `wwwroot/lib/qrcode/qrcode.min.js` (qrcode-generator, MIT) rendered to SVG; illustrations as inline SVG from the mockup.
- [ ] APK download: WebHost maps `GET /download/ipop.apk` to the file at config `AndroidApp:ApkPath` (outside the repo, e.g. `C:/ipop-ops/android/ipop.apk`), `Content-Type: application/vnd.android.package-archive`, `Content-Disposition: attachment`; 404 when missing (page then hides the APK button using `HEAD`).

### W1.8 Digital Asset Links + TWA project

- [ ] WebHost: `AssetLinks.cs` (static `Build(IConfiguration) → string json`), `app.MapGet("/.well-known/assetlinks.json", …)` **before** `MapFallbackToFile`, `application/json`, from config `AndroidApp:PackageName` (`id.co.santos.ipop`) + `AndroidApp:Sha256CertFingerprints` (string array; fingerprints are public). Tests: add `IPop.WebHost` ProjectReference to the test project (or a `WebApplicationFactory<Program>` with `public partial class Program`) → `AssetLinksTests.cs`: JSON shape `[{relation:["delegate_permission/common.handle_all_urls"],target:{namespace:"android_app",package_name,sha256_cert_fingerprints}}]`, empty array when unconfigured, not swallowed by the SPA fallback, CSP headers still applied.
- [ ] `android/twa/twa-manifest.json` (committed): `packageId:"id.co.santos.ipop"`, `host:"ipop.santos.co.id"`, `name:"iPop"`, `launcherName:"iPop"`, `startUrl:"/dashboard?source=twa"`, `themeColor:"#123528"`, `navigationColor:"#123528"`, `backgroundColor:"#123528"`, `enableNotifications:true` (notification delegation → Web Push keeps working), `iconUrl`/`maskableIconUrl` = the W1.5 icons on prod, `splashScreenFadeOutDuration:300`, `fallbackType:"customtabs"`, `display:"standalone"`, `orientation:"default"`, `signingKey:{path:"C:/ipop-ops/android/ipop-release.keystore", alias:"ipop"}`, `appVersionCode/Name` bumped per release, `webManifestUrl:"https://ipop.santos.co.id/manifest.webmanifest"`.
- [ ] `.gitignore`: `android/twa/**` except `twa-manifest.json` and `README.md`; plus `*.keystore`, `*.jks`, `*.apk`, `*.aab`, `keystore*.properties`.
- [ ] `android/twa/README.md` build steps:
  1. Tooling: Node ≥18 (24 present), `npm i -g @bubblewrap/cli`, `bubblewrap doctor`. Bubblewrap wants **JDK 17** and the Android SDK command-line tools: let `bubblewrap init` download both into `%USERPROFILE%\.bubblewrap` (needs internet), or set `jdkPath`/`androidSdkPath` in `%USERPROFILE%\.bubblewrap\config.json`. The machine's JDK 21 may be rejected by doctor — use the downloaded 17.
  2. Key (once, **outside the repo**): `keytool -genkeypair -v -keystore C:\ipop-ops\android\ipop-release.keystore -alias ipop -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=iPop, O=PT Santos Jaya Abadi, C=ID"`. Password goes into the team password manager only — never in files, scripts, env files or commits; pass it at the prompt or via session-only `BUBBLEWRAP_KEYSTORE_PASSWORD`/`BUBBLEWRAP_KEY_PASSWORD`. Back the keystore up to the NAS ops share + offline copy: losing it means users must uninstall to update.
  3. Fingerprint: `keytool -list -v -keystore … -alias ipop` → SHA-256 into prod `appsettings` `AndroidApp:Sha256CertFingerprints`; verify `https://ipop.santos.co.id/.well-known/assetlinks.json` and Google's `digitalassetlinks.googleapis.com/v1/statements:list?source.web.site=…`.
  4. `cd android/twa && bubblewrap build` → `app-release-signed.apk` → copy to `AndroidApp:ApkPath`. Test on a device: no URL bar (= DAL verified), push toast, Back exits from tab roots, file upload/camera, call mic permission.
- Risks: TWA requires valid HTTPS on `ipop.santos.co.id` and the proxy IPS stall (memory *ipop-proxy-ips-blocks-wasm*) — verify the proxy serves https end-to-end before shipping the APK; if DAL fails, the app shows a URL bar (still works).

### Wave 1 QA / risks
- QA: protocol above on `/dashboard`, `/chat`, More sheet, Edit tabs, notification card, `/app` (Android UA, iPhone UA, desktop), offline page; Back closes Edit tabs → More sheet; 1440/768 pixel diff = 0 on all routes.
- Risks: SW caching stale `index.html` (mitigated: network-first + no-store bypass + kill switch); changing default tabs surprises users (one release note toast); `display-mode: standalone` rules leaking into desktop installed PWA (all inside phone query).

---

## Wave 2 — Communication

Pattern for every module: page keeps its state/logic; phone markup lives in `Components/Phone/<Module>/*.razor` rendered under `<PhoneOnly>`; desktop markup under `<DesktopOnly>` is untouched. List screens = `PhoneScreen` + `ListRow`; detail = `PhoneScreen` with `Leading` ‹ back + `pushDetail`; forms = `BottomSheet`.

### W2.1 Chat (frames a, b; CH1–CH3)
- [ ] Tests `PhoneChatTests.cs`: list renders filter chips (All / Unread / Groups / Pinned per mockup), pinned rows tinted, unread counts; conversation hides tab bar (`hideTabbar`), header tap opens CH1; CH1 lists Call/Meeting/Search/Mute, pinned/mentions/saved rows → `/chat/pins|mentions|saved`, members without NIK; CH2 attach sheet tiles (Gallery, Document, From NAS, Sticker, Poll, Event) call existing handlers; CH3 long-press: reaction bar + Reply/Forward/Copy/Pin/Save/Delete (own only).
- [ ] Files: `Components/Phone/Chat/ChatListPhone.razor`, `ChatInfoSheet.razor` (CH1, wraps `GroupInfoPanel` data), `ChatAttachSheet.razor` (CH2; recent photos via `<input type=file accept=image/* multiple>` — no gallery API on web, so "recent photos" strip shows the last session uploads; document as deviation), `MessageActionsSheet.razor` (CH3; long-press 450 ms in `chat-micro.js`, haptics `navigator.vibrate(10)` when available). Modify `Pages/Chat.razor` (wrap list/conversation), `Chat.Composer.cs` (composer pinned to `visualViewport`), `css/mobile/chat.css`.
- Mapping without mockup: `/chat/search|pins|mentions|saved` → `PhoneScreen` lists reusing `ListRow`; `/chat/group/{id}/settings` → CH1-style grouped list; NewChatPanel → `BottomSheet Size=Full`; sticker editor / poll create → full sheets.

### W2.2 Mail (frames c, d; M1–M2)
- [ ] Tests `PhoneMailTests.cs`: 3-line rows (sender+time, subject, preview, label chips, thread count, blue unread dot); ‹ Mailboxes opens M2 (folders with counts, labels); reading view collapsed recipients + "Read by X of Y"; bottom bar Reply / Reply all / Forward; M1 compose as full sheet with chip recipients, >50 confirm (existing rule), address book icon.
- [ ] Files: `Components/Phone/Mail/MailInboxPhone.razor`, `MailboxesScreen.razor` (M2), `MailReadingPhone.razor`; `MailComposer.razor` gets a phone variant class only (`mc-sheet` stays — version-check busy detector depends on it). Swipe actions (archive/delete) behind `ph-kit.js swipeRow` — optional, last task. `/mail/advanced` → phone shows a `PhoneScreen` with the same form stacked (no mockup).

### W2.3 Calls & Meetings (frame e; C1–C3)
- [ ] Tests `PhoneCallsTests.cs`: segmented Recents / Scheduled / Rooms; room cards keep `r-*` colour classes; C1 group tiles + "Ringing…" for invited; C1b minimised pill (name, timer, mute, end) visible on every route while a call runs; C2 meeting: screen share on top, film strip, REC badge; C2b chat sheet over video; C3 `BookingSheet` in `BottomSheet Size=Tall` with horizontal timeline.
- [ ] Files: `Components/Phone/Calls/CallsHomePhone.razor`, `CallPill.razor` (C1b, rendered by `GlobalCallOverlay` on phone), modify `GlobalCallOverlay.razor`, `MeetingRoomOverlay.razor`, `MeetingRoom.razor` (`/meet/{room}`), `BookingSheet.razor` (phone wrapper only), `css/mobile/calls.css`. `call-drag.js` disabled on phone (pill instead of draggable window).
- Risk: LiveKit layout JS (`meeting-room.js`) measures container size — re-test `meeting-rooms-layout.test.cjs` with phone sizes; rotation → `screen.orientation` full screen on share tap.

### W2.4 Notifications (NF1–NF2)
- [ ] Tests: Today / Earlier groups, module-coloured icons, unread dot; swipe-left reveals Mark read / Delete; NF2 one grouped list (General, Quiet hours, Categories) with instant-save switches (no Save button).
- [ ] Files: `Components/Phone/Notifications/NotificationListPhone.razor`, `NotificationPrefsPhone.razor`; `Pages/Notifications.razor`, `Pages/NotificationSettings.razor`. The topbar bell popover is not used on phone (bell → `/notifications`).

### Wave 2 QA / risks
- Keyboard: composer (chat), quick reply (mail), meeting chat stay above `visualViewport`; sending while keyboard open keeps focus. Back: CH3 → CH1 → conversation → list. Two-browser call test on a phone viewport (C1, C1b, C2).
- Risks: dual DOM doubles event subscriptions — phone components must not re-subscribe hubs (state stays in page/services); scroll-position restore list ↔ conversation; long-press vs text selection on iOS (`-webkit-touch-callout:none` on bubbles only).

---

## Wave 3 — Notes (frames i, j, k, k2)

- [ ] Tests `PhoneNotesTests.cs`: list = search, segmented Recent | Favorites | All, recent cards strip, page tree with chevrons + emoji (tap chevron expands, tap row opens); editor hides tab bar; ⋯ sheet quick actions Share · Favorite · Move to… · Comments, Export expanded (PDF / Markdown / HTML); comments `BottomSheet Size=Tall ScaleBack` with quoted block, Reply / Resolve, composer; FAB "New page" creates a blank page (existing behaviour).
- [ ] Node test `tests/notes-kb-toolbar.test.cjs`: pure `toolbarOffset(vv:{offsetTop,height}, innerHeight)` and `shouldShow(focusInEditor, kbHeight)` (show only when keyboard height > 120 px).
- [ ] Files: `Components/Phone/Notes/NotesListPhone.razor`, `NotesMoreSheet.razor`, `NotesCommentsSheet.razor`, `NotesKeyboardToolbar.razor` (buttons: Aa heading, bold, italic, bullet, checklist, @mention, image, undo, hide-kb); `wwwroot/js/notes-kb.js` positions the bar with `transform: translateY(...)` from `visualViewport` `resize`+`scroll` (iOS doesn't resize layout; Android `resizes-content` does — both handled), commands forwarded to the existing editor API in `js/notes/notes-editor.js` (bundle from `tools/notes-editor`; if a command is missing add it in `tools/notes-editor/src` and rebuild — bundle is committed). Modify `Pages/Notes.razor`, `Notes.Sidebar.cs` (tree data reused), `css/mobile/notes.css` (new file).
- QA: type in a long page with the keyboard simulated, caret stays visible above toolbar; Yjs collab with a second browser while on phone; mention picker as sheet without NIK; import (Notion zip) → "Open on a computer" note on phone (no mockup).
- Risks: ProseMirror selection lost when tapping toolbar (use `pointerdown` + `preventDefault`), iOS `visualViewport` scroll jitter, comments sheet + editor both owning Back (sheet via OverlayHook, editor via pushDetail — order verified by test).

---

## Wave 4 — Directory, Calendar, Dashboard

### W4.1 Directory (frame m; D1–D5)
- [ ] Tests `PhoneDirectoryTests.cs`: D1 rows Title-case name, Department · Site, presence dot, Chat / Call buttons; D2 site chips + one tree per site (Department › Unit preserved); D3 breadcrumb, member count, online count, "Chat with all"; D4 big photo, star favourite, office phone, organization (reports to), free/busy; D5 highlighted matches, scope chips with counts, empty state with typo suggestion. **No NIK/position for non-admin** in any of them.
- [ ] Files: `Components/Phone/Directory/{PeoplePhone,DepartmentsPhone,DepartmentDetailPhone,PersonPhone,DirectorySearchPhone}.razor`; `Pages/Directory.razor`, `Pages/EmployeeProfile.razor` (replace stage-1 `.m-detail-bar` with `PhoneScreen`), `Services/DirectoryNavigation.cs` (reuse), `css/mobile/directory.css`. Typo suggestion: Levenshtein ≤2 over loaded names (pure C# helper `DirectorySuggest.cs` with unit tests).

### W4.2 Calendar (frame f; CA1–CA2)
- [ ] Tests: week strip (swipe = next/prev week, `ph-kit.js swipe`), agenda with now-line, kind colours; CA1 room-coloured header, countdown + Join, RSVP, password/reminder; CA2 kind tiles first that change the form (Meeting room → room + `RoomTimeline`, In person → location), FAB "New event".
- [ ] Files: `Components/Phone/Calendar/{WeekStripAgenda,EventDetailPhone,NewEventSheet}.razor` reusing `MeetingDetail`, `MeetingKindPicker`, `RoomTimeline`; `Pages/Calendar.razor(.cs)`, `css/mobile/calendar.css`. Month/Day views and sharing dialogs: reachable from ⋯ as `BottomSheet` (no mockup; reuse `CalendarSettingsSheet`).

### W4.3 Dashboard (frame l)
- [ ] Tests: one column — next-meeting hero, 2×2 counters, today agenda, latest news; pencil → reorder mode; order saved in ui-prefs `dash-order`; "New post" still only for HR/Admin (stage-1 rule).
- [ ] Files: `Components/Phone/Dashboard/DashboardPhone.razor`; `Pages/Dashboard.razor`; `css/mobile/dashboard.css`.

QA: D1→D4→Back→D1 keeps scroll; calendar swipe at 360 px; dashboard reorder persists across sign-out/in. Risks: free/busy for non-granted shows Busy only (calendar-access rules) — test with two users.

---

## Wave 5 — Projects, Tickets, News, Files, Profile/Settings/Search, Admin, Login

### W5.1 Projects (P1–P4, frame n)
- Tests: P1 cards (status, progress counts, members, due orange/red); P2 one column at a time with column tabs + counts, swipe between columns, long-press lift → "Move to" sheet (not cross-column drag); P3 full screen with ‹ Board and ↑ ↓ stepping, status/priority/due/estimate as tappable rows opening sheets; P4 four tiles + burndown + milestones (Velocity/Export desktop only); New task = `BottomSheet ScaleBack` (frame n). Viewer read-only and owner rules unchanged.
- Files: `Components/Phone/Projects/{ProjectListPhone,BoardPhone,TaskDetailPhone,ReportsPhone,NewTaskSheet}.razor`; reuse `ProjectReportMath.cs`, `TaskDiscussion`, `TaskAttachments`, `TaskEvidenceModel`; `Pages/Projects.razor`; `css/mobile/projects.css`.
- No mockup: Table / Timeline / Calendar / Portfolio / Workload / Executive views → a "View" sheet offering Board and List only, others show "Open on a computer for this view".

### W5.2 Tickets (T1–T3)
- Tests: sections Needs you / Open (Closed only under All); T2 queue grid → category → template fills fields; T3 stepper, key facts, iDesk messages read-only bubbles, ⋯ Schedule follow-up. Files: `Components/Phone/Tickets/*`, `Pages/Tickets.razor(.cs)`, `Pages/TicketNew.razor(.cs)` (as sheet on phone), `css/mobile/tickets.css` (new).

### W5.3 News (N1–N3)
- Tests: hero + compact rows + chips; N2a reading (27 px title, 17/1.6 body, edge-to-edge images); N2b condolence calm style (no cover, no likes, serif); N3 compose full screen with toolbar on keyboard (reuse W3 keyboard toolbar component generalised as `KeyboardToolbar`). Files: `Components/Phone/News/*`, `Pages/News.razor`, `NewsCompose.razor`, `NewsDrafts.razor` (list rows, no mockup), `css/mobile/news.css`.

### W5.4 Files (F1–F4)
- Tests: F1 locations (Recent / Starred / Shared, one row per NAS with connection state); F2 breadcrumb, sort menu, List/Grid; F3 full-screen preview + share sheet (Download, Share link, …); F4 upload sheet (Camera `capture`, Photos, Files, New folder) with progress rows + cancel — same sheet reused by Tasks/Chat attach. Files: `Components/Phone/Files/*`, `Pages/Files.razor(.cs)`, `Pages/FileEditor.razor` (ONLYOFFICE full screen, `mobile` editor type), `css/mobile/files.css`.

### W5.5 Profile / Settings / Search (S1–S3)
- Tests: S1 photo/email/extension editable, HRIS fields locked, no NIK/position; S2 appearance previews, Notifications, Mail density, Calendar sharing, Edit tabs (opens W1 sheet), Spell check; S3 global search across modules, progressive providers, empty state with recents (clear one / all) and frequent people. Files: `Pages/Profile.razor`, new `Pages/SettingsPhone.razor` `@page "/settings"` (phone hub that links to existing dialogs; desktop route redirects to `/profile`), `Components/Phone/Search/GlobalSearchPhone.razor` wrapping `CommandPaletteState`.

### W5.6 Admin (AD1–AD2)
- Tests: AD1 grouped list (People, Structure, Security, Communication, Integrations) admin-only; AD2a users list shows and searches NIK, role chips, filters; AD2b sheet labelled "Admin only". Files: `Components/Phone/Admin/*`, `Pages/Admin.razor`, `css/mobile/admin.css`. No mockup: Org chart editor, audit trail, HRIS sync → read-only card lists with "Edit on a computer".

### W5.7 Login (L1–L2)
- Tests: L1 Kapal Api Group · SJA logos light/dark, Employee ID + password, eye toggle; L2 three onboarding cards once after first sign-in (pref `onboarded`), card 2 opens Edit tabs + theme, card 3 = notification card. Files: `Pages/Login.razor`, `Components/Auth/AuthBrand.razor`, new `Components/Phone/Onboarding.razor`, `css/mobile/login.css`; `/forgot-password`, `/reset-password`, `/set-password` share the L1 style (no mockup).

QA: protocol for every route above; NIK sweep: Playwright greps all phone screens' text for `/\b\d{4,}\b/` patterns matching the signed-in demo users' NIKs (must only appear under `/admin`).

---

## Current features without a mockup (mapping)

| Feature | Phone treatment | Wave |
|---|---|---|
| Topbar timer (`TopbarTimer.razor`) | row in More sheet + small pill in `.ph-nav` when running | 1 |
| Command palette (Ctrl+K) | S3 search screen; topbar search icon opens it | 1 / 5 |
| Tutorial host / practice bar | hidden on phone (tours are desktop-written) | 1 |
| Session lock host | unchanged (full-screen already) | — |
| Chat pins/mentions/saved/search, group settings, new chat, poll, sticker editor | `PhoneScreen` lists / full sheets | 2 |
| Mail advanced, mass mail panel | stacked `PhoneScreen` form; mass >50 confirm unchanged | 2 |
| Notification gateway | no UI | — |
| Notes import/export | export in ⋯ sheet; import "Open on a computer" | 3 |
| Calendar month/day, sharing, delegates | ⋯ sheet reusing existing dialogs in `BottomSheet` | 4 |
| Projects table/timeline/portfolio/workload/executive | Board + List only; others "Open on a computer" | 5 |
| News drafts | list rows | 5 |
| Device link `/devices/link` | single centered card | 5 |
| Admin org chart, audit, HRIS sync, holidays, meeting rooms | read-only lists, edit on desktop | 5 |
| SJA Hall `/hall` | out of scope (not on prod) | — |
| NotFound | empty state component | 5 |

## Risks (cross-wave)

1. **Desktop regression** — guarded by phone-only media query, `PhoneOnly/DesktopOnly` DOM split, the CSS guard test (extend `mobile-breakpoints.test.cjs`: every rule in `css/mobile/*.css` outside the phone query must be `display:none` on a `.ph-`/`.m-` selector) and the 1440/768 pixel diff per wave.
2. **Service worker** serving stale builds — no-store bypass, network-first HTML, fingerprint-only cache-first, kill switch, `SW_VERSION` bump per deploy.
3. **`PhoneState` flip** on rotate/resize mid-edit re-renders different markup — page state lives in the page, not the phone component; drafts already persisted (chat drafts, compose).
4. **Dual subscriptions / memory** — phone components are presentational; no hub or timer registration inside them (review checklist item).
5. **TWA** — HTTPS + proxy IPS on `ipop.santos.co.id`, keystore loss, DAL fingerprint mismatch (URL bar appears). APK tooling must be installed first (see below).
6. **iOS** — no Web Push outside installed PWA (iOS ≥16.4 only when added to Home Screen); `/app` iPhone steps say so.
7. **Default tab change** removes Dashboard/Projects from the bar — More sheet + Edit tabs; announce in release notes.

## Tooling status on this machine (checked 2026-10-16)

- `java -version` → OpenJDK **21.0.11** (Microsoft) at `JAVA_HOME`; also Temurin JDK 25 and JRE 8. `keytool` available → keystore can be generated now. Bubblewrap expects JDK 17 (it downloads its own).
- `where bubblewrap` → **not installed**. `node -v` → **v24.14.0** (OK for `@bubblewrap/cli`).
- `ANDROID_HOME`/`ANDROID_SDK_ROOT` → **unset**, no `%LOCALAPPDATA%\Android\Sdk`, no `~/.bubblewrap`.
- ⇒ APK cannot be built today; it can after `npm i -g @bubblewrap/cli` + `bubblewrap doctor`/`init` downloads JDK 17 and the Android SDK (≈1 GB, internet required). Everything else in Wave 1 (PWA, SW, `/app`, assetlinks) does not need it.
