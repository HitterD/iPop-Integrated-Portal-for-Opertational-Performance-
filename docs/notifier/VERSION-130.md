# iPop Notifier 1.3.0 — versioning, announced updates, browser sign-in

Implementation branch: `notifier-130`, based on production `fc6465a` (Notifier 1.2.2).

**Not deployed.** Production feed and app remain untouched. Publishing to production requires the user's explicit deployment instruction.

## Approved behavior

- Semantic versions (`MAJOR.MINOR.PATCH`), one version used by assembly and package. Visible in tray and web Account download row.
- Download updates in the background. Offer **Update now / Later**, with plain-English release notes.
- Later asks again after four hours. After 24 hours, install automatically when the Windows session has been idle for five minutes.
- Mandatory releases install without a choice, but no release may interrupt an active remote session or call.
- Preserve update deadlines across app/PC restarts; disable Velopack startup auto-apply so a restart cannot bypass Later.
- On server publication, SignalR `notifierUpdate` wakes apps to check, staggered across a minute. Startup and six-hour polling provide fallback.
- ICT/Admin pilot channel first, then explicit promotion to everyone. Admin screen is read-only.
- Browser sign-in creates an independent Notifier session. Browser logout does not log out the app.
- Account changes must tear down the previous feed, remote sessions, windows and old actionable toasts before installing new credentials.

## Security corrections found during verification

The initial IP-only design is insufficient: several PCs or Windows profiles can share an address, and an unsolicited protocol link can cause login CSRF. IP is only a discovery hint, not proof of identity or same-PC ownership.

Use a short-lived browser-local nonce, an app-held PKCE verifier, and a fixed same-origin browser callback. The browser must prove it initiated the flow before issuing the account's code; the app must prove it initiated the challenge before redeeming it. Do not accept unsolicited `signin` codes. Confirm a different account locally before switching. Never pass access/refresh tokens in protocol URLs or log them.

## Rollout caveat: first upgrade from 1.2.2

Version 1.2.2 only reads the public Velopack feed and does not know about the API-selected pilot channel or the new update popup. Therefore:

1. Keep the production public feed on 1.2.2 during QA.
2. Install the 1.3.0 pilot installer explicitly on the pilot PCs (per-user, no admin).
3. Test 1.3.0 → a newer QA version to exercise the new Update now / Later behavior, persistence, mandatory update, busy deferral, and session preservation.
4. Only after explicit approval, promote 1.3.0 to the public feed. Existing 1.2.2 clients perform their existing silent update behavior once; subsequent releases use the new flow.

Do not claim all existing clients can honor the new popup before they have installed the code implementing it.

## Code signing

Read-only certificate inspection on 2026-10-26 found SJA root certificates trusted on this PC, but no code-signing certificate with a private key in CurrentUser/My or LocalMachine/My. No signing key was created or obtained. Installer remains unsigned. Internal CA trust alone neither signs the package nor guarantees public SmartScreen reputation.

## Verification artifacts

Isolated QA API: `http://127.0.0.1:6491`, detached build worktree `notifier-130-qa`, copy database used by `mm-qa` (not production). Other hosted workers and outbound integrations disabled. No production API/Web/Notifier process stopped.

- Final verification: **2995 .NET tests passed, 0 failed**, after the security/lifecycle corrections and final waiting-store capacity guard. Windows Notifier build succeeded with 0 errors (existing WindowsBase reference warning).
- Strengthened API smoke on isolated port 6491 passed: PKCE challenge, wrong verifier refused, correct verifier succeeds, replay refused, missing challenge refused, Admin releases, installer version, independent Notifier session after browser logout.
- Browser nonce tests: 3 passed (foreign/expired/replayed nonce, callback URL cleanup, scheme binding, protocol allowlist). Update-button recovery test: 1 passed.
- Publishing regression tests: 24 passed in PowerShell 5.1 using fake dotnet/vpk and fresh temporary folders; production and existing QA feeds untouched.
- Playwright renders local Notifier HTML with a mock WebView bridge: update light/dark buttons and footer bounds, browser sign-in button, JavaScript errors. Screenshots under `.scratch/e2e/` (not included in git).
- Actual installed-app update/restart and pilot-PC 10.10.6.51 verification are separate acceptance gates, not implied by unit tests or mocked HTML.
- Native account-confirmation, Windows toast activation, LiveKit teardown and deterministic refresh/link interleavings were not runtime-tested. Session-race handling was source-traced and compiled, not verified end to end.
- Build also reports existing NuGet vulnerability advisories for MessagePack, ImageSharp and Cryptography.Pkcs; these dependencies were not upgraded in this feature. Do not treat a green build as a clean security audit.
