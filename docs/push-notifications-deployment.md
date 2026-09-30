# Windows notifications (Web Push): deployment and GPO

iPop shows Windows toasts through Web Push: the API sends an encrypted message to the browser's
push service (Chrome → Google FCM, Edge → Microsoft WNS), and the service worker
(`src/IPop.Web/wwwroot/sw.js`) shows it as a toast. It works while iPop is closed, as long as
Chrome is running (by default Chrome keeps running in the background after the window closes).

Users only have to click **Aktifkan** on the banner after sign-in and then **Allow** in Chrome's
prompt. The steps below make that automatic across the company.

## 1. Server configuration (API)

The VAPID keypair lives in the git-ignored `src/IPop.Api/appsettings.Local.json` (or environment
variables `Push__VapidPublicKey` / `Push__VapidPrivateKey`):

```json
{
  "Push": {
    "VapidPublicKey": "<base64url public key>",
    "VapidPrivateKey": "<base64url private key>",
    "Subject": "mailto:it@santos.co.id"
  }
}
```

- **Missing keys = push off.** `/api/push/config` reports `enabled: false`, the banner and
  Settings section explain it, and nothing is sent.
- **Never rotate the keypair.** Every browser subscription is tied to the public key. A new key
  silently breaks all existing subscriptions until each user signs in again (PushState then
  re-subscribes with the new key).
- Keep the private key out of Git, logs and the browser. Only the public key is ever sent to
  the client.
- To generate a new pair (only for a brand-new deployment):
  `npx web-push generate-vapid-keys` (or any P-256 VAPID generator; values are base64url).

Migration `AddPushSubscriptions` adds the `PushSubscriptions` table and
`NotificationPreferences.PushShowPreview`. It is additive.

## 2. Network

Web Push only works in a secure context. Use **https://ipop.santos.co.id**. The plain LAN
address `http://10.10.6.13:5103` now redirects there (`index.html`); on http Chrome refuses
notifications and service workers.

Outbound access that must stay open:

| From | To | Why |
|---|---|---|
| API host (10.10.6.13) | `https://fcm.googleapis.com` (443) | send pushes to Chrome |
| API host | `https://*.notify.windows.com` (443) | send pushes to Edge |
| Client PCs (Chrome) | `mtalk.google.com` TCP 5228 (fallback 443) | Chrome's push channel |
| Client PCs (Edge) | `*.wns.windows.com` (443) | Edge's push channel |

If the proxy/IPS blocks these, subscribing hangs or toasts never arrive (the in-app bell still
works).

## 3. Group Policy (optional, recommended)

Chrome ADMX templates: <https://chromeenterprise.google/browser/download/> →
`Computer/User Configuration → Administrative Templates → Google → Google Chrome`.
Edge has the same policy names under `Microsoft Edge`.

**Allow notifications for iPop without the prompt**

- Policy: *Content settings → Allow notifications on these sites* (`NotificationsAllowedForUrls`)
- Value: `https://ipop.santos.co.id`

With this policy the "Allow" prompt never appears: after sign-in iPop subscribes silently and
toasts start working. The banner does not show, because permission is already granted.

**Install iPop as an app (optional)**

- Policy: *Web App Settings → Web App installation force list* (`WebAppInstallForceList`)
- Value (JSON):

```json
[{ "url": "https://ipop.santos.co.id/", "default_launch_container": "window", "create_desktop_shortcut": true }]
```

This installs iPop as a standalone window with its own taskbar icon. Toasts then show the iPop
icon and name.

**Keep Chrome running in the background (toasts while all windows are closed)**

- Policy: *Continue running background apps when Google Chrome is closed* (`BackgroundModeEnabled`) = Enabled

Registry equivalent (HKLM\SOFTWARE\Policies\Google\Chrome):

```
NotificationsAllowedForUrls\1 = "https://ipop.santos.co.id"   (REG_SZ)
BackgroundModeEnabled         = 1                              (REG_DWORD)
```

Verify on a client at `chrome://policy`.

## 4. Windows side

Toasts follow Windows settings. If a user sees nothing:

1. **Settings → System → Notifications**: notifications on, and **Google Chrome** (or the iPop
   app) on.
2. **Do not disturb** / Focus off (or add Chrome to its priority list).
3. In Chrome, the padlock next to the address → Site settings → Notifications = Allow.
4. iPop → **Pengaturan notifikasi → Notifikasi Windows → Kirim tes**.

## 5. Behaviour reference

- Server always pushes; the service worker skips the toast when an iPop window is focused and
  visible (the in-app banner/call modal covers it). The test toast is always shown.
- Chat: one toast per room (`chat:{roomId}`), newer messages replace it quietly with
  "(+N pesan lain)". Preview on by default; users can hide message text in Settings.
- Calls: sticky toast with **Jawab/Tolak**, closed on every device once the call is answered,
  declined, or missed. "Tolak" uses a 2-minute signed token (no sign-in needed from the service
  worker). The ring push TTL equals the ring timeout, so a late ring never arrives.
- Respected: quiet hours, muted types, muted/archived rooms, blocked senders. Emergencies and
  calls bypass them.
- Each browser is one subscription (Settings lists them). Sign-out removes this browser's
  subscription. The push service reporting 404/410 deletes it automatically.
