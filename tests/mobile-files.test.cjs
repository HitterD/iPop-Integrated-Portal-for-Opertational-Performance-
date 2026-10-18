const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '../src/IPop.Web');
const read = p => fs.readFileSync(path.join(root, p), 'utf8');
test('Files phone branch uses existing state, handlers and layered history', () => {
 const page = read('Pages/Files.razor');
 assert.match(page, /@inject PhoneState Phone/);
 assert.match(page, /if \(Phone.IsPhone\)/);
 assert.match(page, /<LocationsPhone/); assert.match(page, /<BrowsePhone/); assert.match(page, /<UploadSheet/);
 const code = read('Pages/Files.Phone.cs');
 assert.match(code, /ipop.mobile.pushDetail/); assert.match(code, /ipop.mobile.popDetail/);
 assert.match(code, /Phone.Changed \+=/); assert.match(code, /Phone.Changed -=/);
 assert.match(code, /if \(showPreview\)/);
 assert.match(code, /RestorePhoneDetail/);
 assert.match(read('Pages/Files.Preview.cs'), /RestorePhoneDetail/);
 assert.match(code, /OpenShareModal/); assert.match(code, /OpenMoveCopyModal/); assert.match(code, /Download\(/);
 assert.doesNotMatch(code, /api\//);
});
test('Files CSS uses exact phone query and guards only outside', () => {
 const css = read('wwwroot/css/mobile/files.css');
 const query = '(max-width: 767px), (max-height: 500px) and (max-width: 932px) and (pointer: coarse)';
 assert.ok(css.includes('@media ' + query));
 assert.match(css.split('@media')[0], /display:\s*none/);
 assert.match(css, /\.m-files-preview/);
});
test('ONLYOFFICE selects mobile type without changing signed config', () => {
 const page = read('Pages/FileEditor.razor');
 assert.match(page, /PhoneState Phone/);
 assert.match(page, /configJson, dotNetRef, Phone.IsPhone/);
 const js = read('wwwroot/js/ipop.js');
 assert.match(js, /initOnlyOffice[\s\S]*?isPhone/);
 assert.match(js, /type: isPhone \? "mobile" : "desktop"/);
});
