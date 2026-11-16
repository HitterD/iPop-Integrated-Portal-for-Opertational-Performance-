// The /draw block: a hand-drawn canvas inside a page, and the whole-page whiteboard. Both load the Excalidraw bundle
// (wwwroot/js/notes/draw/, built from tools/draw-editor) the first time one is shown; pages without a drawing never download it.
// The scene is a Y.Map named "draw:<id>" inside the page's own Yjs document, so drawing is real-time, saved with the page,
// part of version history, and merged after being offline.
import { Node } from '@tiptap/core';
import { el } from './nodes.js';

let bundle = null;
/// Loads the drawing bundle once. Resolves the module, or rejects with a readable error.
export function loadDrawBundle() {
  if (bundle) return bundle;
  const base = new URL('./draw/', import.meta.url);
  window.EXCALIDRAW_ASSET_PATH = base.href;
  if (!document.querySelector('link[data-ipop-draw-css]')) {
    for (const name of ['excalidraw.css', 'draw-theme.css']) {
      const l = document.createElement('link');
      l.rel = 'stylesheet'; l.href = new URL(name, base).href; l.dataset.ipopDrawCss = name;
      document.head.appendChild(l);
    }
  }
  bundle = import(/* @vite-ignore */ new URL('draw.js', base).href).catch(e => { bundle = null; throw e; });
  return bundle;
}

export const sceneName = id => `draw:${id}`;
const isDark = () => document.documentElement.dataset.theme === 'dark' || document.body.classList.contains('dark');
export const newDrawId = () => (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(16).slice(2));

/// Mounts a drawing into `container`. Returns { destroy, png, setReadOnly }.
/// Every drawing currently on screen, so a page lock can switch all of them to read-only at once.
const live = new Set();
const lockListeners = new Set();   // inline blocks re-read the editor's editable state when the page lock changes
export function setAllReadOnly(readOnly) { live.forEach(i => i.setReadOnly(readOnly)); lockListeners.forEach(f => f(readOnly)); }

export async function mountScene(container, { ydoc, id, readOnly, mode = 'block', onLinkOpen }) {
  const mod = await loadDrawBundle();
  const ymap = ydoc.getMap(sceneName(id));
  const inst = mod.mountDraw(container, { ymap, doc: ydoc, readOnly, theme: isDark() ? 'dark' : 'light', mode, onLinkOpen });
  const mo = new MutationObserver(() => inst.setTheme(isDark() ? 'dark' : 'light'));
  mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  const handle = { ...inst, destroy() { live.delete(handle); mo.disconnect(); inst.destroy(); } };
  live.add(handle);
  return handle;
}

export const NoteDraw = Node.create({
  name: 'noteDraw',
  group: 'block',
  atom: true,
  draggable: true,
  selectable: true,
  addAttributes() {
    return {
      id: { default: null, parseHTML: e => e.getAttribute('data-id') },
      height: { default: 320, parseHTML: e => Math.min(900, Math.max(180, +e.getAttribute('data-height') || 320)) },
    };
  },
  parseHTML() { return [{ tag: 'div[data-draw]' }]; },
  renderHTML({ node: { attrs: a } }) { return ['div', { 'data-draw': '', 'data-id': a.id, 'data-height': a.height }]; },
  addOptions() { return { host: null }; },
  addNodeView() {
    return ({ node, getPos, editor }) => {
      const host = this.options.host?.() ?? {};
      const editable = editor.isEditable;
      const dom = el('div', 'nt-draw');
      dom.contentEditable = 'false';
      dom.style.height = `${node.attrs.height}px`;
      const stage = el('div', 'nt-draw-stage');
      dom.append(stage);
      let inst = null, gone = false, current = node;
      const status = el('div', 'nt-draw-wait', 'Loading drawing…');
      stage.append(status);

      const bar = el('div', 'nt-draw-top');
      const mkBtn = (label, fn, title) => { const b = el('button', 'nt-draw-btn', label); b.type = 'button'; b.title = title || label; b.addEventListener('click', e => { e.preventDefault(); e.stopPropagation(); fn(); }); return b; };
      bar.append(
        mkBtn('Full screen', () => host.drawFullscreen?.(current.attrs.id), 'Open this drawing full screen'),
        mkBtn('PNG', async () => {
          try { const blob = await inst?.png({ dark: isDark() }); if (!blob) { host.toast?.('Nothing drawn yet.'); return; }
            const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'drawing.png'; a.click(); setTimeout(() => URL.revokeObjectURL(a.href), 4000); }
          catch { host.toast?.('Couldn’t make the PNG. Try again.'); }
        }, 'Download as PNG'),
      );
      dom.append(bar);

      if (editable) {
        const grip = el('div', 'nt-draw-grip');
        grip.title = 'Drag to resize';
        grip.addEventListener('pointerdown', e => {
          e.preventDefault(); grip.setPointerCapture(e.pointerId);
          const y0 = e.clientY, h0 = dom.clientHeight;
          const move = ev => { dom.style.height = `${Math.max(180, Math.min(900, h0 + ev.clientY - y0))}px`; };
          const up = () => {
            grip.removeEventListener('pointermove', move); grip.removeEventListener('pointerup', up);
            const p = getPos();
            if (typeof p === 'number') editor.view.dispatch(editor.state.tr.setNodeMarkup(p, undefined, { ...current.attrs, height: Math.round(dom.clientHeight) }));
          };
          grip.addEventListener('pointermove', move); grip.addEventListener('pointerup', up);
        });
        dom.append(grip);
      }

      // The block follows the page lock: no toolbar and no resize grip while locked.
      const syncLock = () => { const ro = !editor.isEditable; dom.classList.toggle('ro', ro); const g = dom.querySelector('.nt-draw-grip'); if (g) g.style.display = ro ? 'none' : ''; };
      lockListeners.add(syncLock);
      syncLock();

      const start = async () => {
        const ydoc = host.drawDoc?.();
        if (!ydoc || !current.attrs.id) { status.textContent = 'This drawing can’t be shown here.'; return; }
        try {
          inst = await mountScene(stage, { ydoc, id: current.attrs.id, readOnly: !editable, onLinkOpen: url => host.openLink?.(url) });
          if (gone) { inst.destroy(); inst = null; return; }
          status.remove();
          dom.classList.toggle('ro', !editable);
        } catch {
          status.textContent = 'Couldn’t load the drawing tools. Check your connection and reload the page.';
        }
      };
      // Only start when the block is near the screen: a long page with many drawings must not mount them all at once.
      const io = new IntersectionObserver(es => { if (es.some(e => e.isIntersecting)) { io.disconnect(); start(); } }, { rootMargin: '300px' });
      io.observe(dom);

      return {
        dom,
        update(n) { if (n.type.name !== 'noteDraw') return false; current = n; dom.style.height = `${n.attrs.height}px`; return true; },
        ignoreMutation: () => true,
        stopEvent: () => true,
        destroy() { gone = true; lockListeners.delete(syncLock); io.disconnect(); inst?.destroy(); inst = null; },
      };
    };
  },
});
