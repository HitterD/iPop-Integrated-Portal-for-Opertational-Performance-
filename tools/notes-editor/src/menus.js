import { Extension } from '@tiptap/core';
import Suggestion from '@tiptap/suggestion';
import { PluginKey } from '@tiptap/pm/state';
import { COLORS, cap, esc } from './nodes.js';

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
];

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
    ['Basic blocks', 'Quote', 'Capture a quote', '❝', '>', setBlock(byKey('quote'))],
    ['Basic blocks', 'Callout', 'Make writing stand out', '💡', '', setBlock(c => c.insertContent({ type: 'callout', attrs: { emoji: '💡', color: 'yellow' }, content: [{ type: 'paragraph' }] }))],
    ['Basic blocks', 'Divider', 'Visually divide blocks', '—', '---', setBlock(c => c.setHorizontalRule())],
    ['Basic blocks', 'Page', 'Embed a sub-page', '📄', '', async ({ editor, range }) => {
      editor.chain().focus().deleteRange(range).run();
      const p = await host.createSubPage();
      if (p) editor.chain().focus().insertContent({ type: 'subPage', attrs: { id: p.id, title: p.title, emoji: p.emoji || '📄' } }).run();
    }],
    ['Media', 'Image', 'Upload or paste — stored on NAS', '🖼', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.pickFiles('image/*'); }],
    ['Media', 'File', 'Attach any file', '📎', '', ({ editor, range }) => { editor.chain().focus().deleteRange(range).run(); host.pickFiles(''); }],
    ['Media', 'Code', 'Capture a code snippet', '</>', '```', setBlock(byKey('code'))],
    ['Inline', 'Mention person', 'Notify someone', '@', '@', ({ editor, range }) => editor.chain().focus().deleteRange(range).insertContent('@').run()],
    ['Inline', 'Date', 'Insert today\'s date', '📅', '', ({ editor, range }) => editor.chain().focus().deleteRange(range)
      .insertContent(new Date().toLocaleDateString('en-US', { day: 'numeric', month: 'long', year: 'numeric' }) + ' ').run()],
  ].map(([group, name, desc, box, kbd, run]) => ({ group, name, desc, box, kbd, run }));
}

/// A keyboard-driven popup list used by both "/" and "@".
function listPopup(renderItem, emptyText) {
  let pop = null, items = [], sel = 0, command = null;
  const paint = () => {
    let g = '', html = '';
    items.forEach((it, i) => {
      if (it.group && it.group !== g) { g = it.group; html += `<div class="grp">${esc(g)}</div>`; }
      html += `<div class="it ${i === sel ? 'sel' : ''}" data-i="${i}">${renderItem(it)}</div>`;
    });
    pop.innerHTML = html || `<div class="empty">${emptyText}</div>`;
    pop.querySelector('.it.sel')?.scrollIntoView({ block: 'nearest' });
  };
  return {
    onStart(props) {
      pop = $el('nt-pop nt-list');
      pop.addEventListener('mousedown', e => {
        e.preventDefault();
        const it = e.target.closest('[data-i]'); if (it) command(items[+it.dataset.i]);
      });
      pop.addEventListener('mousemove', e => { const it = e.target.closest('[data-i]'); if (it && +it.dataset.i !== sel) { sel = +it.dataset.i; paint(); } });
      document.body.appendChild(pop);
      this.onUpdate(props);
    },
    onUpdate(props) {
      items = props.items; command = props.command; sel = Math.min(sel, Math.max(items.length - 1, 0));
      paint();
      const r = props.clientRect?.(); if (r) place(pop, r);
    },
    onKeyDown({ event }) {
      if (event.key === 'Escape') { pop?.remove(); pop = null; return true; }
      if (!items.length) return false;
      if (event.key === 'ArrowDown') { sel = (sel + 1) % items.length; paint(); return true; }
      if (event.key === 'ArrowUp') { sel = (sel - 1 + items.length) % items.length; paint(); return true; }
      if (event.key === 'Enter' || event.key === 'Tab') { command(items[sel]); return true; }
      return false;
    },
    onExit() { pop?.remove(); pop = null; sel = 0; },
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
      items: ({ query }) => cmds.filter(c => c.name.toLowerCase().includes(query.toLowerCase())),
      command: ({ editor, range, props }) => props.run({ editor, range }),
      render: () => listPopup(c => `<span class="bx">${esc(c.box)}</span><div><div class="nm">${esc(c.name)}</div><div class="ds">${esc(c.desc)}</div></div>${c.kbd ? `<kbd>${esc(c.kbd)}</kbd>` : ''}`, 'No results'),
    })];
  },
});

/// Options for @tiptap/extension-mention.
export function mentionSuggestion(host) {
  return {
    char: '@',
    items: async ({ query }) => (query.length < 2 ? [] : await host.searchPeople(query)),
    command: ({ editor, range, props }) => {
      editor.chain().focus().insertContentAt(range, [
        { type: 'mention', attrs: { id: props.id, label: props.name } }, { type: 'text', text: ' ' },
      ]).run();
      host.mentioned(props);
    },
    render: () => listPopup(p => `<span class="nt-av" style="background:${host.avatarColor(p.nik)}">${esc(host.initials(p.name))}</span><div><div class="nm">${esc(p.name)}</div><div class="ds">${esc(p.position || p.nik)}</div></div>`,
      'Type at least 2 letters of a name'),
  };
}

