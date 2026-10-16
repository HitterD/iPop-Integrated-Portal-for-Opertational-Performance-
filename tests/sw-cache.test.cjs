// node --test tests/sw-cache.test.cjs — app-shell caching in wwwroot/sw.js (Wave 1, W1.6).
// Loads the real worker in a vm sandbox with fake caches/fetch, next to push-worker.test.cjs.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SRC = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/sw.js'), 'utf8');
const ORIGIN = 'https://ipop.test';

function sandbox({ network } = {}) {
  const stores = new Map();           // cacheName -> Map(url -> Response)
  const events = {};
  const fetched = [];
  let unregistered = false;
  const store = name => { if (!stores.has(name)) stores.set(name, new Map()); return stores.get(name); };
  const keyOf = r => (typeof r === 'string' ? new URL(r, ORIGIN + '/').href : r.url);
  const cacheApi = name => ({
    async match(r) { const v = store(name).get(keyOf(r)); return v ? v.clone() : undefined; },
    async put(r, res) { store(name).set(keyOf(r), res); },
    async delete(r) { return store(name).delete(keyOf(r)); },
    async keys() { return [...store(name).keys()].map(url => ({ url })); },
  });
  const ctx = {
    URL, Response, Request, Headers, setTimeout, clearTimeout, console, Promise,
    caches: {
      async open(n) { return cacheApi(n); },
      async keys() { return [...stores.keys()]; },
      async delete(n) { return stores.delete(n); },
      async match(r) { for (const n of stores.keys()) { const v = store(n).get(keyOf(r)); if (v) return v.clone(); } return undefined; },
    },
    fetch: async (r, init) => {
      const url = keyOf(r);
      fetched.push(url);
      return network ? network(url, r, init) : new Response('net:' + url, { status: 200 });
    },
    self: {
      location: { origin: ORIGIN },
      addEventListener(n, h) { events[n] = h; },
      skipWaiting() {},
      clients: { async matchAll() { return []; }, async claim() {} },
      registration: { scope: ORIGIN + '/', async unregister() { unregistered = true; return true; }, async getNotifications() { return []; } },
    },
  };
  vm.createContext(ctx);
  vm.runInContext(SRC, ctx);
  return { ctx, events, stores, store, fetched, get unregistered() { return unregistered; } };
}

function req(url, { method = 'GET', mode = 'cors', cache = 'default', headers = {}, destination = '' } = {}) {
  const full = new URL(url, ORIGIN + '/').href;
  return { url: full, method, mode, cache, destination, headers: new Headers(headers) };
}

/** Dispatches a fetch event; returns the Response the worker answered with, or null if it let the browser handle it. */
async function dispatch(sb, r) {
  let answered = null;
  sb.events.fetch({ request: r, respondWith(p) { answered = p; }, waitUntil() {} });
  return answered ? await answered : null;
}

const route = (sb, r) => sb.ctx.routeFor(r);

