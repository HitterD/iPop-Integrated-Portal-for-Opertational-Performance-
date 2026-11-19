import { newDrawId } from './draw-block.js';
import { Extension } from '@tiptap/core';
import Suggestion from '@tiptap/suggestion';
import { PluginKey, TextSelection } from '@tiptap/pm/state';
import { COLORS, cap, esc } from './nodes.js';
import { columnsJson, toggleJson, isoDay } from './extras.js';
import { openSymbolPanel } from './symbols.js';

const $el = (cls, html = '') => { const e = document.createElement('div'); e.className = cls; e.innerHTML = html; return e; };

/// Floating element placed next to a rect, flipped above when the viewport has no room below.
export function place(pop, rect, { align = 'left', gap = 6 } = {}) {
  pop.style.visibility = 'hidden'; pop.classList.add('show');
  const w = pop.offsetWidth, h = pop.offsetHeight;
  let top = rect.bottom + gap;
  if (top + h > innerHeight - 10) top = Math.max(10, rect.top - h - gap);
  let left = align === 'right' ? rect.right - w : align === 'center' ? rect.left + rect.width / 2 - w / 2 : rect.left;
  left = Math.min(Math.max(8, left), innerWidth - w - 8);
  pop.style.top = `${top}px`; pop.style.left = `${left}px`; pop.style.visibility = '';
}

// ── Block types (shared by the slash menu and "Turn into") ──────────────────

const setBlock = (fn) => ({ editor, range }) => { const c = editor.chain().focus(); if (range) c.deleteRange(range); fn(c).run(); };
/// Replaces the typed "/command" with a block (columns, toggle heading) and puts the cursor at the start of it, so typing goes
/// into the first column / the toggle title. Finds the block it just inserted instead of counting positions.
export function insertAt(editor, range, json) {
  const at = range.from;
  editor.chain().focus().deleteRange(range).insertContentAt(at, json).command(({ tr, dispatch }) => {
    // The block now starts at the nearest block boundary at or before `at` (the emptied paragraph is replaced, or the block
    // lands after it): look for the first node of this type from there.
    let start = -1;
    tr.doc.nodesBetween(Math.max(0, at - 2), Math.min(tr.doc.content.size, at + 2), (n, pos) => { if (start < 0 && n.type.name === json.type) start = pos; return start < 0; });
    if (start >= 0 && dispatch) tr.setSelection(TextSelection.near(tr.doc.resolve(start + 1), 1));
    return true;
  }).run();
}

export const TURN_INTO = [
  { key: 'text', name: 'Text', box: 'T', run: c => c.setParagraph() },
  { key: 'h1', name: 'Heading 1', box: 'H1', run: c => c.setHeading({ level: 1 }) },
  { key: 'h2', name: 'Heading 2', box: 'H2', run: c => c.setHeading({ level: 2 }) },
  { key: 'h3', name: 'Heading 3', box: 'H3', run: c => c.setHeading({ level: 3 }) },
  { key: 'todo', name: 'To-do list', box: '☑', run: c => c.toggleTaskList() },
  { key: 'bullet', name: 'Bulleted list', box: '•', run: c => c.toggleBulletList() },
  { key: 'number', name: 'Numbered list', box: '1.', run: c => c.toggleOrderedList() },
  { key: 'quote', name: 'Quote', box: '❝', run: c => c.toggleBlockquote() },
  { key: 'code', name: 'Code', box: '</>', run: c => c.toggleCodeBlock() },
  { key: 'callout', name: 'Callout', box: '💡', run: c => c.wrapIn('callout', { emoji: '💡', color: 'yellow' }) },
  { key: 'toggle', name: 'Toggle', box: '▸', run: c => c.setDetails() },
];

