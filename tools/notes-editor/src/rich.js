// Rich blocks for Notes: resizable/captioned images + galleries + lightbox, tables, to-dos with
// photos/assignee/due date, link bookmarks and video embeds. See docs/mockups/ipop-apple/notes-enrich.html.
import { Node, Extension } from '@tiptap/core';
import { Plugin, PluginKey, NodeSelection } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';
import { TaskItem } from '@tiptap/extension-list';
import { Table, TableRow, TableHeader, TableCell } from '@tiptap/extension-table';
import { COLORS, cap, el, esc, loadBlob } from './nodes.js';
import { place } from './menus.js';

const SNAPS = [33, 60, 100];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const isoToday = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; };
const fmtDue = iso => { const [y, m, d] = iso.split('-').map(Number); return `${MONTHS[m - 1] ?? ''} ${d}${y !== new Date().getFullYear() ? ` ${y}` : ''}`; };
const validDue = v => /^\d{4}-\d{2}-\d{2}$/.test(v || '') ? v : null;
export const httpUrl = s => { try { const u = new URL(String(s || '').trim()); return u.protocol === 'https:' || u.protocol === 'http:' ? u.href : null; } catch { return null; } };

// ── Images ───────────────────────────────────────────────────────────────────

async function download(host, id, name) {
  try {
    const url = await loadBlob(host, id);
    const a = document.createElement('a'); a.href = url; a.download = name || 'image'; a.click();
  } catch { host.toast?.("Couldn't download the file"); }
}

/// Every image on the page in reading order (single images, gallery tiles and to-do photos).
function pageImages(doc) {
  const list = [];
  doc.descendants(n => {
    if (n.type.name === 'noteImage' && n.attrs.id) list.push({ id: n.attrs.id, name: n.attrs.name, caption: n.attrs.caption });
    else if (n.type.name === 'noteGallery') galleryItems(n).forEach(it => list.push({ id: it.id, name: it.name, caption: it.caption || '' }));
  });
  return list;
}

const lightboxes = new WeakMap();
export function closeLightboxes(host) { lightboxes.get(host)?.forEach(close => close()); }
export function openLightbox(host, list, start = 0) {
  if (!list.length) return;
  let i = Math.max(0, Math.min(start, list.length - 1));
  const box = el('div', 'nt-lbx', `
    <button type="button" class="x" title="Close (Esc)" aria-label="Close">✕</button>
    <button type="button" class="ar prev" title="Previous" aria-label="Previous">‹</button>
    <div class="stage"><img alt=""></div>
    <button type="button" class="ar next" title="Next" aria-label="Next">›</button>
    <div class="ft"><span class="cap"></span><span class="n"></span><button type="button" class="dl">Download</button></div>`);
  box.setAttribute('role', 'dialog'); box.setAttribute('aria-modal', 'true');
  const img = box.querySelector('img');
  const show = () => {
    const it = list[i];
    img.removeAttribute('src');
    loadBlob(host, it.id).then(u => { if (list[i] === it) img.src = u; }, () => {});
    img.alt = it.caption || it.name || '';
    box.querySelector('.cap').textContent = it.caption || it.name || '';
    box.querySelector('.n').textContent = list.length > 1 ? `${i + 1} / ${list.length}` : '';
    box.querySelectorAll('.ar').forEach(b => b.hidden = list.length < 2);
  };
  const go = d => { i = (i + d + list.length) % list.length; show(); };
  const owned = lightboxes.get(host) || new Set(); lightboxes.set(host, owned);
  const close = () => { document.removeEventListener('keydown', key, true); box.remove(); owned.delete(close); };
  owned.add(close);
  const key = e => {
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); go(-1); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); go(1); }
  };
  box.addEventListener('click', e => {
    if (e.target.closest('.x') || e.target === box) close();
    else if (e.target.closest('.prev')) go(-1);
    else if (e.target.closest('.next')) go(1);
    else if (e.target.closest('.dl')) download(host, list[i].id, list[i].name);
  });
  document.addEventListener('keydown', key, true);
  document.body.appendChild(box);
  show();
  box.querySelector('.x').focus();
}

