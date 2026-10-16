// node --test tests/pwa-manifest.test.cjs — installable PWA (Wave 1, mockup v1 frames q, r, r2).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const WEB = path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot');
const manifest = JSON.parse(fs.readFileSync(path.join(WEB, 'manifest.webmanifest'), 'utf8'));

function pngSize(file) {
  const b = fs.readFileSync(file);
  assert.equal(b.toString('latin1', 1, 4), 'PNG', file + ' is a PNG');
  assert.equal(b.toString('latin1', 12, 16), 'IHDR');
  return [b.readUInt32BE(16), b.readUInt32BE(20)];
}

test('identity, scope and colours', () => {
  assert.equal(manifest.id, '/');
  assert.equal(manifest.name, 'iPop');
  assert.equal(manifest.short_name, 'iPop');
  assert.equal(manifest.start_url, '/dashboard?source=pwa');
  assert.equal(manifest.scope, '/');
  assert.equal(manifest.display, 'standalone');
  assert.equal(manifest.theme_color, '#123528');
  assert.equal(manifest.background_color, '#123528');
});

test('any + maskable icons at 192 and 512, each file has the stated size', () => {
  for (const purpose of ['any', 'maskable'])
    for (const size of [192, 512]) {
      const icon = manifest.icons.find(i => i.purpose === purpose && i.sizes === `${size}x${size}`);
      assert.ok(icon, `${purpose} ${size}`);
      assert.equal(icon.type, 'image/png');
      assert.deepEqual(pngSize(path.join(WEB, icon.src)), [size, size], icon.src);
    }
});

test('shortcuts for Chat, Mail and Calendar', () => {
  assert.deepEqual(manifest.shortcuts.map(s => [s.name, s.url]), [['Chat', '/chat'], ['Mail', '/mail'], ['Calendar', '/calendar']]);
  for (const s of manifest.shortcuts) for (const i of s.icons) assert.ok(fs.existsSync(path.join(WEB, i.src)), i.src);
});

test('iOS startup images exist at the sizes index.html links', () => {
  const html = fs.readFileSync(path.join(WEB, 'index.html'), 'utf8');
  const links = [...html.matchAll(/rel="apple-touch-startup-image" href="([^"]+)"/g)].map(m => m[1]);
  assert.equal(links.length, 3);
  for (const l of links) {
    const [w, h] = l.match(/(\d+)x(\d+)/).slice(1).map(Number);
    assert.deepEqual(pngSize(path.join(WEB, l)), [w, h], l);
  }
});

test('desktop keeps its grey theme colour; phones get forest green', () => {
  const html = fs.readFileSync(path.join(WEB, 'index.html'), 'utf8');
  assert.match(html, /<meta name="theme-color" content="#F5F5F7" \/>/);
  assert.match(html, /<meta name="theme-color" content="#123528" media="\(max-width: 767px\)" \/>/);
});
