// Smaller editor features that share no state with the rest: word count, find & replace, columns, date chips, emoji shortcodes,
// toggle headings. Pure helpers are exported so they can be tested without an editor.
import { Node, Extension, InputRule, mergeAttributes } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';
import { DetailsSummary } from '@tiptap/extension-details';
import { el } from './nodes.js';

// ── Word count ──────────────────────────────────────────────────────────────

/// Words, characters and reading time of a document. Counts text and the labels of mentions/page links; ignores code-block
/// syntax colouring and drawings. 200 words a minute, at least 1 minute once there is any text.
export function wordStats(doc) {
  let text = '';
  doc.descendants(n => {
    if (n.isText) text += n.text;
    else if (n.type.name === 'mention') text += ` ${n.attrs.label || ''} `;
    else if (n.type.name === 'pageLink') text += ` ${n.attrs.title || ''} `;
    else if (n.isBlock) text += ' ';
  });
  const words = (text.match(/[^\s]+/g) || []).length;
  const chars = text.replace(/\s+/g, ' ').trim().length;
  return { words, chars, minutes: words === 0 ? 0 : Math.max(1, Math.round(words / 200)) };
}

// ── Find & replace ──────────────────────────────────────────────────────────

/// Every match of `query` in the document's text, as ProseMirror ranges. Matches never span two blocks.
export function findMatches(doc, query, { caseSensitive = false } = {}) {
  const q = String(query || '');
  if (!q) return [];
  const needle = caseSensitive ? q : q.toLowerCase();
  const out = [];
  doc.descendants((node, pos) => {
    if (!node.isText) return;
    const hay = caseSensitive ? node.text : node.text.toLowerCase();
    for (let i = hay.indexOf(needle); i !== -1; i = hay.indexOf(needle, i + Math.max(needle.length, 1))) out.push({ from: pos + i, to: pos + i + needle.length });
  });
  return out;
}

export const findKey = new PluginKey('noteFind');

export const FindReplace = Extension.create({
  name: 'findReplace',
  addOptions() { return { host: null }; },
  addProseMirrorPlugins() {
    return [new Plugin({
      key: findKey,
      state: {
        init: () => ({ query: '', caseSensitive: false, current: 0, matches: [] }),
        apply(tr, v) {
          const m = tr.getMeta(findKey);
          const next = m ? { ...v, ...m } : v;
          if (!m && !tr.docChanged) return v;
          if (!next.query) return { ...next, matches: [], current: 0 };
          const matches = findMatches(tr.doc, next.query, next);
          return { ...next, matches, current: Math.min(next.current, Math.max(matches.length - 1, 0)) };
        },
      },
      props: {
        decorations(state) {
          const v = findKey.getState(state);
          if (!v.matches.length) return null;
          return DecorationSet.create(state.doc, v.matches.map((r, i) => Decoration.inline(r.from, r.to, { class: i === v.current ? 'nt-find nt-find-cur' : 'nt-find' })));
        },
      },
    })];
  },
  addCommands() {
    return {
      setFind: (query, caseSensitive = false) => ({ tr, dispatch }) => { dispatch?.(tr.setMeta(findKey, { query, caseSensitive, current: 0 })); return true; },
      stepFind: dir => ({ state, tr, dispatch }) => {
        const v = findKey.getState(state);
        if (!v.matches.length) return false;
        const current = (v.current + dir + v.matches.length) % v.matches.length;
        dispatch?.(tr.setMeta(findKey, { current }));
        return true;
      },
      replaceFind: (text, all = false) => ({ state, tr, dispatch }) => {
        const v = findKey.getState(state);
        if (!v.matches.length) return false;
        const targets = all ? v.matches : [v.matches[v.current]];
        // Back to front so earlier ranges stay valid.
        for (const r of [...targets].reverse()) { text ? tr.insertText(text, r.from, r.to) : tr.delete(r.from, r.to); }
        dispatch?.(tr);
        return true;
      },
    };
  },
});

