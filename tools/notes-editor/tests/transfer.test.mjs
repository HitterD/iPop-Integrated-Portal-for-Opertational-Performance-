import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { zipSync, strToU8 } from 'fflate';

const w = new Window();
globalThis.window = w; globalThis.document = w.document; globalThis.DOMParser = w.DOMParser;

const { getSchema } = await import('@tiptap/core');
const { default: StarterKit } = await import('@tiptap/starter-kit');
const { TaskList } = await import('@tiptap/extension-list');
const { TextStyle, Color, BackgroundColor } = await import('@tiptap/extension-text-style');
const { Details, DetailsSummary, DetailsContent } = await import('@tiptap/extension-details');
const { default: Mention } = await import('@tiptap/extension-mention');
const { RichTaskItem, tableExtensions, NoteImage, NoteGallery, NoteBookmark, NoteEmbed } = await import('../src/rich.js');
const { BlockBackground, Callout, NoteWidget, SubPage, NoteFile } = await import('../src/nodes.js');
const T = await import('../src/transfer.js');

const extensions = [
  StarterKit.configure({ heading: { levels: [1, 2, 3] } }),
  TaskList, RichTaskItem.configure({ nested: true }), ...tableExtensions(),
  TextStyle, Color, BackgroundColor, Details, DetailsSummary, DetailsContent,
  BlockBackground, Callout, NoteWidget, SubPage, NoteFile, NoteImage, NoteGallery, NoteBookmark, NoteEmbed, Mention,
];
const schema = getSchema(extensions);

// Trimmed copy of a real Notion HTML export (secrets replaced).
const notionPage = (title, body, extra = '') => `<html><head><title>${title}</title></head><body><article id="e5f02d5e-594a-463b-9118-0a4ad8ee3cec" class="page sans" data-notion-page-icon="💻"><header>${extra}<div class="page-header-icon page-header-icon-with-cover"><span class="icon" data-emoji="💻"></span></div><h1 class="page-title" dir="auto">${title}</h1><p class="page-description" dir="auto"></p></header><div class="page-body">${body}</div></article></body></html>`;
const SAMPLE = notionPage('Install Aplikasi Standard SJA',
  '<h1 id="a" class="" dir="auto">Update Windows</h1><ol type="1" class="numbered-list" start="1" dir="auto"><li>Nyalakan Netframework 3.5</li></ol><ol type="1" class="numbered-list" start="2" dir="auto"><li></li></ol><p class="" dir="auto">🆕</p>'
  + '<h1 class="" dir="auto">Oracle</h1><ol type="1" class="numbered-list" start="1" dir="auto"><li>buka configure java<ul class="bulleted-list" dir="auto"><li style="list-style-type:disc">appsprod.santos.co.id:8000</li></ul><ul class="bulleted-list" dir="auto"><li style="list-style-type:disc">appsdev1.santos.co.id:8000</li></ul></li></ol><ol type="1" class="numbered-list" start="2" dir="auto"><li>pastikan <mark class="highlight-teal_background">Always Allow</mark></li></ol>'
  + '<ul class="toggle"><li><details open=""><summary>Regedit policy</summary><ul class="bulleted-list"><li>Chrome_Machine</li></ul></details></li></ul>'
  + '<p>server <mark class="highlight-teal">192.168.2.9</mark> pwd <mark class="highlight-red_background">xxxx</mark></p>',
  '<img class="page-cover-image" src="https://app.notion.com/images/page-cover/gradients_11.jpg"/>');

const zip = files => zipSync(Object.fromEntries(Object.entries(files).map(([k, v]) => [k, typeof v === 'string' ? strToU8(v) : v])));
const convert = (plan, page, pageOf = () => null) => T.pageToDoc(page, { files: plan.files, extensions, pageOf });
const find = (json, type) => { const out = []; const walk = n => { if (n.type === type) out.push(n); (n.content || []).forEach(walk); }; walk(json); return out; };

test('the sample Notion page keeps its title, icon, cover, lists, toggle and colours', () => {
  const name = '9abb7ad1-1d2c-4994-8a8e-fb7daf13c4f5_ExportBlock-ccceb1b9.zip';
  const plan = T.planImport(name, zip({ 'Install Aplikasi Standard SJA e5f02d5e594a463b91180a4ad8ee3cec.html': SAMPLE }));
  assert.equal(plan.pages.length, 1);
  const [p] = plan.pages;
  assert.equal(p.title, 'Install Aplikasi Standard SJA');
  assert.equal(p.emoji, '💻');
  assert.equal(p.cover, 'teal');
  assert.equal(p.importKey, 'notion:e5f02d5e594a463b91180a4ad8ee3cec');
  assert.equal(plan.source, 'notion');

  const { json, notes } = convert(plan, p);
  schema.nodeFromJSON(json).check();
  assert.equal(notes.size, 0);
  assert.deepEqual(find(json, 'heading').map(h => h.content[0].text), ['Update Windows', 'Oracle']);
  const lists = find(json, 'orderedList');
  assert.equal(lists[0].content.length, 2, 'split Notion list items join back into one list, the empty item kept');
  assert.equal(lists[1].content[0].content[1].type, 'bulletList');
  assert.equal(lists[1].content[0].content[1].content.length, 2);
  const det = find(json, 'details')[0];
  assert.equal(det.content[0].content[0].text, 'Regedit policy');
  const marks = find(json, 'text').flatMap(t => (t.marks || []).map(m => m.attrs));
  assert.ok(marks.some(a => a.backgroundColor === 'var(--nb-green)'));
  assert.ok(marks.some(a => a.color === 'var(--nt-green)'));
  assert.ok(marks.some(a => a.backgroundColor === 'var(--nb-red)'));
  const s = T.docToState(json, extensions);
  assert.ok(s.state.length > 100);
  assert.match(s.plainText, /Always Allow/);
});

