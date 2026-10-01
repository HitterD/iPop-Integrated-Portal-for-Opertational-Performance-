# iPop as a desktop app: GPO install and autostart

Goal: when someone signs in to Windows, iPop opens on its own, in its own window, already signed
in. Users never type the address or the password again.

What this guide relies on:

- **Session:** iPop sessions last 90 days and slide. Every app open refreshes the token for another
  90 days (`AuthWorkflow.RefreshLifetime`). "Keep me signed in" is ticked by default on the
  login page, so after one sign-in per PC, iPop stays signed in as long as the PC opens it at
  least once every 90 days.
- **Install:** iPop is a PWA (`manifest.webmanifest` + `sw.js`), so Edge and Chrome can install it
  as an app with a taskbar icon. Browsers only allow this over **https**, so use
  `https://ipop.santos.co.id`. The plain `http://10.10.6.13:…` address can't be installed. Users can
  also install it themselves from Account › App › **Install iPop**.

The domain is `SJA.COM` (logon server `DC2`). The policies below are Microsoft Edge policies
(computer side). They need the Edge ADMX templates in the central store
(`\\SJA.COM\SYSVOL\SJA.COM\Policies\PolicyDefinitions`). Download them from
<https://www.microsoft.com/edge/business/download> → *Get policy files*.

## 1. Create the GPO

1. On DC2, open **Group Policy Management**.
2. Create a GPO named `iPop - Edge app`, and link it to the OU that holds the office PCs. Start
   with a pilot OU of 2–3 PCs.
3. Edit it: **Computer Configuration › Policies › Administrative Templates › Microsoft Edge**.

## 2. Force-install iPop

**Microsoft Edge › Web App Settings › Configure list of force-installed Web Apps**
(`WebAppInstallForceList`) → *Enabled*, value:

```json
[
  {
    "url": "https://ipop.santos.co.id/",
    "default_launch_container": "window",
    "create_desktop_shortcut": true,
    "custom_name": "iPop"
  }
]
```

Edge installs iPop for every user on the PC at its next policy refresh. It adds a Start menu
entry and a desktop shortcut, and iPop opens in its own window instead of a tab. Users can't
uninstall it while the policy applies.

## 3. Open iPop at Windows sign-in

**Microsoft Edge › Web App Settings › Web App management settings** (`WebAppSettings`) →
*Enabled*, value:

```json
[
  {
    "manifest_id": "https://ipop.santos.co.id/",
    "run_on_os_login": "run_windowed"
  }
]
```

- `run_windowed` opens iPop as soon as the user signs in to Windows.
- `allowed` lets each user decide instead: right-click the app › *Start app when you sign in*.

**Fallback for older Edge builds:** if `run_on_os_login` is ignored, put a shortcut in the startup
folder with a logon script, or use *User Configuration › Preferences › Windows Settings ›
Shortcuts*. The shortcut target is:

```
"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --app=https://ipop.santos.co.id/
```

The startup folder is `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\iPop.lnk`.

## 4. Keep Edge (and notifications) alive

These are recommended together with `push-notifications-deployment.md`:

| Policy | Value | Why |
|---|---|---|
| `BackgroundModeEnabled` (*Continue running background apps when Microsoft Edge is closed*) | Enabled | Windows toasts still arrive after the iPop window is closed |
| `NotificationsAllowedForUrls` | `https://ipop.santos.co.id` | No "Allow notifications?" prompt |
| `StartupBoostEnabled` | Enabled | iPop's window opens faster at sign-in |

Don't clear cookies or site data on exit (`ClearBrowsingDataOnExit` / `SavingBrowserHistoryDisabled`
must stay off). The iPop session is kept in the site's local storage, and wiping it signs the user
out every day.

## 5. Roll out and check

1. On a pilot PC, run `gpupdate /force`, then sign out and back in.
2. Open `edge://policy` and confirm that `WebAppInstallForceList` and `WebAppSettings` show
   *Status OK*.
3. Open `edge://apps` and confirm that iPop is listed and marked *Installed by your organization*.
4. Sign out of Windows and back in. iPop should open by itself. The very first time, sign in to
   iPop once. Every later sign-in lands straight in the app.
5. When the pilot looks right, link the GPO to the remaining OUs.

## Later: real single sign-on (Kerberos)

This guide removes the *repeated* sign-in. The first sign-in on each PC is still manual. True
"signed in because you're signed in to Windows" needs the API to accept Kerberos/Negotiate for
`SJA.COM`. That work includes an SPN for the iPop host, an `AuthServerAllowlist` Edge policy for
`ipop.santos.co.id`, and mapping the AD account to the iPop NIK. It is planned as a separate
project.