/// The little bar shown on Ctrl+F while the editor has focus. Returns { open, close, destroy }.
export function createFindBar(editor, host) {
  const bar = el('div', 'nt-findbar');
  bar.innerHTML = '<input class="f" type="search" placeholder="Find" aria-label="Find in page" spellcheck="false">'
    + '<span class="n" aria-live="polite"></span>'
    + '<button type="button" data-a="prev" title="Previous (Shift+Enter)" aria-label="Previous match">↑</button>'
    + '<button type="button" data-a="next" title="Next (Enter)" aria-label="Next match">↓</button>'
    + '<button type="button" data-a="case" title="Match case" aria-label="Match case">Aa</button>'
    + '<input class="r" type="text" placeholder="Replace with" aria-label="Replace with" spellcheck="false">'
    + '<button type="button" data-a="rep" class="txt">Replace</button><button type="button" data-a="all" class="txt">All</button>'
    + '<button type="button" data-a="x" title="Close (Esc)" aria-label="Close find">✕</button>';
  bar.hidden = true;
  const f = bar.querySelector('.f'), r = bar.querySelector('.r'), n = bar.querySelector('.n');
  let cs = false;
  const paint = () => {
    const v = findKey.getState(editor.state);
    n.textContent = !f.value ? '' : v.matches.length ? `${v.current + 1} of ${v.matches.length}` : 'No matches';
    const readOnly = !editor.isEditable;
    r.hidden = readOnly; bar.querySelectorAll('.txt').forEach(b => { b.hidden = readOnly; });
  };
  const reveal = () => {
    const v = findKey.getState(editor.state), m = v.matches[v.current];
    if (!m) return;
    const dom = editor.view.domAtPos(m.from).node;
    (dom.nodeType === 1 ? dom : dom.parentElement)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  };
  const search = () => { editor.commands.setFind(f.value, cs); paint(); reveal(); };
  f.addEventListener('input', search);
  f.addEventListener('keydown', e => {
    if (e.key === 'Enter') { e.preventDefault(); editor.commands.stepFind(e.shiftKey ? -1 : 1); paint(); reveal(); }
    if (e.key === 'Escape') close();
  });
  r.addEventListener('keydown', e => { if (e.key === 'Escape') close(); if (e.key === 'Enter') { editor.commands.replaceFind(r.value); paint(); } });
  bar.addEventListener('click', e => {
    const a = e.target.closest('[data-a]')?.dataset.a; if (!a) return;
    if (a === 'x') return close();
    if (a === 'prev') editor.commands.stepFind(-1);
    if (a === 'next') editor.commands.stepFind(1);
    if (a === 'case') { cs = !cs; e.target.closest('button').classList.toggle('on', cs); search(); return; }
    if (a === 'rep' && editor.isEditable) editor.commands.replaceFind(r.value);
    if (a === 'all' && editor.isEditable) editor.commands.replaceFind(r.value, true);
    paint(); reveal();
  });
  const onUpdate = () => { if (!bar.hidden) paint(); };
  editor.on('transaction', onUpdate);
  function open() {
    bar.hidden = false;
    const sel = editor.state.doc.textBetween(editor.state.selection.from, editor.state.selection.to);
    if (sel && !sel.includes('\n')) f.value = sel;
    f.focus(); f.select(); search();
  }
  function close() { bar.hidden = true; editor.commands.setFind(''); editor.commands.focus(); }
  return { dom: bar, open, close, destroy() { editor.off('transaction', onUpdate); bar.remove(); } };
}

// ── Columns ─────────────────────────────────────────────────────────────────

export const Columns = Node.create({
  name: 'columns',
  group: 'block',
  content: 'column{2,4}',
  defining: true,
  isolating: true,
  parseHTML() { return [{ tag: 'div[data-columns]' }]; },
  renderHTML({ HTMLAttributes }) { return ['div', mergeAttributes(HTMLAttributes, { 'data-columns': '', class: 'nt-cols' }), 0]; },
});
export const Column = Node.create({
  name: 'column',
  content: 'block+',
  isolating: true,
  defining: true,
  parseHTML() { return [{ tag: 'div[data-column]' }]; },
  renderHTML({ HTMLAttributes }) { return ['div', mergeAttributes(HTMLAttributes, { 'data-column': '', class: 'nt-col' }), 0]; },
});

/// Inserts `count` empty columns where the cursor is (the slash command).
export function columnsJson(count) {
  return { type: 'columns', content: Array.from({ length: count }, () => ({ type: 'column', content: [{ type: 'paragraph' }] })) };
}

// ── Date chip ───────────────────────────────────────────────────────────────

export const isoDay = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
export function dateLabel(iso, today = new Date()) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(iso || ''));
  if (!m) return 'Pick a date';
  const d = new Date(+m[1], +m[2] - 1, +m[3]);
  if (Number.isNaN(d.getTime()) || d.getMonth() !== +m[2] - 1) return 'Pick a date';
  const diff = Math.round((d - new Date(today.getFullYear(), today.getMonth(), today.getDate())) / 86400000);
  if (diff === 0) return 'Today';
  if (diff === 1) return 'Tomorrow';
  if (diff === -1) return 'Yesterday';
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' });
}

