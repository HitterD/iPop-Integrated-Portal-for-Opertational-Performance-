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

export const CALLOUT_EMOJI = ['💡', '⚠️', '⛔', '✅', '✨', '📌', '👥', '🎯', '📖', '🙋', '👋', '📅', '🔥', '❗', '❓', '📝', '🚀', '🔒', '💬', '🛠️', '📣', '🎉', '🧪', '🏁'];
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
      let picker = null;
      const closePicker = () => { picker?.remove(); picker = null; document.removeEventListener('mousedown', outside, true); };
      const outside = ev => { if (picker && !picker.contains(ev.target) && ev.target !== emo) closePicker(); };
      emo.addEventListener('mousedown', e => {
        e.preventDefault();
        if (!editor.isEditable) return;
        if (picker) return closePicker();
        picker = el('div', 'nt-pop nt-emopick');
        picker.setAttribute('role', 'listbox');
        picker.innerHTML = CALLOUT_EMOJI.map(x => `<button type="button" role="option" data-e="${esc(x)}" aria-label="Icon ${esc(x)}">${esc(x)}</button>`).join('');
        picker.addEventListener('mousedown', ev => {
          ev.preventDefault();
          const b = ev.target.closest('[data-e]'); if (!b) return;
          const pos = getPos();
          if (pos != null) editor.view.dispatch(editor.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, emoji: b.dataset.e }));
          closePicker();
        });
        document.body.appendChild(picker);
        const r = emo.getBoundingClientRect();
        picker.style.position = 'fixed';
        picker.style.left = `${Math.min(r.left, innerWidth - 260)}px`;
        picker.style.top = `${Math.min(r.bottom + 6, innerHeight - 190)}px`;
        document.addEventListener('mousedown', outside, true);
      });
      dom.append(emo, body);
      return {
        dom, contentDOM: body,
        destroy: closePicker,
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

/// File type from the name, for the card before the server answers (and for files it can't describe).
export function fileKind(name) {
  const ext = String(name || '').toLowerCase().match(/\.([a-z0-9]+)$/)?.[1] || '';
  if (ext === 'pdf') return 'pdf';
  if (['docx', 'doc', 'rtf', 'odt'].includes(ext)) return 'word';
  if (ext === 'pptx') return 'slides';
  if (['xlsx', 'xls', 'xlsm', 'csv', 'ods'].includes(ext)) return 'sheet';
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'avif'].includes(ext)) return 'image';
  if (['mp4', 'webm', 'mov', 'm4v'].includes(ext)) return 'video';
  if (['mp3', 'wav', 'ogg', 'opus', 'm4a', 'aac', 'flac'].includes(ext)) return 'audio';
  if (['txt', 'md', 'log', 'json', 'xml', 'yml', 'yaml'].includes(ext)) return 'text';
  return 'none';
}
const KIND_LABEL = { pdf: 'PDF', word: 'Word', slides: 'PowerPoint', sheet: 'Excel', image: 'Image', video: 'Video', audio: 'Audio', text: 'Text' };
const extOf = name => (String(name || '').match(/\.([a-z0-9]+)$/i)?.[1] || 'file').toUpperCase().slice(0, 4);
/// "PDF · 3.9 MB · 18 pages" (an unknown count is left out; one page/sheet is singular).
export function fileSubLine(name, size, info) {
  const kind = info?.kind || fileKind(name);
  const parts = [KIND_LABEL[kind] || extOf(name), fmtSize(size || 0)];
  if (info?.pages) {
    const sheets = info.unit === 'sheets';
    parts.push(`${info.pages} ${sheets ? (info.pages === 1 ? 'sheet' : 'sheets') : (info.pages === 1 ? 'page' : 'pages')}`);
  }
  return parts.join(' · ');
}
const icon = (d, size = 14) => `<svg viewBox="0 0 24 24" width="${size}" height="${size}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${d}</svg>`;
const SVG = {
  eye: icon('<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>'),
  dl: icon('<path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M5 21h14"/>'),
  more: '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true"><circle cx="5" cy="12" r="1.8"/><circle cx="12" cy="12" r="1.8"/><circle cx="19" cy="12" r="1.8"/></svg>',
  full: icon('<path d="M15 3h6v6"/><path d="M9 21H3v-6"/><path d="M21 3l-7 7"/><path d="M3 21l7-7"/>'),
  card: icon('<rect x="3" y="7" width="18" height="10" rx="2"/>'),
  inline: icon('<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18"/>'),
  trash: icon('<path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/>'),
};