/// Words people type after "/" that should find a command even though they are not in its name.
export const SLASH_ALIASES = {
  'Heading 1': 'h1 title big', 'Heading 2': 'h2 subtitle', 'Heading 3': 'h3',
  'To-do list': 'todo task checkbox checklist', 'Bulleted list': 'bullet ul list', 'Numbered list': 'number ol ordered list',
  Toggle: 'collapse expand details accordion', Quote: 'blockquote cite', Callout: 'info note tip warning alert box',
  Divider: 'hr line separator rule', Code: 'snippet pre programming', Table: 'grid spreadsheet', Image: 'picture photo img',
  File: 'attachment upload document', Drawing: 'draw sketch whiteboard paint', Date: 'day calendar today time',
  'Link to page': 'page link backlink wiki', '2 columns': 'column layout split', '3 columns': 'column layout split',
  'Table of contents': 'toc contents outline', 'Embed video': 'youtube video mp4', Bookmark: 'link card url',
  Symbol: 'simbol arrow panah check centang degree derajat special char', Today: 'date tanggal hari ini now',
};
/// Slash menu search: matches the name, a word in the name, or an alias. Names that start with the query come first.
export function filterSlash(cmds, query) {
  const q = String(query || '').trim().toLowerCase();
  if (!q) return cmds;
  const rank = c => {
    const n = c.name.toLowerCase(), a = (SLASH_ALIASES[c.name] || '').toLowerCase();
    if (n.startsWith(q)) return 0;
    if (n.split(/\s+/).some(w => w.startsWith(q))) return 1;
    if (n.includes(q)) return 2;
    if (a.split(/\s+/).some(w => w.startsWith(q))) return 3;
    return 9;
  };
  return cmds.map((c, i) => ({ c, i, r: rank(c) })).filter(x => x.r < 9).sort((x, y) => x.r - y.r || x.i - y.i).map(x => x.c);
}

export function slashCommands(host) {
  const byKey = k => TURN_INTO.find(t => t.key === k).run;
  return [
    ['Basic blocks', 'Text', 'Just start writing', 'T', '', setBlock(byKey('text'))],
    ['Basic blocks', 'Heading 1', 'Big section heading', 'H1', '#', setBlock(byKey('h1'))],
    ['Basic blocks', 'Heading 2', 'Medium section heading', 'H2', '##', setBlock(byKey('h2'))],
    ['Basic blocks', 'Heading 3', 'Small section heading', 'H3', '###', setBlock(byKey('h3'))],
    ['Basic blocks', 'To-do list', 'Track tasks with a checkbox', '☑', '[ ]', setBlock(byKey('todo'))],
    ['Basic blocks', 'Bulleted list', 'Simple bulleted list', '•', '-', setBlock(byKey('bullet'))],
    ['Basic blocks', 'Numbered list', 'List with numbering', '1.', '1.', setBlock(byKey('number'))],
    ['Basic blocks', 'Toggle', 'Hide content inside', '▸', '', setBlock(c => c.setDetails())],
    ['Basic blocks', 'Toggle heading 1', 'A big heading that opens and closes', '▸H1', '', ({ editor, range }) => insertAt(editor, range, toggleJson(1))],
    ['Basic blocks', 'Toggle heading 2', 'A medium heading that opens and closes', '▸H2', '', ({ editor, range }) => insertAt(editor, range, toggleJson(2))],
    ['Basic blocks', 'Toggle heading 3', 'A small heading that opens and closes', '▸H3', '', ({ editor, range }) => insertAt(editor, range, toggleJson(3))],
    ['Basic blocks', '2 columns', 'Put blocks side by side', '▥', '', ({ editor, range }) => insertAt(editor, range, columnsJson(2))],
    ['Basic blocks', '3 columns', 'Three blocks side by side', '▥', '', ({ editor, range }) => insertAt(editor, range, columnsJson(3))],
    ['Basic blocks', 'Quote', 'Capture a quote', '❝', '>', setBlock(byKey('quote'))],
    ['Basic blocks', 'Callout', 'Make writing stand out', '💡', '', setBlock(c => c.insertContent({ type: 'callout', attrs: { emoji: '💡', color: 'yellow' }, content: [{ type: 'paragraph' }] }))],
    ['Basic blocks', 'Divider', 'Visually divide blocks', '—', '---', setBlock(c => c.setHorizontalRule())],
    ['Basic blocks', 'Page', 'Embed a sub-page', '📄', '', async ({ editor, range }) => {
      editor.chain().focus().deleteRange(range).run();
      const p = await host.createSubPage();
      if (p) editor.chain().focus().insertContent({ type: 'subPage', attrs: { id: p.id, title: p.title, emoji: p.emoji || '📄' } }).run();
    }],
    ['Basic blocks', 'Table of contents', 'A live list of the headings on this page', '☰', '', setBlock(c => c.insertContent({ type: 'noteToc' }))],
    ['Basic blocks', 'Link to page', 'Mention another page, type [[ too', '↗', '[[', ({ editor, range }) => editor.chain().focus().deleteRange(range).insertContent('[[').run()],
    ['Media', 'Drawing', 'Sketch by hand, together in real time', '✏️', '', setBlock(c => c.insertContent({ type: 'noteDraw', attrs: { id: newDrawId(), height: 320 } }))],
    ['Media', 'Image', 'Upload or paste — stored on NAS', '🖼', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.pickFiles('image/*'); }],
    ['Media', 'File', 'Attach any file', '📎', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.pickFiles(''); }],
    ['Media', 'Code', 'Capture a code snippet', '</>', '```', setBlock(byKey('code'))],
    ['Media', 'Table', 'Rows and columns of plain text', '▦', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.tablePicker?.(); }],
    ['Media', 'Bookmark', 'Link card with title and picture', '🔖', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.askLink?.('bookmark'); }],
    ['Media', 'Embed video', 'YouTube or .mp4 link, plays in the page', '▶', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.askLink?.('embed'); }],
    ['Inline', 'Mention person', 'Notify someone', '@', '@', ({ editor, range }) => editor.chain().focus().deleteRange(range).insertContent('@').run()],
    ['Inline', 'Date', 'A date chip you can change, today by default', '📅', '', ({ editor, range }) => editor.chain().focus().deleteRange(range)
      .insertContent([{ type: 'dateChip', attrs: { date: isoDay(new Date()) } }, { type: 'text', text: ' ' }]).run()],
    ['Inline', 'Today', "Today's date as a chip (also @today)", '📅', '', ({ editor, range }) => editor.chain().focus().deleteRange(range)
      .insertContent([{ type: 'dateChip', attrs: { date: isoDay(new Date()) } }, { type: 'text', text: ' ' }]).run()],
    ['Inline', 'Symbol', 'Arrows, ticks, ° ± ≠ … and what to type for them', '→', '->', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); openSymbolPanel(editor); }],
  ].map(([group, name, desc, box, kbd, run]) => ({ group, name, desc, box, kbd, run }));
}

