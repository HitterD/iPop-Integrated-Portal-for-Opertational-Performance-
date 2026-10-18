// Copy-DB QA: "Sign in with iPop" end to end, acting as iDesk (client "idesk", redirect http://127.0.0.1:6399/callback).
// Browser: authorize → iPop /login → /sso/authorize → callback with code. Node: code + PKCE → tokens → userinfo.
// Then: password grant (NIK + iPop password), wrong password, tampered redirect, reused code.
const fs = require('node:fs'); const crypto = require('node:crypto');
const { chromium } = require('C:/Users/IT12/.claude/skills/playwright-skill/skills/playwright-skill/node_modules/playwright');
const Q = process.env.QA_DIR || 'C:/Users/IT12/orca/workspaces/mm-qa', API = 'http://127.0.0.1:6391', WEB = 'http://127.0.0.1:6392';
const PASS = fs.readFileSync(Q + '/qa-login.secret', 'utf8').trim(), SECRET = fs.readFileSync(Q + '/oidc-client.secret', 'utf8').trim();
const REDIRECT = 'http://127.0.0.1:6399/callback';
const b64u = b => b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const out = {};
const form = o => new URLSearchParams(o).toString();
const token = async body => { const r = await fetch(API + '/connect/token', { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: form(body) }); return { status: r.status, json: await r.json().catch(() => null) }; };
const claims = jwt => JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url').toString());
(async () => {
  const verifier = b64u(crypto.randomBytes(32)), challenge = b64u(crypto.createHash('sha256').update(verifier).digest()), state = b64u(crypto.randomBytes(12)), nonce = b64u(crypto.randomBytes(12));
  const authUrl = `${API}/connect/authorize?` + form({ client_id: 'idesk', response_type: 'code', scope: 'openid profile email offline_access', redirect_uri: REDIRECT, state, nonce, code_challenge: challenge, code_challenge_method: 'S256' });
  const b = await chromium.launch({ args: ['--disable-features=PrivateNetworkAccessSendPreflights,PrivateNetworkAccessRespectPreflightResults,BlockInsecurePrivateNetworkRequests,LocalNetworkAccessChecks'] }); const ctx = await b.newContext({ serviceWorkers: 'block' });
  await ctx.route('**/appsettings*.json', r => r.fulfill({ contentType: 'application/json', body: JSON.stringify({ ApiBase: API }) }));
  let callback = null;
  await ctx.route('http://127.0.0.1:6399/**', r => { callback = r.request().url(); return r.fulfill({ contentType: 'text/html', body: '<h1>iDesk callback</h1>' }); });
  const p = await ctx.newPage(); const steps = [];
  p.on('framenavigated', f => { if (f === p.mainFrame()) steps.push(new URL(f.url()).pathname); });
  p.on('response', r => { const l = r.headers()['location']; if (l && l.startsWith('http://127.0.0.1:6399/')) callback = callback || l; });
  await p.goto(authUrl);
  await p.locator('input[type=password]').waitFor({ timeout: 60000 });
  out.loginUrlHasReturn = /returnUrl=%2Fsso%2Fauthorize/.test(p.url());
  await p.locator('input:not([type=password]):not([type=checkbox])').first().fill('ipopadmin');
  await p.locator('input[type=password]').fill(PASS); await p.locator('input[type=password]').press('Enter');
  for (let i = 0; i < 60 && !callback; i++) await p.waitForTimeout(500);
  await p.screenshot({ path: Q + '/oidc-end.png' });
  out.steps = [...new Set(steps)];
  out.callback = callback ? callback.replace(/code=[^&]+/, 'code=…') : null;
  if (!callback) { console.log(JSON.stringify(out, null, 1)); await b.close(); return; }
  const cb = new URL(callback); out.stateOk = cb.searchParams.get('state') === state;
  const code = cb.searchParams.get('code');
  const t1 = await token({ grant_type: 'authorization_code', code, redirect_uri: REDIRECT, client_id: 'idesk', client_secret: SECRET, code_verifier: verifier });
  out.codeExchange = t1.status;
  if (t1.json?.id_token) { const c = claims(t1.json.id_token); out.idToken = { iss: c.iss, aud: c.aud, nik: c.nik, name: c.name, nonceOk: c.nonce === nonce, sub: !!c.sub }; }
  out.hasRefresh = !!t1.json?.refresh_token;
  const ui = await fetch(API + '/connect/userinfo', { headers: { authorization: 'Bearer ' + t1.json?.access_token } });
  out.userinfo = { status: ui.status, body: await ui.json().catch(() => null) };
  out.refresh = (await token({ grant_type: 'refresh_token', refresh_token: t1.json?.refresh_token, client_id: 'idesk', client_secret: SECRET })).status;
  out.codeReuse = (await token({ grant_type: 'authorization_code', code, redirect_uri: REDIRECT, client_id: 'idesk', client_secret: SECRET, code_verifier: verifier })).status;
  out.passwordOk = (await token({ grant_type: 'password', username: 'ipopadmin', password: PASS, client_id: 'idesk', client_secret: SECRET, scope: 'openid profile email' })).status;
  const bad = await token({ grant_type: 'password', username: 'ipopadmin', password: 'wrong-pass', client_id: 'idesk', client_secret: SECRET, scope: 'openid' });
  out.passwordBad = { status: bad.status, error: bad.json?.error, desc: bad.json?.error_description };
  out.badSecret = (await token({ grant_type: 'password', username: 'ipopadmin', password: PASS, client_id: 'idesk', client_secret: 'nope', scope: 'openid' })).status;
  const evil = await fetch(`${API}/connect/authorize?` + form({ client_id: 'idesk', response_type: 'code', scope: 'openid', redirect_uri: 'https://evil.example/cb', code_challenge: challenge, code_challenge_method: 'S256' }), { redirect: 'manual' });
  out.evilRedirect = { status: evil.status, location: (evil.headers.get('location') || '').slice(0, 60) };
  const noPkce = await fetch(`${API}/connect/authorize?` + form({ client_id: 'idesk', response_type: 'code', scope: 'openid', redirect_uri: REDIRECT }), { redirect: 'manual' });
  out.noPkce = { status: noPkce.status, location: (noPkce.headers.get('location') || '').slice(0, 80) };
  console.log(JSON.stringify(out, null, 1)); await b.close();
})();