export const NoteImage = Node.create({
  name: 'noteImage',
  group: 'block',
  atom: true,
  draggable: true,
  addAttributes() {
    return {
      id: { default: null, parseHTML: e => e.getAttribute('data-id') },
      name: { default: '', parseHTML: e => e.getAttribute('data-name') },
      size: { default: 0, parseHTML: e => +e.getAttribute('data-size') || 0 },
      // Width is a % of the text column so it fits on phones too; older images are full width.
      width: { default: 100, parseHTML: e => Math.min(100, Math.max(15, +e.getAttribute('data-width') || 100)) },
      align: { default: 'center', parseHTML: e => ['left', 'right'].includes(e.getAttribute('data-align')) ? e.getAttribute('data-align') : 'center' },
      caption: { default: '', parseHTML: e => e.getAttribute('data-caption') || '' },
    };
  },
  parseHTML() { return [{ tag: 'figure[data-image]' }]; },
  renderHTML({ node: { attrs: a } }) {
    return ['figure', { 'data-image': '', 'data-id': a.id, 'data-name': a.name, 'data-size': a.size, 'data-width': a.width, 'data-align': a.align, 'data-caption': a.caption }];
  },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const host = this.options.host?.() ?? {};
      const editable = editor.isEditable;
      const dom = el('figure', 'nt-img');
      dom.contentEditable = 'false';
      const frame = el('div', 'nt-img-frame');
      const box = el('div', 'nt-img-box', '<span class="nt-img-wait">Loading image…</span>');
      frame.append(box);
      const capEl = editable ? el('input', 'nt-img-cap') : el('figcaption', 'nt-img-cap');
      let pct = null, resizing = false, cancelResize = () => {};
      const lbl = el('span', 'nt-img-pct');
      const set = attrs => { const p = getPos(); if (typeof p === 'number') editor.view.dispatch(editor.state.tr.setNodeMarkup(p, undefined, { ...node.attrs, ...attrs })); };

      if (editable) {
        capEl.type = 'text'; capEl.placeholder = 'Add a caption'; capEl.maxLength = 300; capEl.setAttribute('aria-label', 'Image caption');
        capEl.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === 'Escape') { e.preventDefault(); capEl.blur(); } });
        capEl.addEventListener('change', () => set({ caption: capEl.value.trim() }));
        for (const dir of [-1, 1]) {
          const h = el('span', `rs ${dir < 0 ? 'l' : 'r'}`);
          h.title = 'Drag to resize';
          h.addEventListener('mousedown', e => e.preventDefault());
          h.addEventListener('pointerdown', e => {
            e.preventDefault(); e.stopPropagation();
            cancelResize();
            const full = dom.clientWidth || 1, start = frame.offsetWidth, x0 = e.clientX;
            const mult = node.attrs.align === 'center' ? 2 : 1;
            resizing = true; pct = node.attrs.width; lbl.classList.add('show');
            const move = ev => {
              pct = Math.max(15, Math.min(100, Math.round((start + (ev.clientX - x0) * dir * mult) / full * 100)));
              dom.style.setProperty('--w', `${pct}%`); lbl.textContent = `${pct}%`;
            };
            const up = () => {
              cancelResize();
              const snap = SNAPS.find(s => Math.abs(pct - s) <= 5);
              set({ width: snap ?? pct });
            };
            cancelResize = () => {
              window.removeEventListener('pointermove', move);
              window.removeEventListener('pointerup', up);
              window.removeEventListener('pointercancel', cancelResize);
              resizing = false; lbl.classList.remove('show');
            };
            window.addEventListener('pointermove', move);
            window.addEventListener('pointerup', up);
            window.addEventListener('pointercancel', cancelResize);
          });
          frame.append(h);
        }
        frame.append(lbl);
        dom.addEventListener('dragstart', e => { if (resizing) e.preventDefault(); });
      }
      dom.append(frame, capEl);

      const paint = n => {
        dom.className = `nt-img al-${n.attrs.align}`;
        dom.style.setProperty('--w', `${n.attrs.width}%`);
        if (editable) { if (document.activeElement !== capEl) capEl.value = n.attrs.caption || ''; }
        else { capEl.textContent = n.attrs.caption || ''; capEl.hidden = !n.attrs.caption; }
      };
      paint(node);
      if (node.attrs.id) loadBlob(host, node.attrs.id).then(url => {
        const img = document.createElement('img'); img.src = url; img.alt = node.attrs.caption || node.attrs.name; img.draggable = false;
        box.replaceChildren(img);
      }).catch(() => { box.querySelector('.nt-img-wait').textContent = "Couldn't load the image"; });

      const view = () => { const all = pageImages(editor.state.doc); openLightbox(host, all, all.findIndex(x => x.id === node.attrs.id)); };
      dom.addEventListener(editable ? 'dblclick' : 'click', e => { if (!e.target.closest('input,.rs')) view(); });
      return {
        dom,
        update(n) { if (n.type.name !== 'noteImage' || n.attrs.id !== node.attrs.id) return false; node = n; paint(n); return true; },
        ignoreMutation: () => true,
        stopEvent: e => !!e.target.closest?.('input,.rs'),
        destroy: () => cancelResize(),
      };
    };
  },
});

export function galleryItems(node) {
  try { return (JSON.parse(node.attrs.items || '[]') || []).filter(x => x && typeof x.id === 'string'); } catch { return []; }
}

/// A grid of images stored as one block. Items are a JSON string attribute (plain values sync safely in Yjs).
export const NoteGallery = Node.create({
  name: 'noteGallery',
  group: 'block',
  atom: true,
  draggable: true,
  addAttributes() { return { items: { default: '[]', parseHTML: e => e.getAttribute('data-items') || '[]' } }; },
  parseHTML() { return [{ tag: 'div[data-gallery]' }]; },
  renderHTML({ node }) { return ['div', { 'data-gallery': '', 'data-items': node.attrs.items }]; },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node, editor }) => {
      const host = this.options.host?.() ?? {};
      const dom = el('div', 'nt-gal');
      dom.contentEditable = 'false';
      let generation = 0;
      const paint = n => {
        const current = ++generation;
        const items = galleryItems(n), shown = items.slice(0, 4), more = items.length - shown.length;
        dom.className = `nt-gal n${Math.min(items.length, 4)}`;
        dom.innerHTML = shown.map((it, k) => `<button type="button" class="t${k === 3 && more > 0 ? ' more' : ''}" data-k="${k}" ${k === 3 && more > 0 ? `data-more="+${more}"` : ''} title="${esc(it.name)}"><span class="nt-img-wait"></span></button>`).join('');
        shown.forEach((it, k) => loadBlob(host, it.id).then(u => {
          const b = dom.querySelector(`[data-k="${k}"]`); if (!b || current !== generation) return;
          const img = document.createElement('img'); img.src = u; img.alt = it.name || ''; img.draggable = false; b.replaceChildren(img);
        }, () => {}));
      };
      paint(node);
      dom.addEventListener('click', e => {
        const t = e.target.closest('[data-k]'); if (!t) return;
        const all = pageImages(editor.state.doc), first = galleryItems(node)[+t.dataset.k];
        openLightbox(host, all, all.findIndex(x => x.id === first?.id));
      });
      return { dom, update(n) { if (n.type.name !== 'noteGallery') return false; if (n.attrs.items !== node.attrs.items) paint(n); node = n; return true; }, ignoreMutation: () => true, destroy: () => { generation++; } };
    };
  },
});

// ── Tables ───────────────────────────────────────────────────────────────────

const cellBg = {
  bg: {
    default: null,
    parseHTML: e => COLORS.includes(e.getAttribute('data-bg')) ? e.getAttribute('data-bg') : null,
    renderHTML: a => a.bg && COLORS.includes(a.bg) ? { 'data-bg': a.bg, class: `nt-cbg c-${a.bg}` } : {},
  },
};
export const tableExtensions = () => [
  Table.configure({ resizable: true, cellMinWidth: 80, HTMLAttributes: { class: 'nt-table' } }),
  TableRow,
  TableHeader.extend({ content: 'paragraph+', addAttributes() { return { ...this.parent?.(), ...cellBg }; } }),
  TableCell.extend({ content: 'paragraph+', addAttributes() { return { ...this.parent?.(), ...cellBg }; } }),
];

// ── Bookmarks + embeds ───────────────────────────────────────────────────────