/// A keyboard-driven popup list used by both "/" and "@".
/// emptyText: a string, or (query) => string for a message that follows what was typed.
export function listPopup(renderItem, emptyText, host) {
  let pop = null, items = [], sel = 0, command = null, query = '';
  const phonePosition = () => {
    if (!pop?.classList.contains('m-notes-mentions')) return;
    const vv = window.visualViewport;
    pop.style.bottom = `${Math.max(0, innerHeight - ((vv?.offsetTop || 0) + (vv?.height || innerHeight))) + 48}px`;
  };
  const unwatch = () => { window.visualViewport?.removeEventListener('resize', phonePosition); window.visualViewport?.removeEventListener('scroll', phonePosition); };
  let painted = '';
  // Moves the highlight without rebuilding the list, and scrolls only inside the popup (scrollIntoView would also scroll the
  // page, which made the page jump while someone else was typing on it).
  const highlight = (reveal = true) => {
    pop.querySelectorAll('.it.sel').forEach(x => x.classList.remove('sel'));
    const cur = pop.querySelector(`.it[data-i="${sel}"]`);
    if (!cur) return;
    cur.classList.add('sel');
    if (!reveal) return;
    const top = cur.offsetTop, bottom = top + cur.offsetHeight;
    if (top < pop.scrollTop) pop.scrollTop = top - 4;
    else if (bottom > pop.scrollTop + pop.clientHeight) pop.scrollTop = bottom - pop.clientHeight + 4;
  };
  const paint = () => {
    let g = '', html = '';
    items.forEach((it, i) => {
      if (it.group && it.group !== g) { g = it.group; html += `<div class="grp">${esc(g)}</div>`; }
      html += `<div class="it" data-i="${i}">${renderItem(it)}</div>`;
    });
    html ||= `<div class="empty">${typeof emptyText === 'function' ? emptyText(query) : emptyText}</div>`;
    // Same list as before (an edit by someone else on the page re-runs the suggestion): keep the DOM and its scroll.
    const fresh = html !== painted;
    if (fresh) { pop.innerHTML = html; painted = html; pop.scrollTop = 0; }
    highlight(fresh);   // the same list again: don't scroll it back to the selected row
  };
  return {
    onStart(props) {
      painted = ''; query = props.query ?? '';
      pop = $el('nt-pop nt-list');
      if (host && window.ipop?.mobile?.isMobile()) {
        pop.classList.add('m-notes-mentions');
        pop.setAttribute('role', 'dialog');
        pop.setAttribute('aria-label', 'Mention a person');
        host.mentionSheet(true);
        window.visualViewport?.addEventListener('resize', phonePosition);
        window.visualViewport?.addEventListener('scroll', phonePosition);
      }
      pop.addEventListener('mousedown', e => {
        e.preventDefault();
        const it = e.target.closest('[data-i]'); if (it) command(items[+it.dataset.i]);
      });
      pop.addEventListener('mousemove', e => { const it = e.target.closest('[data-i]'); if (it && +it.dataset.i !== sel) { sel = +it.dataset.i; highlight(false); } });
      document.body.appendChild(pop);
      this.onUpdate(props);
    },
    onUpdate(props) {
      if (!pop) return; // a late async search must not revive a dismissed suggestion
      // Every update (also one caused by someone else typing elsewhere on the page) first arrives "loading" with no items,
      // then again with the results. Painting the empty one flashed "No results" and reset the list's scroll: keep what is
      // shown until the results come (a first paint still shows the empty text).
      if (props.loading && painted) { command = props.command; const r = props.clientRect?.(); if (r && !pop.classList.contains('m-notes-mentions')) place(pop, r); return; }
      if (query !== (props.query ?? '')) sel = 0; // a new search starts at the top
      items = props.items; command = props.command; query = props.query ?? ''; sel = Math.min(sel, Math.max(items.length - 1, 0));
      paint();
      const r = props.clientRect?.();
      if (pop.classList.contains('m-notes-mentions')) {
        pop.classList.add('show');
        phonePosition();
      } else if (r) place(pop, r);
    },
    onKeyDown({ event }) {
      if (event.key === 'Escape') { unwatch(); host?.mentionSheet(false); pop?.remove(); pop = null; return true; }
      if (!items.length) return false;
      if (event.key === 'ArrowDown') { sel = (sel + 1) % items.length; highlight(); return true; }
      if (event.key === 'ArrowUp') { sel = (sel - 1 + items.length) % items.length; highlight(); return true; }
      if (event.key === 'Enter' || event.key === 'Tab') { command(items[sel]); return true; }
      return false;
    },
    onExit() { unwatch(); host?.mentionSheet(false); pop?.remove(); pop = null; sel = 0; painted = ''; query = ''; },
  };
}

