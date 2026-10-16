// iPop Notes editor: TipTap on a shared Yjs document, synced through the NotesHub (SignalR).
//
// Sync model (see docs/notes spec): the server stores a compacted Yjs state (+ version) and a tail of
// raw updates. Clients push every local update through the hub (which stores it and fans it out), and
// an editor periodically folds the tail into a new state with a compare-and-set on the version.
import { Editor, Extension } from '@tiptap/core';
import { exitSuggestion } from '@tiptap/suggestion';
import StarterKit from '@tiptap/starter-kit';
import { TaskList } from '@tiptap/extension-list';
import { TextStyle, Color, BackgroundColor } from '@tiptap/extension-text-style';
import { Details, DetailsSummary, DetailsContent } from '@tiptap/extension-details';
import { Placeholder } from '@tiptap/extensions';
import Collaboration from '@tiptap/extension-collaboration';
import CollaborationCaret from '@tiptap/extension-collaboration-caret';
import Mention from '@tiptap/extension-mention';
import DragHandle from '@tiptap/extension-drag-handle';
import { ySyncPluginKey, absolutePositionToRelativePosition, relativePositionToAbsolutePosition, yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap';
import * as Y from 'yjs';
import { Awareness, encodeAwarenessUpdate, applyAwarenessUpdate, removeAwarenessStates } from 'y-protocols/awareness';
import * as signalR from '@microsoft/signalr';

import { BlockBackground, Callout, NoteWidget, SubPage, NoteFile, CommentHighlights, commentKey, countTodos, releaseBlobs, esc } from './nodes.js';
import { SlashMenu, mentionSuggestion, phoneMentionKey, createToolbar, createHandle, createBlockMenu, place } from './menus.js';
import { NoteImage, NoteGallery, NoteBookmark, NoteEmbed, RichTaskItem, TodoExtras, tableExtensions, taskItemAt, todoInsertPos, createRichUi, closeLightboxes } from './rich.js';
import { TEMPLATES, GUIDE_COMMENT_TEXT } from './templates.js';
import { importer, exportPages } from './transfer.js';

const FIELD = 'default';
const SNAPSHOT_AFTER_UPDATES = 40;
const SNAPSHOT_IDLE_MS = 4000;

const b64 = u8 => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); };
const unb64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
const debounce = (fn, ms) => { let t; const d = (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; d.cancel = () => clearTimeout(t); return d; };

/// Extensions shared by the live editor and read-only previews (version history).
function baseExtensions(host, { collab = true, rich = {} } = {}) {
  return [
    StarterKit.configure({
      heading: { levels: [1, 2, 3] },
      undoRedo: collab ? false : undefined,
      link: { openOnClick: true, autolink: true, HTMLAttributes: { rel: 'noopener noreferrer', target: '_blank' } },
      trailingNode: {},
    }),
    TaskList, RichTaskItem.configure({ nested: true }),
    TodoExtras.configure(rich),
    ...tableExtensions(),
    TextStyle, Color, BackgroundColor,
    Details.configure({ persist: true, HTMLAttributes: { class: 'nt-toggle' } }), DetailsSummary, DetailsContent,
    BlockBackground, Callout,
    NoteWidget.configure({ host: () => host }),
    SubPage.configure({ host: () => host }),
    NoteImage.configure({ host: () => host }),
    NoteFile.configure({ host: () => host }),
    NoteGallery.configure({ host: () => host }),
    NoteBookmark.configure({ host: () => host }),
    NoteEmbed,
    Mention.configure({
      HTMLAttributes: { class: 'nt-mention' },
      renderText: ({ node }) => `@${node.attrs.label ?? ''}`,
      renderHTML: ({ node, options }) => ['span', { ...options.HTMLAttributes, 'data-id': node.attrs.id }, `@${node.attrs.label ?? ''}`],
      suggestion: mentionSuggestion(host),
    }),
  ];
}

/// Renders a stored version read-only (history dialog). opts: { apiBase, dotnet } so images and files
/// in the old version can still be fetched with the user's token.
export function preview(el, state, opts = {}) {
  const doc = new Y.Doc();
  Y.applyUpdate(doc, unb64(state));
  const json = yXmlFragmentToProsemirrorJSON(doc.getXmlFragment(FIELD));
  const apiBase = (opts.apiBase || '').replace(/\/$/, '');
  const host = {
    fetch: async path => {
      if (!opts.dotnet) throw new Error('preview');
      const t = await opts.dotnet.invokeMethodAsync('GetToken');
      return fetch(apiBase + path, { headers: { Authorization: `Bearer ${t}` } });
    },
    apiBase,
    pageInfo: () => null,
    toast: () => {},
  };
  const ed = new Editor({ element: el, editable: false, extensions: baseExtensions(host, { collab: false }), content: json,
    editorProps: { attributes: { class: 'nt-editor' } } });
  return { destroy: () => { closeLightboxes(host); ed.destroy(); doc.destroy(); } };
}

/// opts: { apiBase, pageId, access: view|comment|edit|owner, user: {name,nik,color}, accent, pages, dotnet }
/// dotnet callbacks: GetToken(), OnStatus(s), OnPresence(users), OnProgress(done,total), OnOpenPage(id),
/// OnComment(quote,anchor), OnThreadClick(id), OnToast(msg), CreateSubPage() -> {id,title,emoji}, SetAccent(k),
/// OnMeta(page) and OnThreadsChanged() (hub pushes for the open page),
/// AvatarColor(nik)? (computed in JS from opts.user palette fallback)
export async function mount(el, opts) {
  const net = opts.dotnet;
  const call = (m, ...a) => net.invokeMethodAsync(m, ...a).catch(() => {});
  const apiBase = (opts.apiBase || '').replace(/\/$/, '');
  const pageId = opts.pageId;
  const canEdit = opts.access === 'edit' || opts.access === 'owner';
  const canComment = canEdit || opts.access === 'comment';
  const token = () => net.invokeMethodAsync('GetToken');
  const api = async (path, init = {}) => {
    const headers = { ...(init.headers || {}), Authorization: `Bearer ${await token()}` };
    if (init.json !== undefined) { headers['Content-Type'] = 'application/json'; init = { ...init, body: JSON.stringify(init.json) }; }
    return fetch(apiBase + path, { ...init, headers });
  };

  let destroyed = false;
  let rich = null; // floating UI for images/tables/to-dos/links (edit mode only)
  let closing = false; // destroy() started: the editor may be gone by the time Blazor or an await calls back
  let accent = opts.accent;
  const accentListeners = new Set();
  let pages = new Map((opts.pages || []).map(p => [p.id, p]));

  // ── Host services used by nodes and menus ────────────────────────────────
  const palette = ['#0A84FF', '#5856D6', '#AF52DE', '#FF2D55', '#FF9500', '#34C759', '#30B0C7', '#A2845E'];
  const host = {
    fetch: path => api(path),
    accent: () => accent,
    setAccent: k => { accent = k; call('SetAccent', k); accentListeners.forEach(f => f()); },
    onAccent: f => accentListeners.add(f),
    pageInfo: id => pages.get(id),
    openPage: id => call('OnOpenPage', id),
    toast: m => call('OnToast', m),
    canComment: () => canComment,
    createSubPage: () => net.invokeMethodAsync('CreateSubPage').catch(() => null),
    pickFiles: (accept, pos) => pickFiles(accept, pos),
    apiBase,
    tablePicker: () => rich?.tablePicker(),
    askLink: kind => rich?.askLink(kind),
    searchPeople: async q => {
      try {
        const r = await api(`/api/notes/people?q=${encodeURIComponent(q)}&pageId=${pageId}`);
        const rows = r.ok ? await r.json() : [];
        rows.forEach(p => { if (p.hasAccess != null) accessKnown.set(p.id, p.hasAccess); });
        return rows;
      } catch { return []; }
    },
    mentioned: p => queueMention(p),
    mentionSheet: open => call('OnPhoneMention', open),
    avatarColor: nik => palette[[...String(nik)].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7) % palette.length],
    initials: name => String(name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join(''),
    commentSelection: () => commentSelection(),
    lockHandle: on => editor && !closing && (on ? editor.commands.lockDragHandle() : editor.commands.unlockDragHandle()),
  };

  // ── Yjs doc + awareness ──────────────────────────────────────────────────
  const ydoc = new Y.Doc();
  const awareness = new Awareness(ydoc);
  const peers = new Map(); // SignalR connection id -> awareness client ids
  let version = 0;
  const unmerged = new Set(); // server update ids already applied here but not folded into the state

  // ── SignalR ──────────────────────────────────────────────────────────────
  const hub = new signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/hubs/notes`, { accessTokenFactory: token })
    .withAutomaticReconnect([0, 1000, 3000, 5000, 10000, 20000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  let loaded = false;
  const early = [];
  hub.on('noteUpdate', m => {
    if (m.pageId !== pageId) return;
    if (!loaded) { early.push(m); return; }
    Y.applyUpdate(ydoc, unb64(m.data), 'remote');
    unmerged.add(m.id);
  });
  hub.on('noteAwareness', m => {
    if (m.pageId !== pageId) return;
    const before = new Set(awareness.getStates().keys());
    applyAwarenessUpdate(awareness, unb64(m.data), m.connectionId);
    const ids = peers.get(m.connectionId) || new Set();
    for (const k of awareness.getStates().keys()) if (!before.has(k)) ids.add(k);
    const isNew = !peers.has(m.connectionId);
    peers.set(m.connectionId, ids);
    if (isNew) sendAwareness([ydoc.clientID]); // introduce ourselves to the newcomer
  });
  hub.on('noteMeta', m => { if (m?.id === pageId) call('OnMeta', m); });
  hub.on('noteThreads', pid => { if (pid === pageId) call('OnThreadsChanged'); });
  hub.on('noteLeft', (pid, connectionId) => {
    if (pid !== pageId) return;
    const ids = peers.get(connectionId);
    if (ids) removeAwarenessStates(awareness, [...ids], 'remote');
    peers.delete(connectionId);
  });
  hub.onreconnecting(() => status('offline'));
  hub.onreconnected(async () => { await join(); await refetch(); flush(); sendAwareness([ydoc.clientID]); });

  async function join() {
    const ok = await hub.invoke('JoinPage', pageId).catch(() => false);
    if (!ok) throw new Error('forbidden');
  }

  function applyDoc(d) {
    if (d.state) Y.applyUpdate(ydoc, unb64(d.state), 'remote');
    for (const u of d.updates) { Y.applyUpdate(ydoc, unb64(u.data), 'remote'); }
    version = d.version;
    unmerged.clear();
    d.updates.forEach(u => unmerged.add(u.id));
  }
  async function refetch() {
    const r = await api(`/api/notes/${pageId}/doc`);
    if (!r.ok) throw new Error(String(r.status));
    applyDoc(await r.json());
  }

  await hub.start();
  await join();
  await refetch();
  loaded = true;
  for (const m of early.splice(0)) { Y.applyUpdate(ydoc, unb64(m.data), 'remote'); unmerged.add(m.id); }

  // ── Push local updates ───────────────────────────────────────────────────
  let queue = [];      // local updates not yet stored by the server
  let pushing = false;
  let saveState = 'saved';
  const status = s => { if (s !== saveState) { saveState = s; call('OnStatus', s); } };
  ydoc.on('update', (update, origin) => {
    if (origin === 'remote' || !canEdit) return;
    queue.push(update);
    status('saving');
    flush();
  });
  async function flush() {
    if (pushing || !queue.length || destroyed) return;
    if (hub.state !== signalR.HubConnectionState.Connected) { status('offline'); return; }
    pushing = true;
    const batch = queue; queue = [];
    const merged = batch.length === 1 ? batch[0] : Y.mergeUpdates(batch);
    try {
      const id = await hub.invoke('PushUpdate', pageId, b64(merged));
      if (id == null) throw new Error('refused');
      unmerged.add(id);
      scheduleSnapshot();
      if (unmerged.size >= SNAPSHOT_AFTER_UPDATES) snapshot();
    } catch {
      queue = [merged, ...queue];
      status('offline');
      pushing = false;
      setTimeout(flush, 3000);
      return;
    }
    pushing = false;
    if (queue.length) flush(); else status('saved');
  }

  // ── Snapshots (fold the update tail into the stored state) ──────────────
  let snapping = false;
  const scheduleSnapshot = debounce(() => snapshot(), SNAPSHOT_IDLE_MS);
  async function snapshot() {
    if (!canEdit || snapping || !unmerged.size || destroyed) return;
    snapping = true;
    const ids = [...unmerged];
    const { done, total } = countTodos(editor.state.doc);
    try {
      const r = await api(`/api/notes/${pageId}/snapshot`, { method: 'POST', json: {
        state: b64(Y.encodeStateAsUpdate(ydoc)), baseVersion: version, mergedUpdateIds: ids,
        plainText: editor.getText({ blockSeparator: '\n' }), todoDone: done, todoTotal: total,
      } });
      if (r.ok) { version = (await r.json()).version; ids.forEach(i => unmerged.delete(i)); }
      else if (r.status === 409) await refetch(); // someone else compacted first; take theirs and retry later
    } catch { /* retried on the next edit */ }
    snapping = false;
  }

  // ── Editor ───────────────────────────────────────────────────────────────
  let editor = null;
  let handlePos = -1;
  const handleEl = createHandle(host);
  const Keys = Extension.create({
    name: 'noteKeys',
    addKeyboardShortcuts() { return { 'Mod-Shift-m': () => { commentSelection(); return true; } }; },
  });

  editor = new Editor({
    element: el,
    editable: canEdit,
    extensions: [
      ...baseExtensions(host, { rich: { onMenu: (pos, a) => rich?.openTodoMenu(pos, a), onChip: (pos, a, k) => rich?.onChip(pos, a, k) } }),
      Collaboration.configure({ document: ydoc, field: FIELD }),
      CollaborationCaret.configure({ provider: { awareness }, user: { name: opts.user.name, nik: opts.user.nik, color: opts.user.color } }),
      Placeholder.configure({
        includeChildren: true,
        placeholder: ({ node }) => node.type.name === 'heading' ? `Heading ${node.attrs.level}` : node.type.name === 'detailsSummary' ? 'Toggle' : "Type '/' for commands…",
      }),
      SlashMenu.configure({ host }),
      CommentHighlights.configure({ resolve: resolveAnchor, onClick: id => call('OnThreadClick', id) }),
      Keys,
      ...(canEdit ? [DragHandle.configure({
        render: () => handleEl,
        onNodeChange: ({ pos }) => { handlePos = pos ?? -1; },
      })] : []),
    ],
    editorProps: {
      attributes: { class: 'nt-editor', spellcheck: 'true' },
      handlePaste: (view, event) => dropFiles(event.clipboardData?.files, todoInsertPos(view.state.doc, view.state.selection.from))
        || !!rich?.handlePasteUrl(event),
      handleDrop: (view, event, _slice, moved) => {
        if (moved || !event.dataTransfer?.files?.length) return false;
        const at = view.posAtCoords({ left: event.clientX, top: event.clientY });
        const pos = at?.pos ?? view.state.selection.from;
        return dropFiles(event.dataTransfer.files, todoInsertPos(view.state.doc, pos));
      },
    },
  });

  const toolbar = createToolbar(editor, host);
  rich = canEdit ? createRichUi(editor, host) : null;
  const blockMenu = createBlockMenu(editor, host, () => handlePos >= 0 ? handlePos : null);
  handleEl.querySelector('.grip').addEventListener('click', e => { e.preventDefault(); blockMenu.open(e.currentTarget); });
  handleEl.querySelector('.add').addEventListener('click', e => {
    e.preventDefault();
    const node = handlePos >= 0 ? editor.state.doc.nodeAt(handlePos) : null;
    if (!node) return;
    const at = handlePos + node.nodeSize;
    editor.chain().insertContentAt(at, { type: 'paragraph', content: [{ type: 'text', text: '/' }] }).setTextSelection(at + 2).focus().run();
  });
  if (!canEdit) el.addEventListener('mouseup', () => setTimeout(toolbar.sync, 0));
  // Touch has no hover, and the drag handle only follows mousemove: a tap shows it for the tapped block.
  if (canEdit) editor.view.dom.addEventListener('pointerup', e => {
    if (e.pointerType === 'mouse') return;
    editor.view.dom.dispatchEvent(new MouseEvent('mousemove', { clientX: e.clientX, clientY: e.clientY, bubbles: true }));
  });

  // Progress (tutorial ring + sidebar pill) follows the checkboxes.
  let lastProgress = '';
  const progress = debounce(() => {
    const { done, total } = countTodos(editor.state.doc);
    const k = `${done}/${total}`;
    if (k !== lastProgress) { lastProgress = k; call('OnProgress', done, total); }
  }, 250);
  editor.on('update', progress);
  progress();

  // Presence: everyone in the awareness set except us.
  function sendAwareness(clients) {
    if (hub.state !== signalR.HubConnectionState.Connected) return;
    const data = b64(encodeAwarenessUpdate(awareness, clients));
    if (data.length <= 16 * 1024) hub.invoke('Awareness', pageId, data).catch(() => {});
  }
  const sendMine = debounce(() => sendAwareness([ydoc.clientID]), 60);
  awareness.on('update', ({ added, updated, removed }, origin) => {
    if (origin === 'local' && [...added, ...updated, ...removed].includes(ydoc.clientID)) sendMine();
    const users = [];
    awareness.getStates().forEach((s, id) => { if (id !== ydoc.clientID && s.user) users.push(s.user); });
    call('OnPresence', users);
  });
  sendAwareness([ydoc.clientID]);

  // ── Seeding (template for brand-new pages, handed out once by the server) ─
  if (canEdit && ydoc.getXmlFragment(FIELD).length === 0) {
    try {
      const r = await api(`/api/notes/${pageId}/seed`, { method: 'POST' });
      const t = r.ok ? (await r.json()).template : null;
      if (!closing && t && TEMPLATES[t] && ydoc.getXmlFragment(FIELD).length === 0) {
        editor.commands.setContent(TEMPLATES[t]());
        if (t === 'guide') await postGuideThread();
      }
    } catch { /* an empty page is fine */ }
  }

  async function postGuideThread() {
    let range = null;
    editor.state.doc.descendants((n, pos) => {
      if (range || !n.isText) return !range;
      const i = n.text.indexOf(GUIDE_COMMENT_TEXT);
      if (i >= 0) range = { from: pos + i, to: pos + i + GUIDE_COMMENT_TEXT.length };
      return false;
    });
    if (!range) return;
    await api(`/api/notes/${pageId}/guide-thread`, { method: 'POST', json: { quote: GUIDE_COMMENT_TEXT, anchor: makeAnchor(range.from, range.to) } }).catch(() => {});
  }

  // ── Comments ─────────────────────────────────────────────────────────────
  function makeAnchor(from, to) {
    const y = ySyncPluginKey.getState(editor.state);
    const rel = p => Y.relativePositionToJSON(absolutePositionToRelativePosition(p, y.type, y.binding.mapping));
    return JSON.stringify({ a: rel(from), b: rel(to) });
  }
  const anchorCache = new Map();
  function resolveAnchor(anchor, state) {
    if (!anchor) return null;
    const y = ySyncPluginKey.getState(state);
    if (!y?.binding) return null;
    try {
      let rel = anchorCache.get(anchor);
      if (!rel) {
        const j = JSON.parse(anchor);
        rel = { a: Y.createRelativePositionFromJSON(j.a), b: Y.createRelativePositionFromJSON(j.b) };
        anchorCache.set(anchor, rel);
      }
      const from = relativePositionToAbsolutePosition(y.doc, y.type, rel.a, y.binding.mapping);
      const to = relativePositionToAbsolutePosition(y.doc, y.type, rel.b, y.binding.mapping);
      return from == null || to == null ? null : { from, to };
    } catch { return null; }
  }
  function commentSelection() {
    if (!canComment) return;
    const { from, to, empty } = editor.state.selection;
    if (empty) { host.toast('Select the text you want to comment on'); return; }
    const quote = editor.state.doc.textBetween(from, to, ' ').trim().slice(0, 280);
    if (!quote) return;
    toolbar.hide();
    call('OnComment', quote, makeAnchor(from, to));
  }

  // ── Mentions in the body notify people (batched) ──────────────────────────
  // Only people who can open the page are ever notified (the server checks again). Tagging someone
  // without access asks the owner to share the page; otherwise the tag stays as plain text.
  const mentionQueue = new Map();
  const sendMentions = debounce(async () => {
    if (!mentionQueue.size) return;
    const present = new Set();
    editor.state.doc.descendants(n => {
      if (n.type.name === 'mention' && n.attrs.id) present.add(n.attrs.id);
      else if (n.type.name === 'taskItem' && n.attrs.assigneeId) present.add(n.attrs.assigneeId);
    });
    // Typed then deleted (or undone) before the batch went out: nobody to notify.
    const ids = [...mentionQueue.keys()].filter(id => present.has(id)); mentionQueue.clear();
    if (!ids.length) return;
    const $from = editor.state.selection.$from;
    const context = $from.parent.textContent.slice(0, 200);
    await api(`/api/notes/${pageId}/mentions`, { method: 'POST', json: { userIds: ids, context } }).catch(() => {});
  }, 4000);
  function queueMention(p) {
    if (!canEdit) return;
    if (p.hasAccess === false) { offerShare(p); paintAccess(); return; }
    mentionQueue.set(p.id, p); sendMentions();
  }

  // "Share this page with X?" — owners can grant access (then X is notified); editors can't share,
  // so they only get a hint. Either way the tag stays in the text.
  function offerShare(p) {
    if (closing) return;
    document.querySelectorAll('.nt-share-ask').forEach(e => e.remove());
    const first = String(p.name || '').split(/\s+/)[0] || 'They';
    const ask = document.createElement('div');
    ask.className = 'nt-pop nt-share-ask show';
    ask.setAttribute('role', 'dialog');
    ask.innerHTML = opts.access === 'owner'
      ? `<div class="t">${esc(p.name)} can't open this page</div>
         <div class="d">Share it so ${esc(first)} can see the note and gets your mention.</div>
         <div class="b"><button type="button" class="no">Not now</button><button type="button" class="yes">Share (can edit)</button></div>`
      : `<div class="t">${esc(p.name)} can't open this page</div>
         <div class="d">They won't be notified. Ask the page owner to share it.</div>
         <div class="b"><button type="button" class="no">OK</button></div>`;
    document.body.appendChild(ask);
    const sel = window.getSelection();
    const r = sel && sel.rangeCount ? sel.getRangeAt(0).getBoundingClientRect() : editor.view.dom.getBoundingClientRect();
    place(ask, r);
    const close = () => { ask.remove(); document.removeEventListener('mousedown', outside, true); document.removeEventListener('keydown', onKey, true); };
    const outside = e => { if (!ask.contains(e.target)) close(); };
    const onKey = e => { if (e.key === 'Escape') { e.preventDefault(); close(); editor.commands.focus(); } };
    setTimeout(() => { document.addEventListener('mousedown', outside, true); document.addEventListener('keydown', onKey, true); }, 0);
    ask.querySelector('.no').addEventListener('click', () => { close(); editor.commands.focus(); });
    ask.querySelector('.yes')?.addEventListener('click', async () => {
      const btn = ask.querySelector('.yes'); btn.disabled = true; btn.textContent = 'Sharing…';
      const ok = await net.invokeMethodAsync('ShareFromMention', p.id, p.name).catch(() => false);
      close(); editor.commands.focus();
      if (!ok) return;
      accessKnown.set(p.id, true); paintAccess();
      mentionQueue.set(p.id, p); sendMentions();
    });
  }

  // Tags of people who can't open the page are greyed ("No access" tooltip). Asked once per person
  // when the page opens, then kept up to date from picker results and shares made here.
  const accessKnown = new Map();
  function mentionIds() {
    const ids = new Set();
    editor.state.doc.descendants(n => { if (n.type.name === 'mention' && n.attrs.id) ids.add(n.attrs.id); });
    return ids;
  }
  function paintAccess() {
    if (closing) return;
    for (const span of editor.view.dom.querySelectorAll('.nt-mention[data-id]')) {
      const no = accessKnown.get(span.dataset.id) === false;
      span.classList.toggle('noaccess', no);
      if (no) span.title = "Can't open this page — not notified"; else if (span.title) span.removeAttribute('title');
    }
  }
  const checkAccess = debounce(async () => {
    if (closing) return;
    const unknown = [...mentionIds()].filter(id => !accessKnown.has(id));
    if (unknown.length) {
      try {
        const r = await api(`/api/notes/${pageId}/access-check`, { method: 'POST', json: { userIds: unknown } });
        if (r.ok) { const ok = new Set(await r.json()); unknown.forEach(id => accessKnown.set(id, ok.has(id))); }
      } catch { /* leave them unknown: rendered as normal tags */ }
    }
    paintAccess();
  }, 600);
  editor.on('update', checkAccess);
  checkAccess();
  host.accessChanged = () => { accessKnown.clear(); checkAccess(); };
  window.addEventListener('focus', host.accessChanged);

  // ── Uploads ──────────────────────────────────────────────────────────────
  function pickFiles(accept, pos) {
    if (closing || !canEdit) return;
    const y = ySyncPluginKey.getState(editor.state);
    const at = pos ?? todoInsertPos(editor.state.doc, editor.state.selection.from);
    const anchor = absolutePositionToRelativePosition(at, y.type, y.binding.mapping);
    const input = document.createElement('input');
    input.type = 'file'; input.multiple = true; if (accept) input.accept = accept;
    input.onchange = () => {
      if (closing) return;
      const current = ySyncPluginKey.getState(editor.state);
      const resolved = relativePositionToAbsolutePosition(current.doc, current.type, anchor, current.binding.mapping);
      if (resolved != null) dropFiles(input.files, resolved);
    };
    input.click();
  }
  function dropFiles(files, pos) {
    if (!canEdit || !files?.length) return false;
    const list = [...files];
    (async () => {
      // Files land in the order they were picked, each right after the previous one. The insert point
      // is a Yjs relative position so edits by others while uploading don't shift it.
      const y = ySyncPluginKey.getState(editor.state);
      const toRel = p => absolutePositionToRelativePosition(p, y.type, y.binding.mapping);
      const toAbs = r => relativePositionToAbsolutePosition(y.doc, y.type, r, y.binding.mapping);
      let rel = toRel(Math.min(pos, editor.state.doc.content.size));
      let done = 0, failed = 0;
      const images = [];
      // One toast when it starts and one summary at the end; per-file toasts just stack up.
      host.toast(list.length > 1 ? `Uploading ${list.length} files…` : `Uploading ${list[0].name}…`);
      for (const f of list) {
        const form = new FormData(); form.append('file', f, f.name);
        try {
          const r = await api(`/api/notes/${pageId}/uploads`, { method: 'POST', body: form });
          if (!r.ok) { const e = await r.json().catch(() => ({})); host.toast(e.message || `Couldn't upload ${f.name}`); failed++; continue; }
          const u = await r.json();
          const node = { type: u.contentType?.startsWith('image/') ? 'noteImage' : 'noteFile', attrs: { id: u.id, name: u.fileName, size: u.size } };
          if (closing) return;
          const at = Math.min(toAbs(rel) ?? editor.state.doc.content.size, editor.state.doc.content.size);
          const before = editor.state.doc.content.size;
          editor.chain().insertContentAt(at, node).run();
          rel = toRel(Math.min(at + (editor.state.doc.content.size - before), editor.state.doc.content.size));
          if (node.type === 'noteImage') images.push(u.id);
          done++;
        } catch { host.toast(`Couldn't upload ${f.name}`); failed++; }
      }
      if (images.length >= 3 && !closing) rich?.offerGallery(images);
      if (list.length > 1 && !closing) host.toast(failed ? `Uploaded ${done} of ${list.length} files` : `Uploaded ${done} files`);
    })();
    return true;
  }

  // ── API for Blazor ───────────────────────────────────────────────────────
  return {
    closeMobileMention() { if (!closing) exitSuggestion(editor.view, phoneMentionKey); },
    mobileCommand(command) {
      if (closing || !canEdit) return;
      if (command === 'hide') { editor.view.dom.blur(); return; }
      if (command === 'image') { pickFiles('image/*'); return; }
      const chain = editor.chain().focus();
      switch (command) {
        case 'heading': chain.toggleHeading({ level: 2 }).run(); break;
        case 'bold': chain.toggleBold().run(); break;
        case 'italic': chain.toggleItalic().run(); break;
        case 'bullet': chain.toggleBulletList().run(); break;
        case 'checklist': chain.toggleTaskList().run(); break;
        case 'mention': chain.insertContent('@').run(); break;
        case 'undo': chain.undo().run(); break;
      }
    },
    setThreads(threads, focus) {
      if (closing) return;
      const open = (threads || []).filter(t => !t.resolvedAt && t.anchor).map(t => ({ id: t.id, anchor: t.anchor }));
      editor.view.dispatch(editor.state.tr.setMeta(commentKey, { threads: open, focus: focus ?? null }));
    },
    focusThread(id) {
      if (closing) return;
      const st = commentKey.getState(editor.state);
      editor.view.dispatch(editor.state.tr.setMeta(commentKey, { ...st, focus: id }));
      const t = st.threads.find(x => x.id === id);
      const r = t && resolveAnchor(t.anchor, editor.state);
      if (r) editor.view.domAtPos(r.from)?.node?.parentElement?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    },
    setAccent(k) { accent = k; accentListeners.forEach(f => f()); },
    /// Shares changed (dialog or a share from a mention): re-check who the tags can reach.
    sharesChanged() { host.accessChanged?.(); },
    setPages(list) { pages = new Map((list || []).map(p => [p.id, p])); if (!closing) editor.view.dispatch(editor.state.tr.setMeta('pagesChanged', true)); },
    applyTemplate(key) {
      if (closing || !canEdit || !TEMPLATES[key]) return false;
      editor.commands.setContent(TEMPLATES[key]());
      if (key === 'guide') postGuideThread();
      editor.commands.focus('end');
      return true;
    },
    isEmpty() { return !closing && editor.isEmpty; },
    focus() { if (!closing) editor.commands.focus('start'); },
    restore(state) {
      if (closing || !canEdit) return;
      const tmp = new Y.Doc();
      Y.applyUpdate(tmp, unb64(state));
      editor.commands.setContent(yXmlFragmentToProsemirrorJSON(tmp.getXmlFragment(FIELD)));
      tmp.destroy();
    },
    async flushNow() { await flush(); scheduleSnapshot.cancel(); await snapshot(); },
    async destroy() {
      if (closing) return;
      closing = true;
      scheduleSnapshot.cancel();
      try { await flush(); await snapshot(); } catch { /* best effort */ }
      destroyed = true;
      removeAwarenessStates(awareness, [ydoc.clientID], 'local');
      await hub.invoke('LeavePage', pageId).catch(() => {});
      await hub.stop().catch(() => {});
      closeLightboxes(host);
      window.removeEventListener('focus', host.accessChanged);
      document.querySelectorAll('.nt-share-ask').forEach(e => e.remove());
      toolbar.destroy(); blockMenu.destroy(); rich?.destroy();
      editor.destroy(); awareness.destroy(); ydoc.destroy();
      releaseBlobs();
    },
  };
}

