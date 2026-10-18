# "Sign in with iPop" — OpenID Connect provider design (phase A4)

iPop's web app keeps its session in the browser (JWT + refresh token in local storage), not in a server cookie, so the
OIDC authorize step cannot see "who is signed in" by itself. Flow:

1. App (iDesk) redirects the browser to `https://ipop.santos.co.id/connect/authorize?client_id=…&redirect_uri=…&code_challenge=…`.
2. The API's authorize endpoint (OpenIddict passthrough) has no iPop cookie → it redirects to the Web page
   `/sso/authorize?req=<opaque id>` and keeps the validated request parameters for 10 minutes (server cache, keyed by id).
3. `/sso/authorize` (Blazor, inside the normal guard): if the user is not signed in, the existing /login runs first and
   returns here. Signed in → shows "Continue to iDesk as Bagas" (auto-continues for first-party clients; no consent screen
   for internal apps) → calls `POST /api/sso/approve { req }` with the user's normal access token.
4. `/api/sso/approve` checks the token, loads the stored request, and answers with a one-time `continueUrl`
   (`/connect/authorize?…&ipop_grant=<10 s single-use code bound to that user + request>`). The Web page navigates there.
5. The authorize endpoint sees `ipop_grant`, validates it (single use, same request, not expired), and OpenIddict issues
   the authorization code to the app's redirect URI. Tokens carry `sub`, `nik`, `name`, `email`, `department`, `site`.

Password grant (`grant_type=password`, for the NIK + iPop password form inside iDesk): confidential clients only,
reuses `IAuthWorkflow.LoginAsync` (lockout, audit, default-password rule) — the client never stores the password.

Keys: RS256 signing + encryption certificates generated once and stored in the DataProtection-protected app setting
`Oidc:Keys` (or `Oidc:SigningCertificate` path in config). Clients are registered by an Admin page (client id, secret,
redirect URIs, allowed grants); `idesk` is seeded disabled until ICT enables it.

Silent "Open in iDesk" from the Notifier: the Notifier opens the Web `/sso/authorize?…` with its own session (the app
passes a one-time login code to the browser), so the user is not asked again.