const YT_HOSTS = ['youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtube-nocookie.com', 'www.youtube-nocookie.com', 'youtu.be'];
const YT_ID = /^[A-Za-z0-9_-]{11}$/;
/// Same rules as News (NewsHtml.ParseYoutubeId): a YouTube video id, or a direct https .mp4/.webm.
export function parseEmbed(url) {
  let u; try { u = new URL(String(url || '').trim()); } catch { return null; }
  const host = u.hostname.toLowerCase();
  if (YT_HOSTS.includes(host) && /^https?:$/.test(u.protocol)) {
    let id = null;
    if (host === 'youtu.be') id = u.pathname.split('/').filter(Boolean)[0];
    else if (u.pathname.toLowerCase() === '/watch') id = u.searchParams.get('v');
    else { const p = u.pathname.split('/').filter(Boolean); if (p.length >= 2 && ['embed', 'shorts', 'live'].includes(p[0])) id = p[1]; }
    return id && YT_ID.test(id) ? { kind: 'youtube', id } : null;
  }
  if (u.protocol === 'https:' && !u.username && !u.password && /\.(mp4|webm)$/i.test(u.pathname)) return { kind: 'video', src: u.href };
  return null;
}

const previewCaches = new WeakMap();
const PREVIEW_TTL = 5 * 60 * 1000;
function linkPreview(host, url) {
  let previewCache = previewCaches.get(host);
  if (!previewCache) { previewCache = new Map(); previewCaches.set(host, previewCache); }
  const cached = previewCache.get(url);
  if (cached && cached.expires <= Date.now()) previewCache.delete(url);
  if (!previewCache.has(url)) {
    const p = (async () => {
      const r = await host.fetch(`/api/link-preview?url=${encodeURIComponent(url)}`);
      if (r.status === 204) return null;
      if (!r.ok) throw new Error(String(r.status));
      return r.json();
    })();
    const entry = { promise: p, expires: Date.now() + PREVIEW_TTL };
    previewCache.set(url, entry);
    p.catch(() => { if (previewCache.get(url) === entry) previewCache.delete(url); });
  }
  return previewCache.get(url).promise;
}
const navigate = path => { try { window.Blazor.navigateTo(path); } catch { location.assign(path); } };
const INTERNAL_ICON = { news: '📰', project: '📁', task: '☑', page: '📄' };

export const NoteBookmark = Node.create({
  name: 'noteBookmark',
  group: 'block',
  atom: true,
  draggable: true,
  addAttributes() {
    return {
      url: { default: null, parseHTML: e => httpUrl(e.getAttribute('data-url')) },
      title: { default: '', parseHTML: e => e.getAttribute('data-title') || '' },
      desc: { default: '', parseHTML: e => e.getAttribute('data-desc') || '' },
      site: { default: '', parseHTML: e => e.getAttribute('data-site') || '' },
    };
  },
  parseHTML() { return [{ tag: 'div[data-bookmark]' }]; },
  renderHTML({ node: { attrs: a } }) { return ['div', { 'data-bookmark': '', 'data-url': a.url, 'data-title': a.title, 'data-desc': a.desc, 'data-site': a.site }]; },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const host = this.options.host?.() ?? {};
      const url = httpUrl(node.attrs.url);
      const dom = el('a', 'nt-bm');
      dom.contentEditable = 'false';
      let internalPath = null;
      if (url) { dom.href = url; dom.target = '_blank'; dom.rel = 'noopener noreferrer'; dom.title = url; }
      let hostName = ''; try { hostName = new URL(url).hostname; } catch { /* invalid */ }
      const paint = (d, img) => {
        const it = d?.internal;
        dom.classList.toggle('internal', !!it);
        if (it) {
          internalPath = it.path;
          dom.innerHTML = `<span class="ic">${INTERNAL_ICON[it.type] || '📄'}</span><span class="tx"><span class="t">${esc(it.title || d.title || 'iPop link')}</span><span class="d">${esc(it.subtitle || it.path)}</span></span>`;
          return;
        }
        const t = d?.title || node.attrs.title || url || 'Link', ds = d?.description ?? node.attrs.desc, site = d?.siteName || node.attrs.site || hostName;
        dom.innerHTML = `<span class="tx"><span class="t">${esc(t)}</span>${ds ? `<span class="d">${esc(ds)}</span>` : ''}<span class="u"><span class="fav">🔗</span>${esc(site)}</span></span>`
          + (img ? `<span class="og"><img alt="" src="${esc(img)}" loading="lazy"></span>` : '');
      };
      paint(null);
      if (url && host.fetch) linkPreview(host, url).then(d => {
        if (!d) return;
        const img = d.image ? `${host.apiBase || ''}${d.image}` : null;
        paint(d, img);
        // Remember the title so it still reads well offline / in version history.
        if (editor.isEditable && d.kind !== 'internal' && d.title && d.title !== node.attrs.title) {
          const p = getPos();
          const cur = typeof p === 'number' ? editor.state.doc.nodeAt(p) : null;
          if (cur?.type.name === 'noteBookmark' && cur.attrs.url === node.attrs.url)
            editor.view.dispatch(editor.state.tr.setNodeMarkup(p, undefined, { ...cur.attrs, title: d.title.slice(0, 300), desc: (d.description || '').slice(0, 500), site: (d.siteName || '').slice(0, 120) }).setMeta('addToHistory', false));
        }
      }, () => {});
      dom.addEventListener('click', e => {
        e.preventDefault();
        if (internalPath) navigate(internalPath);
        else if (url) window.open(url, '_blank', 'noopener,noreferrer');
      });
      return { dom, update: n => n.type.name === 'noteBookmark' && n.attrs.url === node.attrs.url ? (node = n, true) : false, ignoreMutation: () => true };
    };
  },
});

