const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const read = name => fs.readFileSync(path.join(__dirname, '..', name), 'utf8');
test('Notes uses phone components without replacing the collaborative editor', () => {
  const razor = read('src/IPop.Web/Pages/Notes.razor');
  for (const component of ['NotesListPhone', 'NotesMoreSheet', 'NotesCommentsSheet', 'NotesKeyboardToolbar']) assert.ok(razor.includes(component), component);
  assert.equal((razor.match(/@ref="editorRef"/g) || []).length, 1);
  assert.ok(razor.includes('Phone.IsPhone'));
  assert.ok(razor.includes('Search="@searchQ"'), 'search must bind the page value, not literal searchQ');
});
test('Notes detail integrates with the existing mobile history and cleans up chrome', () => {
  const code = read('src/IPop.Web/Pages/Notes.Phone.cs');
  for (const method of ['ipop.mobile.pushDetail', 'ipop.mobile.popDetail', 'ipop.mobile.clearDetail', 'ipop.mobile.hideTabbar']) assert.ok(code.includes(method), method);
});
test('Notes rebinds desktop sidebar after a responsive phone transition', () => {
  const code = read('src/IPop.Web/Pages/Notes.Phone.cs');
  assert.ok(code.includes('ReconcilePhoneSidebar'));
  assert.ok(read('src/IPop.Web/Pages/Notes.razor.cs').includes('await ReconcilePhoneSidebar()'));
});
test('mention suggestions use a phone sheet and existing overlay history', () => {
  assert.ok(read('tools/notes-editor/src/menus.js').includes('m-notes-mentions'));
  assert.ok(read('src/IPop.Web/Pages/Notes.razor').includes('phoneMentionOpen'));
  assert.ok(read('tools/notes-editor/src/index.js').includes('exitSuggestion(editor.view, phoneMentionKey)'));
});
test('mobile formatting commands use existing TipTap and upload bridge with permission guard', () => {
  const code = read('tools/notes-editor/src/index.js');
  assert.match(code, /mobileCommand\(command\)/);
  assert.match(code, /if \(closing \|\| !canEdit\) return/);
  for (const command of ['toggleHeading', 'toggleBold', 'toggleItalic', 'toggleBulletList', 'toggleTaskList']) assert.ok(code.includes(command), command);
});
