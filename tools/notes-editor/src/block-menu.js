// Block menu (the ⠿ handle): a search box, "Turn into" as a grid of tiles, colours in one row, numbering options for numbered
// lists, then actions with their keyboard keys. Typing filters everything ("h2" → Heading 2, "red" → red background,
// "dup" → Duplicate); Enter runs the highlighted match.
import { COLORS, cap, esc } from './nodes.js';
import { TURN_INTO, SLASH_ALIASES } from './menus.js';

const $el = (cls, html = '') => { const e = document.createElement('div'); e.className = cls; e.innerHTML = html; return e; };

/// Keyboard shortcuts shown on "Turn into" tiles (handled by noteKeys in index.js).
export const TURN_KEYS = { text: 'Ctrl Alt 0', h1: 'Ctrl Alt 1', h2: 'Ctrl Alt 2', h3: 'Ctrl Alt 3', number: 'Ctrl ⇧ 7', bullet: 'Ctrl ⇧ 8', todo: 'Ctrl ⇧ 9', code: 'Ctrl Alt C' };

/// Which "Turn into" tile describes this block (shown as selected).
export function currentTurn(node) {
  if (!node) return null;
  switch (node.type.name) {
    case 'paragraph': return 'text';
    case 'heading': return `h${node.attrs.level}`;
    case 'taskList': return 'todo';
    case 'bulletList': return 'bullet';
    case 'orderedList': return 'number';
    case 'blockquote': return 'quote';
    case 'codeBlock': return 'code';
    case 'callout': return 'callout';
    case 'details': return 'toggle';
    default: return null;
  }
}

/// Does a search word match an entry's keywords? ("h2" matches "heading 2 h2", "dup" matches "duplicate")
export function menuMatch(keywords, q) {
  q = String(q || '').trim().toLowerCase();
  if (!q) return true;
  const k = String(keywords || '').toLowerCase();
  return k.split(/\s+/).some(w => w.startsWith(q)) || k.includes(q);
}

/// Moves the block at `pos` one place up or down among its siblings. Returns the transaction, or null at the edge.
export function moveBlockTr(state, pos, dir) {
  const node = state.doc.nodeAt(pos);
  if (!node) return null;
  const $p = state.doc.resolve(pos);
  const idx = $p.index(), parent = $p.parent;
  if ((dir < 0 && idx === 0) || (dir > 0 && idx >= parent.childCount - 1)) return null;
  const other = parent.child(idx + dir);
  const tr = state.tr.delete(pos, pos + node.nodeSize);
  tr.insert(dir < 0 ? pos - other.nodeSize : pos + other.nodeSize, node);
  return tr;
}