export const SlashMenu = Extension.create({
  name: 'slashMenu',
  addOptions() { return { host: null }; },
  addProseMirrorPlugins() {
    const cmds = slashCommands(this.options.host);
    return [Suggestion({
      editor: this.editor,
      pluginKey: new PluginKey('slash'),
      char: '/',
      allowedPrefixes: null,
      allow: ({ state, range }) => {
        const $from = state.doc.resolve(range.from);
        return $from.parent.type.name !== 'codeBlock';
      },
      // While searching, results are ranked best-first, so group titles would repeat ("Media … Basic blocks … Media"): drop them.
      items: ({ query }) => (String(query || '').trim() ? filterSlash(cmds, query).map(c => ({ ...c, group: null })) : cmds),
      command: ({ editor, range, props }) => props.run({ editor, range }),
      render: () => listPopup(c => `<span class="bx">${esc(c.box)}</span><div><div class="nm">${esc(c.name)}</div><div class="ds">${esc(c.desc)}</div></div>${c.kbd ? `<kbd>${esc(c.kbd)}</kbd>` : ''}`, 'No results'),
    })];
  },
});

/// Options for @tiptap/extension-mention.
export const phoneMentionKey = new PluginKey('noteMention');
export function mentionSuggestion(host) {
  return {
    pluginKey: phoneMentionKey,
    char: '@',
    // Full names have spaces: keep the popup open while typing "yudi arta", close after two spaces.
    allowSpaces: true,
    // Two spaces in a row = the user moved on: no popup at all (not even "No one named …").
    allow: ({ state, range }) => !/\s{2,}/.test(state.doc.textBetween(range.from, range.to, ' ')),
    items: async ({ query }) => (query.trim().length < 2 || /\s{2,}/.test(query) ? [] : await host.searchPeople(query.trim())),
    command: ({ editor, range, props }) => {
      editor.chain().focus().insertContentAt(range, [
        { type: 'mention', attrs: { id: props.id, label: props.name } }, { type: 'text', text: ' ' },
      ]).run();
      host.mentioned(props);
    },
    // Second line: department (never the NIK — office rule). People who can't open the page are
    // marked; picking one asks whether to share the page with them (host.mentioned).
    render: () => listPopup(p => `<span class="nt-av" style="background:${host.avatarColor(p.nik || p.id)}">${esc(host.initials(p.name))}</span><div><div class="nm">${esc(p.name)}</div><div class="ds">${esc(p.department || '')}</div></div>${p.hasAccess === false ? '<span class="nt-noacc">No access</span>' : ''}`,
      q => (q.trim().length < 2 ? 'Type at least 2 letters of a name' : `No one named “${esc(q.trim())}”`), host),
  };
}