export const DateChip = Node.create({
  name: 'dateChip',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  addAttributes() { return { date: { default: '', parseHTML: e => e.getAttribute('data-date') || '' } }; },
  parseHTML() { return [{ tag: 'span[data-date-chip]' }]; },
  renderHTML({ node }) { return ['span', { 'data-date-chip': '', 'data-date': node.attrs.date, class: 'nt-datechip' }, node.attrs.date]; },
  renderText({ node }) { return node.attrs.date; },
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const dom = el('span', 'nt-datechip');
      dom.contentEditable = 'false'; dom.tabIndex = 0; dom.setAttribute('role', 'button');
      const input = document.createElement('input');
      input.type = 'date'; input.className = 'nt-datechip-in'; input.tabIndex = -1; input.setAttribute('aria-label', 'Choose date');
      const paint = n => {
        const past = n.attrs.date && n.attrs.date < isoDay(new Date());
        dom.classList.toggle('past', !!past);
        dom.replaceChildren(document.createTextNode(`📅 ${dateLabel(n.attrs.date)}`), input);
        input.value = n.attrs.date || '';
        dom.title = n.attrs.date || 'Choose a date';
      };
      paint(node);
      const pick = () => { if (editor.isEditable) { try { input.showPicker?.(); } catch { input.focus(); } } };
      dom.addEventListener('click', e => { if (e.target !== input) pick(); });
      dom.addEventListener('keydown', e => { if (e.key === 'Enter') pick(); });
      input.addEventListener('change', () => {
        if (!input.value || !editor.isEditable) return;
        editor.view.dispatch(editor.state.tr.setNodeMarkup(getPos(), undefined, { ...node.attrs, date: input.value }));
      });
      return { dom, update(n) { if (n.type.name !== 'dateChip') return false; node = n; paint(n); return true; }, ignoreMutation: () => true };
    };
  },
});

// ── Emoji shortcodes ────────────────────────────────────────────────────────

export const SHORTCODES = {
  smile: '😄', grin: '😁', joy: '😂', laugh: '😆', wink: '😉', blush: '😊', heart_eyes: '😍', thinking: '🤔', sunglasses: '😎', cry: '😢', sob: '😭',
  angry: '😠', scream: '😱', sleepy: '😴', hug: '🤗', party: '🥳', cool: '😎', neutral: '😐', worried: '😟', pray: '🙏', clap: '👏', wave: '👋',
  thumbsup: '👍', '+1': '👍', thumbsdown: '👎', '-1': '👎', ok: '👌', muscle: '💪', point_right: '👉', raised_hands: '🙌', eyes: '👀', brain: '🧠',
  heart: '❤️', broken_heart: '💔', fire: '🔥', star: '⭐', sparkles: '✨', zap: '⚡', boom: '💥', tada: '🎉', rocket: '🚀', trophy: '🏆', gift: '🎁',
  check: '✅', white_check_mark: '✅', x: '❌', warning: '⚠️', stop: '⛔', question: '❓', exclamation: '❗', bulb: '💡', memo: '📝', pushpin: '📌',
  calendar: '📅', clock: '🕒', hourglass: '⏳', bell: '🔔', lock: '🔒', key: '🔑', link: '🔗', mag: '🔍', folder: '📁', file: '📄', books: '📚',
  computer: '💻', phone: '📱', printer: '🖨️', server: '🖥️', wifi: '📶', battery: '🔋', gear: '⚙️', wrench: '🔧', hammer: '🔨', bug: '🐛',
  chart: '📈', chart_down: '📉', bar_chart: '📊', money: '💰', dollar: '💵', package: '📦', truck: '🚚', ship: '🚢', plane: '✈️', car: '🚗', factory: '🏭',
  office: '🏢', house: '🏠', coffee: '☕', pizza: '🍕', cake: '🎂', sun: '☀️', moon: '🌙', cloud: '☁️', rain: '🌧️', snow: '❄️', tree: '🌳', flower: '🌸',
  dog: '🐶', cat: '🐱', fish: '🐟', bird: '🐦', flag: '🚩', target: '🎯', puzzle: '🧩', shield: '🛡️', crown: '👑', gem: '💎', bomb: '💣', handshake: '🤝',
  arrow_right: '➡️', arrow_left: '⬅️', arrow_up: '⬆️', arrow_down: '⬇️', plus: '➕', minus: '➖', hundred: '💯', one: '1️⃣', two: '2️⃣', three: '3️⃣',
};