test('routing table', () => {
  const sb = sandbox();
  const cases = [
    [req('/api/me/badges'), 'none'],
    [req('/api/auth/refresh', { method: 'POST' }), 'none'],
    [req('/hubs/app?id=1'), 'none'],
    [req('/livekit/rtc'), 'none'],
    [req('/onlyoffice/web-apps/x.js'), 'none'],
    [req('/meet/robusta'), 'none'],
    [req('/download/ipop.apk'), 'none'],
    [req('/.well-known/assetlinks.json'), 'none'],
    [req('/sw.js'), 'none'],
    [req('/sw-kill'), 'none'],
    [req('/index.html', { cache: 'no-store' }), 'none'],
    [req('/_framework/dotnet.js', { method: 'HEAD', cache: 'no-store' }), 'none'],
    [req('/_framework/dotnet.native.f749u69f30.wasm'), 'cache-first'],
    [req('/_framework/IPop.Web.izvv9xw6gu.wasm'), 'cache-first'],
    [req('/_framework/icudt_CJK.tjcz0u77k5.dat'), 'cache-first'],
    [req('/_framework/dotnet.js'), 'network-first'],
    [req('/_framework/blazor.webassembly.js'), 'network-first'],
    [req('/_framework/IPop.Core.lqpourngtc.wasm'), 'cache-first'],
    [req('/_framework/System.Text.Encodings.Web.popirdekzj.wasm'), 'cache-first'],
    [req('/_framework/dotnet.native.wasm'), 'network-first'],
    [req('/css/mobile/phone-kit.css?v=20261016-w1'), 'swr'],
    [req('/js/ph-kit.js?v=20261016-w1'), 'swr'],
    [req('/img/brand/icon-192.png'), 'swr'],
    // Unversioned files the shell needs to boot: always from the network, the cached copy only offline.
    [req('/css/ipop.css'), 'network-first'],
    [req('/js/lucide.min.js'), 'network-first'],
    [req('/appsettings.json'), 'network-first'],
    [req('/_content/Radzen.Blazor/Radzen.Blazor.js'), 'network-first'],
    [req('/lib/driver.js/driver.js.iife.js'), 'network-first'],
    [req('/manifest.webmanifest'), 'none'],
    [req('/favicon.png'), 'none'],
    [req('/chat', { mode: 'navigate' }), 'navigate'],
    [req('/', { mode: 'navigate' }), 'navigate'],
    [req('https://cdn.tailwindcss.com/'), 'none'],
    [req('https://api.ipop.test/api/x'), 'none'],
    [req('/_framework/dotnet.native.f749u69f30.wasm', { headers: { Range: 'bytes=0-10' } }), 'none'],
    [req('/img/brand/icon-192.png', { headers: { Authorization: 'Bearer x' } }), 'none'],
    [req('/uploads/a.png'), 'none'],
  ];
  for (const [r, want] of cases) assert.equal(route(sb, r), want, `${r.method} ${r.url} ${r.cache}`);
});

test('the worker does not answer requests it must not handle', async () => {
  const sb = sandbox();
  for (const u of ['/api/me', '/hubs/app', 'https://cdn.jsdelivr.net/x.js'])
    assert.equal(await dispatch(sb, req(u)), null, u);
  assert.equal(await dispatch(sb, req('/index.html', { cache: 'no-store' })), null, 'version-check keeps the network');
});

test('navigation: network first and the copy kept for offline', async () => {
  const sb = sandbox();
  const res = await dispatch(sb, req('/chat', { mode: 'navigate' }));
  assert.equal(await res.text(), 'net:' + ORIGIN + '/index.html');
  assert.ok([...sb.stores.keys()].some(n => n.startsWith('ipop-shell-')));
});

test('navigation offline falls back to the cached shell, then to offline.html', async () => {
  let online = true;
  const sb = sandbox({ network: url => { if (!online) throw new TypeError('offline'); return new Response('net:' + url); } });
  await dispatch(sb, req('/chat', { mode: 'navigate' }));
  online = false;
  const cached = await dispatch(sb, req('/mail', { mode: 'navigate' }));
  assert.equal(await cached.text(), 'net:' + ORIGIN + '/index.html');

  const fresh = sandbox({ network: url => {
    if (url.endsWith('/offline.html')) return new Response('offline page');
    throw new TypeError('offline');
  } });
  await fresh.events.install && new Promise(r => fresh.events.install({ waitUntil: p => p.then(r, r) }));
  const page = await dispatch(fresh, req('/chat', { mode: 'navigate' }));
  assert.equal(await page.text(), 'offline page');
});

test('navigation that hangs times out to the cached shell', async () => {
  let hang = false;
  const sb = sandbox({ network: url => hang ? new Promise(() => {}) : new Response('net:' + url) });
  sb.ctx.NAV_TIMEOUT_MS = 30;
  vm.runInContext('NAV_TIMEOUT_MS = 30', sb.ctx);
  await dispatch(sb, req('/chat', { mode: 'navigate' }));
  hang = true;
  const res = await dispatch(sb, req('/chat', { mode: 'navigate' }));
  assert.equal(await res.text(), 'net:' + ORIGIN + '/index.html');
});

test('fingerprinted framework files: cache first, fetched once', async () => {
  const sb = sandbox();
  const u = '/_framework/IPop.Web.izvv9xw6gu.wasm';
  await dispatch(sb, req(u));
  await dispatch(sb, req(u));
  assert.equal(sb.fetched.filter(f => f.endsWith(u)).length, 1);
});