// ── Selection toolbar ───────────────────────────────────────────────────────

export function createToolbar(editor, host) {
  let disposed = false;
  const bar = $el('nt-pop nt-fmt', `
    <button data-c="bold" title="Bold (Ctrl B)"><b>B</b></button>
    <button data-c="italic" title="Italic (Ctrl I)"><i>I</i></button>
    <button data-c="underline" title="Underline (Ctrl U)"><u>U</u></button>
    <button data-c="strike" title="Strikethrough"><s>S</s></button>
    <button data-c="code" title="Inline code"><span style="font-family:var(--mono)">&lt;/&gt;</span></button>
    <button data-c="link" title="Link">🔗</button>
    <span class="div"></span>
    <button data-c="color" title="Text colour & highlight"><span class="a-ico">A</span>▾</button>
    <span class="div nt-cmt-only"></span>
    <button data-c="comment" class="nt-cmt-only" title="Comment (Ctrl Shift M)">💬 Comment</button>`);
  const colors = $el('nt-pop nt-menu nt-colors',
    '<div class="mh">TEXT COLOUR</div><div class="mi" data-tc=""><span class="sw">A</span>Default</div>'
    + COLORS.map(c => `<div class="mi" data-tc="${c}"><span class="sw" style="color:var(--nt-${c})">A</span>${cap(c)}</div>`).join('')
    + '<hr><div class="mh">HIGHLIGHT</div><div class="mi" data-hc=""><span class="sw"></span>No highlight</div>'
    + COLORS.map(c => `<div class="mi" data-hc="${c}"><span class="sw" style="background:var(--nb-${c})"></span>${cap(c)} background</div>`).join(''));
  // Link editor: an inline popover instead of the browser's prompt().
  const linkPop = $el('nt-pop nt-link-pop', `
    <input type="url" class="nt-link-in" placeholder="Paste or type a link" aria-label="Link URL" spellcheck="false">
    <div class="nt-link-acts"><button type="button" class="nt-link-rm" data-l="remove">Remove</button><button type="button" class="nt-link-ok" data-l="apply">Apply</button></div>`);
  const linkIn = linkPop.querySelector('input');
  let linkRange = null;
  document.body.append(bar, colors, linkPop);
  [bar, colors].forEach(e => e.addEventListener('mousedown', ev => ev.preventDefault()));
  linkPop.addEventListener('mousedown', ev => { if (ev.target !== linkIn) ev.preventDefault(); });
  const closeLink = (refocus = true) => {
    linkPop.classList.remove('show');
    if (refocus && linkRange) editor.chain().focus().setTextSelection(linkRange).run();
    linkRange = null;
  };
  const applyLink = remove => {
    if (!linkRange) return;
    const url = linkIn.value.trim();
    const c = editor.chain().focus().setTextSelection(linkRange).extendMarkRange('link');
    if (remove || !url) c.unsetLink().run();
    else c.setLink({ href: /^(https?:|mailto:|\/)/i.test(url) ? url : `https://${url}` }).run();
    linkRange = null;
    linkPop.classList.remove('show');
  };
  function openLink(anchor) {
    const { from, to } = editor.state.selection;
    linkRange = { from, to };
    linkIn.value = editor.getAttributes('link').href || '';
    linkPop.querySelector('[data-l="remove"]').hidden = !editor.isActive('link');
    colors.classList.remove('show');
    place(linkPop, anchor.getBoundingClientRect());
    linkIn.focus(); linkIn.select();
  }
  linkPop.addEventListener('click', e => { const b = e.target.closest('[data-l]'); if (b) applyLink(b.dataset.l === 'remove'); });
  linkIn.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); applyLink(false); }
    else if (e.key === 'Escape') { e.preventDefault(); closeLink(); }
  });
  const linkOutside = ev => { if (linkPop.classList.contains('show') && !linkPop.contains(ev.target) && !bar.contains(ev.target)) closeLink(false); };
  document.addEventListener('mousedown', linkOutside);

  bar.addEventListener('click', e => {
    const b = e.target.closest('[data-c]'); if (!b) return;
    const c = editor.chain().focus();
    switch (b.dataset.c) {
      case 'bold': c.toggleBold().run(); break;
      case 'italic': c.toggleItalic().run(); break;
      case 'underline': c.toggleUnderline().run(); break;
      case 'strike': c.toggleStrike().run(); break;
      case 'code': c.toggleCode().run(); break;
      case 'link':
        if (linkPop.classList.contains('show')) closeLink(); else openLink(b);
        return;
      case 'color':
        if (colors.classList.contains('show')) colors.classList.remove('show');
        else place(colors, b.getBoundingClientRect());
        break;
      case 'comment': host.commentSelection(); break;
    }
    sync();
  });
  colors.addEventListener('click', e => {
    const t = e.target.closest('[data-tc]'), hc = e.target.closest('[data-hc]');
    if (t) t.dataset.tc ? editor.chain().focus().setColor(`var(--nt-${t.dataset.tc})`).run() : editor.chain().focus().unsetColor().run();
    if (hc) hc.dataset.hc ? editor.chain().focus().setBackgroundColor(`var(--nb-${hc.dataset.hc})`).run() : editor.chain().focus().unsetBackgroundColor().run();
    colors.classList.remove('show');
  });

  const hide = () => { if (linkPop.contains(document.activeElement)) return; bar.classList.remove('show'); colors.classList.remove('show'); };
  function sync() {
    const { state, view } = editor;
    const { from, to, empty } = state.selection;
    const canComment = host.canComment();
    if (empty || !view.hasFocus() || (!editor.isEditable && !canComment) || state.selection.node) { hide(); return; }
    if (!state.doc.textBetween(from, to, ' ').trim()) { hide(); return; }
    bar.classList.toggle('readonly', !editor.isEditable);
    bar.querySelectorAll('.nt-cmt-only').forEach(x => x.hidden = !canComment);
    ['bold', 'italic', 'underline', 'strike', 'code', 'link'].forEach(m => bar.querySelector(`[data-c="${m}"]`)?.classList.toggle('on', editor.isActive(m)));
    const a = view.coordsAtPos(from), z = view.coordsAtPos(to, -1);
    const rect = { top: Math.min(a.top, z.top), bottom: Math.min(a.top, z.top), left: Math.min(a.left, z.left), right: Math.max(a.right, z.right) };
    rect.width = rect.right - rect.left;
    bar.style.visibility = 'hidden'; bar.classList.add('show');
    const w = bar.offsetWidth, h = bar.offsetHeight;
    let top = rect.top - h - 8; if (top < 8) top = Math.max(a.bottom, z.bottom) + 8;
    bar.style.top = `${top}px`;
    bar.style.left = `${Math.min(Math.max(8, rect.left + rect.width / 2 - w / 2), innerWidth - w - 8)}px`;
    bar.style.visibility = '';
  }
  editor.on('selectionUpdate', sync);
  editor.on('blur', ({ event }) => { if (!bar.contains(event?.relatedTarget) && !colors.contains(event?.relatedTarget) && !linkPop.contains(event?.relatedTarget)) setTimeout(() => { if (!disposed && !editor.isDestroyed && !editor.view.hasFocus()) hide(); }, 120); });
  return { sync, hide, destroy() { disposed = true; document.removeEventListener('mousedown', linkOutside); bar.remove(); colors.remove(); linkPop.remove(); } };
}

// ── Block handle + menu ─────────────────────────────────────────────────────

export function createHandle(host) {
  const dom = $el('nt-handle', '<button type="button" class="add" title="Add block below">+</button><button type="button" class="grip" title="Drag to move · Click for options" data-drag-handle>⠿</button>');
  return dom;
}

export { createBlockMenu } from './block-menu.js';
