# iPop native Android app (2.0.0)

Native Kotlin shell that replaces the TWA. Kotlin owns notifications, calls, ringtone, CA trust and
updates; every page is the existing Blazor app in an in-app WebView from `https://ipop.santos.co.id`.
Visual spec: `docs/mockups/android-native-app.html` (frames 1–6).

Package `id.co.santos.ipop`, versionCode 2, signed with the TWA key, so it installs over the TWA.
The launcher component keeps the TWA's name (`.LauncherActivity`, an alias), so existing home-screen
icons and the pinned Chat/Mail/Calendar shortcuts keep working.

## Build

```powershell
$env:JAVA_HOME = "F:\ipop-tools\jdk17"
cd android\native
.\gradlew.bat assembleDebug     # app\build\outputs\apk\debug\app-debug.apk
.\gradlew.bat assembleRelease   # app\build\outputs\apk\release\app-release.apk (R8 + shrinkResources, signed)
```

- `local.properties` (gitignored) must contain `sdk.dir=F\:\\ipop-tools\\android-sdk`.
- Release signing reads `C:/ipop-ops/android/ipop-release.keystore` (alias `ipop`) and the password
  from the file `C:/ipop-ops/android/keystore.secret` at build time. Nothing is hardcoded or printed.
  Override with `-PipopKeystore=... -PipopKeystoreSecret=...`. Without those files the release APK is unsigned.
- Verify:
  `F:\ipop-tools\android-sdk\build-tools\36.1.0\apksigner.bat verify --print-certs app\build\outputs\apk\release\app-release.apk`
  → SHA-256 must be `d13ea89e2c4a4bfc27f0ec47dc894ca39ab06e03e3d85b33be2b8f90c6d5d111` (same as the TWA / assetlinks.json).
- Gradle 8.11.1 (wrapper), AGP 8.9.1, Kotlin 2.1.21, compileSdk 36, targetSdk 35, minSdk 26.
  No AppCompat / Material / Compose: framework `Activity` + programmatic views.

## Server endpoints the app calls

All on `https://ipop.santos.co.id`.

| Method | Path | Auth | Used for |
|---|---|---|---|
| GET | `/dashboard?source=android` | cookie (WebView) | start page |
| GET | `/api/push/native/config` | none | `{"enabled","projectId","applicationId","apiKey","senderId"}`; cached; `enabled=false` or non-200 = no FCM |
| POST | `/api/push/native/devices` | Bearer | `{"token","platform":"android","appVersion":"2.0.0","deviceName":Build.MODEL}` after `onSignedIn()` / token refresh |
| POST | `/api/push/native/devices/remove` | Bearer | `{"token"}` on `onSignedOut()` |
| POST | `/api/push/call-action` (on `apiBase` from the push if https, else the host above) | none (signed `actionToken`) | `{"token":actionToken,"action":"decline"}` for Tolak |
| POST | `/api/chat/rooms/{roomId}/messages` | Bearer (stored session) | notification "Balas": `{"content":text,"idempotencyKey":uuid}` |
| POST | `/api/chat/rooms/{roomId}/read` | Bearer (stored session) | notification "Tandai dibaca", body `{}` |
| GET | `/api/app/android/latest` | none | `{"versionCode","versionName","sizeBytes","url","notes":[],"minSupported"}`; 404 = silent; max once per 6 h |
| GET | `url` from the above (default `/download/ipop.apk`) | none | APK download into app cache, then the package installer |

Notes for the server side:
- **`POST /api/chat/rooms/{roomId}/read` does not exist yet** in `ChatEndpoints.cs` (read state is
  currently set through the SignalR hub `MarkRead(messageId)`). Until it is added, "Tandai dibaca" only
  dismisses the notification locally.
- `SendMessageBody` has a non-nullable `IdempotencyKey`, so the app sends one with each reply.
- Pushes are data-only FCM messages. String fields: `kind` (chat|notif|call|close|test), `title`, `body`,
  `url`, `tag`, `callId`, `actionToken`, `apiBase`, `roomId`, `sender`, `channel`
  (chat|mail|calls|meetings|tickets|news|system), optional `urgent="1"`. `FcmTransport.DataOf` does not
  send `sender` today; the app falls back to `title` (and strips an `Incoming call · ` prefix for calls).
- On 2026-10-20 both `/api/push/native/config` and `/api/app/android/latest` return 404 on prod (server
  commit 2b7186e not deployed), so FCM stays off and the update check is silent until it is.

## Web side: `window.iPopAndroid`

Exposed only to pages on `https://ipop.santos.co.id` (every method re-checks the current top URL).
`src/IPop.Web/wwwroot/js/native-app.js` already wraps it as `window.ipopNative`.

