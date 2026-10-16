# iPop Android app (TWA)

The APK is a Trusted Web Activity: Chrome opening https://ipop.santos.co.id full screen. It updates
with every web deploy; rebuild the APK only for a new icon, name, package or version.

**Never commit or share** the keystore, its passwords, the APK/AAB or `keystore*.properties`
(`.gitignore` blocks them). The keystore lives outside the repo: `C:/ipop-ops/android/`.

## 1. Tooling (once, needs internet, about 1 GB)

```powershell
node -v                      # >= 18 (machine has 24)
npm i -g @bubblewrap/cli
bubblewrap doctor
```

Bubblewrap needs **JDK 17** and the Android SDK command-line tools. Let `bubblewrap init`/`doctor`
download both into `%USERPROFILE%\.bubblewrap`, or point `jdkPath` / `androidSdkPath` in
`%USERPROFILE%\.bubblewrap\config.json` at existing installs. The machine's JDK 21 may be rejected
by `doctor`; use the downloaded 17.

## 2. Release key (once)

```powershell
New-Item -ItemType Directory -Force C:\ipop-ops\android
keytool -genkeypair -v -keystore C:\ipop-ops\android\ipop-release.keystore -alias ipop `
  -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=iPop, O=PT Santos Jaya Abadi, C=ID"
```

Type the passwords at the prompt. Store them **only** in the team password manager, never in files,
scripts, `.env` files or commits. For a build in one session you may set
`$env:BUBBLEWRAP_KEYSTORE_PASSWORD` / `$env:BUBBLEWRAP_KEY_PASSWORD` and close the window afterwards.

Back the keystore up to the NAS ops share plus one offline copy. If it is lost, no update can be
installed over the existing app; every user must uninstall and reinstall.

## 3. Fingerprint → Digital Asset Links

```powershell
keytool -list -v -keystore C:\ipop-ops\android\ipop-release.keystore -alias ipop
```

Copy the `SHA256:` line (format `AB:CD:…`, 32 pairs) into the **prod WebHost** config, for example
`appsettings.Production.json` next to `IPop.WebHost.dll` (fingerprints are public, the keystore is not):

```json
{ "AndroidApp": { "PackageName": "id.co.santos.ipop",
                  "Sha256CertFingerprints": [ "AB:CD:…" ],
                  "ApkPath": "C:/ipop-ops/android/ipop.apk" } }
```

Restart the WebHost, then check:

- https://ipop.santos.co.id/.well-known/assetlinks.json shows the package + fingerprint
  (`[]` means the fingerprint is missing or not 32 hex pairs);
- https://digitalassetlinks.googleapis.com/v1/statements:list?source.web.site=https://ipop.santos.co.id&relation=delegate_permission/common.handle_all_urls

## 4. Build

```powershell
cd android\twa
bubblewrap update        # first time: regenerates the Android project from twa-manifest.json
bubblewrap build         # asks for the keystore passwords
Copy-Item app-release-signed.apk C:\ipop-ops\android\ipop.apk
```

The WebHost serves that file at `/download/ipop.apk` (config `AndroidApp:ApkPath`); `/app` shows the
Download button as soon as it exists. For each new release bump `appVersionCode` (and
`appVersionName`) in `twa-manifest.json` first.

## 5. Test on a device

- Opens without a URL bar (= Digital Asset Links verified; a URL bar means a fingerprint/host mismatch).
- Push notification arrives with the app closed (notification delegation, `enableNotifications`).
- Back from a tab root leaves the app; Back inside closes sheets/details first.
- File upload and camera, call microphone permission, deep links from mail open inside the app.

## Prerequisite

The TWA only works on valid HTTPS end to end. Confirm `https://ipop.santos.co.id` loads reliably
through the proxy (the IPS stall seen on the http upstream) before handing out the APK.