/// Page-level shortcuts while /notes is open. Ctrl K stays with the global search; browsers reserve
/// Ctrl N, so a new page is Alt N.
export function hotkeys(net) {
  const onKey = e => {
    const k = e.key.toLowerCase();
    if (k === 'n' && e.altKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault(); e.stopImmediatePropagation(); net.invokeMethodAsync('OnHotkey', 'new').catch(() => {});
    }
  };
  window.addEventListener('keydown', onKey, true);
  return { dispose: () => window.removeEventListener('keydown', onKey, true) };
}

/// Import / export (transfer.js). opts: { apiBase, dotnet } — dotnet provides GetToken() and the
/// OnImport*/OnExportProgress callbacks.
function apiFor(opts) {
  const apiBase = (opts.apiBase || '').replace(/\/$/, '');
  return async (path, init = {}) => {
    const headers = { ...(init.headers || {}), Authorization: `Bearer ${await opts.dotnet.invokeMethodAsync('GetToken')}` };
    if (init.json !== undefined) { headers['Content-Type'] = 'application/json'; init = { ...init, body: JSON.stringify(init.json) }; }
    return fetch(apiBase + path, { ...init, headers });
  };
}
export function transfer(opts) {
  const api = apiFor(opts);
  return {
    importer: () => importer({ api, net: opts.dotnet, extensions: baseExtensions({}, { collab: false }) }),
    exportPages: o => exportPages(api, opts.dotnet, o),
    // Result numbers count up (micro-interaction #33); ipopMicro skips it under reduced motion.
    countUp: sel => document.querySelectorAll(sel).forEach(e => window.ipopMicro?.countUp(e, +e.dataset.countUp, 600)),
  };
}

export function copyText(text) { return navigator.clipboard?.writeText(text).then(() => true, () => false) ?? false; }
export { sidebar, place, openTab, focusSelect } from './sidebar.js';
