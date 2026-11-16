import { Node, Extension, mergeAttributes } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';

export const COLORS = ['gray', 'brown', 'orange', 'yellow', 'green', 'teal', 'blue', 'purple', 'pink', 'red'];
export const ACCENTS = { blue: '#0A84FF', indigo: '#5856D6', purple: '#AF52DE', pink: '#FF2D55', red: '#FF3B30', orange: '#FF9500', yellow: '#FFCC00', green: '#34C759', teal: '#30B0C7', gray: '#8E8E93' };
export const cap = s => s[0].toUpperCase() + s.slice(1);

export const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; };
export const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
export const fmtSize = n => n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1048576).toFixed(1)} MB`;

/// Background colour on any block (block menu → Background colour). Stored as a palette key.
export const BlockBackground = Extension.create({
  name: 'blockBackground',
  addGlobalAttributes() {
    return [{
      types: ['paragraph', 'heading', 'blockquote', 'taskList', 'bulletList', 'orderedList', 'codeBlock', 'details'],
      attributes: {
        bg: {
          default: null,
          parseHTML: e => e.getAttribute('data-bg'),
          renderHTML: a => a.bg && COLORS.includes(a.bg) ? { 'data-bg': a.bg, class: `nt-bg c-${a.bg}` } : {},
        },
      },
    }];
  },
});

export const Callout = Node.create({
  name: 'callout',
  group: 'block',
  content: 'block+',
  defining: true,
  addAttributes() {
    return {
      emoji: { default: '💡', parseHTML: e => e.getAttribute('data-emoji') },
      color: { default: 'yellow', parseHTML: e => e.getAttribute('data-color') },
    };
  },
  parseHTML() { return [{ tag: 'div[data-callout]' }]; },
  renderHTML({ node, HTMLAttributes }) {
    return ['div', mergeAttributes(HTMLAttributes, { 'data-callout': '', class: `nt-callout c-${node.attrs.color}`, 'data-emoji': node.attrs.emoji, 'data-color': node.attrs.color }),
      ['span', { class: 'nt-callout-emo', contenteditable: 'false' }, node.attrs.emoji], ['div', { class: 'nt-callout-body' }, 0]];
  },
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const dom = el('div', `nt-callout c-${node.attrs.color}`);
      dom.dataset.callout = '';
      const emo = el('button', 'nt-callout-emo', esc(node.attrs.emoji));
      emo.type = 'button'; emo.contentEditable = 'false'; emo.title = 'Change icon';
      const body = el('div', 'nt-callout-body');
      const cycle = ['💡', '⚠️', '⛔', '✅', '✨', '📌', '👥', '🎯', '📖', '🙋', '👋', '📅'];
      emo.addEventListener('mousedown', e => {
        e.preventDefault();
        if (!editor.isEditable) return;
        const cur = cycle.indexOf(node.attrs.emoji);
        const pos = getPos();
        editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, emoji: cycle[(cur + 1) % cycle.length] }));
      });
      dom.append(emo, body);
      return {
        dom, contentDOM: body,
        update(n) {
          if (n.type.name !== 'callout') return false;
          node = n; dom.className = `nt-callout c-${n.attrs.color}`; emo.textContent = n.attrs.emoji; return true;
        },
      };
    };
  },
});

/// Non-editable tutorial helpers: progress ring, accent swatches, permission table.
/// A heading briefly highlighted after a click in the table of contents. ProseMirror re-creates DOM elements it owns, so a
/// class added by hand would vanish; this is a decoration instead, driven by a transaction meta and cleared by a timer.
export const flashKey = new PluginKey('headingFlash');
export const HeadingFlash = Extension.create({
  name: 'headingFlash',
  addProseMirrorPlugins() {
    return [new Plugin({
      key: flashKey,
      state: {
        init: () => null,
        apply(tr, v) {
          const m = tr.getMeta(flashKey);
          if (m !== undefined) return m;
          if (!v || !tr.docChanged) return v;
          // Typing before the heading moves it; keep the highlight on the same node (start sticks right, end left).
          const from = tr.mapping.map(v.from, 1), to = tr.mapping.map(v.to, -1);
          return to > from && tr.doc.nodeAt(from)?.type.name === 'heading' ? { from, to } : null;
        },
      },
      props: { decorations(state) { const v = flashKey.getState(state); return v ? DecorationSet.create(state.doc, [Decoration.node(v.from, v.to, { class: 'nt-flash' })]) : null; } },
    })];
  },
});

/// Table of contents: a live list of the page's headings. The block stores nothing but its own existence; the list is
/// rebuilt from the headings whenever the document changes, and clicking a line scrolls to that heading.
export const NoteToc = Node.create({
  name: 'noteToc',
  group: 'block',
  atom: true,
  selectable: true,
  draggable: true,
  parseHTML() { return [{ tag: 'div[data-toc]' }]; },
  renderHTML() { return ['div', { 'data-toc': '' }]; },
  addNodeView() {
    return ({ editor }) => {
      const dom = el('nav', 'nt-toc');
      dom.contentEditable = 'false';
      dom.setAttribute('aria-label', 'Table of contents');
      const paint = () => {
        const items = [];
        editor.state.doc.descendants((n, pos) => {
          if (n.type.name === 'heading') { const text = n.textContent.trim(); if (text) items.push({ level: n.attrs.level, text, pos }); return false; }
          return true;
        });
        dom.innerHTML = `<div class="tt"><span>CONTENTS</span></div>` + (items.length
          ? items.map(i => `<a data-pos="${i.pos}" class="l${i.level}" tabindex="0" role="link">${esc(i.text)}</a>`).join('')
          : '<div class="empty">Add headings and they will appear here.</div>');
      };
      const go = a => {
        const pos = Number(a.dataset.pos);
        const node = editor.state.doc.nodeAt(pos);
        if (!node || node.type.name !== 'heading') return;
        editor.view.dispatch(editor.state.tr.setMeta(flashKey, { from: pos, to: pos + node.nodeSize }).setMeta('addToHistory', false));
        const target = editor.view.nodeDOM(pos);
        if (target instanceof HTMLElement) target.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
        setTimeout(() => { try { if (editor.view && !editor.isDestroyed) editor.view.dispatch(editor.state.tr.setMeta(flashKey, null).setMeta('addToHistory', false)); } catch { /* editor closed */ } }, 1100);
      };
      dom.addEventListener('click', e => { const a = e.target.closest('a[data-pos]'); if (a) { e.preventDefault(); go(a); } });
      dom.addEventListener('keydown', e => { if (e.key === 'Enter') { const a = e.target.closest('a[data-pos]'); if (a) go(a); } });
      paint();
      editor.on('update', paint);
      return { dom, ignoreMutation: () => true, stopEvent: () => true, destroy: () => editor.off('update', paint) };
    };
  },
});

export const NoteWidget = Node.create({
  name: 'noteWidget',
  group: 'block',
  atom: true,
  selectable: false,
  draggable: false,
  addAttributes() { return { kind: { default: 'progress', parseHTML: e => e.getAttribute('data-kind') } }; },
  parseHTML() { return [{ tag: 'div[data-widget]' }]; },
  renderHTML({ node }) { return ['div', { 'data-widget': '', 'data-kind': node.attrs.kind }]; },
  addNodeView() {
    return ({ node, editor }) => {
      const host = this.options.host?.() ?? {};
      const dom = el('div', `nt-widget nt-w-${node.attrs.kind}`);
      dom.contentEditable = 'false';
      let off = null;
      if (node.attrs.kind === 'progress') {
        dom.innerHTML = `<svg class="nt-ring" viewBox="0 0 44 44"><circle class="bg" cx="22" cy="22" r="18"/><circle class="fg" cx="22" cy="22" r="18" stroke-dasharray="113.1" stroke-dashoffset="113.1"/></svg>
          <div class="nt-prog-txt"><div class="pt"></div><div class="ps">Progress is saved automatically. You can reopen the guide any time from the 👋 row at the top of the sidebar.</div></div>`;
        const paint = () => {
          const { done, total } = countTodos(editor.state.doc);
          dom.querySelector('.pt').textContent = total && done === total ? '🎉 All steps done — you are all set!' : `${done} of ${total} steps done`;
          dom.querySelector('.fg').style.strokeDashoffset = String(113.1 * (1 - (total ? done / total : 0)));
        };
        paint(); editor.on('update', paint); off = () => editor.off('update', paint);
      } else if (node.attrs.kind === 'accents') {
        const paint = () => {
          const cur = host.accent?.();
          dom.innerHTML = Object.entries(ACCENTS).map(([k, v]) => `<button type="button" class="nt-dot ${k === cur ? 'on' : ''}" data-a="${k}" style="--k:${v}" title="${cap(k)}"></button>`).join('');
        };
        paint();
        dom.addEventListener('click', e => { const d = e.target.closest('[data-a]'); if (d) { host.setAccent?.(d.dataset.a); setTimeout(paint, 50); } });
        host.onAccent?.(paint);
      } else if (node.attrs.kind === 'perms') {
        const y = '<span class="yes">●</span>', n = '<span class="no">—</span>';
        const row = (lbl, ...c) => `<div class="tr"><div>${lbl}</div>${c.map(x => `<div>${x ? y : n}</div>`).join('')}</div>`;
        dom.innerHTML = `<div class="tr"><div>Permission</div><div>Read</div><div>Comment</div><div>Edit</div><div>Share</div></div>`
          + row('👁 Can view', 1, 0, 0, 0) + row('💬 Can comment', 1, 1, 0, 0) + row('✏️ Can edit', 1, 1, 1, 0) + row('👑 Owner', 1, 1, 1, 1);
      }
      return { dom, ignoreMutation: () => true, stopEvent: () => true, destroy: () => off?.() };
    };
  },
  addOptions() { return { host: null }; },
});

export const SubPage = Node.create({
  name: 'subPage',
  group: 'block',
  atom: true,
  draggable: true,
  addAttributes() {
    return {
      id: { default: null, parseHTML: e => e.getAttribute('data-id') },
      title: { default: 'Untitled', parseHTML: e => e.getAttribute('data-title') },
      emoji: { default: '📄', parseHTML: e => e.getAttribute('data-emoji') },
    };
  },
  parseHTML() { return [{ tag: 'div[data-subpage]' }]; },
  renderHTML({ node }) { return ['div', { 'data-subpage': '', 'data-id': node.attrs.id, 'data-title': node.attrs.title, 'data-emoji': node.attrs.emoji }]; },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node }) => {
      const host = this.options.host?.() ?? {};
      const dom = el('div', 'nt-subpage');
      dom.contentEditable = 'false';
      const paint = n => {
        const live = host.pageInfo?.(n.attrs.id);
        dom.innerHTML = `<span class="emo">${esc(live?.emoji || n.attrs.emoji || '📄')}</span><span class="t">${esc(live?.title || n.attrs.title || 'Untitled')}</span>`;
      };
      paint(node);
      dom.addEventListener('click', () => node.attrs.id && host.openPage?.(node.attrs.id));
      return { dom, update(n) { if (n.type.name !== 'subPage') return false; node = n; paint(n); return true; }, ignoreMutation: () => true };
    };
  },
});

/// Attachment-backed image/file blocks. The API needs a bearer token, so bytes are fetched then shown
/// from an object URL.
const blobCache = new Map();
export async function loadBlob(host, id) {
  if (blobCache.has(id)) return blobCache.get(id);
  const p = (async () => {
    const res = await host.fetch(`/api/attachments/${id}`);
    if (!res.ok) throw new Error(String(res.status));
    return URL.createObjectURL(await res.blob());
  })();
  blobCache.set(id, p);
  p.catch(() => blobCache.delete(id));
  return p;
}
/// Frees every object URL; called when the editor for a page is torn down.
export function releaseBlobs() {
  for (const p of blobCache.values()) p.then(u => URL.revokeObjectURL(u), () => {});
  blobCache.clear();
}

export const NoteFile = Node.create({
  name: 'noteFile',
  group: 'block',
  atom: true,
  draggable: true,
  addAttributes() {
    return {
      id: { default: null, parseHTML: e => e.getAttribute('data-id') },
      name: { default: '', parseHTML: e => e.getAttribute('data-name') },
      size: { default: 0, parseHTML: e => +e.getAttribute('data-size') || 0 },
    };
  },
  parseHTML() { return [{ tag: 'div[data-file]' }]; },
  renderHTML({ node }) { return ['div', { 'data-file': '', 'data-id': node.attrs.id, 'data-name': node.attrs.name, 'data-size': node.attrs.size }]; },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node }) => {
      const host = this.options.host?.() ?? {};
      const dom = el('div', 'nt-file');
      dom.contentEditable = 'false';
      dom.innerHTML = `<span class="ico">📎</span><span class="nm">${esc(node.attrs.name)}</span><span class="sz">${fmtSize(node.attrs.size || 0)}</span>`;
      dom.addEventListener('click', async () => {
        try {
          const url = await loadBlob(host, node.attrs.id);
          const a = document.createElement('a'); a.href = url; a.download = node.attrs.name || 'file'; a.click();
        } catch { host.toast?.("Couldn't download the file"); }
      });
      return { dom, ignoreMutation: () => true };
    };
  },
});

export function countTodos(doc) {
  let done = 0, total = 0;
  doc.descendants(n => { if (n.type.name === 'taskItem') { total++; if (n.attrs.checked) done++; } });
  return { done, total };
}

/// To-dos that have a person AND a due date, for the server's reminders: [{ key, assigneeId, due, text, done }].
/// The key identifies an item across saves (so a reminder that was already sent is not sent again): it is built from the
/// person, the due date and the item's own text, and made unique when two items would collide.
export function todoDueItems(doc) {
  const items = [], seen = new Map();
  doc.descendants(n => {
    if (n.type.name !== 'taskItem' || !n.attrs.assigneeId || !n.attrs.due) return;
    const text = (n.firstChild?.textContent ?? '').trim().slice(0, 200);
    let raw = `${n.attrs.assigneeId}|${n.attrs.due}|${text}`;
    let h = 5381; for (let i = 0; i < raw.length; i++) h = ((h << 5) + h + raw.charCodeAt(i)) >>> 0;
    const base = `t${h.toString(36)}`;
    const k = (seen.get(base) ?? 0) + 1; seen.set(base, k);
    items.push({ key: k === 1 ? base : `${base}-${k}`, assigneeId: n.attrs.assigneeId, due: n.attrs.due, text, done: !!n.attrs.checked });
  });
  return items.slice(0, 200);
}

/// Comment highlights: threads are anchored by Yjs relative positions, so they follow edits made by
/// anyone. `resolve(anchor)` maps an anchor to current absolute positions (or null if the text is gone).
export function buildCommentDecorations(state, { threads, focus }, opts) {
  const decos = [];
  for (const t of threads) {
    const r = opts.resolve?.(t.anchor, state);
    if (!r || r.from >= r.to) continue;
    decos.push(Decoration.inline(r.from, r.to, { class: `nt-cmt${t.id === focus ? ' focus' : ''}`, 'data-thread': t.id }));
  }
  return DecorationSet.create(state.doc, decos);
}

export const commentKey = new PluginKey('noteComments');
export const CommentHighlights = Extension.create({
  name: 'commentHighlights',
  addOptions() { return { resolve: null, onClick: null }; },
  addProseMirrorPlugins() {
    const opts = this.options;
    return [new Plugin({
      key: commentKey,
      // Anchors are resolved against the Yjs document, so highlights are rebuilt when the document or the thread list /
      // focus changes, and reused for selection-only transactions (moving the cursor used to re-resolve every anchor).
      state: {
        init: () => ({ threads: [], focus: null, set: DecorationSet.empty }),
        apply(tr, v, _old, state) {
          const meta = tr.getMeta(commentKey);
          if (!meta && !tr.docChanged) return v;
          const next = meta ? { threads: meta.threads, focus: meta.focus } : v;
          return { ...next, set: buildCommentDecorations(state, next, opts) };
        },
      },
      props: {
        decorations(state) { return commentKey.getState(state)?.set; },
        handleClick(view, pos, event) {
          const t = event.target.closest?.('[data-thread]');
          if (t) opts.onClick?.(t.dataset.thread);
          return false;
        },
      },
    })];
  },
});