/// Every file block in the document, in order: the viewer's ← → goes through these.
export function pageFiles(doc) {
  const out = [];
  doc.descendants(n => { if (n.type.name === 'noteFile' && n.attrs.id) out.push({ id: n.attrs.id, name: n.attrs.name || 'file', size: n.attrs.size || 0 }); });
  return out;
}

async function downloadFile(host, id, name) {
  try {
    const url = await loadBlob(host, id);
    const a = document.createElement('a'); a.href = url; a.download = name || 'file'; a.click();
  } catch { host.toast?.("Couldn't download the file"); }
}

const NOT_AVAILABLE = 'Preview not available for this file type';
const LOAD_FAILED = "Couldn't load the preview. Use Download to open the file.";

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
      // ⋯ → Show inline: the file is shown in the page (a preview frame) instead of as a card, for everyone.
      inline: { default: false, parseHTML: e => e.getAttribute('data-inline') === 'true' },
    };
  },
  parseHTML() { return [{ tag: 'div[data-file]' }]; },
  renderHTML({ node }) {
    const a = node.attrs;
    return ['div', { 'data-file': '', 'data-id': a.id, 'data-name': a.name, 'data-size': a.size, ...(a.inline ? { 'data-inline': 'true' } : {}) }];
  },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const host = this.options.host?.() ?? {};
      const dom = el('div', 'nt-file');
      dom.contentEditable = 'false';
      let cur = node, info = null, menu = null, alive = true;
      const editable = () => editor.isEditable;
      const setInline = on => {
        const p = getPos();
        if (typeof p === 'number') editor.view.dispatch(editor.state.tr.setNodeMarkup(p, undefined, { ...cur.attrs, inline: on }));
      };
      const previewable = () => (info ? info.previewable : !['none', 'slides'].includes(fileKind(cur.attrs.name)));
      const download = () => downloadFile(host, cur.attrs.id, cur.attrs.name);
      const preview = () => {
        if (!previewable()) { host.toast?.(NOT_AVAILABLE); return; }
        if (host.previewFile) host.previewFile(cur.attrs.id, pageFiles(editor.state.doc));
        else download();
      };
      const outside = e => { if (menu && !menu.contains(e.target) && !e.target.closest?.('[data-act="menu"]')) closeMenu(); };
      function closeMenu() { menu?.remove(); menu = null; document.removeEventListener('pointerdown', outside, true); }
      const openMenu = () => {
        if (menu) { closeMenu(); return; }
        menu = el('div', 'nt-file-menu');
        menu.setAttribute('role', 'menu');
        const items = [];
        if (editable() && previewable()) items.push(['inline', cur.attrs.inline ? SVG.card : SVG.inline, cur.attrs.inline ? 'Show as card' : 'Show inline']);
        if (previewable()) items.push(['full', SVG.full, 'Open full screen']);
        items.push(['dl', SVG.dl, 'Download']);
        if (editable()) items.push(['remove', SVG.trash, 'Remove']);
        menu.innerHTML = items.map(([k, ic, t]) => `<button type="button" role="menuitem" data-mi="${k}"${k === 'remove' ? ' class="danger"' : ''}>${ic}<span>${t}</span></button>`).join('');
        menu.addEventListener('click', e => {
          const b = e.target.closest('[data-mi]'); if (!b) return;
          e.stopPropagation(); closeMenu();
          const k = b.dataset.mi;
          if (k === 'inline') setInline(!cur.attrs.inline);
          else if (k === 'full') preview();
          else if (k === 'dl') download();
          else if (k === 'remove') { const p = getPos(); if (typeof p === 'number') editor.view.dispatch(editor.state.tr.delete(p, p + cur.nodeSize)); }
        });
        dom.append(menu);
        document.addEventListener('pointerdown', outside, true);
      };

      const thumbBox = () => {
        const kind = info?.kind || fileKind(cur.attrs.name);
        const box = el('div', `th k-${kind}${kind === 'slides' ? ' wide' : ''}`);
        box.innerHTML = kind === 'sheet' ? `<div class="grid">${'<i></i>'.repeat(16)}</div>` : `<span class="ext">${esc(extOf(cur.attrs.name))}</span>`;
        const tid = info?.thumbnailId;
        if (tid && kind !== 'sheet') {
          loadBlob(host, tid).then(url => {
            if (!alive) return;
            const img = new Image(); img.alt = ''; img.src = url;
            img.onload = () => box.classList.add('has-img');
            box.prepend(img);
          }, () => {});
        }
        return box;
      };

      const renderCard = () => {
        dom.className = 'nt-file nt-fcard';
        dom.setAttribute('tabindex', '0'); dom.setAttribute('role', 'button');
        dom.setAttribute('aria-label', `${previewable() ? 'Preview' : 'Download'} ${cur.attrs.name || 'file'}`);
        dom.innerHTML = '';
        const meta = el('div', 'meta', `<div class="nm" title="${esc(cur.attrs.name)}">${esc(cur.attrs.name || 'file')}</div><div class="sub">${esc(fileSubLine(cur.attrs.name, cur.attrs.size, info))}</div>`);
        if (!previewable()) meta.insertAdjacentHTML('beforeend', `<div class="na">${NOT_AVAILABLE}</div>`);
        const acts = el('div', 'acts');
        if (previewable()) acts.insertAdjacentHTML('beforeend', `<button type="button" class="fb pri" data-act="open">${SVG.eye}<span>Preview</span></button>`);
        acts.insertAdjacentHTML('beforeend', `<button type="button" class="fb dl${previewable() ? '' : ' keep'}" data-act="dl">${SVG.dl}<span>Download</span></button>`);
        acts.insertAdjacentHTML('beforeend', `<button type="button" class="fb ib" data-act="menu" aria-label="More" aria-haspopup="menu">${SVG.more}</button>`);
        dom.append(thumbBox(), meta, acts);
      };

      const fillInline = async body => {
        const kind = info?.kind;
        const src = kind === 'word' || kind === 'slides' ? info?.pdfId : cur.attrs.id;
        const msg = t => { const m = el('div', 'msg'); m.textContent = t; body.replaceChildren(m); };
        if (!src) { msg(NOT_AVAILABLE); return; }
        try {
          if (kind === 'sheet' || kind === 'text') {
            const blob = await (await fetch(await loadBlob(host, src))).blob();
            if (!alive) return;
            if (kind === 'text') { const pre = el('pre', 'txt'); pre.textContent = (await blob.text()).slice(0, 200000); body.replaceChildren(pre); return; }
            if (!window.XLSX) await window.ipopLazy?.xlsx?.();
            const lib = window.XLSX;
            if (!lib) throw new Error('no-lib');
            const wb = lib.read(new Uint8Array(await blob.arrayBuffer()), { type: 'array' });
            body.classList.add('sheet');
            body.innerHTML = lib.utils.sheet_to_html(wb.Sheets[wb.SheetNames[0]], { editable: false });
            return;
          }
          const url = await loadBlob(host, src);
          if (!alive) return;
          let media;
          if (kind === 'image') { media = new Image(); media.alt = cur.attrs.name || ''; }
          else if (kind === 'video') { media = document.createElement('video'); media.controls = true; media.playsInline = true; }
          else if (kind === 'audio') { media = document.createElement('audio'); media.controls = true; }
          else { media = document.createElement('iframe'); media.title = cur.attrs.name || 'File preview'; }
          // A PDF in the inline frame: no thumbnail sidebar, page fitted to the frame width (Chrome / Edge / Firefox viewers read these).
          media.className = 'frame'; media.src = media.tagName === 'IFRAME' ? `${url}#navpanes=0&view=FitH` : url;
          body.replaceChildren(media);
        } catch { msg(LOAD_FAILED); }
      };

      const renderInline = () => {
        dom.className = 'nt-file nt-fembed';
        dom.removeAttribute('role'); dom.removeAttribute('tabindex'); dom.removeAttribute('aria-label');
        dom.innerHTML = '';
        const kind = info?.kind || fileKind(cur.attrs.name);
        const count = info?.pages ? fileSubLine(cur.attrs.name, 0, info).split(' · ').pop() : '';
        const head = el('div', 'hd', `<span class="badge k-${kind}">${esc(extOf(cur.attrs.name))}</span><span class="nm" title="${esc(cur.attrs.name)}">${esc(cur.attrs.name || 'file')}</span>${count ? `<small>${count}</small>` : ''}`
          + `<button type="button" class="fb ib" data-act="open" aria-label="Full screen" title="Full screen">${SVG.full}</button>`
          + (editable() ? `<button type="button" class="fb ib" data-act="card" aria-label="Show as card" title="Show as card">${SVG.card}</button>` : '')
          + `<button type="button" class="fb ib" data-act="menu" aria-label="More" aria-haspopup="menu">${SVG.more}</button>`);
        const body = el('div', 'bd', '<div class="msg">Loading preview…</div>');
        dom.append(head, body);
        fillInline(body);
      };

      // Inline only once the server has said what the file is (unknown types and failed conversions stay cards).
      const render = () => {
        closeMenu();
        if (cur.attrs.inline && info && previewable()) renderInline(); else renderCard();
      };

      dom.addEventListener('click', e => {
        const b = e.target.closest('[data-act]');
        if (b) {
          e.stopPropagation(); e.preventDefault();
          const a = b.dataset.act;
          if (a === 'open') preview();
          else if (a === 'dl') download();
          else if (a === 'menu') openMenu();
          else if (a === 'card') setInline(false);
          return;
        }
        if (e.target.closest('.nt-file-menu')) return;
        if (dom.classList.contains('nt-fcard')) { if (previewable()) preview(); else download(); }
      });
      dom.addEventListener('keydown', e => {
        if (e.key === 'Escape' && menu) { closeMenu(); return; }
        if (e.target !== dom) return;
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); if (previewable()) preview(); else download(); }
      });

      const loadInfo = () => {
        if (!host.filePreview || !cur.attrs.id) return;
        const id = cur.attrs.id;
        Promise.resolve(host.filePreview(id)).then(r => { if (alive && r && cur.attrs.id === id) { info = r; render(); } }, () => {});
      };
      render();
      loadInfo();

      return {
        dom,
        update: n => {
          if (n.type.name !== 'noteFile') return false;
          const prev = cur.attrs;
          cur = n;
          if (n.attrs.id !== prev.id) { info = null; render(); loadInfo(); }
          else if (n.attrs.inline !== prev.inline || n.attrs.name !== prev.name || n.attrs.size !== prev.size) render();
          return true;
        },
        stopEvent: e => !!e.target.closest?.('button,iframe,video,audio,.nt-file-menu,.bd'),
        ignoreMutation: () => true,
        destroy: () => { alive = false; closeMenu(); },
      };
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
    if (n.type.name !== 'taskItem' || !n.attrs.due || (!n.attrs.assigneeId && !n.attrs.taskId)) return;
    const text = (n.firstChild?.textContent ?? '').trim().slice(0, 200);
    let raw = `${n.attrs.assigneeId || ''}|${n.attrs.due}|${text}`;
    let h = 5381; for (let i = 0; i < raw.length; i++) h = ((h << 5) + h + raw.charCodeAt(i)) >>> 0;
    const base = `t${h.toString(36)}`;
    const k = (seen.get(base) ?? 0) + 1; seen.set(base, k);
    items.push({ key: k === 1 ? base : `${base}-${k}`, assigneeId: n.attrs.assigneeId || null, due: n.attrs.due, text, done: !!n.attrs.checked, taskId: n.attrs.taskId || null });
  });
  return items.slice(0, 200);
}

/// To-dos that were turned into Projects tasks ("→ Task"): reported with every save so a ticked to-do completes its task. One entry
/// per task (a task shared by two to-dos is done when either is ticked), at most 50.
export function todoTaskItems(doc) {
  const byTask = new Map();
  doc.descendants(n => {
    if (n.type.name !== 'taskItem' || !n.attrs.taskId) return;
    byTask.set(n.attrs.taskId, (byTask.get(n.attrs.taskId) || false) || !!n.attrs.checked);
  });
  return [...byTask].slice(0, 50).map(([taskId, done]) => ({ taskId, done }));
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
