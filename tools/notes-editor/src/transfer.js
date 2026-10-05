// Notes import / export, done entirely in the browser.
//
// In: a Notion export (.zip of HTML or Markdown pages, possibly zipped twice) or a single .md / .html
// file. Pages are converted to the editor's own ProseMirror schema and written as a Yjs state, so an
// imported page is exactly what the editor would have produced. Out: Markdown, HTML (one file, or a zip
// with one folder per page like Notion) and PDF through the browser's print dialog.
//
// The pure parts (planImport, pageToDoc, docToMarkdown, docToHtml, ...) take no network and are covered
// by tests/transfer.test.mjs; importer() and exporter() wire them to the API.
import { getSchema, generateJSON } from '@tiptap/core';
import { unzipSync, zipSync, strFromU8, strToU8 } from 'fflate';
import { marked } from 'marked';
import * as Y from 'yjs';
import { prosemirrorJSONToYDoc, yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap';
import { COLORS } from './nodes.js';

export const LIMITS = { bytes: 100 * 1024 * 1024, pages: 300 };
const FIELD = 'default';

export class ImportError extends Error { constructor(code, message) { super(message); this.code = code; } }

const b64 = u8 => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); };
const unb64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
const extOf = p => (/\.([a-z0-9]+)$/i.exec(p)?.[1] || '').toLowerCase();
const baseName = p => p.split('/').pop();
const dirOf = p => p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '';
const NOTION_ID = /\s+([0-9a-f]{32})$/i;
const stripId = s => s.replace(NOTION_ID, '').trim();
const fnv = s => { let h = 0x811c9dc5; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; } return h.toString(16).padStart(8, '0'); };
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const MIME = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', svg: 'image/svg+xml', bmp: 'image/bmp', pdf: 'application/pdf' };
const isImagePath = p => /^(png|jpe?g|gif|webp|bmp|svg)$/.test(extOf(p));

// ── Reading the upload ───────────────────────────────────────────────────────

/// Flattens the upload into path → bytes. Notion wraps big exports in a zip of zips ("…-Part-1.zip").
export function readArchive(name, bytes) {
  if (bytes.length > LIMITS.bytes) throw new ImportError('too_big', 'The file is larger than 100 MB.');
  const files = new Map();
  const add = (prefix, data, depth) => {
    let entries;
    try { entries = unzipSync(data); } catch { throw new ImportError('bad_zip', "This zip file couldn't be opened."); }
    for (const [p, d] of Object.entries(entries)) {
      if (p.endsWith('/') || /(^|\/)(__MACOSX|\.DS_Store)/.test(p)) continue;
      if (extOf(p) === 'zip' && depth < 2) add(prefix, d, depth + 1);
      else files.set(prefix + p, d);
    }
  };
  const ext = extOf(name);
  if (ext === 'zip') add('', bytes, 0);
  else if (['md', 'markdown', 'html', 'htm'].includes(ext)) files.set(name, bytes);
  else throw new ImportError('unsupported', 'Choose a Notion export (.zip), a Markdown (.md) or an HTML (.html) file.');
  return files;
}

const pageExt = p => ['md', 'markdown', 'html', 'htm'].includes(extOf(p));

