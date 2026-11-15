const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8');
function setup(fetch, send) {
  const events = [];
  const window = { addEventListener() {}, isSecureContext: false };
  const sandbox = { window, navigator: {}, document: { documentElement: { setAttribute() {} } },
    localStorage: { getItem() { return null; } }, matchMedia: () => ({ matches: false }),
    URL, AbortController, performance, Date, Math, setTimeout, clearTimeout, fetch,
    FormData: class { append() {} }, XMLHttpRequest: class {
      constructor() { this.upload = {}; this.headers = {}; }
      open(method, url) { this.url = url; }
      setRequestHeader(k, v) { this.headers[k] = v; }
      send(body) { events.push(this); send(this, body); }
    } };
  Object.defineProperty(sandbox, 'ipop', { get: () => window.ipop });
  vm.runInNewContext(source, sandbox);
  const bridge = { invokeMethodAsync: async (method, refresh) => method === 'Token' ? (refresh ? 'new-token' : 'token') : undefined };
  const input = { tagName: 'INPUT', files: [{ name: 'one.txt', size: 10 }], value: 'selected' };
  return { helpers: window.ipop, events, bridge, input };
}
const response = (status, body = {}, retry = null) => ({ status, ok: status >= 200 && status < 300,
  headers: { get: () => retry }, json: async () => body });

test('Mail sends no multipart before reservation resolves and never retries uncertain transport', async () => {
  let grant;
  const calls = [];
  const s = setup(async (url, options) => {
    calls.push(options);
    if (options.method === 'DELETE') return response(204);
    return new Promise(resolve => { grant = () => resolve(response(200, { token: 'permit' })); });
  }, xhr => xhr.onerror());
  const pending = s.helpers.mailUploadInput(s.input, 'https://api.example/api/mail/attachments/upload', s.bridge, 5, 100);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(s.events.length, 0);
  assert.equal(calls[0].body, undefined);
  grant();
  const result = await pending;
  assert.equal(s.events.length, 1);
  assert.equal(s.events[0].headers['X-Upload-Reservation'], 'permit');
  assert.equal(result.files.length, 0);
  assert.equal(result.errors.length, 1);
  assert.equal(calls.filter(c => c.method === 'POST').length, 1);
  assert.equal(calls.filter(c => c.method === 'DELETE').length, 1);
});

test('reservation retries respect Retry-After, bounded deadline, and auth refresh before body', async () => {
  const calls = []; const waits = [];
  const s = setup(async (_, options) => {
    calls.push(options);
    if (options.method === 'DELETE') return response(204);
    const attempt = calls.filter(c => c.method === 'POST').length;
    return attempt === 1 ? response(401) : attempt === 2 ? response(429, {}, '5') : response(200, { token: 'permit' });
  }, xhr => { xhr.status = 200; xhr.responseText = '[{"id":"saved"}]'; xhr.onload(); });
  const lease = await s.helpers.reserveUpload('https://api.example/api/mail/attachments/upload', s.bridge,
    { sleep: async ms => waits.push(ms), random: () => 0 });
  assert.ok(waits[0] >= 5000);
  assert.equal(lease.token, 'new-token');
  await lease.cancel();
  assert.equal(s.events.length, 0);
  const short = setup(async () => response(429, {}, '120'), () => assert.fail('No upload on busy'));
  await assert.rejects(() => short.helpers.reserveUpload('https://api.example/api/mail/attachments/upload', short.bridge,
    { deadlineMs: 100, sleep: async () => assert.fail('Must not wait past deadline') }));
});

test('lost reservation responses have bounded retries and never send multipart', async () => {
  let calls = 0, time = 0;
  const s = setup(async () => { calls++; throw new Error('offline'); }, () => assert.fail('No multipart without permit'));
  await assert.rejects(() => s.helpers.reserveUpload('https://api.example/api/mail/attachments/upload', s.bridge,
    { now: () => time, sleep: async ms => { time += ms; }, random: () => 0 }));
  assert.equal(calls, 10);
  assert.equal(s.events.length, 0);
  assert.ok(time < 60000);
});

test('Mail does not retry multipart even for HTTP errors; reservations are cancelled on setup failure', async () => {
  for (const status of [401, 409, 429, 500]) {
    let cancelled = 0;
    const s = setup(async (_, o) => { if (o.method === 'DELETE') { cancelled++; return response(204); }
      return response(200, { token: 'permit' }); }, xhr => { xhr.status = status; xhr.onload(); });
    const result = await s.helpers.mailUploadInput(s.input, 'https://api.example/api/mail/attachments/upload', s.bridge, 5, 100);
    assert.equal(s.events.length, 1); assert.equal(result.errors.length, 1); assert.equal(cancelled, 1);
  }
  let cancelled = 0;
  const s = setup(async (_, o) => { if (o.method === 'DELETE') cancelled++; return response(200, { token: 'permit' }); },
    () => { throw new Error('setup failed'); });
  const result = await s.helpers.mailUploadInput(s.input, 'https://api.example/api/mail/attachments/upload', s.bridge, 5, 100);
  assert.equal(result.errors.length, 1); assert.equal(cancelled, 1);
});