export const NoteEmbed = Node.create({
  name: 'noteEmbed',
  group: 'block',
  atom: true,
  draggable: true,
  addAttributes() { return { url: { default: null, parseHTML: e => httpUrl(e.getAttribute('data-url')) } }; },
  parseHTML() { return [{ tag: 'div[data-embed]' }]; },
  renderHTML({ node }) { return ['div', { 'data-embed': '', 'data-url': node.attrs.url }]; },
  addNodeView() {
    return ({ node }) => {
      const e = parseEmbed(node.attrs.url);
      const dom = el('div', 'nt-embed');
      dom.contentEditable = 'false';
      if (e?.kind === 'youtube') {
        dom.classList.add('yt');
        dom.innerHTML = `<img alt="" loading="lazy" src="https://i.ytimg.com/vi/${e.id}/hqdefault.jpg"><button type="button" class="play" aria-label="Play video">▶</button><span class="cap"><span>YouTube</span><span>Loads when you click play</span></span>`;
        dom.querySelector('.play').addEventListener('click', ev => {
          ev.preventDefault();
          const f = document.createElement('iframe');
          f.src = `https://www.youtube-nocookie.com/embed/${e.id}?autoplay=1`;
          f.title = 'YouTube video player'; f.allow = 'autoplay; encrypted-media; picture-in-picture; fullscreen'; f.allowFullscreen = true;
          f.referrerPolicy = 'strict-origin-when-cross-origin';
          dom.replaceChildren(f);
        });
      } else if (e?.kind === 'video') {
        const v = document.createElement('video'); v.controls = true; v.preload = 'none'; v.playsInline = true; v.src = e.src;
        dom.classList.add('vid'); dom.append(v);
      } else {
        dom.classList.add('bad'); dom.textContent = `Can't embed this link: ${node.attrs.url || ''}`;
      }
      return { dom, update: n => n.type.name === 'noteEmbed' && n.attrs.url === node.attrs.url, ignoreMutation: () => true, stopEvent: ev => !!ev.target.closest?.('iframe,video,.play') };
    };
  },
});

// ── To-dos: assignee + due date, per-item actions, progress bar ──────────────

export const RichTaskItem = TaskItem.extend({
  addAttributes() {
    return {
      ...this.parent?.(),
      assigneeId: { default: null, keepOnSplit: false, parseHTML: e => e.getAttribute('data-assignee-id'), renderHTML: a => a.assigneeId ? { 'data-assignee-id': a.assigneeId } : {} },
      assigneeName: { default: null, keepOnSplit: false, parseHTML: e => e.getAttribute('data-assignee'), renderHTML: a => a.assigneeName ? { 'data-assignee': a.assigneeName } : {} },
      completedOn: { default: null, keepOnSplit: false, parseHTML: e => validDue(e.getAttribute('data-completed')), renderHTML: a => a.completedOn ? { 'data-completed': a.completedOn } : {} },
      due: { default: null, keepOnSplit: false, parseHTML: e => validDue(e.getAttribute('data-due')), renderHTML: a => a.due ? { 'data-due': a.due } : {} },
    };
  },
});

/// The innermost to-do item around `pos`, or null.
export function taskItemAt(doc, pos) {
  const $p = doc.resolve(Math.max(0, Math.min(pos, doc.content.size)));
  for (let d = $p.depth; d > 0; d--) if ($p.node(d).type.name === 'taskItem') return { node: $p.node(d), pos: $p.before(d) };
  return null;
}
/// Where a photo/file dropped into a to-do goes: right under the item's text, inside the item.
export function todoInsertPos(doc, pos) {
  const it = taskItemAt(doc, pos);
  if (!it) return pos;
  let at = it.pos + 1 + it.node.firstChild.nodeSize;
  // after any photos/files already there, so they stay in the order they were added
  it.node.forEach((c, off, i) => { if (i > 0 && it.pos + 1 + off === at && ['noteImage', 'noteFile'].includes(c.type.name)) at += c.nodeSize; });
  return at;
}

const todoKey = new PluginKey('todoExtras');
export const TodoExtras = Extension.create({
  name: 'todoExtras',
  addOptions() { return { onMenu: null, onChip: null }; },
  addProseMirrorPlugins() {
    const editor = this.editor, opts = this.options;
    return [new Plugin({
      key: todoKey,
      appendTransaction(transactions, oldState, state) {
        if (!transactions.some(t => t.docChanged) || transactions.some(t => t.getMeta('todoDates'))) return null;
        const tr = state.tr;
        state.doc.descendants((n, pos) => {
          if (n.type.name !== 'taskItem') return;
          let oldPos = pos;
          for (let i = transactions.length - 1; i >= 0; i--) oldPos = transactions[i].mapping.invert().map(oldPos);
          const old = oldState.doc.nodeAt(Math.min(oldPos, oldState.doc.content.size));
          if (old?.type.name !== 'taskItem' || old.attrs.checked === n.attrs.checked) return;
          tr.setNodeMarkup(pos, undefined, { ...n.attrs, completedOn: n.attrs.checked ? isoToday() : null });
        });
        return tr.docChanged ? tr.setMeta('todoDates', true).setMeta('addToHistory', false) : null;
      },
      props: {
        decorations(state) {
          const editable = editor.isEditable, decos = [], today = isoToday();
          state.doc.descendants((node, pos, parent) => {
            const t = node.type.name;
            if (t === 'taskList' && parent?.type.name !== 'taskItem') {
              let done = 0, total = 0;
              node.descendants(n => { if (n.type.name === 'taskItem') { total++; if (n.attrs.checked) done++; } });
              if (total >= 3) decos.push(Decoration.widget(pos, () => {
                const d = el('div', 'nt-tprog', `<span class="bar"><i style="width:${Math.round(done / total * 100)}%"></i></span><span>${done === total ? '✓ All done' : `${done} of ${total} done`}</span>`);
                d.contentEditable = 'false';
                return d;
              }, { side: -1, key: `tp-${done}-${total}`, ignoreSelection: true }));
            }
            if (t !== 'taskItem' || node.firstChild?.type.name !== 'paragraph') return;
            const { assigneeId, assigneeName, due, checked, completedOn } = node.attrs;
            if (!assigneeId && !due && !completedOn && !editable) return;
            const at = pos + 1 + node.firstChild.nodeSize - 1;
            decos.push(Decoration.widget(at, (view, getPos) => {
              const d = el('span', 'nt-tmeta');
              d.contentEditable = 'false';
              if (assigneeId) d.append(el('button', 'chip who', `@${esc(assigneeName || 'Someone')}`));
              if (due) {
                const c = el('button', `chip due${!checked && due < today ? ' late' : ''}${due === today ? ' today' : ''}`, `📅 ${esc(due === today ? 'Today' : fmtDue(due))}`);
                c.title = !checked && due < today ? 'Overdue' : 'Due date';
                d.append(c);
              }
              if (checked && completedOn) d.append(el('span', 'chip done', `✓ Done ${esc(fmtDue(completedOn))}`));
              if (editable) { const m = el('button', 'more', '⋯'); m.title = 'Photo, file, sub-item, assignee, due date'; m.setAttribute('aria-label', 'To-do options'); d.append(m); }
              d.querySelectorAll('button').forEach(b => { b.type = 'button'; b.tabIndex = -1; });
              d.addEventListener('mousedown', e => e.preventDefault());
              d.addEventListener('click', e => {
                const b = e.target.closest('button'); if (!b || !editable) return;
                const it = taskItemAt(view.state.doc, getPos());
                if (!it) return;
                if (b.classList.contains('more')) opts.onMenu?.(it.pos, b);
                else opts.onChip?.(it.pos, b, b.classList.contains('who') ? 'who' : 'due');
              });
              return d;
            }, { side: 1, key: `tm-${assigneeId}-${assigneeName}-${due}-${checked}-${completedOn}-${editable}-${due && due < today}`, ignoreSelection: true, stopEvent: () => true }));
          });
          return DecorationSet.create(state.doc, decos);
        },
      },
    })];
  },
});