/// Works out the pages, their tree and where they came from, without converting any content yet.
/// Returns { source, title, pages: [{ key, parentKey, path, title, emoji, cover, coverImage, importKey, kind, text }], files, attachments }.
export function planImport(name, bytes, { parse = html => new DOMParser().parseFromString(html, 'text/html') } = {}) {
  const files = readArchive(name, bytes);
  const paths = [...files.keys()].filter(pageExt).filter(p => !(baseName(p) === 'index.html' && !dirOf(p)) || files.size === 1).sort();
  if (!paths.length) throw new ImportError('no_pages', "There are no pages in this file. Notion exports contain .html or .md files.");

  const notionish = paths.some(p => NOTION_ID.test(baseName(p).replace(/\.[^.]+$/, '')));
  const pages = [];
  for (const path of paths) {
    const kind = extOf(path).startsWith('htm') ? 'html' : 'md';
    const text = strFromU8(files.get(path));
    const file = baseName(path).replace(/\.[^.]+$/, '');
    const id = NOTION_ID.exec(file)?.[1]?.toLowerCase() ?? null;
    let title = stripId(file), emoji = null, coverSrc = null;
    let notion = notionish;
    if (kind === 'html') {
      const doc = parse(text);
      const art = doc.querySelector('article.page, article');
      notion ||= !!art?.classList?.contains('page') || /notion/i.test(text.slice(0, 4000));
      title = doc.querySelector('.page-title')?.textContent?.trim() || doc.querySelector('title')?.textContent?.trim() || title;
      const ic = doc.querySelector('.page-header-icon .icon');
      emoji = ic?.getAttribute('data-emoji') || (ic && ic.tagName !== 'IMG' ? ic.textContent.trim() : '') || art?.getAttribute('data-notion-page-icon') || null;
      coverSrc = doc.querySelector('img.page-cover-image')?.getAttribute('src') || null;
    } else {
      const h = /^\s*#\s+(.+)$/m.exec(text.slice(0, 2000));
      if (h && text.trimStart().startsWith('#')) title = h[1].trim();
    }
    const cover = coverSrc ? coverFor(coverSrc, path, files) : null;
    pages.push({
      key: path, parentKey: null, path, kind, text, title: title.slice(0, 200) || 'Untitled',
      emoji: emoji && [...emoji].length <= 4 ? emoji : null,
      cover: cover?.preset ?? null, coverImage: cover?.image ?? null,
      importKey: id ? `notion:${id}` : `file:${fnv(`${name}|${bytes.length}|${path}`)}`,
      source: notion ? 'notion' : kind === 'md' ? 'markdown' : 'html',
    });
  }

  // Notion keeps a page's children in a folder named like the page, next to the page's own file.
  const byStem = new Map(pages.map(p => [p.path.replace(/\.[^.]+$/, ''), p]));
  for (const p of pages) {
    for (let d = dirOf(p.path); d; d = dirOf(d)) {
      const parent = byStem.get(d);
      if (parent && parent !== p) { p.parentKey = parent.key; break; }
    }
  }
  // Parents before children (depth-first, siblings by name).
  const kids = new Map();
  for (const p of pages) { const k = p.parentKey ?? ''; if (!kids.has(k)) kids.set(k, []); kids.get(k).push(p); }
  const ordered = [];
  const walk = (k, depth) => (kids.get(k) || []).forEach(p => { p.depth = depth; ordered.push(p); walk(p.key, depth + 1); });
  walk('', 0);

  const roots = ordered.filter(p => !p.parentKey);
  const source = ordered.some(p => p.source === 'notion') ? 'notion' : ordered[0].source;
  if (roots.length > 1) {
    // Several top-level pages: keep them together under one page named after the export.
    const zipTitle = stripId(name.replace(/\.[^.]+$/, '').replace(/^[0-9a-f-]{36}_/i, '').replace(/^(ExportBlock-[0-9a-f-]+|Export(-[0-9a-f-]+)?)(-Part-\d+)?$/i, ''));
    const label = { notion: 'Notion', markdown: 'Markdown', html: 'HTML' }[source];
    const wrap = { key: '#root', parentKey: null, path: null, kind: null, text: '', depth: 0, title: zipTitle || `Imported from ${label}`, emoji: '🗂',
      cover: null, coverImage: null, importKey: `file:${fnv(`${name}|${bytes.length}|#root`)}`, source };
    roots.forEach(r => { r.parentKey = wrap.key; });
    ordered.forEach(p => { p.depth++; });
    ordered.unshift(wrap);
  }
  if (ordered.length > LIMITS.pages) throw new ImportError('too_many', `This export has ${ordered.length} pages. Import up to ${LIMITS.pages} pages at a time.`);

  const pagePaths = new Set(paths);
  const attachments = [...files.keys()].filter(p => !pagePaths.has(p)).length;
  return { source, title: ordered[0].title, pages: ordered, files, attachments };
}

/// Notion's cover gallery has no equivalent here, so gradients and solids map to the nearest iPop cover;
/// a cover the user uploaded to Notion is in the zip and becomes an image at the top of the page.
export function coverFor(src, pagePath, files) {
  const m = /page-cover\/([a-z]+)_?([a-z0-9]*)\.(?:jpe?g|png|gif|webp)/i.exec(src || '');
  if (m) {
    const [, kind, v] = m;
    if (kind === 'gradients') return { preset: { 2: 'ocean', 3: 'sunset', 4: 'lavender', 5: 'rose', 8: 'aurora', 9: 'mesh', 10: 'sunset', 11: 'teal', 13: 'ocean' }[+v] || 'ocean' };
    if (kind === 'solid') return { preset: { red: 'rose', yellow: 'sand', blue: 'ocean', beige: 'sand' }[v] || 'graphite' };
    if (/^(nasa|webb)/.test(kind)) return { preset: 'night' };
    return { preset: 'graphite' };
  }
  const local = resolvePath(dirOf(pagePath), src);
  if (local && files?.has(local)) return { image: local };
  return /^https?:/i.test(src || '') ? { preset: 'graphite' } : null;
}

export function resolvePath(base, href) {
  if (!href || /^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('#') || href.startsWith('/')) return null;
  let rel = href.split('#')[0].split('?')[0];
  try { rel = decodeURIComponent(rel); } catch { /* keep as is */ }
  const out = base ? base.split('/') : [];
  for (const seg of rel.split('/')) {
    if (seg === '..') out.pop();
    else if (seg && seg !== '.') out.push(seg);
  }
  return out.join('/');
}

// ── Converting one page ──────────────────────────────────────────────────────

const NOTION_COLORS = { gray: 'gray', brown: 'brown', orange: 'orange', yellow: 'yellow', teal: 'green', green: 'green', blue: 'blue', purple: 'purple', pink: 'pink', red: 'red' };
function notionColor(cls) {
  const m = /(?:^|\s)(?:highlight|block-color)-([a-z]+?)(_background)?(?=\s|$)/.exec(cls || '');
  const c = m && NOTION_COLORS[m[1]];
  return c ? { color: c, bg: !!m[2] } : null;
}

const NOTE_LABELS = {
  database: 'Notion database → plain table', equation: 'equation → code block', columns: 'column layout → stacked top to bottom',
  embed: 'embed → bookmark card', remoteImage: 'web image → bookmark card', toc: 'table of contents → removed',
  properties: 'page properties → table',
};

/// Converts one planned page to editor JSON.
/// ctx: { files, extensions, pageOf(path) → { id, title, emoji } | null, parse(html) → Document }
/// Returns { json, assets: [{ ref, path?, dataUrl?, name, image }], notes: Map(label → count) }.
export function pageToDoc(page, ctx) {
  const notes = new Map();
  const note = k => notes.set(NOTE_LABELS[k], (notes.get(NOTE_LABELS[k]) || 0) + 1);
  const assets = [];
  const asset = a => { const ref = `asset:${assets.length}`; assets.push({ ...a, ref }); return ref; };
  const base = page.path ? dirOf(page.path) : '';
  const local = href => { const p = resolvePath(base, href); return p && ctx.files.has(p) ? p : null; };

  // The page that wraps several top-level pages lists them, like a Notion index page.
  if (!page.path) {
    const kids = (ctx.childrenOf?.(page.key) || []).filter(Boolean);
    const content = kids.length ? kids.map(k => ({ type: 'subPage', attrs: { id: k.id, title: k.title, emoji: k.emoji || '📄' } })) : [{ type: 'paragraph' }];
    return { json: { type: 'doc', content }, assets: [], notes };
  }
  let html;
  if (!page.path) html = '';
  else if (page.kind === 'md') html = markdownToHtml(page.text, page.title);
  else html = page.text;
  const doc = (ctx.parse || (h => new DOMParser().parseFromString(h, 'text/html')))(`<!doctype html><html><body>${page.kind === 'html' ? '' : html}</body></html>`);
  let root = doc.body;
  if (page.kind === 'html') {
    const src = (ctx.parse || (h => new DOMParser().parseFromString(h, 'text/html')))(html);
    const body = src.querySelector('.page-body') || src.body;
    const desc = src.querySelector('.page-description')?.innerHTML?.trim();
    const props = src.querySelector('table.properties');
    root.innerHTML = (desc ? `<p>${desc}</p>` : '') + (props ? props.outerHTML : '') + (body ? body.innerHTML : '');
    if (props) note('properties');
  }

  const $$ = sel => [...root.querySelectorAll(sel)];
  const make = (tag, attrs = {}, inner) => {
    const e = doc.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) if (v != null) e.setAttribute(k, String(v));
    if (inner != null) e.innerHTML = inner;
    return e;
  };
  const swap = (a, b) => a.parentNode?.replaceChild(b, a);
  const unwrap = e => { while (e.firstChild) e.parentNode.insertBefore(e.firstChild, e); e.remove(); };
  const linkTarget = href => { const p = local(href); return p && ctx.pageOf?.(p) ? { path: p, ...ctx.pageOf(p) } : null; };
  const bookmark = (url, title, desc) => make('div', { 'data-bookmark': '', 'data-url': url, 'data-title': title || '', 'data-desc': desc || '' });
  const imageBlock = (ref, name, caption) => make('figure', { 'data-image': '', 'data-id': ref, 'data-name': name || '', 'data-caption': caption || '' });
  const fileBlock = (ref, name) => make('div', { 'data-file': '', 'data-id': ref, 'data-name': name || '' });
  const imgFrom = (src, caption) => {
    if (/^data:image\//i.test(src || '')) return imageBlock(asset({ dataUrl: src, name: 'image.png', image: true }), 'image.png', caption);
    const p = local(src);
    if (p) return imageBlock(asset({ path: p, name: baseName(p), image: true }), baseName(p), caption);
    if (/^https?:/i.test(src || '')) { note('remoteImage'); return bookmark(src, caption || baseName(src.split('?')[0]) || src); }
    return null;
  };

  // Noise and layout-only wrappers.
  $$('script, style, link, meta, svg, iframe:not([src])').forEach(e => e.remove());
  $$('nav.table_of_contents, .table_of_contents').forEach(e => { note('toc'); e.remove(); });
  $$('div.column-list').forEach(e => { note('columns'); $$('div.column').forEach(c => e.contains(c) && unwrap(c)); unwrap(e); });
  $$('div.column').forEach(unwrap);
  $$('div.indented').forEach(unwrap);

  // Toggles: Notion wraps each in <ul class="toggle"><li><details>.
  $$('ul.toggle').forEach(ul => {
    const items = [...ul.children].map(li => li.querySelector('details')).filter(Boolean);
    items.forEach(d => ul.parentNode.insertBefore(d, ul));
    ul.remove();
  });
  $$('details').forEach(d => {
    const sum = [...d.children].find(c => c.tagName === 'SUMMARY');
    const text = (sum?.textContent || '').replace(/\s+/g, ' ').trim();
    const body = make('div', { 'data-type': 'detailsContent' });
    [...d.childNodes].forEach(n => { if (n !== sum) body.appendChild(n); });
    if (!body.textContent.trim() && !body.querySelector('img, figure, table')) body.innerHTML = '<p></p>';
    const out = make('details', { open: '' });
    out.appendChild(make('summary', {}, esc(text)));
    out.appendChild(body);
    swap(d, out);
  });

  // To-dos.
  const taskItem = (checked, inner, rest) => {
    const li = make('li', { 'data-type': 'taskItem', 'data-checked': checked ? 'true' : 'false' });
    const box = make('div', {}, `<p>${inner}</p>`);
    rest.forEach(n => box.appendChild(n));
    li.appendChild(box);
    return li;
  };
  $$('ul.to-do-list').forEach(ul => {
    const out = make('ul', { 'data-type': 'taskList' });
    for (const li of [...ul.children]) {
      const checked = !!li.querySelector('.checkbox-on');
      const span = li.querySelector('.to-do-children-checked, .to-do-children-unchecked');
      const rest = [...li.children].filter(c => c !== span && !c.classList.contains('checkbox'));
      out.appendChild(taskItem(checked, span ? span.innerHTML : esc(li.textContent), rest));
    }
    swap(ul, out);
  });
  $$('ul').forEach(ul => {
    const lis = [...ul.children];
    if (!lis.length || ul.getAttribute('data-type') === 'taskList') return;
    const boxOf = li => { const f = li.querySelector('input[type=checkbox]'); return f && (li.firstElementChild === f || li.firstElementChild?.firstElementChild === f) ? f : null; };
    if (!lis.every(boxOf)) return;
    const out = make('ul', { 'data-type': 'taskList' });
    for (const li of lis) {
      const box = boxOf(li), checked = box.hasAttribute('checked');
      const holder = box.parentElement === li ? li : box.parentElement;
      box.remove();
      const rest = [...holder.children].filter(c => /^(UL|OL|P|DIV|BLOCKQUOTE|PRE)$/.test(c.tagName) && holder === li);
      rest.forEach(r => r.remove());
      out.appendChild(taskItem(checked, holder.innerHTML.trim(), rest));
    }
    swap(ul, out);
  });

  // Callouts.
  $$('figure.callout').forEach(f => {
    const [iconBox, bodyBox] = f.children;
    const emoji = iconBox?.querySelector('.icon')?.getAttribute('data-emoji') || iconBox?.textContent?.trim() || '💡';
    const c = notionColor(f.className);
    const out = make('div', { 'data-callout': '', 'data-emoji': [...emoji].length <= 4 ? emoji : '💡', 'data-color': c?.color || 'gray' });
    const inner = bodyBox || f;
    const hasBlocks = [...inner.children].some(e => /^(P|H[1-6]|UL|OL|DIV|BLOCKQUOTE|PRE|FIGURE|TABLE|DETAILS)$/.test(e.tagName));
    out.innerHTML = hasBlocks ? inner.innerHTML : `<p>${inner.innerHTML}</p>`;
    swap(f, out);
  });

  // Figures: equations, link-to-page, bookmarks, images, files and embeds.
  $$('figure.equation').forEach(f => {
    note('equation');
    const tex = f.querySelector('annotation')?.textContent || f.textContent;
    swap(f, make('pre', {}, `<code>${esc(tex.trim())}</code>`));
  });
  $$('span.notion-text-equation-token').forEach(s => swap(s, make('code', {}, esc(s.querySelector('annotation')?.textContent || s.textContent))));
  $$('figure.link-to-page').forEach(f => {
    const a = f.querySelector('a'), t = a && linkTarget(a.getAttribute('href'));
    swap(f, t ? make('div', { 'data-subpage': '', 'data-id': t.id, 'data-title': t.title, 'data-emoji': t.emoji || '📄' }) : make('p', {}, esc(f.textContent.trim())));
  });
  $$('figure').forEach(f => {
    if (!f.parentNode) return;
    const cap = f.querySelector('figcaption')?.textContent?.trim() || '';
    const bm = f.querySelector('a.bookmark');
    if (bm) { swap(f, bookmark(bm.getAttribute('href'), bm.querySelector('.bookmark-title')?.textContent?.trim(), bm.querySelector('.bookmark-description')?.textContent?.trim())); return; }
    const img = f.querySelector('img');
    if (img) { const b = imgFrom(img.getAttribute('src'), cap); if (b) swap(f, b); else f.remove(); return; }
    const media = f.querySelector('video[src], audio[src], source[src]');
    const a = f.querySelector('.source a, a[href]');
    const href = media?.getAttribute('src') || a?.getAttribute('href');
    const p = href && local(href);
    if (p) { swap(f, fileBlock(asset({ path: p, name: baseName(p), image: isImagePath(p) }), baseName(p))); return; }
    if (href && /^https?:/i.test(href)) { note('embed'); swap(f, bookmark(href, cap || a?.textContent?.trim())); return; }
    if (f.textContent.trim()) swap(f, make('p', {}, esc(f.textContent.trim()))); else f.remove();
  });
  $$('iframe[src]').forEach(f => { note('embed'); swap(f, bookmark(f.getAttribute('src'))); });
  $$('video[src], audio[src]').forEach(v => {
    const p = local(v.getAttribute('src'));
    swap(v, p ? fileBlock(asset({ path: p, name: baseName(p), image: false }), baseName(p)) : bookmark(v.getAttribute('src')));
  });

  // Loose images (Markdown, plain HTML): a paragraph holding just an image becomes the image block.
  $$('img').forEach(img => {
    const block = imgFrom(img.getAttribute('src'), img.getAttribute('alt') || '');
    const holder = img.closest('p, h1, h2, h3, li, td, th');
    if (!block) { img.remove(); return; }
    if (holder && holder.tagName === 'P' && holder.textContent.trim() === '' && holder.querySelectorAll('img').length === 1) swap(holder, block);
    else if (holder && !/^(TD|TH)$/.test(holder.tagName)) { holder.parentNode.insertBefore(block, holder.nextSibling); img.remove(); }
    else img.remove();
  });

  // A paragraph that is only a link to another imported page or to a file in the export.
  $$('p').forEach(p => {
    const kids = [...p.childNodes].filter(n => n.nodeType !== 3 || n.textContent.trim());
    if (kids.length !== 1 || kids[0].tagName !== 'A') return;
    const href = kids[0].getAttribute('href');
    const t = linkTarget(href);
    if (t) { swap(p, make('div', { 'data-subpage': '', 'data-id': t.id, 'data-title': t.title, 'data-emoji': t.emoji || '📄' })); return; }
    const f = local(href);
    if (f && !pageExt(f)) swap(p, isImagePath(f) ? imageBlock(asset({ path: f, name: baseName(f), image: true }), baseName(f), '') : fileBlock(asset({ path: f, name: baseName(f), image: false }), baseName(f)));
  });

  // Tables: cells hold plain paragraphs in the editor.
  $$('table').forEach(t => {
    if (t.classList.contains('collection-content')) note('database');
    const rows = [...t.querySelectorAll('tr')].map(tr => [...tr.children].filter(c => /^(TD|TH)$/.test(c.tagName)));
    const width = Math.max(1, ...rows.map(r => r.length));
    const out = make('table');
    const body = make('tbody');
    rows.forEach((cells, i) => {
      const tr = make('tr');
      for (let k = 0; k < width; k++) {
        const c = cells[k];
        const head = i === 0 && (c?.tagName === 'TH' || t.querySelector('thead')?.contains(c));
        const txt = c ? c.textContent.replace(/\s+/g, ' ').trim() : '';
        tr.appendChild(make(head ? 'th' : 'td', {}, `<p>${esc(txt)}</p>`));
      }
      body.appendChild(tr);
    });
    out.appendChild(body);
    if (rows.length) swap(t, out); else t.remove();
  });

  // Inline colours and mentions.
  $$('mark, span[class*="highlight-"]').forEach(m => {
    const c = notionColor(m.className);
    if (!c) { unwrap(m); return; }
    swap(m, make('span', { style: c.bg ? `background-color: var(--nb-${c.color})` : `color: var(--nt-${c.color})` }, m.innerHTML));
  });
  $$('span.user, span.notion-user, time').forEach(s => swap(s, doc.createTextNode(s.textContent)));

  // Links: other imported pages open in iPop, anything relative that isn't in the export is dropped.
  $$('a[href]').forEach(a => {
    const href = a.getAttribute('href');
    if (/^(https?:|mailto:|tel:)/i.test(href)) return;
    const t = linkTarget(href);
    if (t) a.setAttribute('href', `/notes/${t.id}`); else unwrap(a);
  });

  // Block background colours.
  $$('[class*="block-color-"]').forEach(e => {
    const c = notionColor(e.className);
    if (c?.bg && COLORS.includes(c.color) && /^(P|H1|H2|H3|BLOCKQUOTE|UL|OL|DETAILS|PRE)$/.test(e.tagName)) e.setAttribute('data-bg', c.color);
  });
  $$('h4, h5, h6').forEach(h => swap(h, make('h3', {}, h.innerHTML)));

  // Notion puts each list item in its own <ol>/<ul>; join neighbours back into one list.
  $$('ol.numbered-list, ul.bulleted-list').forEach(list => {
    if (!list.parentNode) return;
    let next = list.nextElementSibling;
    while (next && next.tagName === list.tagName && next.className === list.className && !textBetween(list, next)) {
      [...next.children].forEach(li => list.appendChild(li));
      const gone = next; next = next.nextElementSibling; gone.remove();
    }
  });

  // Notion text keeps line breaks as "\n" (white-space: pre-wrap).
  const walker = doc.createTreeWalker(root, 4);
  const texts = [];
  while (walker.nextNode()) texts.push(walker.currentNode);
  for (const t of texts) {
    if (!t.textContent.includes('\n') || !t.textContent.trim() || t.parentElement?.closest('pre, code')) continue;
    let s = t.textContent;
    if (!t.previousSibling) s = s.replace(/^\n+/, '');
    if (!t.nextSibling) s = s.replace(/\n+$/, '');
    const frag = doc.createDocumentFragment();
    s.split('\n').forEach((part, i) => { if (i) frag.appendChild(make('br')); if (part) frag.appendChild(doc.createTextNode(part)); });
    t.parentNode.replaceChild(frag, t);
  }
  $$('input').forEach(e => e.remove());

  let json = generateJSON(root.innerHTML, ctx.extensions);
  if (page.coverImage) {
    const ref = asset({ path: page.coverImage, name: baseName(page.coverImage), image: true });
    json = { ...json, content: [{ type: 'noteImage', attrs: { id: ref, name: baseName(page.coverImage), size: 0, width: 100, align: 'center', caption: '' } }, ...(json.content || [])] };
  }
  if (!json.content?.length) json = { type: 'doc', content: [{ type: 'paragraph' }] };
  return { json, assets, notes };
}

function textBetween(a, b) {
  for (let n = a.nextSibling; n && n !== b; n = n.nextSibling) if (n.nodeType !== 3 || n.textContent.trim()) return true;
  return false;
}

/// Markdown → HTML, with Notion's Markdown quirks: the page title as the first heading and callouts as
/// <aside> blocks whose inside is still Markdown.
export function markdownToHtml(md, title) {
  let text = md.replace(/\r\n?/g, '\n');
  const first = /^\s*#\s+(.+)\n?/.exec(text);
  if (first && first[1].trim() === title) text = text.slice(first[0].length);
  text = text.replace(/<aside>\s*([\s\S]*?)<\/aside>/g, (_, inner) => {
    const m = /^(\p{Extended_Pictographic}️?)\s*/u.exec(inner.trim());
    const body = marked.parse(m ? inner.trim().slice(m[0].length) : inner.trim(), { gfm: true }).replace(/\n/g, ' ');
    return `<div data-callout="" data-emoji="${esc(m?.[1] || '💡')}" data-color="gray">${body}</div>\n\n`;
  });
  return marked.parse(text, { gfm: true });
}

/// Puts uploaded attachment ids into the placeholders; placeholders whose upload failed are dropped
/// (an empty paragraph stands in so containers stay valid).
export function fillAssets(json, uploaded) {
  const fix = node => {
    if (!node.content) return node;
    const content = [];
    for (const c of node.content) {
      if ((c.type === 'noteImage' || c.type === 'noteFile') && String(c.attrs?.id || '').startsWith('asset:')) {
        const u = uploaded.get(c.attrs.id);
        if (!u) { content.push({ type: 'paragraph' }); continue; }
        const type = c.type === 'noteImage' && !u.image ? 'noteFile' : c.type;
        content.push({ ...c, type, attrs: type === 'noteFile' ? { id: u.id, name: u.name, size: u.size } : { ...c.attrs, id: u.id, name: c.attrs.name || u.name, size: u.size } });
      } else content.push(fix(c));
    }
    return { ...node, content };
  };
  return fix(json);
}

/// Editor JSON → the base64 Yjs state the snapshot endpoint stores, plus the search text and to-do counts.
export function docToState(json, extensions) {
  const schema = getSchema(extensions);
  const node = schema.nodeFromJSON(json);
  node.check();
  const ydoc = prosemirrorJSONToYDoc(schema, json, FIELD);
  const state = b64(Y.encodeStateAsUpdate(ydoc));
  ydoc.destroy();
  let done = 0, total = 0;
  node.descendants(n => { if (n.type.name === 'taskItem') { total++; if (n.attrs.checked) done++; } });
  return { state, plainText: node.textBetween(0, node.content.size, '\n', leaf => leaf.type.name === 'mention' ? `@${leaf.attrs.label ?? ''}` : ''), todoDone: done, todoTotal: total };
}

export function stateToDoc(doc) {
  const y = new Y.Doc();
  if (doc.state) Y.applyUpdate(y, unb64(doc.state));
  for (const u of doc.updates || []) Y.applyUpdate(y, unb64(u.data));
  const json = yXmlFragmentToProsemirrorJSON(y.getXmlFragment(FIELD));
  y.destroy();
  return json;
}

// ── Export ───────────────────────────────────────────────────────────────────

export const TEXT_HEX = { gray: '#8E8E93', brown: '#A2845E', orange: '#C93400', yellow: '#B25E00', green: '#248A3D', teal: '#0E7C8B', blue: '#0A64D6', purple: '#8944AB', pink: '#D30F45', red: '#D70015' };
export const BG_HEX = { gray: '#EFEFF0', brown: '#F3EDE6', orange: '#FFEFD9', yellow: '#FFF6CC', green: '#E1F6E6', teal: '#DFF2F6', blue: '#DCEBFF', purple: '#F3E5F9', pink: '#FFE0E8', red: '#FFE1DF' };
const colorKey = v => /--n[tb]-([a-z]+)/.exec(v || '')?.[1] ?? null;

export function safeName(s) {
  return (String(s || '').replace(/[\\/:*?"<>|\u0000-\u001f]/g, '-').replace(/\s+/g, ' ').trim().replace(/[. ]+$/, '') || 'Untitled').slice(0, 80);
}

/// Every attachment id a page refers to (images, files, gallery items).
export function attachmentIds(json) {
  const ids = [];
  const walk = n => {
    if ((n.type === 'noteImage' || n.type === 'noteFile') && n.attrs?.id) ids.push({ id: n.attrs.id, name: n.attrs.name || 'file', image: n.type === 'noteImage' });
    if (n.type === 'noteGallery') { try { JSON.parse(n.attrs?.items || '[]').forEach(i => i?.id && ids.push({ id: i.id, name: i.name || 'image', image: true })); } catch { /* ignore */ } }
    (n.content || []).forEach(walk);
  };
  walk(json);
  return ids;
}

const relPath = (fromDir, to) => {
  const a = fromDir ? fromDir.split('/') : [], b = to.split('/');
  let i = 0;
  while (i < a.length && i < b.length - 1 && a[i] === b[i]) i++;
  return [...a.slice(i).map(() => '..'), ...b.slice(i)].map(encodeURIComponent).join('/');
};

/// ctx: { dir, link(pageId) → path | null, file(attachmentId) → { path?, dataUrl? } | null, origin }
function inlineMd(nodes, ctx) {
  return (nodes || []).map(n => {
    if (n.type === 'hardBreak') return '<br>';
    if (n.type === 'mention') return `@${n.attrs?.label ?? ''}`;
    if (n.type !== 'text') return '';
    let t = n.text.replace(/([\\`*_[\]<>])/g, '\\$1');
    const marks = n.marks || [];
    if (marks.some(m => m.type === 'code')) t = '`' + n.text.replace(/`/g, 'ˋ') + '`';
    if (marks.some(m => m.type === 'bold')) t = `**${t}**`;
    if (marks.some(m => m.type === 'italic')) t = `_${t}_`;
    if (marks.some(m => m.type === 'strike')) t = `~~${t}~~`;
    const link = marks.find(m => m.type === 'link')?.attrs?.href;
    if (link) t = `[${t}](${mdHref(link, ctx)})`;
    return t;
  }).join('');
}
function mdHref(href, ctx) {
  const m = /^\/notes\/([0-9a-f-]{36})/i.exec(href || '');
  if (m) { const p = ctx.link(m[1]); return p ? relPath(ctx.dir, p) : `${ctx.origin}${href}`; }
  return href;
}
const indent = (s, pad) => s.split('\n').map(l => (l ? pad + l : l)).join('\n');

export function docToMarkdown(json, ctx) {
  const block = (n) => {
    const kids = () => (n.content || []).map(block).filter(s => s != null).join('\n\n');
    switch (n.type) {
      case 'paragraph': return inlineMd(n.content, ctx);
      case 'heading': return `${'#'.repeat(n.attrs?.level || 1)} ${inlineMd(n.content, ctx)}`;
      case 'bulletList': case 'orderedList': case 'taskList':
        return (n.content || []).map((li, i) => {
          const marker = n.type === 'orderedList' ? `${(n.attrs?.start ?? 1) + i}.` : n.type === 'taskList' ? `- [${li.attrs?.checked ? 'x' : ' '}]` : '-';
          const [first, ...rest] = li.content || [];
          const head = first?.type === 'paragraph' ? inlineMd(first.content, ctx) : block(first || { type: 'paragraph' });
          const pad = ' '.repeat(n.type === 'orderedList' ? marker.length + 1 : 2);
          const tail = rest.map(block).filter(Boolean).map(s => indent(s, pad)).join('\n');
          return `${marker} ${head}${tail ? '\n' + tail : ''}`;
        }).join('\n');
      case 'blockquote': return kids().split('\n').map(l => `> ${l}`).join('\n');
      case 'codeBlock': return '```' + (n.attrs?.language || '') + '\n' + (n.content || []).map(t => t.text).join('') + '\n```';
      case 'horizontalRule': return '---';
      case 'details': {
        const sum = n.content?.find(c => c.type === 'detailsSummary');
        const body = n.content?.find(c => c.type === 'detailsContent');
        return `<details>\n<summary>${esc((sum?.content || []).map(t => t.text || '').join(''))}</summary>\n\n${body ? (body.content || []).map(block).join('\n\n') : ''}\n\n</details>`;
      }
      case 'callout': return `<aside>\n${n.attrs?.emoji || '💡'} ${kids()}\n</aside>`;
      case 'table': {
        const rows = (n.content || []).map(r => (r.content || []).map(c => (c.content || []).map(p => inlineMd(p.content, ctx)).join(' ').replace(/\|/g, '\\|') || ' '));
        if (!rows.length) return '';
        const w = Math.max(...rows.map(r => r.length));
        const line = r => `| ${Array.from({ length: w }, (_, i) => r[i] ?? ' ').join(' | ')} |`;
        return [line(rows[0]), `|${' --- |'.repeat(w)}`, ...rows.slice(1).map(line)].join('\n');
      }
      case 'noteImage': { const f = ctx.file(n.attrs?.id); return f ? `![${(n.attrs?.caption || n.attrs?.name || '').replace(/[[\]]/g, '')}](${f.dataUrl || relPath(ctx.dir, f.path)})` : `_[image: ${n.attrs?.name || ''}]_`; }
      case 'noteGallery': { let items = []; try { items = JSON.parse(n.attrs?.items || '[]'); } catch { /* ignore */ } return items.map(i => block({ type: 'noteImage', attrs: i })).join('\n\n'); }
      case 'noteFile': { const f = ctx.file(n.attrs?.id); return f ? `[${n.attrs?.name || 'file'}](${f.dataUrl || relPath(ctx.dir, f.path)})` : `_[file: ${n.attrs?.name || ''}]_`; }
      case 'noteBookmark': return `[${(n.attrs?.title || n.attrs?.url || '').replace(/[[\]]/g, '')}](${n.attrs?.url || ''})`;
      case 'noteEmbed': return `[${n.attrs?.url || ''}](${n.attrs?.url || ''})`;
      case 'subPage': { const p = ctx.link(n.attrs?.id); const t = `${n.attrs?.emoji ? n.attrs.emoji + ' ' : ''}${ctx.title?.(n.attrs?.id) || n.attrs?.title || 'Untitled'}`; return p ? `[${t}](${relPath(ctx.dir, p)})` : t; }
      case 'noteWidget': return null;
      default: return kids();
    }
  };
  return (json.content || []).map(block).filter(s => s != null).join('\n\n').replace(/\n{3,}/g, '\n\n') + '\n';
}

function inlineHtml(nodes, ctx) {
  return (nodes || []).map(n => {
    if (n.type === 'hardBreak') return '<br>';
    if (n.type === 'mention') return `<span class="mention">@${esc(n.attrs?.label ?? '')}</span>`;
    if (n.type !== 'text') return '';
    let t = esc(n.text);
    for (const m of n.marks || []) {
      if (m.type === 'bold') t = `<strong>${t}</strong>`;
      else if (m.type === 'italic') t = `<em>${t}</em>`;
      else if (m.type === 'strike') t = `<s>${t}</s>`;
      else if (m.type === 'underline') t = `<u>${t}</u>`;
      else if (m.type === 'code') t = `<code>${t}</code>`;
      else if (m.type === 'link') t = `<a href="${esc(mdHref(m.attrs?.href, ctx))}">${t}</a>`;
      else if (m.type === 'textStyle') {
        const c = colorKey(m.attrs?.color), b = colorKey(m.attrs?.backgroundColor);
        const st = [c && TEXT_HEX[c] ? `color:${TEXT_HEX[c]}` : '', b && BG_HEX[b] ? `background:${BG_HEX[b]}` : ''].filter(Boolean).join(';');
        if (st) t = `<span style="${st}">${t}</span>`;
      }
    }
    return t;
  }).join('');
}

export function docToHtml(json, ctx) {
  const bg = n => n.attrs?.bg && BG_HEX[n.attrs.bg] ? ` style="background:${BG_HEX[n.attrs.bg]}"` : '';
  const block = n => {
    const kids = () => (n.content || []).map(block).join('');
    switch (n.type) {
      case 'paragraph': return `<p${bg(n)}>${inlineHtml(n.content, ctx) || '<br>'}</p>`;
      case 'heading': return `<h${n.attrs?.level || 1}${bg(n)}>${inlineHtml(n.content, ctx)}</h${n.attrs?.level || 1}>`;
      case 'bulletList': return `<ul${bg(n)}>${kids()}</ul>`;
      case 'orderedList': return `<ol start="${n.attrs?.start ?? 1}"${bg(n)}>${kids()}</ol>`;
      case 'listItem': return `<li>${kids()}</li>`;
      case 'taskList': return `<ul class="todo"${bg(n)}>${kids()}</ul>`;
      case 'taskItem': return `<li class="${n.attrs?.checked ? 'done' : ''}"><span class="box">${n.attrs?.checked ? '✓' : ''}</span><div>${kids()}</div></li>`;
      case 'blockquote': return `<blockquote${bg(n)}>${kids()}</blockquote>`;
      case 'codeBlock': return `<pre${bg(n)}><code>${esc((n.content || []).map(t => t.text).join(''))}</code></pre>`;
      case 'horizontalRule': return '<hr>';
      case 'details': return `<details open${bg(n)}>${kids()}</details>`;
      case 'detailsSummary': return `<summary>${inlineHtml(n.content, ctx)}</summary>`;
      case 'detailsContent': return `<div class="toggle-body">${kids()}</div>`;
      case 'callout': return `<div class="callout" style="background:${BG_HEX[n.attrs?.color] || BG_HEX.yellow}"><span class="ce">${esc(n.attrs?.emoji || '💡')}</span><div>${kids()}</div></div>`;
      case 'table': return `<table>${kids()}</table>`;
      case 'tableRow': return `<tr>${kids()}</tr>`;
      case 'tableHeader': return `<th>${(n.content || []).map(p => inlineHtml(p.content, ctx)).join('<br>')}</th>`;
      case 'tableCell': return `<td>${(n.content || []).map(p => inlineHtml(p.content, ctx)).join('<br>')}</td>`;
      case 'noteImage': {
        const f = ctx.file(n.attrs?.id), w = Math.min(100, Math.max(15, +n.attrs?.width || 100));
        const src = f ? (f.dataUrl || relPath(ctx.dir, f.path)) : '';
        return `<figure style="text-align:${n.attrs?.align || 'center'}">${src ? `<img src="${esc(src)}" alt="${esc(n.attrs?.caption || n.attrs?.name || '')}" style="width:${w}%">` : `<div class="missing">${esc(n.attrs?.name || 'image')}</div>`}${n.attrs?.caption ? `<figcaption>${esc(n.attrs.caption)}</figcaption>` : ''}</figure>`;
      }
      case 'noteGallery': { let items = []; try { items = JSON.parse(n.attrs?.items || '[]'); } catch { /* ignore */ } return `<div class="gallery">${items.map(i => block({ type: 'noteImage', attrs: i })).join('')}</div>`; }
      case 'noteFile': { const f = ctx.file(n.attrs?.id); const href = f ? (f.dataUrl || relPath(ctx.dir, f.path)) : ''; return `<p class="file">📎 ${href ? `<a href="${esc(href)}">${esc(n.attrs?.name || 'file')}</a>` : esc(n.attrs?.name || 'file')}</p>`; }
      case 'noteBookmark': return `<a class="bookmark" href="${esc(n.attrs?.url || '')}"><b>${esc(n.attrs?.title || n.attrs?.url || '')}</b>${n.attrs?.desc ? `<span>${esc(n.attrs.desc)}</span>` : ''}<small>${esc(n.attrs?.url || '')}</small></a>`;
      case 'noteEmbed': return `<p><a href="${esc(n.attrs?.url || '')}">${esc(n.attrs?.url || '')}</a></p>`;
      case 'subPage': { const p = ctx.link(n.attrs?.id); const t = `${esc(n.attrs?.emoji || '📄')} ${esc(ctx.title?.(n.attrs?.id) || n.attrs?.title || 'Untitled')}`; return p ? `<p class="subpage"><a href="${esc(relPath(ctx.dir, p))}">${t}</a></p>` : `<p class="subpage">${t}</p>`; }
      case 'noteWidget': return '';
      default: return kids();
    }
  };
  return (json.content || []).map(block).join('\n');
}

export const HTML_CSS = `body{font:15px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",system-ui,sans-serif;color:#1d1d1f;max-width:820px;margin:40px auto;padding:0 24px}
h1.title{font-size:34px;line-height:1.2;margin:0 0 4px}.emoji{font-size:44px;line-height:1}.meta{color:#86868b;font-size:13px;margin:0 0 24px}
h1{font-size:26px}h2{font-size:21px}h3{font-size:18px}h1,h2,h3{margin:1.3em 0 .3em}p{margin:.25em 0}
a{color:#0a64d6}code{background:#f2f2f4;border-radius:4px;padding:1px 4px;font-size:.9em}pre{background:#f5f5f7;border-radius:8px;padding:12px 14px;overflow:auto}
pre code{background:none;padding:0}blockquote{border-left:3px solid #d2d2d7;margin:.5em 0;padding:0 0 0 14px;color:#424245}
.callout{display:flex;gap:10px;border-radius:10px;padding:12px 14px;margin:.6em 0}.callout .ce{font-size:18px}
ul.todo{list-style:none;padding-left:4px}ul.todo li{display:flex;gap:8px}ul.todo .box{flex:none;width:16px;height:16px;margin-top:4px;border:1.5px solid #c7c7cc;border-radius:4px;font-size:11px;line-height:14px;text-align:center;color:#fff}
ul.todo li.done>.box{background:#34a853;border-color:#34a853}ul.todo li.done>div>p:first-child{text-decoration:line-through;color:#86868b}
details{margin:.4em 0}summary{cursor:pointer;font-weight:500}.toggle-body{padding-left:20px}
table{border-collapse:collapse;margin:.6em 0;width:100%}th,td{border:1px solid #d2d2d7;padding:6px 10px;text-align:left;vertical-align:top}th{background:#f5f5f7}
figure{margin:.8em 0}figure img{max-width:100%;border-radius:8px}figcaption{font-size:13px;color:#86868b;text-align:center}
.gallery{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:6px}.gallery figure{margin:0}
.bookmark{display:block;border:1px solid #d2d2d7;border-radius:10px;padding:10px 14px;margin:.6em 0;color:inherit;text-decoration:none}.bookmark span,.bookmark small{display:block;color:#86868b;font-size:13px}
.subpage a,.file a{font-weight:500}.mention{color:#0a64d6}.missing{color:#86868b;font-style:italic}hr{border:0;border-top:1px solid #d2d2d7;margin:1.2em 0}
.page+.page{break-before:page;margin-top:48px}@media print{body{margin:0 auto}a{color:inherit}}`;

export function htmlDocument(title, bodyHtml) {
  return `<!doctype html>\n<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${esc(title)}</title><style>${HTML_CSS}</style></head><body>\n${bodyHtml}\n</body></html>\n`;
}
export function pageHtml(meta, json, ctx) {
  return `<article class="page">${meta.emoji ? `<div class="emoji">${esc(meta.emoji)}</div>` : ''}<h1 class="title">${esc(meta.title || 'Untitled')}</h1>${meta.subtitle ? `<p class="meta">${esc(meta.subtitle)}</p>` : ''}\n${docToHtml(json, ctx)}</article>`;
}

/// Lays out the exported files: page P in folder D is D/P.md, its attachments go to D/P/attachments/ and
/// its sub-pages to D/P/. Names are made unique among siblings.
export function layoutExport(pages, rootIds, ext) {
  const byParent = new Map();
  const ids = new Set(pages.map(p => p.id));
  for (const p of pages) {
    const k = p.parentId && ids.has(p.parentId) && !rootIds.includes(p.id) ? p.parentId : '';
    if (!byParent.has(k)) byParent.set(k, []);
    byParent.get(k).push(p);
  }
  const out = new Map();
  const place = (list, dir) => {
    const used = new Set();
    for (const p of list) {
      let n = safeName(p.title), k = 2;
      while (used.has(n.toLowerCase())) n = `${safeName(p.title)} (${k++})`;
      used.add(n.toLowerCase());
      const folder = dir ? `${dir}/${n}` : n;
      out.set(p.id, { file: `${folder}.${ext}`, dir, folder, title: p.title, emoji: p.emoji });
      place(byParent.get(p.id) || [], folder);
    }
  };
  place(rootIds.map(id => pages.find(p => p.id === id)).filter(Boolean), '');
  return out;
}

// ── Browser side: import runner ──────────────────────────────────────────────

const yieldUi = () => new Promise(r => setTimeout(r, 0));

/// Import flow for the Blazor dialog. api(path, init) does authenticated fetches; net receives
/// OnImportFileChosen(name) / OnImportProgress(progress).
export function importer({ api, net, extensions }) {
  let file = null, plan = null, stopRequested = false, running = false;
  const call = (m, ...a) => net.invokeMethodAsync(m, ...a).catch(() => {});
  const choose = f => { if (!f || running) return; file = f; plan = null; call('OnImportFileChosen', f.name); };
  const pick = () => {
    const i = document.createElement('input');
    i.type = 'file'; i.accept = '.zip,.md,.markdown,.html,.htm';
    i.onchange = () => choose(i.files?.[0]);
    i.click();
  };
  const bound = [];
  const bindDrop = (el, opts = {}) => {
    if (!el) return;
    const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
    const over = e => { if (!hasFiles(e)) return; e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; el.classList.add('nt-x-over'); };
    const leave = e => { if (!e.relatedTarget || !el.contains(e.relatedTarget)) el.classList.remove('nt-x-over'); };
    const drop = e => {
      el.classList.remove('nt-x-over');
      if (!hasFiles(e)) return;
      const f = e.dataTransfer.files?.[0];
      if (opts.onlyImportable && !/\.(zip|md|markdown|html?)$/i.test(f?.name || '')) return;
      e.preventDefault(); e.stopPropagation();
      choose(f);
    };
    el.addEventListener('dragover', over); el.addEventListener('dragleave', leave); el.addEventListener('drop', drop);
    bound.push(() => { el.removeEventListener('dragover', over); el.removeEventListener('dragleave', leave); el.removeEventListener('drop', drop); });
  };

  async function analyze() {
    if (!file) return { error: 'Choose a file first.' };
    try {
      if (file.size > LIMITS.bytes) throw new ImportError('too_big', 'The file is larger than 100 MB.');
      const bytes = new Uint8Array(await file.arrayBuffer());
      plan = planImport(file.name, bytes);
      let previous = [];
      try {
        const r = await api('/api/notes/imported', { method: 'POST', json: { keys: plan.pages.map(p => p.importKey) } });
        if (r.ok) previous = await r.json();
      } catch { /* the warning is optional */ }
      return { fileName: file.name, size: file.size, source: plan.source, pages: plan.pages.length, attachments: plan.attachments, title: plan.title, previous };
    } catch (e) {
      plan = null;
      return { fileName: file.name, size: file.size, error: e instanceof ImportError ? e.message : "This file couldn't be read." };
    }
  }

  async function run(parentId) {
    if (!plan || running) return null;
    running = true; stopRequested = false;
    const rows = plan.pages.map(p => ({ key: p.key, title: p.title, emoji: p.emoji, depth: p.depth, status: 'wait', detail: '' }));
    let filesDone = 0;
    const filesTotal = plan.attachments;
    let last = 0;
    const progress = (force) => {
      const now = Date.now();
      if (!force && now - last < 120) return;
      last = now;
      call('OnImportProgress', { done: rows.filter(r => r.status === 'ok' || r.status === 'fail').length, total: rows.length, filesDone, filesTotal, rows });
    };
    const result = { pages: 0, attachments: 0, failed: [], converted: [], covers: [], rootId: null, rootTitle: plan.pages[0].title, stopped: false, source: plan.source };
    const converted = new Map();
    const pageOfPath = new Map();
    try {
      const r = await api('/api/notes/import', { method: 'POST', json: {
        parentId: parentId || null, source: plan.source,
        pages: plan.pages.map(p => ({ key: p.key, parentKey: p.parentKey, title: p.title, emoji: p.emoji, cover: p.cover, importKey: p.importKey })),
      } });
      if (!r.ok) { const e = await r.json().catch(() => ({})); throw new Error(e.message || 'create_failed'); }
      const ids = new Map((await r.json()).pages.map(x => [x.key, x.id]));
      plan.pages.forEach(p => { if (p.path) pageOfPath.set(p.path, { id: ids.get(p.key), title: p.title, emoji: p.emoji }); });
      result.rootId = ids.get(plan.pages[0].key);
      const gradients = plan.pages.filter(p => p.cover).length, photos = plan.pages.filter(p => p.coverImage).length;
      if (gradients) result.covers.push(`${gradients} Notion cover${gradients > 1 ? 's' : ''} → nearest iPop cover`);
      if (photos) result.covers.push(`${photos} photo cover${photos > 1 ? 's' : ''} → image at the top of the page`);
      progress(true);

      for (let i = 0; i < plan.pages.length; i++) {
        const p = plan.pages[i], row = rows[i], id = ids.get(p.key);
        if (stopRequested) break;
        row.status = 'run'; progress(true);
        try {
          await yieldUi();
          const childrenOf = key => plan.pages.filter(c => c.parentKey === key).map(c => ({ id: ids.get(c.key), title: c.title, emoji: c.emoji }));
          const { json, assets, notes } = pageToDoc(p, { files: plan.files, extensions, pageOf: path => pageOfPath.get(path) ?? null, childrenOf });
          for (const [label, n] of notes) { const c = converted.get(label) || { count: 0, pages: [] }; c.count += n; if (!c.pages.includes(p.title)) c.pages.push(p.title); converted.set(label, c); }
          const uploaded = new Map();
          let k = 0;
          for (const a of assets) {
            if (stopRequested) break;
            row.detail = `Uploading ${++k} / ${assets.length}…`; progress();
            const bytes = a.dataUrl ? unb64(a.dataUrl.split(',')[1] || '') : plan.files.get(a.path);
            const type = a.dataUrl ? (/^data:([^;,]+)/.exec(a.dataUrl)?.[1] || 'application/octet-stream') : (MIME[extOf(a.path)] || 'application/octet-stream');
            const form = new FormData();
            form.append('file', new Blob([bytes], { type }), a.name);
            try {
              const u = await api(`/api/notes/${id}/uploads`, { method: 'POST', body: form });
              if (!u.ok) {
                const e = await u.json().catch(() => ({}));
                result.failed.push({ title: p.title, reason: `${a.name}: ${e.message || "couldn't be uploaded"}. The rest of the page was imported without it.` });
                continue;
              }
              const d = await u.json();
              uploaded.set(a.ref, { id: d.id, name: d.fileName, size: d.size, image: String(d.contentType || '').startsWith('image/') });
              filesDone++; result.attachments++;
            } catch {
              result.failed.push({ title: p.title, reason: `${a.name} couldn't be uploaded. The rest of the page was imported without it.` });
            }
          }
          const s = docToState(fillAssets(json, uploaded), extensions);
          const w = await api(`/api/notes/${id}/snapshot`, { method: 'POST', json: { ...s, baseVersion: 0, mergedUpdateIds: [] } });
          if (!w.ok) throw new Error(w.status === 409 ? 'Someone edited the page while it was being imported.' : `The server refused the page (${w.status}).`);
          row.status = 'ok';
          row.detail = uploaded.size ? `${uploaded.size} file${uploaded.size > 1 ? 's' : ''}` : '';
          result.pages++;
        } catch (e) {
          row.status = 'fail'; row.detail = 'Failed';
          result.failed.push({ title: p.title, reason: e?.message || "The page couldn't be converted." });
        }
        progress(true);
      }

      if (stopRequested) {
        // Keep what's done; pages that never got their content go away (with their sub-pages).
        result.stopped = true;
        const done = new Set(plan.pages.filter((p, i) => rows[i].status === 'ok' || rows[i].status === 'fail').map(p => p.key));
        for (const p of plan.pages) {
          if (done.has(p.key) || (p.parentKey && !done.has(p.parentKey))) continue;
          const id = ids.get(p.key);
          await api(`/api/notes/${id}`, { method: 'DELETE' }).catch(() => {});
          await api(`/api/notes/${id}/purge`, { method: 'DELETE' }).catch(() => {});
        }
        if (!done.has(plan.pages[0].key)) result.rootId = null;
      }
    } catch (e) {
      result.failed.push({ title: plan.title, reason: e?.message === 'create_failed' ? "The pages couldn't be created." : e?.message || 'Import failed.' });
    } finally {
      running = false;
    }
    result.converted = [...converted].map(([label, c]) => ({ label, count: c.count, pages: c.pages.slice(0, 4) }));
    progress(true);
    return result;
  }

  return {
    pick, choose, bindDrop, analyze, run,
    stop() { stopRequested = true; },
    reset() { if (!running) { file = null; plan = null; } },
    dispose() { bound.splice(0).forEach(f => f()); },
  };
}

// ── Browser side: export ─────────────────────────────────────────────────────

function download(blob, name) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob); a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 60000);
}
const blobToDataUrl = blob => new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(r.result); r.onerror = rej; r.readAsDataURL(blob); });

/// opts: { pages: [{ id, parentId, title, emoji }], rootIds, format: md|html|pdf, includeSub, includeFiles,
/// name, by, sharedRootIds? } — net gets OnExportProgress(done, total, filesDone, filesTotal).
export async function exportPages(api, net, opts) {
  const call = (m, ...a) => net?.invokeMethodAsync(m, ...a).catch(() => {});
  const all = opts.pages || [];
  const kids = new Map();
  all.forEach(p => { if (p.parentId) { if (!kids.has(p.parentId)) kids.set(p.parentId, []); kids.get(p.parentId).push(p); } });
  const chosen = [];
  const seen = new Set();
  const add = (p, depth) => {
    if (!p || seen.has(p.id)) return;
    seen.add(p.id); chosen.push({ ...p, depth });
    if (opts.includeSub) (kids.get(p.id) || []).slice().sort((a, b) => a.title.localeCompare(b.title)).forEach(k => add(k, depth + 1));
  };
  const roots = opts.rootIds.map(id => all.find(p => p.id === id)).filter(Boolean);
  roots.forEach(r => add(r, 0));
  const shared = new Set(opts.sharedRootIds || []);

  // Content first (so the attachment total is known), then files.
  const docs = new Map();
  let done = 0;
  for (const p of chosen) {
    const r = await api(`/api/notes/${p.id}/doc`);
    docs.set(p.id, r.ok ? stateToDoc(await r.json()) : { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: "This page couldn't be exported." }] }] });
    call('OnExportProgress', ++done, chosen.length, 0, 0);
  }
  const wanted = opts.includeFiles || opts.format === 'pdf' ? chosen.flatMap(p => attachmentIds(docs.get(p.id)).map(a => ({ ...a, page: p.id }))) : [];
  const ext = opts.format === 'md' ? 'md' : 'html';
  const sharedPrefix = id => (shared.has(id) ? 'Shared with me/' : '');
  const layout = layoutExport(chosen, roots.map(r => r.id), ext);
  if (shared.size) for (const [id, l] of layout) {
    let top = chosen.find(p => p.id === id);
    while (top && top.depth > 0) top = chosen.find(p => p.id === top.parentId);
    const pre = top ? sharedPrefix(top.id) : '';
    if (pre) { l.file = pre + l.file; l.dir = l.dir ? pre + l.dir : pre.slice(0, -1); l.folder = pre + l.folder; }
  }

  const files = new Map();
  const zip = {};
  let filesDone = 0;
  for (const a of wanted) {
    if (files.has(a.id)) continue;
    try {
      const r = await api(`/api/attachments/${a.id}`);
      if (!r.ok) throw new Error(String(r.status));
      const blob = await r.blob();
      if (opts.format === 'pdf') files.set(a.id, { dataUrl: await blobToDataUrl(blob) });
      else {
        const folder = `${layout.get(a.page).folder}/attachments`;
        let n = safeName(a.name), k = 2;
        while (zip[`${folder}/${n}`]) n = safeName(a.name).replace(/(\.[^.]*)?$/, m => ` (${k++})${m}`);
        zip[`${folder}/${n}`] = new Uint8Array(await blob.arrayBuffer());
        files.set(a.id, { path: `${folder}/${n}` });
      }
    } catch { /* exported without it */ }
    call('OnExportProgress', chosen.length, chosen.length, ++filesDone, wanted.length);
  }

  const origin = location.origin;
  const titleOf = id => all.find(p => p.id === id)?.title;
  const ctxFor = id => ({ dir: layout.get(id).dir, link: pid => layout.get(pid)?.file ?? null, file: aid => files.get(aid) ?? null, origin, title: titleOf });
  const stamp = new Date().toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' });

  if (opts.format === 'pdf') {
    const body = chosen.map(p => pageHtml(p, docs.get(p.id), { ...ctxFor(p.id), link: () => null })).join('\n');
    printHtml(htmlDocument(roots[0]?.title || 'Notes', body));
    return { pages: chosen.length, files: filesDone };
  }

  for (const p of chosen) {
    const l = layout.get(p.id), json = docs.get(p.id);
    const text = ext === 'md'
      ? `# ${p.emoji ? p.emoji + ' ' : ''}${p.title || 'Untitled'}\n\n${docToMarkdown(json, ctxFor(p.id))}`
      : htmlDocument(p.title || 'Untitled', pageHtml(p, json, ctxFor(p.id)));
    zip[l.file] = strToU8(text);
  }
  const name = safeName(opts.name || roots[0]?.title || 'Notes');
  const keys = Object.keys(zip);
  if (keys.length === 1) {
    download(new Blob([zip[keys[0]]], { type: ext === 'md' ? 'text/markdown;charset=utf-8' : 'text/html;charset=utf-8' }), `${name}.${ext}`);
  } else {
    zip['README.txt'] = strToU8(`Exported from iPop Notes\n${stamp}${opts.by ? ` by ${opts.by}` : ''}\n${chosen.length} page(s), ${files.size} attachment(s).\n\nOpen the ${ext === 'md' ? 'Markdown' : 'HTML'} files in any editor or browser, or import this zip into Notion or iPop Notes.\n`);
    download(new Blob([zipSync(zip, { level: 6 })], { type: 'application/zip' }), `${name}.zip`);
  }
  return { pages: chosen.length, files: files.size };
}

/// Prints through a hidden frame, so no pop-up window is needed.
export function printHtml(html) {
  const f = document.createElement('iframe');
  f.setAttribute('aria-hidden', 'true');
  f.style.cssText = 'position:fixed;right:0;bottom:0;width:0;height:0;border:0;visibility:hidden';
  document.body.appendChild(f);
  const done = () => setTimeout(() => f.remove(), 1000);
  f.onload = () => {
    const w = f.contentWindow;
    const imgs = [...w.document.images].filter(i => !i.complete);
    Promise.all(imgs.map(i => new Promise(r => { i.onload = i.onerror = r; }))).then(() => {
      w.addEventListener('afterprint', done, { once: true });
      w.focus(); w.print();
      setTimeout(done, 120000);
    });
  };
  f.srcdoc = html;
}