/// `:name:` typed in text becomes the emoji; unknown names are left alone so ordinary colons never get eaten.
export const EmojiShortcodes = Extension.create({
  name: 'emojiShortcodes',
  addInputRules() {
    return [new InputRule({
      find: /(?:^|\s):([a-z0-9_+-]{1,24}):$/,
      handler: ({ state, range, match }) => {
        const emoji = SHORTCODES[match[1]];
        if (!emoji) return null;
        const start = range.from + match[0].indexOf(':');
        state.tr.insertText(emoji, start, range.to);
      },
    })];
  },
});

// ── Toggle headings ─────────────────────────────────────────────────────────

/// A toggle whose title is styled as a heading (level 1-3), like Notion's toggle headings.
export const ToggleSummary = DetailsSummary.extend({
  addAttributes() {
    return { level: { default: 0, parseHTML: e => +(e.getAttribute('data-level') || 0), renderHTML: a => a.level ? { 'data-level': String(a.level), class: `nt-th nt-th${a.level}` } : {} } };
  },
});

export const toggleJson = (level, text = '') => ({
  type: 'details', content: [
    { type: 'detailsSummary', attrs: { level }, content: text ? [{ type: 'text', text }] : undefined },
    { type: 'detailsContent', content: [{ type: 'paragraph' }] },
  ],
});

// ── Links to a block ────────────────────────────────────────────────────────

/// A small id stored on a block the first time someone copies a link to it. It lives in the document (so it follows the block
/// when it moves) and is invisible except as a data attribute.
export const BLOCK_TYPES = ['paragraph', 'heading', 'blockquote', 'taskList', 'bulletList', 'orderedList', 'codeBlock', 'details', 'callout', 'table', 'noteImage', 'noteGallery', 'noteDraw', 'columns'];
export const newBlockId = () => Math.random().toString(36).slice(2, 10);
export const BlockId = Extension.create({
  name: 'blockId',
  addGlobalAttributes() {
    return [{
      types: BLOCK_TYPES,
      attributes: { bid: { default: null, parseHTML: e => e.getAttribute('data-bid'), renderHTML: a => a.bid ? { 'data-bid': a.bid } : {} } },
    }];
  },
});

/// Position of the block carrying `bid`, or -1.
export function findBlock(doc, bid) {
  let found = -1;
  if (!bid) return found;
  doc.descendants((n, pos) => { if (found < 0 && n.attrs?.bid === bid) found = pos; return found < 0; });
  return found;
}

/// The id of the block at `pos`, creating and storing one if it has none. Returns null when there is no node there.
export function ensureBlockId(editor, pos) {
  if (!Number.isInteger(pos) || pos < 0 || pos > editor.state.doc.content.size) return null;
  const node = editor.state.doc.nodeAt(pos);
  if (!node || !('bid' in node.attrs)) return null;
  if (node.attrs.bid) return node.attrs.bid;
  const bid = newBlockId();
  editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, bid }));
  return bid;
}

// ── Upload tray (progress + cancel per file) ────────────────────────────────

/// POSTs a form with progress. Resolves { ok, status, json } and rejects with name "AbortError" when cancelled.
export function uploadXhr(url, form, token, { onProgress, signal } = {}) {
  return new Promise((resolve, reject) => {
    const x = new XMLHttpRequest();
    x.open('POST', url);
    x.setRequestHeader('Authorization', `Bearer ${token}`);
    x.upload.onprogress = e => { if (e.lengthComputable) onProgress?.(e.loaded / e.total); };
    x.onload = () => { let json = null; try { json = JSON.parse(x.responseText); } catch { /* not json */ } resolve({ ok: x.status >= 200 && x.status < 300, status: x.status, json }); };
    x.onerror = () => reject(new Error('network'));
    x.ontimeout = () => reject(new Error('timeout'));
    const abort = () => { x.abort(); reject(Object.assign(new Error('cancelled'), { name: 'AbortError' })); };
    if (signal) { if (signal.aborted) return abort(); signal.addEventListener('abort', abort, { once: true }); }
    x.send(form);
  });
}