// ── Selection toolbar ───────────────────────────────────────────────────────

export function createToolbar(editor, host) {
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
  document.body.append(bar, colors);
  [bar, colors].forEach(e => e.addEventListener('mousedown', ev => ev.preventDefault()));

  bar.addEventListener('click', e => {
    const b = e.target.closest('[data-c]'); if (!b) return;
    const c = editor.chain().focus();
    switch (b.dataset.c) {
      case 'bold': c.toggleBold().run(); break;
      case 'italic': c.toggleItalic().run(); break;
      case 'underline': c.toggleUnderline().run(); break;
      case 'strike': c.toggleStrike().run(); break;
      case 'code': c.toggleCode().run(); break;
      case 'link': {
        const prev = editor.getAttributes('link').href || '';
        const url = window.prompt('Link URL', prev);
        if (url === null) break;
        if (!url) c.unsetLink().run();
        else c.setLink({ href: /^(https?:|mailto:|\/)/i.test(url) ? url : `https://${url}` }).run();
        break;
      }
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

  const hide = () => { bar.classList.remove('show'); colors.classList.remove('show'); };
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
  editor.on('blur', ({ event }) => { if (!bar.contains(event?.relatedTarget) && !colors.contains(event?.relatedTarget)) setTimeout(() => { if (!editor.view.hasFocus()) hide(); }, 120); });
  return { sync, hide, destroy() { bar.remove(); colors.remove(); } };
}

// ── Block handle + menu ─────────────────────────────────────────────────────

export function createHandle(host) {
  const dom = $el('nt-handle', '<button type="button" class="add" title="Add block below">+</button><button type="button" class="grip" title="Drag to move · Click for options" data-drag-handle>⠿</button>');
  return dom;
}

export function createBlockMenu(editor, host, getTarget) {
  const menu = $el('nt-pop nt-menu nt-block-menu');
  document.body.appendChild(menu);
  let target = null;
  const close = () => { menu.classList.remove('show'); host.lockHandle(false); };

  function open(anchor) {
    target = getTarget();
    if (!target) return;
    const node = editor.state.doc.nodeAt(target);
    if (!node) return;
    const isCallout = node.type.name === 'callout';
    const cur = isCallout ? node.attrs.color : node.attrs.bg;
    menu.innerHTML = `<div class="mh">TURN INTO</div>`
      + TURN_INTO.map(t => `<div class="mi" data-turn="${t.key}"><span class="sw">${esc(t.box)}</span>${esc(t.name)}</div>`).join('')
      + `<hr><div class="mh">${isCallout ? 'CALLOUT COLOUR' : 'BACKGROUND COLOUR'}</div><div class="swrow">`
      + (isCallout ? '' : `<span class="sq ${!cur ? 'on' : ''}" data-bg="" title="Default" style="background:var(--surface)"></span>`)
      + COLORS.map(c => `<span class="sq ${cur === c ? 'on' : ''}" data-bg="${c}" title="${cap(c)}" style="background:var(--nb-${c})"></span>`).join('')
      + `</div><hr>`
      + (host.canComment() ? '<div class="mi" data-act="comment"><span class="sw">💬</span>Comment<span class="ds">Ctrl ⇧ M</span></div>' : '')
      + '<div class="mi" data-act="dup"><span class="sw">⧉</span>Duplicate</div>'
      + '<div class="mi danger" data-act="del"><span class="sw">🗑</span>Delete<span class="ds">Del</span></div>';
    host.lockHandle(true);
    place(menu, anchor.getBoundingClientRect());
  }

  menu.addEventListener('mousedown', e => e.preventDefault());
  menu.addEventListener('click', e => {
    const pos = target; const node = pos == null ? null : editor.state.doc.nodeAt(pos);
    if (!node) return close();
    const turn = e.target.closest('[data-turn]'), bg = e.target.closest('[data-bg]'), act = e.target.closest('[data-act]');
    if (turn) {
      const inner = Math.min(pos + 1, editor.state.doc.content.size);
      TURN_INTO.find(t => t.key === turn.dataset.turn).run(editor.chain().focus().setTextSelection(inner)).run();
    } else if (bg) {
      const attrs = node.type.name === 'callout' ? { ...node.attrs, color: bg.dataset.bg || 'gray' } : { ...node.attrs, bg: bg.dataset.bg || null };
      if ('bg' in node.attrs || node.type.name === 'callout') editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, attrs));
    } else if (act?.dataset.act === 'dup') {
      editor.chain().focus().insertContentAt(pos + node.nodeSize, node.toJSON()).run();
    } else if (act?.dataset.act === 'del') {
      editor.chain().focus().deleteRange({ from: pos, to: pos + node.nodeSize }).run();
    } else if (act?.dataset.act === 'comment') {
      editor.chain().focus().setTextSelection({ from: pos + 1, to: pos + node.nodeSize - 1 }).run();
      host.commentSelection();
    } else return;
    close();
  });
  const outside = ev => { if (!menu.contains(ev.target) && !ev.target.closest?.('.nt-handle')) close(); };
  document.addEventListener('mousedown', outside);
  return { open, close, destroy() { document.removeEventListener('mousedown', outside); menu.remove(); } };
}
