# Firebase setup for the native iPop Android app (Google Workspace)

The native app gets notifications through Firebase Cloud Messaging (FCM). FCM is free on the Spark plan: no billing account, no credit card. A Google Workspace administrator does this once, in about 15 minutes.

Menu names in the Google consoles change from time to time. If a label differs slightly, look for the closest match.

## Part A. Workspace Admin console (once)

1. Open https://admin.google.com with the Workspace super admin account.
2. Go to **Apps → Additional Google services → Google Cloud**.
3. Make sure **Service status** is **ON** for the account that will create the project. Firebase runs on Google Cloud, so the Firebase console fails without this.

## Part B. Create the Firebase project

1. Open https://console.firebase.google.com with the same company account.
2. Click **Create a project**. Name it `ipop-santos`.
3. If asked for a parent, choose your company **organization**. That keeps the project owned by the company, not by one person.
4. Turn **Google Analytics** off. Click **Create project**.
5. Stay on the free **Spark** plan. Never upgrade to Blaze; iPop does not need it.

## Part C. Register the Android app

1. On the project overview, click the **Android** icon.
2. Package name: `id.co.santos.ipop`. Nickname: `iPop`. Leave SHA-1 empty.
3. Click **Register app**, then download **google-services.json**.
4. Skip the SDK steps and click **Continue to console**.

## Part D. Allow a service-account key (Workspace organizations only)

Organizations created after May 2024 block service-account keys by default. The iPop server runs in the office, not on Google Cloud, so it needs one key. Allow it for this one project only:

1. Open https://console.cloud.google.com/iam-admin/iam and pick your **organization** in the project picker.
2. Click **Grant access**. Principal: your admin email. Role: **Organization Policy Administrator**. Save. (A super admin can grant this to itself.)
3. Switch the project picker to **ipop-santos**.
4. Open **IAM & Admin → Organization policies**.
5. Search **Disable service account key creation**. There may be two entries (`iam.disableServiceAccountKeyCreation` and `iam.managed.disableServiceAccountKeyCreation`). For each one that shows **Enforced**:
   - **Manage policy → Override parent's policy → Enforcement: Off** (for the managed one: **Not enforced**) → **Set policy**.
6. Leave the rest of the organization unchanged.

If neither entry is enforced, skip this part.

## Part E. Download the server key

1. Back in the Firebase console: gear icon → **Project settings → Service accounts**.
2. Click **Generate new private key → Generate key**. A JSON file downloads.
3. Treat it like a password: don't email it, commit it, or put it on the NAS.
4. Also check **Project settings → Cloud Messaging**: **Firebase Cloud Messaging API (V1)** must say **Enabled**.

## Part F. Hand the files to the iPop server

Copy both files to the server PC (10.10.6.13), for example over the admin share or a USB stick:

| File | Put it at |
|---|---|
| Service-account key (Part E) | `C:\ipop-ops\firebase\service-account.json` |
| `google-services.json` (Part C) | `C:\ipop-ops\firebase\google-services.json` |

The iPop administrator then adds this to `src\IPop.Api\appsettings.Local.json` and restarts the API. The deploy does this step:

```json
"Fcm": {
  "ProjectId": "<project_info.project_id>",
  "ServiceAccountPath": "C:/ipop-ops/firebase/service-account.json",
  "ApplicationId": "<client[0].client_info.mobilesdk_app_id>",
  "ApiKey": "<client[0].api_key[0].current_key>",
  "SenderId": "<project_info.project_number>"
}
```

## Part G. Check

- `https://ipop.santos.co.id/api/push/native/config` returns `"enabled": true`.
- On a phone with the new app, sign in, then Settings → Notifications → **Send test**. The notification shows as **iPop**, and tapping it opens the app.

## Security notes

- Only the service-account file is secret. The values in `google-services.json` are public client identifiers.
- The key only lets the server send notifications to iPop phones. To revoke it: Google Cloud console → IAM & Admin → Service accounts → the `firebase-adminsdk` account → Keys → delete, then generate a new one.