export function createBlockMenu(editor, host, getTarget) {
  const menu = $el('nt-pop nt-menu nt-block-menu');
  menu.setAttribute('role', 'menu');
  document.body.appendChild(menu);
  let target = null;
  const close = () => { menu.classList.remove('show'); host.lockHandle(false); };

  function paint(node) {
    const isCallout = node.type.name === 'callout';
    const cur = isCallout ? node.attrs.color : node.attrs.bg;
    const now = currentTurn(node);
    const isOl = node.type.name === 'orderedList';
    const mi = (act, icon, label, keys, words, extra = '') => `<div class="mi ${extra}" role="menuitem" data-act="${act}" data-q="${esc(words)}"><span class="sw">${icon}</span>${label}${keys ? `<span class="ds">${keys}</span>` : ''}</div>`;
    menu.innerHTML = '<input class="nt-bm-q" type="search" placeholder="Search… e.g. h2, red, duplicate" aria-label="Search block actions" spellcheck="false" autocomplete="off">'
      + '<div class="mh">TURN INTO</div><div class="nt-bm-grid">'
      + TURN_INTO.map(t => `<button type="button" class="nt-bm-tile ${t.key === now ? 'on' : ''}" data-turn="${t.key}" data-q="${esc(`${t.name} ${t.key} ${SLASH_ALIASES[t.name] || ''}`)}" title="${esc(t.name)}${TURN_KEYS[t.key] ? ` · ${TURN_KEYS[t.key]}` : ''}"><span class="sw">${esc(t.box)}</span><span class="nm">${esc(t.name.replace(' list', ''))}</span></button>`).join('')
      + `</div><div class="mh">${isCallout ? 'CALLOUT COLOUR' : 'BACKGROUND'}</div><div class="swrow">`
      + (isCallout ? '' : `<span class="sq ${!cur ? 'on' : ''}" data-bg="" data-q="default none colour color background" title="Default" style="background:var(--surface)"></span>`)
      + COLORS.map(c => `<span class="sq ${cur === c ? 'on' : ''}" data-bg="${c}" data-q="${c} colour color background warna" title="${cap(c)}" style="background:var(--nb-${c})"></span>`).join('')
      + '</div><hr>'
      + (isOl ? '<div class="mh">NUMBERING</div>'
        + mi('num-continue', '↧', 'Continue numbering', node.attrs.numbering === 'continue' ? '✓' : '', 'continue numbering lanjut nomor')
        + mi('num-restart', '1', 'Restart at 1', node.attrs.numbering === 'restart' ? '✓' : '', 'restart numbering ulang nomor')
        + mi('num-start', '#', 'Start at…', '', 'start at number mulai nomor') + '<hr>' : '')
      + (host.canComment() ? mi('comment', '💬', 'Comment', 'Ctrl ⇧ M', 'comment komentar') : '')
      + mi('link', '🔗', 'Copy link to block', '', 'copy link block')
      + mi('dup', '⧉', 'Duplicate', 'Ctrl D', 'duplicate copy salin')
      + mi('up', '↑', 'Move up', 'Ctrl ⇧ ↑', 'move up naik')
      + mi('down', '↓', 'Move down', 'Ctrl ⇧ ↓', 'move down turun')
      + mi('del', '🗑', 'Delete', 'Del', 'delete remove hapus', 'danger')
      + '<div class="nt-bm-empty" hidden>No matching action</div>';
  }

  // Next to the handle (to its right), its top level with the block and moved up only as far as the screen needs, never under
  // the app's top bar; too tall for the screen = it scrolls.
  function sideOf(r) {
    menu.style.visibility = 'hidden'; menu.classList.add('show');
    const bar = document.querySelector('.topbar, .v6-topbar, header.app-top')?.getBoundingClientRect().bottom || 0;
    const minTop = Math.max(10, bar + 6), room = innerHeight - minTop - 10;
    menu.style.maxHeight = `${Math.min(560, room)}px`;
    const h = menu.offsetHeight, w = menu.offsetWidth;
    const top = Math.max(minTop, Math.min(r.top - 6, innerHeight - h - 10));
    let left = r.right + 6;
    if (left + w > innerWidth - 8) left = Math.max(8, r.left - w - 6);
    menu.style.top = `${top}px`; menu.style.left = `${left}px`; menu.style.visibility = '';
  }

  function open(anchor) {
    target = getTarget();
    if (target == null) return;
    const node = editor.state.doc.nodeAt(target);
    if (!node) return;
    paint(node);
    host.lockHandle(true);
    sideOf(anchor.getBoundingClientRect());
    menu.scrollTop = 0;
    menu.querySelector('.nt-bm-q')?.focus({ preventScroll: true });
  }

  const visible = () => [...menu.querySelectorAll('[data-q]')].filter(e => !e.hidden);
  function filter(q) {
    const searching = !!q.trim();
    let any = false;
    menu.querySelectorAll('[data-q]').forEach(e => { const hit = menuMatch(e.dataset.q, q); e.hidden = !hit; any ||= hit; });
    menu.querySelectorAll('.mh, hr').forEach(e => { e.hidden = searching; });
    menu.querySelectorAll('.nt-bm-grid, .swrow').forEach(g => { g.hidden = searching && ![...g.querySelectorAll('[data-q]')].some(x => !x.hidden); });
    menu.querySelector('.nt-bm-empty').hidden = any;
    menu.querySelectorAll('.kb').forEach(e => e.classList.remove('kb'));
    if (searching) visible()[0]?.classList.add('kb');
  }
  menu.addEventListener('input', e => { if (e.target.classList.contains('nt-bm-q')) filter(e.target.value); });
  menu.addEventListener('keydown', e => {
    if (!e.target.classList.contains('nt-bm-q')) return;
    if (e.key === 'Escape') { e.preventDefault(); close(); editor.commands.focus(); return; }
    if (e.key === 'Enter') { e.preventDefault(); const m = menu.querySelector('.kb') || visible()[0]; if (m) run(m); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const list = visible(); if (!list.length) return;
      const i = list.findIndex(x => x.classList.contains('kb'));
      list.forEach(x => x.classList.remove('kb'));
      const next = list[(i + (e.key === 'ArrowDown' ? 1 : -1) + list.length) % list.length];
      next.classList.add('kb');
      const top = next.offsetTop, bottom = top + next.offsetHeight;
      if (top < menu.scrollTop) menu.scrollTop = top - 4; else if (bottom > menu.scrollTop + menu.clientHeight) menu.scrollTop = bottom - menu.clientHeight + 4;
    }
  });

  function run(el) {
    const pos = target; const node = pos == null ? null : editor.state.doc.nodeAt(pos);
    if (!node) { close(); return; }
    const turn = el.closest('[data-turn]'), bg = el.closest('[data-bg]'), act = el.closest('[data-act]');
    if (turn) {
      const inner = Math.min(pos + 1, editor.state.doc.content.size);
      TURN_INTO.find(t => t.key === turn.dataset.turn).run(editor.chain().focus().setTextSelection(inner)).run();
    } else if (bg) {
      const attrs = node.type.name === 'callout' ? { ...node.attrs, color: bg.dataset.bg || 'gray' } : { ...node.attrs, bg: bg.dataset.bg || null };
      if ('bg' in node.attrs || node.type.name === 'callout') editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, attrs));
    } else if (act) {
      const a = act.dataset.act;
      if (a === 'link') { if (!host.copyBlockLink?.(pos)) host.toast('This kind of block cannot be linked to'); }
      else if (a === 'dup') editor.chain().focus().insertContentAt(pos + node.nodeSize, node.toJSON()).run();
      else if (a === 'del') editor.chain().focus().deleteRange({ from: pos, to: pos + node.nodeSize }).run();
      else if (a === 'up' || a === 'down') { const tr = moveBlockTr(editor.state, pos, a === 'up' ? -1 : 1); if (tr) editor.view.dispatch(tr); }
      else if (a === 'comment') { editor.chain().focus().setTextSelection({ from: pos + 1, to: pos + node.nodeSize - 1 }).run(); host.commentSelection(); }
      else if (a === 'num-continue' || a === 'num-restart') {
        const want = a === 'num-continue' ? 'continue' : 'restart';
        editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, numbering: node.attrs.numbering === want ? 'auto' : want, start: 1 }));
      } else if (a === 'num-start') { askStart(pos, node, act); return; }
      else return;
    } else return;
    close();
  }

  // "Start at…": a small number field in place of the menu row
  function askStart(pos, node, row) {
    const f = $el('nt-bm-start', `<label>Start at <input type="number" min="1" max="9999" value="${node.attrs.start ?? 1}"></label><button type="button">Set</button>`);
    row.replaceWith(f);
    const input = f.querySelector('input');
    input.focus({ preventScroll: true }); input.select();
    const done = () => {
      const n = Math.max(1, Math.min(9999, Math.round(+input.value) || 1));
      const cur = editor.state.doc.nodeAt(pos);
      if (cur?.type.name === 'orderedList') editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, { ...cur.attrs, start: n, numbering: n === 1 ? 'restart' : 'auto' }));
      close(); editor.commands.focus();
    };
    f.querySelector('button').addEventListener('click', done);
    input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); done(); } else if (e.key === 'Escape') { e.preventDefault(); close(); } });
  }

  menu.addEventListener('mousedown', e => { if (!e.target.closest('input')) e.preventDefault(); });
  menu.addEventListener('click', e => { if (e.target.closest('input,label')) return; const el = e.target.closest('[data-turn],[data-bg],[data-act]'); if (el) run(el); });
  const outside = ev => { if (menu.classList.contains('show') && !menu.contains(ev.target) && !ev.target.closest?.('.nt-handle')) close(); };
  document.addEventListener('mousedown', outside);
  return { open, close, destroy() { document.removeEventListener('mousedown', outside); menu.remove(); } };
}
