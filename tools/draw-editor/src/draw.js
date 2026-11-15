// Excalidraw mounted for iPop Notes. Loaded lazily (only pages that contain a drawing or are a whiteboard pay for it).
// The scene lives in a Y.Map owned by the page's Yjs document (see scene.js), so drawing is real-time, saved with the
// page, part of version history, and merges after being offline.
import React from 'react';
import { createRoot } from 'react-dom/client';
import { Excalidraw, exportToBlob, exportToSvg, convertToExcalidrawElements } from '@excalidraw/excalidraw';
import { pushElements, readElements, reassert, visible, isElement, compact } from './scene.js';

export { convertToExcalidrawElements };

/// opts: {
///   ymap: Y.Map, doc: Y.Doc,                 shared scene
///   readOnly: bool, theme: 'light'|'dark',
///   mode: 'block'|'board',                    block = inside a page (compact UI), board = whole-page whiteboard
///   onLinkOpen(url), onChange(count)
/// }
export function mountDraw(el, opts) {
  const { ymap, doc } = opts;
  let api = null;
  let destroyed = false;
  let readOnly = !!opts.readOnly;
  let theme = opts.theme || 'light';
  // One origin object per mounted instance: an instance ignores its own writes but sees every other instance's (the inline block
  // and its full-screen copy can be open at once), and other clients' changes arrive with a different origin.
  const ORIGIN = {};
  const sent = new Map();                       // id -> { version, versionNonce } last written by this client
  let applyingRemote = false;

  const root = createRoot(el);

  const initial = () => readElements(ymap).filter(isElement);

  const onYChange = (event, tr) => {
    if (destroyed || !api || tr.origin === ORIGIN) return;
    const changed = [...event.keysChanged];
    const incoming = changed.map(k => ymap.get(k)).filter(isElement);
    const local = api.getSceneElementsIncludingDeleted();
    const byId = new Map(local.map(e => [e.id, e]));
    // Yjs picked a winner per key by client id; Excalidraw's version rule must win instead.
    const n = reassert(ymap, byId, changed);
    if (n) return;                              // our newer copies were written back; the resulting event brings the rest
    const merged = new Map(byId);
    let any = false;
    for (const r of incoming) {
      const cur = merged.get(r.id);
      if (!cur || r.version > cur.version || (r.version === cur.version && r.versionNonce < cur.versionNonce)) { merged.set(r.id, r); any = true; }
      sent.set(r.id, { version: r.version, versionNonce: r.versionNonce });
    }
    if (!any) return;
    applyingRemote = true;
    try {
      const ordered = [...merged.values()].sort((a, b) => (a.index ?? '').localeCompare(b.index ?? '') || String(a.id).localeCompare(String(b.id)));
      api.updateScene({ elements: ordered, captureUpdate: 'NEVER' });
    } finally { applyingRemote = false; }
  };
  ymap.observe(onYChange);

  // Excalidraw reports every pointer move while drawing; writing each one to Yjs would send hundreds of updates per stroke.
  // Changes are written at most every 80 ms (trailing) and immediately when the pointer is released.
  let pending = null, timer = null;
  const flushNow = () => {
    clearTimeout(timer); timer = null;
    if (!pending || destroyed) { pending = null; return; }
    const els = pending; pending = null;
    doc.transact(() => { pushElements(ymap, els, sent); }, ORIGIN);
    opts.onChange?.(visible(els).length);
  };
  const handleChange = elements => {
    if (destroyed || applyingRemote || readOnly) return;
    pending = elements;
    if (!timer) timer = setTimeout(flushNow, 80);
  };

  const isBoard = opts.mode === 'board';
  const ui = {
    canvasActions: { loadScene: false, saveToActiveFile: false, export: false, saveAsImage: !isBoard ? false : true, toggleTheme: false, clearCanvas: true, changeViewBackgroundColor: true },
    tools: { image: false },
  };

  const App = React.createElement(Excalidraw, {
    excalidrawAPI: a => { api = a; opts.onReady?.(a); },
    initialData: { elements: initial(), appState: { viewBackgroundColor: 'transparent', currentItemFontFamily: 5, currentItemRoughness: 1 }, scrollToContent: true },
    onChange: handleChange,
    onPointerUp: () => flushNow(),
    viewModeEnabled: readOnly,
    theme,
    langCode: 'en',
    UIOptions: ui,
    autoFocus: false,
    detectScroll: false,
    handleKeyboardGlobally: false,
    onLinkOpen: (element, event) => { if (opts.onLinkOpen) { event.preventDefault(); opts.onLinkOpen(element.link); } },
  });
  root.render(App);

  return {
    setReadOnly(v) { readOnly = !!v; api?.updateScene({ appState: { viewModeEnabled: readOnly } }); },
    setTheme(t) { theme = t; root.render(React.cloneElement(App, { theme: t, viewModeEnabled: readOnly })); },
    api: () => api,
    /// PNG of everything on the canvas (white background in light theme), for "Download PNG" and exports.
    async png({ scale = 2, dark = false } = {}) {
      const els = visible(readElements(ymap).filter(isElement));
      return await exportToBlob({ elements: els, appState: { exportBackground: true, exportWithDarkMode: dark, viewBackgroundColor: dark ? '#161618' : '#ffffff' }, files: null, mimeType: 'image/png', getDimensions: (w, h) => ({ width: w * scale, height: h * scale, scale }) });
    },
    count: () => visible(readElements(ymap).filter(isElement)).length,
    compact: () => compact(ymap),
    flush: flushNow,
    destroy() { flushNow(); destroyed = true; ymap.unobserve(onYChange); try { root.unmount(); } catch { /* already gone */ } },
  };
}

/// PNG of a stored drawing without mounting any UI: used by exports (Markdown / HTML / PDF).
export async function renderPng(elements, { scale = 2, dark = false } = {}) {
  const els = elements.filter(isElement).filter(e => !e.isDeleted);
  if (!els.length) return null;
  return await exportToBlob({ elements: els, appState: { exportBackground: true, exportWithDarkMode: dark, viewBackgroundColor: dark ? '#161618' : '#ffffff' }, files: null, mimeType: 'image/png', getDimensions: (w, h) => ({ width: w * scale, height: h * scale, scale }) });
}

export async function renderSvg(elements, { dark = false } = {}) {
  const els = elements.filter(isElement).filter(e => !e.isDeleted);
  if (!els.length) return null;
  const svg = await exportToSvg({ elements: els, appState: { exportBackground: true, exportWithDarkMode: dark, viewBackgroundColor: dark ? '#161618' : '#ffffff' }, files: null });
  return svg.outerHTML;
}