/// A small card in the corner listing each file being uploaded with a bar and a Cancel button. add() returns a handle.
export function createUploadTray() {
  const dom = el('div', 'nt-uptray');
  dom.setAttribute('role', 'status'); dom.setAttribute('aria-live', 'polite');
  dom.hidden = true;
  document.body.appendChild(dom);
  const rows = new Set();
  const sync = () => { dom.hidden = rows.size === 0; };
  return {
    dom,
    add(name, onCancel) {
      const row = el('div', 'up');
      row.innerHTML = `<span class="nm"></span><span class="pc">0%</span><button type="button" aria-label="Cancel upload">✕</button><i><b></b></i>`;
      row.querySelector('.nm').textContent = name;
      const pc = row.querySelector('.pc'), bar = row.querySelector('b');
      row.querySelector('button').addEventListener('click', () => onCancel?.());
      dom.appendChild(row); rows.add(row); sync();
      return {
        progress(f) { const v = Math.max(0, Math.min(1, f)); bar.style.width = `${(v * 100).toFixed(0)}%`; pc.textContent = v >= 1 ? 'Saving…' : `${(v * 100).toFixed(0)}%`; },
        fail(msg) { row.classList.add('bad'); pc.textContent = msg; row.querySelector('button').remove(); setTimeout(() => this.done(), 3500); },
        done() { rows.delete(row); row.remove(); sync(); },
      };
    },
    destroy() { dom.remove(); },
  };
}

// ── Version diff ────────────────────────────────────────────────────────────

/// The text of a document as one string per block (paragraph, heading, list item, table cell row...). Empty blocks are skipped.
export function blockTexts(doc) {
  const out = [];
  const walk = (n, task = null) => {
    if (n.type === 'noteDraw') { out.push('[drawing]'); return; }
    if (n.type === 'noteImage') { out.push(`[image${n.attrs?.caption ? ': ' + n.attrs.caption : ''}]`); return; }
    if (n.type === 'noteFile') { out.push(`[file: ${n.attrs?.name || ''}]`); return; }
    if (n.type === 'noteBookmark') { out.push(`[link: ${n.attrs?.title || n.attrs?.url || ''}]`); return; }
    const inlineKids = (n.content || []).some(c => c.type === 'text' || c.type === 'mention' || c.type === 'pageLink' || c.type === 'dateChip' || c.type === 'hardBreak');
    if (inlineKids) {
      const t = (n.content || []).map(c => c.type === 'text' ? c.text : c.type === 'mention' ? `@${c.attrs?.label || ''}` : c.type === 'pageLink' ? (c.attrs?.title || 'page') : c.type === 'dateChip' ? (c.attrs?.date || '') : c.type === 'hardBreak' ? ' ' : '').join('').trim();
      if (t) out.push(task === null ? t : `${task ? '[x]' : '[ ]'} ${t}`);
      return;
    }
    // A to-do's text sits in its first paragraph: hand it the checked state (nested content below that is plain).
    if (n.type === 'taskItem') { (n.content || []).forEach((c, i) => walk(c, i === 0 ? !!n.attrs?.checked : null)); return; }
    (n.content || []).forEach(c => walk(c));
  };
  (doc?.content || []).forEach(n => walk(n));
  return out;
}

/// Line diff by longest common subsequence: [{ kind: 'same' | 'add' | 'del', text }]. Pages are small (a few hundred blocks),
/// and above 2000 lines on a side the diff falls back to "everything changed" rather than freezing the tab.
export function diffLines(before, after) {
  if (before.length > 2000 || after.length > 2000) {
    return [...before.map(text => ({ kind: 'del', text })), ...after.map(text => ({ kind: 'add', text }))];
  }
  const n = before.length, m = after.length;
  const lcs = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) lcs[i][j] = before[i] === after[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const out = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (before[i] === after[j]) { out.push({ kind: 'same', text: before[i] }); i++; j++; }
    else if (lcs[i + 1][j] >= lcs[i][j + 1]) out.push({ kind: 'del', text: before[i++] });
    else out.push({ kind: 'add', text: after[j++] });
  }
  while (i < n) out.push({ kind: 'del', text: before[i++] });
  while (j < m) out.push({ kind: 'add', text: after[j++] });
  return out;
}

/// Long unchanged stretches are folded to "… N unchanged blocks …" so the changes stand out. Keeps `context` lines around a change.
export function foldSame(lines, context = 1) {
  const keep = lines.map(l => l.kind !== 'same');
  const near = keep.map((_, i) => { for (let d = -context; d <= context; d++) if (keep[i + d]) return true; return false; });
  const out = [];
  let skipped = 0;
  lines.forEach((l, i) => {
    if (near[i]) { if (skipped) { out.push({ kind: 'fold', text: `${skipped} unchanged block${skipped === 1 ? '' : 's'}` }); skipped = 0; } out.push(l); }
    else skipped++;
  });
  if (skipped) out.push({ kind: 'fold', text: `${skipped} unchanged block${skipped === 1 ? '' : 's'}` });
  return out;
}