test('non-fingerprinted runtime entry is network first (a new deploy is seen at once)', async () => {
  let v = 1;
  const sb = sandbox({ network: url => new Response('v' + v) });
  assert.equal(await (await dispatch(sb, req('/_framework/dotnet.js'))).text(), 'v1');
  v = 2;
  assert.equal(await (await dispatch(sb, req('/_framework/dotnet.js'))).text(), 'v2');
});

test('versioned static assets: stale-while-revalidate keyed by the query', async () => {
  let v = 1;
  const sb = sandbox({ network: url => new Response('v' + v) });
  assert.equal(await (await dispatch(sb, req('/js/ph-kit.js?v=a'))).text(), 'v1');
  v = 2;
  assert.equal(await (await dispatch(sb, req('/js/ph-kit.js?v=a'))).text(), 'v1', 'served from cache');
  await new Promise(r => setTimeout(r, 5));
  assert.equal(await (await dispatch(sb, req('/js/ph-kit.js?v=a'))).text(), 'v2', 'refreshed in the background');
  assert.equal(await (await dispatch(sb, req('/js/ph-kit.js?v=b'))).text(), 'v2', 'new ?v= is a new key');
});

test('offline reload: unversioned boot files fall back to the cached copy', async () => {
  let online = true;
  const sb = sandbox({ network: url => { if (!online) throw new TypeError('Failed to fetch'); return new Response('cfg'); } });
  assert.equal(await (await dispatch(sb, req('/appsettings.json'))).text(), 'cfg');
  online = false;
  assert.equal(await (await dispatch(sb, req('/appsettings.json'))).text(), 'cfg');
});

test('failed responses are never cached', async () => {
  const sb = sandbox({ network: () => new Response('err', { status: 500 }) });
  await dispatch(sb, req('/_framework/IPop.Core.lqpourngtc.wasm'));
  const fw = [...sb.stores.entries()].filter(([n]) => n.startsWith('ipop-fw-')).flatMap(([, m]) => [...m.keys()]);
  assert.deepEqual(fw, []);
});

async function activate(sb) { let p; sb.events.activate({ waitUntil(x) { p = x; } }); await p; }

test('activate drops old shell/framework caches but keeps push state', async () => {
  const sb = sandbox({ network: url => url.endsWith('/sw-kill') ? new Response('', { status: 404 }) : new Response('x') });
  sb.store('ipop-shell-old'); sb.store('ipop-fw-old'); sb.store('ipop-push-state-v1'); sb.store('other-app');
  await activate(sb);
  const names = [...sb.stores.keys()];
  assert.ok(!names.includes('ipop-shell-old'));
  assert.ok(!names.includes('ipop-fw-old'));
  assert.ok(names.includes('ipop-push-state-v1'));
  assert.ok(names.includes('other-app'));
  assert.equal(sb.unregistered, false);
});

test('kill switch: /sw-kill {"off":true} clears the caches and unregisters', async () => {
  const sb = sandbox({ network: url => url.endsWith('/sw-kill') ? new Response('{"off":true}', { headers: { 'content-type': 'application/json' } }) : new Response('x') });
  sb.store('ipop-shell-' + vm.runInContext('SW_VERSION', sb.ctx)).set('a', new Response('a'));
  await activate(sb);
  assert.equal(sb.unregistered, true);
  assert.ok(![...sb.stores.keys()].some(n => n.startsWith('ipop-shell-')));
  assert.ok(vm.runInContext('killed', sb.ctx));
  assert.equal(await dispatch(sb, req('/chat', { mode: 'navigate' })), null, 'after the kill switch nothing is intercepted');
});

test('framework cache is capped', async () => {
  const sb = sandbox();
  vm.runInContext('FW_LIMIT = 5', sb.ctx);
  for (let i = 0; i < 9; i++) await dispatch(sb, req(`/_framework/F${i}.abcdefgh${i}0.wasm`));
  await new Promise(r => setTimeout(r, 5));
  const fw = [...sb.stores.entries()].filter(([n]) => n.startsWith('ipop-fw-')).flatMap(([, m]) => [...m.keys()]);
  assert.ok(fw.length <= 5, 'kept ' + fw.length);
});

test('push handlers are still registered', () => {
  const sb = sandbox();
  for (const e of ['push', 'notificationclick', 'message', 'pushsubscriptionchange', 'install', 'activate', 'fetch'])
    assert.equal(typeof sb.events[e], 'function', e);
});