| Method | Returns | Notes |
|---|---|---|
| `getAppVersion()` | `"2.0.0"` | presence = native app; the UA also ends with ` iPopAndroid/2.0.0` |
| `getFcmToken()` | string | empty until Firebase has a token |
| `setSession(accessToken, expiresAtEpochMs)` | – | after every login/refresh; stored in app-private prefs for Balas / Tandai dibaca; empty token clears |
| `onSignedIn()` | – | native asks the page for `window.iPopNativeAuth()` (bearer or null), falls back to the stored session, and registers the FCM token |
| `onSignedOut()` | – | unregisters the token (remove endpoint + `deleteToken`), clears session, chat history and all notifications |
| `openSettings(kind)` | – | `notifications` \| `fullscreen` \| `battery` (anything else = app details) |
| `getPermissionState()` | JSON string | `{notifications, fullscreen, battery, mic, camera}` → `granted` \| `denied` \| `default` |
| `setBadge(n)` | – | no-op (launcher badges follow notifications) |
| `callStarted(name)` / `callEnded()` | – | "Sedang menelepon" notification with Tutup / Bisukan (mic foreground service when mic is granted, else a plain ongoing notification) |
| `saveBase64(name, mime, b64)` | – | internal: used by the injected download shim |

Tutup / Bisukan arrive in the page as
`window.dispatchEvent(new CustomEvent('ipop-native', {detail:{action:'hangup'|'mute'}}))`.
The web must define `window.iPopNativeAuth = () => bearerOrNull` (native-app.js does).

Navigation from a notification: if the page is loaded, native calls `Blazor.navigateTo(path)` (or
`history.pushState` + `popstate`), otherwise `loadUrl`. Call answer opens `/calls?answer={callId}`,
"Pesan" on the call screen opens `/chat?new=dm`.

## Behaviour summary

- **CA trust:** `res/raw/sja_root_ca.pem` + `network_security_config.xml` (system + SJA) for native HTTP.
  WebView ignores that file, so `onReceivedSslError` proceeds only for host `ipop.santos.co.id`, only for an
  untrusted-issuer error (never expired / mismatch / not-yet-valid), and only when the leaf validates against
  a trust manager that contains *only* the bundled SJA root and its SAN covers the host. Everything else is
  cancelled. DownloadManager runs in another process and does not see the SJA root, so iPop-host downloads
  are fetched in-app (HttpURLConnection) and written to Download via MediaStore.
- **Downloads:** iPop host → in-app fetch with the WebView cookie + Content-Disposition; other hosts →
  DownloadManager with the cookie header; `blob:` / `data:` links (how the web saves authenticated files)
  are caught by a small injected shim and passed to `saveBase64`. The shim also delays `URL.revokeObjectURL`
  by 60 s so the blob is still readable.
- **Uploads:** `onShowFileChooser` → chooser with camera capture (FileProvider) + documents/gallery,
  multiple when the input allows it.
- **Mic/camera:** WebView `onPermissionRequest` from the iPop origin → runtime permission → grant.
- **Links:** other hosts open in a Custom Tab; `tel:`/`mailto:`/`intent:` go to the system.
- **Channels:** chat, mail, calls (silent; the call screen rings), meetings, tickets, news, system, ongoing_call.
- **Chat notifications:** one MessagingStyle notification per `tag`, last 6 messages kept, Balas (RemoteInput)
  and Tandai dibaca when a session token is stored; without one, Balas opens the conversation.
- **Calls:** CallStyle notification with full-screen intent (45 s timeout) → `IncomingCallActivity` over the
  lock screen, looping default ringtone (respects silent/vibrate) + vibration `[0,800,400,800]`. Angkat
  dismisses the keyguard and opens `/calls?answer={id}`; Tolak posts decline and leaves the phone locked;
  Ingatkan declines and posts a reminder ~5 min later (inexact `AlarmManager`); a `close` push stops it all.
- **First run:** shown on first launch and whenever notifications or full-screen intent are off, at most
  once per process. **Offline:** native screen on main-frame errors or no network; auto-retries when a
  network comes back. **Update:** bottom sheet; no "Nanti saja" when `versionCode < minSupported`.

## Debug QA (debug builds only, `src/debug`)

```sh
ADB="adb -s emulator-5554"
# incoming call
$ADB shell am broadcast -a id.co.santos.ipop.debug.PUSH -p id.co.santos.ipop --es kind call \
  --es title "'Incoming call · RM Bagastyo'" --es tag call:1111 --es callId 1111 --es actionToken x
# stop it
$ADB shell am broadcast -a id.co.santos.ipop.debug.PUSH -p id.co.santos.ipop --es kind close --es tag call:1111
# chat message (repeat to build the conversation)
$ADB shell am broadcast -a id.co.santos.ipop.debug.PUSH -p id.co.santos.ipop --es kind chat \
  --es title "'RM Bagastyo'" --es sender "'RM Bagastyo'" --es body "'tes'" --es tag chat:r1 \
  --es roomId 00000000-0000-0000-0000-0000000000a1 --es url /chat/00000000-0000-0000-0000-0000000000a1
# check the WebView SSL rule against the live certificate (logcat -s iPop)
$ADB shell am broadcast -a id.co.santos.ipop.debug.PUSH -p id.co.santos.ipop --es kind ssltest
# preview the update sheet (optionally forced)
$ADB shell am start -n id.co.santos.ipop/.MainActivity --ez debug_update_sheet true --ei debug_min_supported 3
```

Debug builds also enable WebView remote debugging (`chrome://inspect`). Screenshots go in `qa/` (gitignored).
The emulator's lock-screen credential must be unlocked once after boot, or no app activity resolves.