test('a multi-page export keeps the tree, wraps several roots, rewrites links and attaches files', () => {
  const files = {
    'Wiki aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.md': '# Wiki\n\nSee [Child](Wiki%20aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Child%20bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.md)\n\n- [ ] todo\n- [x] done\n\n![shot](Wiki%20aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/shot.png)\n\n<aside>\n⚠️ careful **now**\n</aside>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n',
    'Wiki aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/Child bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.md': '# Child\n\n[Wiki](../Wiki%20aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.md)\n',
    'Wiki aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/shot.png': new Uint8Array([137, 80, 78, 71]),
    'Other cccccccccccccccccccccccccccccccc.md': '# Other\n\n$$x^2$$\n',
  };
  const plan = T.planImport('Export-Part-1.zip', zip({ 'Export-Part-1.zip': zip(files) }));
  assert.deepEqual(plan.pages.map(p => [p.title, p.depth]), [['Imported from Notion', 0], ['Other', 1], ['Wiki', 1], ['Child', 2]]);
  assert.equal(plan.attachments, 1);
  const wiki = plan.pages.find(p => p.title === 'Wiki');
  const child = plan.pages.find(p => p.title === 'Child');
  assert.equal(child.parentKey, wiki.key);

  const ids = { [wiki.path]: { id: '11111111-1111-1111-1111-111111111111', title: 'Wiki' }, [child.path]: { id: '22222222-2222-2222-2222-222222222222', title: 'Child' } };
  const { json, assets } = convert(plan, wiki, p => ids[p] ?? null);
  schema.nodeFromJSON(json).check();
  assert.equal(find(json, 'taskItem').length, 2);
  assert.equal(find(json, 'taskItem')[1].attrs.checked, true);
  assert.equal(find(json, 'callout')[0].attrs.emoji, '⚠️');
  assert.equal(find(json, 'table').length, 1);
  assert.equal(assets.length, 1);
  assert.equal(find(json, 'noteImage')[0].attrs.id, assets[0].ref);
  const link = find(json, 'text').flatMap(t => t.marks || []).find(m => m.type === 'link');
  assert.equal(link.attrs.href, `/notes/${ids[child.path].id}`, 'an inline link to another imported page opens it in iPop');

  const back = convert(plan, child, p => ids[p] ?? null).json;
  assert.equal(find(back, 'subPage')[0].attrs.id, ids[wiki.path].id);

  const wrapper = T.pageToDoc(plan.pages[0], { files: plan.files, extensions, childrenOf: () => [{ id: 'o', title: 'Other' }, { id: 'w', title: 'Wiki', emoji: '📘' }] }).json;
  assert.deepEqual(find(wrapper, 'subPage').map(n => n.attrs.title), ['Other', 'Wiki']);

  const filled = T.fillAssets(json, new Map([[assets[0].ref, { id: 'att', name: 'shot.png', size: 4, image: true }]]));
  assert.equal(find(filled, 'noteImage')[0].attrs.id, 'att');
  const dropped = T.fillAssets(json, new Map());
  assert.equal(find(dropped, 'noteImage').length, 0);
  schema.nodeFromJSON(dropped).check();
});

test('Notion blocks without an equivalent are converted and reported', () => {
  const html = notionPage('Odd', '<div class="column-list"><div class="column"><p>left</p></div><div class="column"><p>right</p></div></div>'
    + '<figure class="equation"><div class="equation-container"><annotation encoding="application/x-tex">E=mc^2</annotation></div></figure>'
    + '<figure><div class="source"><a href="https://maps.google.com/x">map</a></div></figure>'
    + '<table class="collection-content"><thead><tr><th>Name</th><th>Tags</th></tr></thead><tbody><tr><td>A</td><td><span>x</span></td></tr></tbody></table>'
    + '<figure class="callout block-color-yellow_background"><div style="font-size:1.5em"><span class="icon">💡</span></div><div style="width:100%">Tip text</div></figure>'
    + '<ul class="to-do-list"><li><div class="checkbox checkbox-on"></div> <span class="to-do-children-checked">done</span></li></ul>');
  const plan = T.planImport('Odd.html', strToU8(html));
  const { json, notes } = convert(plan, plan.pages[0]);
  schema.nodeFromJSON(json).check();
  const labels = [...notes.keys()].join('|');
  for (const k of ['column', 'equation', 'embed', 'database']) assert.match(labels, new RegExp(k));
  assert.equal(find(json, 'codeBlock')[0].content[0].text, 'E=mc^2');
  assert.equal(find(json, 'noteBookmark')[0].attrs.url, 'https://maps.google.com/x');
  assert.equal(find(json, 'callout')[0].attrs.color, 'yellow');
  assert.equal(find(json, 'taskItem')[0].attrs.checked, true);
});

