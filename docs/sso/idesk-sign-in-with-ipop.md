# iDesk × iPop — "Sign in with iPop" (integration guide for the iDesk team)

Status (2026-10-19): iPop provider built (branch `notifier`, behind `Oidc:Enabled`); iDesk side built on iDesk branch
`HitterD/sign-in-with-ipop` — see iDesk `docs/integrations/sign-in-with-ipop.md` for the final behaviour. Matching is by
**HRIS NIK only**; email never links accounts. iPop sets `IDesk:SignInWithIpop=true` to send "Open in iDesk" links
through `/v1/auth/oidc/start?returnTo=…`.

## Goal

1. On iDesk's login page: a **Sign in with iPop** button (one click, no password typed in iDesk).
2. iDesk's own login form also accepts **NIK + iPop password** (checked by iPop, never stored by iDesk).
3. Links from iPop / iPop Notifier ("Open in iDesk") land **already signed in**.
4. The same mechanism works for future company web apps — iPop is the single sign-in.

iPop becomes a standard **OpenID Connect (OIDC) provider**. iDesk is registered as a client. Nothing iDesk-specific is
hard-coded in iPop, so the next app only needs a new client registration.

## What iPop provides

| Item | Value (planned) |
|---|---|
| Issuer | `https://ipop.santos.co.id` |
| Discovery | `https://ipop.santos.co.id/.well-known/openid-configuration` |
| Authorize | `/connect/authorize` (Authorization Code + PKCE) |
| Token | `/connect/token` |
| UserInfo | `/connect/userinfo` |
| JWKS | `/.well-known/jwks.json` (RS256) |
| Logout | `/connect/logout` (RP-initiated) |
| Password check (for iDesk's own form) | `/connect/token` with `grant_type=password` — **confidential client only**, rate-limited, see §3 |

ICT registers iDesk once and gives the iDesk team: `client_id=idesk`, a `client_secret` (stored in iDesk's env, never in
git), and the allowed redirect URI(s), e.g. `https://idesk.santos.co.id/api/v1/auth/oidc/callback`.

**Claims in the ID token** (scope `openid profile email`):

| Claim | Example | Notes |
|---|---|---|
| `sub` | `8d2c…` | stable iPop user id |
| `nik` | `00012345` | HRIS employee ID — **the matching key** |
| `name` | `Bagas Anggoro` | |
| `email` | `bagas@santos.co.id` | may be empty for some staff |
| `department`, `site` | `ICT`, `SJA Sepanjang` | for display / routing |

NIK is shown to admins only in iDesk's UI, as in iPop (office rule).

## What iDesk must change

### 1. Account matching (backend)
- Users table already has `employeeId` (unique when not null) — use it for the NIK.
- On sign-in with iPop: find user by `employeeId = nik`.
  - Found → sign in.
  - Not found → find by `email` (case-insensitive, company domain only). If exactly one match **and** its `employeeId`
    is empty → set `employeeId = nik` (one-time link), sign in, write an audit log row.
  - Still not found → **do not create a duplicate**: show "Your iDesk account isn't linked yet — contact ICT (ext. 1604)".
- Never overwrite an existing different `employeeId`; log and refuse instead.

### 2. "Sign in with iPop" button (frontend + backend)
- Frontend: button on the login page → `GET /api/v1/auth/oidc/start` (backend).
- Backend `oidc/start`: create `state` + PKCE `code_verifier`, keep them in a short-lived (5 min) HttpOnly cookie, redirect
  to iPop `/connect/authorize?client_id=idesk&response_type=code&scope=openid%20profile%20email&redirect_uri=…&state=…&code_challenge=…&code_challenge_method=S256`.
- Backend `oidc/callback`: check `state`, exchange `code` at `/connect/token` (with `client_secret` + `code_verifier`),
  validate the ID token (signature via JWKS, `iss`, `aud=idesk`, `exp`, `nonce`), match the account (§1), then issue
  iDesk's **existing** session cookies (same as a normal login, "remember me" on). Redirect to `returnTo` (only relative
  paths on iDesk's own origin).
- Recommended library: `openid-client` (npm). Sketch:

```ts
// auth/infrastructure/oidc/ipop-oidc.service.ts
import { Issuer, generators } from 'openid-client';
const ipop = await Issuer.discover(process.env.IPOP_OIDC_ISSUER!);           // https://ipop.santos.co.id
const client = new ipop.Client({
  client_id: process.env.IPOP_OIDC_CLIENT_ID!,                                // idesk
  client_secret: process.env.IPOP_OIDC_CLIENT_SECRET!,
  redirect_uris: [process.env.IPOP_OIDC_REDIRECT_URI!],
  response_types: ['code'],
});
// start:    const verifier = generators.codeVerifier(); const url = client.authorizationUrl({ scope: 'openid profile email',
//           state, nonce, code_challenge: generators.codeChallenge(verifier), code_challenge_method: 'S256' });
// callback: const tokens = await client.callback(redirectUri, client.callbackParams(req), { state, nonce, code_verifier: verifier });
//           const { nik, email, name } = tokens.claims();  → match account → existing login cookies
```

New env vars: `IPOP_OIDC_ISSUER`, `IPOP_OIDC_CLIENT_ID`, `IPOP_OIDC_CLIENT_SECRET`, `IPOP_OIDC_REDIRECT_URI`.

### 3. NIK + iPop password in iDesk's own form
- In the login form, if the identifier is all digits (a NIK) → backend calls iPop `/connect/token` with
  `grant_type=password`, `username=<nik>`, `password=<typed>`, `client_id`/`client_secret`, `scope=openid profile email`.
  Success → validate the returned ID token like §2 → match → normal iDesk session. Failure → "Wrong employee ID or
  password" (same message for both cases).
- Do **not** store or log the password; do not cache iPop tokens beyond the request.
- If the identifier is an email → keep the current local login (transition period). ICT decides later when to turn
  local passwords off.
- Keep iDesk's existing login throttling on this path too (iPop also rate-limits per NIK + IP).

### 4. Opening iDesk already signed in (from iPop / iPop Notifier)
- iPop links to `https://idesk…/api/v1/auth/oidc/start?returnTo=/tickets/4821`. Because the user already has an iPop session,
  `/connect/authorize` returns immediately (no prompt) → callback → signed in on the ticket page. No custom token needed.
- The existing `s2s/issue-sso-ticket` + `autologin` routes can stay for the old SJA Notifier until it is switched off,
  then be removed.

### 5. Sign out
- iDesk logout: clear iDesk cookies; optionally redirect to iPop `/connect/logout?post_logout_redirect_uri=…` to sign out
  of everything (ask ICT which behaviour is wanted; default: iDesk only).

## Test checklist (iDesk team)
- [ ] Button → iPop login (if not signed in) → back on iDesk, signed in as the right person.
- [ ] Already signed in to iPop → button signs in with no prompt.
- [ ] NIK + iPop password in iDesk's form works; wrong password shows the generic error; throttling kicks in.
- [ ] Email + old iDesk password still works.
- [ ] User with no `employeeId` but matching email → linked once (audit row), next time matched by NIK.
- [ ] User with no match → "contact ICT", no new account.
- [ ] `returnTo` with an external URL is refused.
- [ ] Tampered `state`, expired code, wrong `aud` → rejected.

## Rollout
1. iPop deploys the OIDC provider (behind a feature flag) + registers `idesk` on a test redirect URI.
2. iDesk implements §1–§4 on staging, runs the checklist with ICT pilot users.
3. Production: enable the button; keep email login for the transition; switch off SJA Notifier SSO routes after the
   iPop Notifier pilot.