// ── Floating UI: image bar, table bar + size picker, to-do menus, paste menu ─

const pop = (cls, html = '') => { const p = el('div', `nt-pop ${cls}`, html); document.body.appendChild(p); return p; };
const above = (p, rect) => {
  p.style.visibility = 'hidden'; p.classList.add('show');
  const w = p.offsetWidth, h = p.offsetHeight;
  let top = rect.top - h - 8; if (top < 8) top = Math.min(rect.bottom + 8, innerHeight - h - 8);
  p.style.top = `${top}px`;
  // Left edge wins when the bar is wider than the screen (phones): it scrolls instead of hiding its start.
  p.style.left = `${Math.max(8, Math.min(rect.left + rect.width / 2 - w / 2, innerWidth - w - 8))}px`;
  p.style.visibility = '';
};

export function createRichUi(editor, host) {
  const cleanup = [];
  const on = (t, ev, fn, o) => { t.addEventListener(ev, fn, o); cleanup.push(() => t.removeEventListener(ev, fn, o)); };
  const nodeAt = p => (typeof p === 'number' ? editor.state.doc.nodeAt(p) : null);
  const setAttrs = (p, attrs) => { const n = nodeAt(p); if (n) editor.view.dispatch(editor.state.tr.setNodeMarkup(p, undefined, { ...n.attrs, ...attrs })); };

  // Image / gallery bar (shows when the block is selected).
  const imgBar = pop('nt-fmt nt-imgbar');
  on(imgBar, 'mousedown', e => e.preventDefault());
  let imgPos = null;
  function syncImg() {
    const sel = editor.state.selection;
    const n = sel instanceof NodeSelection ? sel.node : null;
    if (!editor.isEditable || !n || !['noteImage', 'noteGallery'].includes(n.type.name) || !editor.view.hasFocus()) { imgBar.classList.remove('show'); imgPos = null; return; }
    imgPos = sel.from;
    const a = n.attrs, inTodo = !!taskItemAt(editor.state.doc, sel.from);
    imgBar.innerHTML = n.type.name === 'noteGallery'
      ? '<button data-i="view">View</button><button data-i="ungroup">Ungroup</button><span class="div"></span><button data-i="del" class="danger">Delete</button>'
      : (inTodo ? '' : ['left', 'center', 'right'].map(x => `<button data-i="al-${x}" class="${a.align === x ? 'on' : ''}" title="Align ${x}">${cap(x)}</button>`).join('')
        + '<span class="div"></span>' + [['S', 33], ['M', 60], ['Full', 100]].map(([k, v]) => `<button data-i="w-${v}" class="${a.width === v ? 'on' : ''}">${k}</button>`).join('')
        + '<span class="div"></span><button data-i="cap">Caption</button>')
        + '<button data-i="view">View</button><button data-i="dl">Download</button><span class="div"></span><button data-i="del" class="danger">Delete</button>';
    const dom = editor.view.nodeDOM(sel.from);
    if (dom) above(imgBar, (dom.querySelector?.('.nt-img-frame') || dom).getBoundingClientRect());
  }
  on(imgBar, 'click', e => {
    const b = e.target.closest('[data-i]'); const n = nodeAt(imgPos); if (!b || !n) return;
    const k = b.dataset.i;
    if (k.startsWith('al-')) setAttrs(imgPos, { align: k.slice(3) });
    else if (k.startsWith('w-')) setAttrs(imgPos, { width: +k.slice(2) });
    else if (k === 'cap') { editor.view.nodeDOM(imgPos)?.querySelector('input.nt-img-cap')?.focus(); imgBar.classList.remove('show'); return; }
    else if (k === 'view') { const all = pageImages(editor.state.doc); const id = n.type.name === 'noteGallery' ? galleryItems(n)[0]?.id : n.attrs.id; openLightbox(host, all, all.findIndex(x => x.id === id)); }
    else if (k === 'dl') download(host, n.attrs.id, n.attrs.name);
    else if (k === 'del') editor.chain().focus().deleteRange({ from: imgPos, to: imgPos + n.nodeSize }).run();
    else if (k === 'ungroup') editor.chain().focus().insertContentAt({ from: imgPos, to: imgPos + n.nodeSize }, galleryItems(n).map(it => ({ type: 'noteImage', attrs: { ...it, size: it.size || 0 } }))).run();
    syncImg();
  });

  // "Combine into a gallery" offer after 3+ images land next to each other.
  const galPop = pop('nt-galoffer', '<span>🖼 Combine these images into a gallery?</span><button type="button" data-g="yes">Combine</button><button type="button" data-g="no" aria-label="Dismiss">✕</button>');
  let galIds = null, galTimer = 0;
  function findRun(ids) {
    const want = new Set(ids); let run = null;
    editor.state.doc.descendants((n, p, parent) => {
      if (run && run.done) return false;
      if (n.type.name === 'noteImage' && want.has(n.attrs.id)) {
        if (run && run.parent === parent && run.to === p) { run.to = p + n.nodeSize; run.nodes.push(n); }
        else if (!run || run.nodes.length < 3) run = { parent, from: p, to: p + n.nodeSize, nodes: [n] };
        return false;
      }
      if (run && run.parent === parent && run.nodes.length >= 3) run.done = true;
      return true;
    });
    return run && run.nodes.length >= 3 ? run : null;
  }
  function offerGallery(ids) {
    const run = findRun(ids); if (!run || taskItemAt(editor.state.doc, run.from)) return;
    galIds = ids;
    const dom = editor.view.nodeDOM(run.from);
    if (!dom) return;
    place(galPop, dom.getBoundingClientRect(), { align: 'center', gap: -40 });
    clearTimeout(galTimer); galTimer = setTimeout(() => galPop.classList.remove('show'), 12000);
  }
  on(galPop, 'mousedown', e => e.preventDefault());
  on(galPop, 'click', e => {
    const b = e.target.closest('[data-g]'); if (!b) return;
    galPop.classList.remove('show');
    if (b.dataset.g !== 'yes' || !galIds) return;
    const run = findRun(galIds); galIds = null;
    if (!run) return;
    const items = JSON.stringify(run.nodes.map(n => ({ ...n.attrs })));
    editor.chain().focus().insertContentAt({ from: run.from, to: run.to }, { type: 'noteGallery', attrs: { items } }).run();
  });

  // Table bar.
  const tblBar = pop('nt-fmt nt-tblbar', `
    <button data-t="rowBefore" title="Insert row above">↑ Row</button><button data-t="rowAfter" title="Insert row below">↓ Row</button>
    <button data-t="colBefore" title="Insert column left">← Col</button><button data-t="colAfter" title="Insert column right">→ Col</button>
    <span class="div"></span><button data-t="header" title="Header row on/off">Header</button><button data-t="color" title="Cell colour">Colour ▾</button>
    <span class="div"></span><button data-t="delRow" class="danger">Delete row</button><button data-t="delCol" class="danger">Delete col</button><button data-t="delTable" class="danger" title="Delete table">🗑</button>`);
  const cellColors = pop('nt-menu nt-cellcolors', '<div class="mh">CELL COLOUR</div><div class="swrow"><span class="sq" data-cb="" title="Default" style="background:var(--surface)"></span>'
    + COLORS.map(c => `<span class="sq" data-cb="${c}" title="${cap(c)}" style="background:var(--nb-${c})"></span>`).join('') + '</div>');
  [tblBar, cellColors].forEach(p => on(p, 'mousedown', e => e.preventDefault()));
  function syncTable() {
    const { selection } = editor.state;
    const inTable = editor.isEditable && editor.isActive('table') && editor.view.hasFocus() && (selection.empty || selection.constructor.name === 'CellSelection' || !!selection.$anchorCell);
    if (!inTable) { tblBar.classList.remove('show'); cellColors.classList.remove('show'); return; }
    const dom = editor.view.domAtPos(selection.from).node;
    const wrap = (dom.nodeType === 1 ? dom : dom.parentElement)?.closest('.tableWrapper, table');
    if (wrap) {
      const r = wrap.getBoundingClientRect();
      above(tblBar, { top: r.top, bottom: r.top, left: r.left, width: Math.min(r.width, 420) });
    }
  }
  on(tblBar, 'click', e => {
    const b = e.target.closest('[data-t]'); if (!b) return;
    const c = editor.chain().focus();
    switch (b.dataset.t) {
      case 'rowBefore': c.addRowBefore().run(); break;
      case 'rowAfter': c.addRowAfter().run(); break;
      case 'colBefore': c.addColumnBefore().run(); break;
      case 'colAfter': c.addColumnAfter().run(); break;
      case 'header': c.toggleHeaderRow().run(); break;
      case 'delRow': c.deleteRow().run(); break;
      case 'delCol': c.deleteColumn().run(); break;
      case 'delTable': c.deleteTable().run(); break;
      case 'color':
        if (cellColors.classList.contains('show')) cellColors.classList.remove('show'); else place(cellColors, b.getBoundingClientRect());
        return;
    }
    syncTable();
  });
  on(cellColors, 'click', e => {
    const s = e.target.closest('[data-cb]'); if (!s) return;
    editor.chain().focus().setCellAttribute('bg', s.dataset.cb || null).run();
    cellColors.classList.remove('show');
  });

  // /table size picker.
  const grid = pop('nt-gridpick');
  const ROWS = 6, COLS = 6;
  grid.innerHTML = Array.from({ length: ROWS * COLS }, (_, k) => `<i data-r="${Math.floor(k / COLS) + 1}" data-c="${k % COLS + 1}"></i>`).join('') + '<span>3 × 3</span>';
  const paintGrid = (r, c) => {
    grid.querySelectorAll('i').forEach(x => x.classList.toggle('on', +x.dataset.r <= r && +x.dataset.c <= c));
    grid.querySelector('span').textContent = `${r} × ${c}`;
  };
  on(grid, 'mousedown', e => e.preventDefault());
  on(grid, 'mouseover', e => { const x = e.target.closest('i'); if (x) paintGrid(+x.dataset.r, +x.dataset.c); });
  on(grid, 'click', e => {
    const x = e.target.closest('i'); if (!x) return;
    grid.classList.remove('show');
    editor.chain().focus().insertTable({ rows: +x.dataset.r, cols: +x.dataset.c, withHeaderRow: true }).run();
  });
  function tablePicker() {
    paintGrid(3, 3);
    const c = editor.view.coordsAtPos(editor.state.selection.from);
    place(grid, { top: c.top, bottom: c.bottom, left: c.left, right: c.left, width: 0 });
  }

  // To-do item menu, people picker, due date.
  const todoMenu = pop('nt-menu nt-todo-menu');
  const people = pop('nt-people', '<input type="search" placeholder="Assign to… (type a name)" aria-label="Find a person" spellcheck="false"><div class="res"></div>');
  const duePop = pop('nt-duepop', '<input type="date" aria-label="Due date"><div class="row"><button type="button" data-d="0">Today</button><button type="button" data-d="1">Tomorrow</button><button type="button" data-d="7">Next week</button><button type="button" data-d="clear" class="danger">Clear</button></div>');
  let itemPos = null, peopleItems = [], peopleSel = 0, searchSeq = 0;
  const trackItem = ({ transaction }) => {
    if (itemPos === null || !transaction.docChanged) return;
    const mapped = transaction.mapping.mapResult(itemPos, 1);
    if (mapped.deleted || transaction.doc.nodeAt(mapped.pos)?.type.name !== 'taskItem') {
      itemPos = null; searchSeq++;
      [todoMenu, people, duePop].forEach(p => p.classList.remove('show'));
    } else itemPos = mapped.pos;
  };
  editor.on('transaction', trackItem);
  cleanup.push(() => editor.off('transaction', trackItem));
  const setItemAttrs = attrs => {
    if (nodeAt(itemPos)?.type.name !== 'taskItem') return false;
    setAttrs(itemPos, attrs); return true;
  };
  on(todoMenu, 'mousedown', e => e.preventDefault());
  [people, duePop].forEach(p => on(p, 'mousedown', e => { if (!e.target.matches('input')) e.preventDefault(); }));
  function openTodoMenu(pos, anchor) {
    itemPos = pos;
    const n = nodeAt(pos);
    todoMenu.innerHTML = '<div class="mi" data-m="photo"><span class="sw">🖼</span>Add photo</div>'
      + '<div class="mi" data-m="file"><span class="sw">📎</span>Attach file</div>'
      + '<div class="mi" data-m="sub"><span class="sw">↳</span>Add sub-item<span class="ds">Tab</span></div><hr>'
      + `<div class="mi" data-m="who"><span class="sw">@</span>${n?.attrs.assigneeId ? 'Change person' : 'Assign person'}</div>`
      + (n?.attrs.assigneeId ? '<div class="mi" data-m="unwho"><span class="sw">⊘</span>Remove person</div>' : '')
      + `<div class="mi" data-m="due"><span class="sw">📅</span>${n?.attrs.due ? 'Change due date' : 'Due date'}</div><hr>`
      + '<div class="mi danger" data-m="del"><span class="sw">🗑</span>Delete item</div>';
    place(todoMenu, anchor.getBoundingClientRect(), { align: 'right' });
  }
  on(todoMenu, 'click', e => {
    const m = e.target.closest('[data-m]'); if (!m) return;
    const n = nodeAt(itemPos); todoMenu.classList.remove('show');
    if (!n || n.type.name !== 'taskItem') return;
    const anchor = editor.view.nodeDOM(itemPos)?.querySelector('.nt-tmeta .more') || editor.view.nodeDOM(itemPos);
    switch (m.dataset.m) {
      case 'photo': host.pickFiles('image/*', todoInsertPos(editor.state.doc, itemPos + 1)); break;
      case 'file': host.pickFiles('', todoInsertPos(editor.state.doc, itemPos + 1)); break;
      case 'sub': {
        const nested = n.lastChild?.type.name === 'taskList' ? n.lastChild : null;
        const end = itemPos + n.nodeSize - 1;
        const item = { type: 'taskItem', attrs: { checked: false }, content: [{ type: 'paragraph' }] };
        if (nested) editor.chain().insertContentAt(end - 1, item).setTextSelection(end + 1).focus().run();
        else editor.chain().insertContentAt(end, { type: 'taskList', content: [item] }).setTextSelection(end + 3).focus().run();
        break;
      }
      case 'who': openPeople(itemPos, anchor); break;
      case 'unwho': setItemAttrs({ assigneeId: null, assigneeName: null }); break;
      case 'due': openDue(itemPos, anchor); break;
      case 'del': editor.chain().focus().deleteRange({ from: itemPos, to: itemPos + n.nodeSize }).run(); break;
    }
  });
  const peopleIn = people.querySelector('input'), peopleRes = people.querySelector('.res');
  const paintPeople = () => {
    peopleRes.innerHTML = peopleItems.length
      ? peopleItems.map((p, k) => `<div class="it ${k === peopleSel ? 'sel' : ''}" data-k="${k}"><span class="nt-av" style="background:${host.avatarColor?.(p.nik)}">${esc(host.initials?.(p.name))}</span><span class="nm">${esc(p.name)}</span></div>`).join('')
      : `<div class="empty">${peopleIn.value.trim().length < 2 ? 'Type at least 2 letters of a name' : 'No one found'}</div>`;
  };
  function openPeople(pos, anchor) {
    itemPos = pos; peopleItems = []; peopleSel = 0; peopleIn.value = ''; paintPeople();
    place(people, anchor.getBoundingClientRect(), { align: 'right' });
    peopleIn.focus();
  }
  const pickPerson = p => {
    if (!p) return;
    people.classList.remove('show');
    if (setItemAttrs({ assigneeId: p.id, assigneeName: p.name })) host.mentioned?.(p);
    editor.commands.focus();
  };
  on(peopleIn, 'input', async () => {
    const q = peopleIn.value.trim(), seq = ++searchSeq;
    if (q.length < 2) { peopleItems = []; paintPeople(); return; }
    const r = await host.searchPeople(q);
    if (seq === searchSeq) { peopleItems = (r || []).slice(0, 8); peopleSel = 0; paintPeople(); }
  });
  on(peopleIn, 'keydown', e => {
    if (e.key === 'ArrowDown' && peopleItems.length) { e.preventDefault(); peopleSel = (peopleSel + 1) % peopleItems.length; paintPeople(); }
    else if (e.key === 'ArrowUp' && peopleItems.length) { e.preventDefault(); peopleSel = (peopleSel - 1 + peopleItems.length) % peopleItems.length; paintPeople(); }
    else if (e.key === 'Enter') { e.preventDefault(); pickPerson(peopleItems[peopleSel]); }
    else if (e.key === 'Escape') { e.preventDefault(); people.classList.remove('show'); editor.commands.focus(); }
  });
  on(peopleRes, 'click', e => { const it = e.target.closest('[data-k]'); if (it) pickPerson(peopleItems[+it.dataset.k]); });

  const dueIn = duePop.querySelector('input');
  function openDue(pos, anchor) {
    itemPos = pos; dueIn.value = nodeAt(pos)?.attrs.due || '';
    place(duePop, anchor.getBoundingClientRect(), { align: 'right' });
    dueIn.focus();
  }
  const setDue = v => { duePop.classList.remove('show'); setItemAttrs({ due: validDue(v) }); editor.commands.focus(); };
  on(dueIn, 'change', () => { if (dueIn.value) setDue(dueIn.value); });
  on(dueIn, 'keydown', e => { if (e.key === 'Escape') { e.preventDefault(); duePop.classList.remove('show'); } });
  on(duePop, 'click', e => {
    const b = e.target.closest('[data-d]'); if (!b) return;
    if (b.dataset.d === 'clear') return setDue(null);
    const d = new Date(Date.now() + +b.dataset.d * 864e5);
    setDue(`${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`);
  });
  const onChip = (pos, anchor, kind) => (kind === 'who' ? openPeople : openDue)(pos, anchor);

  // Paste a bare URL on an empty line → Link / Bookmark / Embed.
  const pasteMenu = pop('nt-list nt-pastemenu');
  let pasteUrl = null, pasteOpts = [], pasteSel = 0;
  const paintPaste = () => {
    pasteMenu.innerHTML = '<div class="grp">PASTE AS</div>' + pasteOpts.map(([k, ico, name, desc], i) =>
      `<div class="it ${i === pasteSel ? 'sel' : ''}" data-p="${k}"><span class="bx">${ico}</span><div><div class="nm">${name}</div><div class="ds">${desc}</div></div>${k === 'bookmark' ? '<kbd>Enter</kbd>' : ''}</div>`).join('');
  };
  const closePaste = () => { pasteMenu.classList.remove('show'); pasteUrl = null; };
  function convertPaste(kind) {
    const url = pasteUrl; closePaste();
    if (!url || kind === 'link') return;
    const { $from } = editor.state.selection;
    if ($from.parent.type.name !== 'paragraph' || $from.parent.textContent.trim() !== url) return;
    editor.chain().focus().insertContentAt({ from: $from.before(), to: $from.after() }, { type: kind === 'embed' ? 'noteEmbed' : 'noteBookmark', attrs: { url } }).run();
  }
  on(pasteMenu, 'mousedown', e => { e.preventDefault(); const it = e.target.closest('[data-p]'); if (it) convertPaste(it.dataset.p); });
  on(document, 'keydown', e => {
    if (!pasteMenu.classList.contains('show')) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); e.stopPropagation(); pasteSel = (pasteSel + (e.key === 'ArrowDown' ? 1 : -1) + pasteOpts.length) % pasteOpts.length; paintPaste(); }
    else if (e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); convertPaste(pasteOpts[pasteSel][0]); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closePaste(); }
    else closePaste();
  }, true);
  function handlePasteUrl(event) {
    const text = (event.clipboardData?.getData('text/plain') || '').trim();
    const url = /^https?:\/\/[^\s<>"]+$/i.test(text) ? httpUrl(text) : null;
    const { selection } = editor.state, { $from } = selection;
    if (!url || !editor.isEditable || !selection.empty || $from.depth !== 1 || $from.parent.type.name !== 'paragraph' || $from.parent.content.size) return false;
    editor.chain().insertContent({ type: 'text', text, marks: [{ type: 'link', attrs: { href: url } }] }).run();
    pasteUrl = text;
    pasteOpts = [['link', '🔗', 'Link', 'Keep it as a link'], ['bookmark', '🔖', 'Bookmark', 'Card with title and picture']];
    if (parseEmbed(url)) pasteOpts.push(['embed', '▶', 'Embed', 'Play the video in the page']);
    pasteSel = 1; paintPaste();
    const c = editor.view.coordsAtPos(editor.state.selection.from);
    place(pasteMenu, { top: c.top, bottom: c.bottom, left: c.left, right: c.left, width: 0 });
    return true;
  }

  // /bookmark and /embed ask for the link in a small popover.
  const ask = pop('nt-link-pop', '<input type="url" class="nt-link-in" placeholder="Paste a link" spellcheck="false" aria-label="Link"><div class="nt-link-acts"><button type="button" class="nt-link-ok">Insert</button></div>');
  const askIn = ask.querySelector('input'); let askKind = null;
  on(ask, 'mousedown', e => { if (e.target !== askIn) e.preventDefault(); });
  const askDone = () => {
    const url = httpUrl(/^https?:/i.test(askIn.value.trim()) ? askIn.value.trim() : `https://${askIn.value.trim()}`);
    if (!askIn.value.trim()) { ask.classList.remove('show'); return; }
    if (!url) { host.toast?.('That doesn\'t look like a web link'); return; }
    if (askKind === 'embed' && !parseEmbed(url)) { host.toast?.('Only YouTube links or direct .mp4/.webm videos can be embedded'); return; }
    ask.classList.remove('show');
    editor.chain().focus().insertContent({ type: askKind === 'embed' ? 'noteEmbed' : 'noteBookmark', attrs: { url } }).run();
  };
  on(ask, 'click', e => { if (e.target.closest('.nt-link-ok')) askDone(); });
  on(askIn, 'keydown', e => { if (e.key === 'Enter') { e.preventDefault(); askDone(); } else if (e.key === 'Escape') { e.preventDefault(); ask.classList.remove('show'); editor.commands.focus(); } });
  function askLink(kind) {
    askKind = kind; askIn.value = '';
    askIn.placeholder = kind === 'embed' ? 'Paste a YouTube or .mp4 link' : 'Paste a link to bookmark';
    const c = editor.view.coordsAtPos(editor.state.selection.from);
    place(ask, { top: c.top, bottom: c.bottom, left: c.left, right: c.left, width: 0 });
    askIn.focus();
  }

  // Close popovers on outside clicks; keep floating bars in sync.
  const floating = [grid, todoMenu, people, duePop, cellColors, ask, pasteMenu];
  on(document, 'mousedown', e => {
    for (const p of floating) if (p.classList.contains('show') && !p.contains(e.target) && !(p === cellColors && tblBar.contains(e.target))) {
      p.classList.remove('show'); if (p === pasteMenu) pasteUrl = null;
    }
  });
  const sync = () => { syncImg(); syncTable(); };
  editor.on('selectionUpdate', sync);
  editor.on('update', sync);
  editor.on('focus', sync);
  editor.on('blur', () => setTimeout(() => { if (!editor.isDestroyed && !editor.view.hasFocus()) { imgBar.classList.remove('show'); if (!cellColors.classList.contains('show')) tblBar.classList.remove('show'); } }, 150));
  on(window, 'scroll', () => { if (imgBar.classList.contains('show') || tblBar.classList.contains('show')) sync(); }, true);
  on(window, 'resize', sync);

  const later = f => (...a) => setTimeout(() => !editor.isDestroyed && f(...a), 0);
  return {
    tablePicker: later(tablePicker), askLink: later(askLink), offerGallery, openTodoMenu, onChip, handlePasteUrl,
    destroy() {
      closeLightboxes(host);
      cleanup.forEach(f => f()); clearTimeout(galTimer);
      editor.off('selectionUpdate', sync); editor.off('update', sync); editor.off('focus', sync);
      [imgBar, galPop, tblBar, cellColors, grid, todoMenu, people, duePop, pasteMenu, ask].forEach(p => p.remove());
    },
  };
}