test('limits and bad files are refused before anything is created', () => {
  assert.throws(() => T.planImport('x.pdf', new Uint8Array(3)), { code: 'unsupported' });
  assert.throws(() => T.planImport('x.zip', strToU8('nope')), { code: 'bad_zip' });
  assert.throws(() => T.planImport('x.zip', zip({ 'a.txt': 'x' })), { code: 'no_pages' });
  const many = Object.fromEntries(Array.from({ length: 301 }, (_, i) => [`p${i}.md`, `# P${i}`]));
  assert.throws(() => T.planImport('x.zip', zip(many)), { code: 'too_many' });
});

test('plain Markdown and HTML files import too', () => {
  const md = T.planImport('notes.md', strToU8('# Hello\n\nSome *text*.\n'));
  assert.equal(md.source, 'markdown');
  assert.equal(md.pages[0].title, 'Hello');
  const j = convert(md, md.pages[0]).json;
  assert.equal(find(j, 'heading').length, 0, 'the title heading is not repeated in the body');
  const html = T.planImport('page.html', strToU8('<html><head><title>Plain</title></head><body><h2>A</h2><p>b</p></body></html>'));
  assert.equal(html.source, 'html');
  assert.equal(html.pages[0].title, 'Plain');
});

test('export: Markdown and HTML keep structure, links and attachments', () => {
  const plan = T.planImport('s.zip', zip({ 'Install Aplikasi Standard SJA e5f02d5e594a463b91180a4ad8ee3cec.html': SAMPLE }));
  const json = convert(plan, plan.pages[0]).json;
  const withFile = { ...json, content: [...json.content,
    { type: 'noteImage', attrs: { id: 'img1', name: 'a.png', caption: 'cap' } },
    { type: 'subPage', attrs: { id: 'kid', title: 'Kid', emoji: '📄' } },
    { type: 'paragraph', content: [{ type: 'mention', attrs: { id: 'u', label: 'Rina' } }] }] };
  const ctx = { dir: 'Root', link: id => (id === 'kid' ? 'Root/Kid.md' : null), file: id => (id === 'img1' ? { path: 'Root/attachments/a.png' } : null), origin: 'https://ipop' };
  const md = T.docToMarkdown(withFile, ctx);
  assert.match(md, /^# Update Windows/m);
  assert.match(md, /^1\. Nyalakan/m);
  assert.match(md, /^ {3}- appsprod/m);
  assert.match(md, /<details>\n<summary>Regedit policy<\/summary>/);
  assert.match(md, /!\[cap\]\(attachments\/a\.png\)/);
  assert.match(md, /\[📄 Kid\]\(Kid\.md\)/);
  assert.match(md, /@Rina/);
  const html = T.docToHtml(withFile, { ...ctx, link: () => null });
  assert.match(html, /<details open>/);
  assert.match(html, /background:#E1F6E6/);
  assert.match(html, /<img src="attachments\/a\.png"/);

  const re = T.planImport('back.md', strToU8(md));
  schema.nodeFromJSON(convert(re, re.pages[0]).json).check();
});

test('export layout gives each page a unique file and nests sub-pages in a folder', () => {
  const pages = [{ id: 'r', parentId: null, title: 'Wiki' }, { id: 'a', parentId: 'r', title: 'Setup' }, { id: 'b', parentId: 'r', title: 'Setup' }, { id: 'c', parentId: 'a', title: 'A/B: C?' }];
  const l = T.layoutExport(pages, ['r'], 'md');
  assert.equal(l.get('r').file, 'Wiki.md');
  assert.equal(l.get('a').file, 'Wiki/Setup.md');
  assert.equal(l.get('b').file, 'Wiki/Setup (2).md');
  assert.equal(l.get('c').file, 'Wiki/Setup/A-B- C-.md');
});

test('Yjs state round-trips back to the same document', () => {
  const json = { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'hi' }] }, { type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: true }, content: [{ type: 'paragraph', content: [{ type: 'text', text: 't' }] }] }] }] };
  const s = T.docToState(json, extensions);
  assert.equal(s.todoDone, 1); assert.equal(s.todoTotal, 1);
  const back = T.stateToDoc({ state: s.state, updates: [] });
  assert.equal(find(back, 'text').map(t => t.text).join(''), 'hit');
});
