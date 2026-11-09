// One browser, one page, synthetic origin only: no production API or credentials.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const path = require('node:path');
(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage();
    let release, reserved;
    const seenReservation = new Promise(resolve => { reserved = resolve; });
    let uploads = 0, cancels = 0, reservationCalls = 0;
    await page.route('http://upload.test/**', async route => {
      const req = route.request();
      const url = new URL(req.url());
      if (url.pathname === '/api/uploads/reservations') {
        if (req.method() === 'DELETE') { cancels++; await route.fulfill({ status: 204 }); return; }
        reservationCalls++;
        assert.equal(req.postData(), null);
        assert.equal(req.headers().authorization, 'Bearer browser-test-token');
        assert.equal(url.searchParams.get('route'), '/api/mail/attachments/upload');
        await new Promise(resolve => { release = resolve; reserved(); });
        await route.fulfill({ status: 200, contentType: 'application/json', body: '{"token":"browser-permit"}' });
        return;
      }
      if (url.pathname === '/api/mail/attachments/upload') {
        uploads++;
        assert.equal(req.headers()['x-upload-reservation'], 'browser-permit');
        assert.match(req.headers()['content-type'], /multipart\/form-data/);
        await route.abort('failed'); // uncertain result must NOT trigger an upload retry
        return;
      }
      await route.fulfill({ contentType: 'text/html', body: '<!doctype html><input type="file" id="files">' });
    });
    await page.goto('http://upload.test/');
    await page.addScriptTag({ path: path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js') });
    await page.locator('#files').setInputFiles({ name: 'browser.txt', mimeType: 'text/plain', buffer: Buffer.from('browser body') });
    await page.evaluate(() => {
      window.result = ipop.mailUploadInput(document.querySelector('#files'), '/api/mail/attachments/upload',
        { invokeMethodAsync: async method => method === 'Token' ? 'browser-test-token' : undefined }, 5, 1024);
    });
    await seenReservation;
    assert.equal(uploads, 0, 'Browser sent body while reservation response was pending');
    release();
    const result = await page.evaluate(() => window.result);
    assert.equal(uploads, 1); assert.equal(reservationCalls, 1); assert.equal(cancels, 1);
    assert.equal(result.files.length, 0); assert.match(result.errors[0], /uncertain/);
    console.log('Real browser Mail admission passed: permit before body, authenticated reservation, single multipart attempt, cancellation.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
