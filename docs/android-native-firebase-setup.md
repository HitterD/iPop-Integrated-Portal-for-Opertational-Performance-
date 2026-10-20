# Firebase setup for the native iPop Android app

The native app gets notifications through Firebase Cloud Messaging (FCM). FCM is free. This setup does not need a billing account or a credit card. One person with a company Google account does it once. It takes about 10 minutes.

## 1. Create the project

1. Open https://console.firebase.google.com while signed in with the company Google account.
2. Click **Create a project**. Name it `ipop-santos`.
3. Turn **Google Analytics** off, since it is not needed. Click **Create project**.

## 2. Register the Android app

1. On the project overview, click the **Android** icon.
2. Enter the package name `id.co.santos.ipop`. Use the nickname `iPop`. Leave the SHA-1 empty.
3. Click **Register app**.
4. The next screen offers `google-services.json`. Download it. iPop does not put this file in the APK, but the server needs three values from it (step 4).
5. Skip the remaining SDK steps and click **Continue to console**.

## 3. Download the server key

1. Click the gear icon, then **Project settings**, then the **Service accounts** tab.
2. Click **Generate new private key**, then **Generate key**. A JSON file downloads.
3. Treat this file like a password. Do not email it, do not commit it, and do not put it on the NAS.

Also confirm that **Cloud Messaging** shows **Firebase Cloud Messaging API (V1): Enabled**. If it is disabled, click the menu on that row and enable it.

## 4. Hand the files to the iPop server

Copy both files to the server PC (10.10.6.13):

| File | Put it at |
|---|---|
| Service-account key (step 3) | `C:\ipop-ops\firebase\service-account.json` |
| `google-services.json` (step 2) | `C:\ipop-ops\firebase\google-services.json` |

Then tell the iPop administrator. They add this section to `src\IPop.Api\appsettings.Local.json` on the server and restart the API:

```json
"Fcm": {
  "ProjectId": "<project_info.project_id>",
  "ServiceAccountPath": "C:/ipop-ops/firebase/service-account.json",
  "ApplicationId": "<client[0].client_info.mobilesdk_app_id>",
  "ApiKey": "<client[0].api_key[0].current_key>",
  "SenderId": "<project_info.project_number>"
}
```

The values in angle brackets come from `google-services.json`. `ApplicationId`, `ApiKey` and `SenderId` are public client values, and the app downloads them from `/api/push/native/config`. Only the service-account file is secret.

## 5. Check

- `https://ipop.santos.co.id/api/push/native/config` returns `"enabled": true`.
- Open the iPop app on a phone and sign in. In Settings, then Notifications, **Send test** shows a notification from **iPop** (not from the browser). Tapping it opens the app.

## Costs and limits

FCM messaging is free with no quota that iPop can reach. The project stays on the free **Spark** plan. Do not upgrade it to Blaze. Nothing in iPop needs Blaze.
