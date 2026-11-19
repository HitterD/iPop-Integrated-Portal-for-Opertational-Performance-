// iPop Notes editor: TipTap on a shared Yjs document, synced through the NotesHub (SignalR).
//
// Sync model (see docs/notes spec): the server stores a compacted Yjs state (+ version) and a tail of
// raw updates. Clients push every local update through the hub (which stores it and fans it out), and
// an editor periodically folds the tail into a new state with a compare-and-set on the version.
import { createCovers } from './covers.js';
import { restoreScenes } from './restore-scenes.js';
import { PageLink, pageLinkIds } from './page-link.js';
import { listPopup } from './menus.js';
import { NoteDraw, mountScene, newDrawId, setAllReadOnly, refreshBoards } from './draw-block.js';
// Built-in stickers (wwwroot/stickers/fluent, the same set Chat uses).
const FLUENT_STICKERS = ['thumbs-up', 'red-heart', 'face-with-tears-of-joy', 'party-popper', 'fire', 'hundred-points', 'clapping-hands', 'folded-hands', 'raising-hands', 'ok-hand', 'flexed-biceps', 'victory-hand', 'waving-hand', 'thumbs-down', 'check-mark-button', 'light-bulb', 'rocket', 'trophy', 'sparkles', 'star-struck', 'eyes', 'thinking-face', 'smiling-face-with-heart-eyes', 'smiling-face-with-sunglasses', 'beaming-face-with-smiling-eyes', 'winking-face', 'hugging-face', 'partying-face', 'nerd-face', 'saluting-face', 'exploding-head', 'astonished-face', 'face-screaming-in-fear', 'loudly-crying-face', 'pleading-face', 'grimacing-face', 'face-with-rolling-eyes', 'face-with-steam-from-nose', 'pouting-face', 'hot-face', 'cold-face', 'sleeping-face', 'zany-face', 'upside-down-face', 'face-with-hand-over-mouth', 'face-blowing-a-kiss', 'smiling-face-with-hearts', 'rolling-on-the-floor-laughing', 'skull', 'ghost', 'hot-beverage', 'birthday-cake'];
import { analyze as tidyAnalyze, apply as tidyApply } from './tidy.js';
import { parseSticky } from './sticky-task.js';
import { refusedReason, mentionContexts, savePending, takePending, clearPending } from './sync.js';
import { Editor, Extension } from '@tiptap/core';
import { exitSuggestion } from '@tiptap/suggestion';
import StarterKit from '@tiptap/starter-kit';
import { TaskList } from '@tiptap/extension-list';
import { TextStyle, Color, BackgroundColor } from '@tiptap/extension-text-style';
import { Details, DetailsContent } from '@tiptap/extension-details';
import { CodeBlockLowlight } from '@tiptap/extension-code-block-lowlight';
import { common, createLowlight } from 'lowlight';
import { FindReplace, createFindBar, Columns, Column, DateChip, EmojiShortcodes, ToggleSummary, wordStats, BlockId, findBlock, ensureBlockId, uploadXhr, createUploadTray, blockTexts, diffLines, foldSame } from './extras.js';
import { NotePlaceholder } from './placeholder.js';
import { Symbols } from './symbols.js';
import { ListNumbering } from './lists.js';
import { moveBlockTr } from './block-menu.js';
import { createMarginLayout } from './margin-comments.js';
import { sliceText, cleanOfficeHtml, preferFiles } from './paste.js';
import { pictureClipboard } from './clip-images.js';
import Collaboration from '@tiptap/extension-collaboration';
import CollaborationCaret from '@tiptap/extension-collaboration-caret';
import Mention from '@tiptap/extension-mention';
import DragHandle from '@tiptap/extension-drag-handle';
import { ySyncPluginKey, absolutePositionToRelativePosition, relativePositionToAbsolutePosition, yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap';
import * as Y from 'yjs';
import { Awareness, encodeAwarenessUpdate, applyAwarenessUpdate, removeAwarenessStates } from 'y-protocols/awareness';
import * as signalR from '@microsoft/signalr';

import { BlockBackground, Callout, NoteWidget, NoteToc, HeadingFlash, SubPage, NoteFile, CommentHighlights, commentKey, countTodos, todoDueItems, releaseBlobs, esc, flashKey, loadBlob } from './nodes.js';
import { SlashMenu, mentionSuggestion, phoneMentionKey, createToolbar, createHandle, createBlockMenu, place } from './menus.js';
import { NoteImage, NoteGallery, NoteBookmark, NoteEmbed, RichTaskItem, TodoExtras, tableExtensions, taskItemAt, todoInsertPos, createRichUi, closeLightboxes } from './rich.js';
import { TEMPLATES, GUIDE_COMMENT_TEXT } from './templates.js';
import { importer, exportPages } from './transfer.js';

const FIELD = 'default';
const lowlight = createLowlight(common);
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
      codeBlock: false,
      undoRedo: collab ? false : undefined,
      link: { openOnClick: true, autolink: true, HTMLAttributes: { rel: 'noopener noreferrer', target: '_blank' } },
      trailingNode: {},
    }),
    TaskList, RichTaskItem.configure({ nested: true }),
    TodoExtras.configure(rich),
    ...tableExtensions(),
    TextStyle, Color, BackgroundColor,
    Details.configure({ persist: true, HTMLAttributes: { class: 'nt-toggle' } }), ToggleSummary, DetailsContent,
    CodeBlockLowlight.configure({ lowlight, defaultLanguage: null, HTMLAttributes: { class: 'nt-code' } }),
    Columns, Column, DateChip, EmojiShortcodes, Symbols, ListNumbering, BlockId,
    BlockBackground, Callout,
    NoteWidget.configure({ host: () => host }),
    NoteToc, HeadingFlash,
    PageLink.configure({ host: () => host }),
    NoteDraw.configure({ host: () => host }),
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
/// Text of a stored state (base64 Yjs) as one string per block. Used for "what changed" between two versions.
function stateBlocks(state) {
  const tmp = new Y.Doc();
  try { Y.applyUpdate(tmp, unb64(state)); return blockTexts(yXmlFragmentToProsemirrorJSON(tmp.getXmlFragment(FIELD))); }
  finally { tmp.destroy(); }
}
/// Compare two stored states: lines for the history panel ({ kind, text } with unchanged stretches folded).
export function diffVersions(beforeState, afterState) {
  return foldSame(diffLines(stateBlocks(beforeState), stateBlocks(afterState)));
}

export function preview(el, state, opts = {}) {
  const doc = new Y.Doc();
  Y.applyUpdate(doc, unb64(state));
  const json = yXmlFragmentToProsemirrorJSON(doc.getXmlFragment(FIELD));
  const apiBase = (opts.apiBase || '').replace(/\/$/, '');
  const host = {
    drawDoc: () => doc,
    fetch: async path => {
      if (!opts.dotnet) throw new Error('preview');
      const t = await opts.dotnet.invokeMethodAsync('GetToken');
      return fetch(apiBase + path, { headers: { Authorization: `Bearer ${t}` } });
    },
    apiBase,
    pageInfo: () => null,
    toast: () => {},
  };
  // A whiteboard version has no text, only its board: show it as a read-only drawing so the history is not an empty page.
  const hasScene = n => doc.share.has(n) && doc.getMap(n).size > 0;
  const hasBoard = (hasScene('draw:board') || hasScene('wb:board')) && !JSON.stringify(json).includes('"noteDraw"');
  const content = hasBoard ? { ...json, content: [{ type: 'noteDraw', attrs: { id: 'board', height: 420 } }, ...(json.content || [])] } : json;
  const ed = new Editor({ element: el, editable: false, extensions: baseExtensions(host, { collab: false }), content,
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
  const canEditAccess = opts.access === 'edit' || opts.access === 'owner';
  let pageLocked = !!opts.locked;
  // Locked pages are read-only for everyone until unlocked; the drawing blocks read `canEdit` through host.canDraw().
  let canEdit = canEditAccess && !pageLocked;
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
  let beforeConvertDone = null;
  const titleNow = () => pages.get(pageId)?.title || 'whiteboard';
  // whiteboards on this page listen for the comment threads (to draw pins)
  let boardThreads = [];
  const drawThreadListeners = new Set();
  const drawFocusListeners = new Set();
  const projectNames = new Map();
  let emojiData = null;
  const stickerUrls = new Map();
  const palette = ['#0A84FF', '#5856D6', '#AF52DE', '#FF2D55', '#FF9500', '#34C759', '#30B0C7', '#A2845E'];
  // who has a profile photo (nik -> photo id), read once for org charts
  let photoMapP = null;
  const photoMap = () => (photoMapP ||= api('/api/photos').then(r => (r.ok ? r.json() : {})).catch(() => ({}))); 
  const host = {
    drawDoc: () => ydoc,
    drawAwareness: () => awareness,
    // Whiteboard library (My library + team library), stored on the server for the signed-in person.
    drawLibrary: {
      async list() { const r = await api('/api/notes/library'); if (!r.ok) throw new Error(String(r.status)); return r.json(); },
      async save(item) {
        const r = await api('/api/notes/library', { method: 'POST', json: item });
        if (!r.ok) { const e = await r.json().catch(() => ({})); throw new Error(e.code || String(r.status)); }
        return r.json();
      },
      async update(id, patch) { const r = await api(`/api/notes/library/${id}`, { method: 'PATCH', json: patch }); if (!r.ok) throw new Error(String(r.status)); return r.json(); },
      async remove(id) { const r = await api(`/api/notes/library/${id}`, { method: 'DELETE' }); if (!r.ok) throw new Error(String(r.status)); },
    },
    // Template Center (round 6): boards people saved as templates (mine + shared with the team). An admin may remove shared ones.
    drawTemplates: {
      async list() { const r = await api('/api/notes/board-templates'); if (!r.ok) throw new Error(String(r.status)); return r.json(); },
      async save(t) {
        const r = await api('/api/notes/board-templates', { method: 'POST', json: t });
        if (!r.ok) { const e = await r.json().catch(() => ({})); throw new Error(e.code || String(r.status)); }
        return r.json();
      },
      async update(id, patch) { const r = await api(`/api/notes/board-templates/${id}`, { method: 'PATCH', json: patch }); if (!r.ok) throw new Error(String(r.status)); return r.json(); },
      async remove(id) { const r = await api(`/api/notes/board-templates/${id}`, { method: 'DELETE' }); if (!r.ok) throw new Error(String(r.status)); },
      isAdmin: () => !!opts.notesAdmin,
      /// "As a new board page": a whiteboard page next to this one, opened in this tab; the template is put on it there.
      newBoardPage: async (title, key, o = {}) => {
        try { sessionStorage.setItem('ipop.wb.pendingTemplate', JSON.stringify({ key, at: Date.now(), howTo: o.howTo !== false, examples: o.examples !== false })); } catch { /* blocked */ }
        return call('OnNewBoardFromTemplate', title);
      },
    },
    // Per-person whiteboard prefs that follow the account: "My pens" and the templates used last.
    drawPrefs: {
      async get(key) { const r = await api(`/api/me/ui-prefs/${key}`); if (r.status !== 200) return null; return r.json().catch(() => null); },
      async set(key, value) { await api(`/api/me/ui-prefs/${key}`, { method: 'PUT', json: value }).catch(() => {}); },
    },
    // Comments pinned to a board go through the page's own threads (same panel, mentions, reactions, resolve).
    drawComments: {
      start: (quote, anchor) => { if (canComment) call('OnComment', quote, anchor); },
      open: id => call('OnThreadClick', id),
      canComment: () => canComment,
      subscribe(fn) { drawThreadListeners.add(fn); fn(boardThreads); return () => drawThreadListeners.delete(fn); },
      onFocus(fn) { drawFocusListeners.add(fn); return () => drawFocusListeners.delete(fn); },
    },
    // Projects: task cards (live) and "Make task" from stickies. A task the person cannot open comes back as private.
    drawTasks: {
      async get(id) {
        // the card endpoint answers { private: true } for a task this person can't open (no red 404 in the console)
        const r = await api(`/api/projects/tasks/${id}/card`);
        if (!r.ok) return { private: true };
        const c = await r.json();
        if (c.private || !c.task) return { private: true };
        const t = c.task;
        return { id: t.id, title: t.title, status: t.status, due: t.dueDate ? new Date(t.dueDate).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) : null,
          people: (t.assignees || []).map(a => String(a.name || '').split(' ')[0]), project: projectNames.get(t.projectId) || null, projectId: t.projectId };
      },
      open: id => call('OnOpenTask', id),
      create: stickies => makeTasksDialog(stickies),
      /// diagram steps → tasks: [{ id, title, pic, due, description }] → [{ stepId, task }]
      createMany: steps => makeTasksDialog(steps.map(s => ({ id: s.id, text: s.title, pic: s.pic, dueIso: s.due, description: s.description })), { fromSteps: true })
        .then(out => out.map(o => ({ stepId: o.stickyId, task: o.task }))),
    },
    drawPages: { info: id => pages.get(id), open: id => call('OnOpenPage', id), all: () => [...pages.values()].filter(p => p.id !== pageId) },
    drawTaskSearch: async q => {
      let projects = [];
      try { const r = await api('/api/projects/writable'); if (r.ok) projects = await r.json(); } catch { /* none */ }
      for (const p of projects) projectNames.set(p.id, p.name);
      const out = [];
      for (const p of projects.slice(0, 12)) {
        try {
          const r = await api(`/api/projects/${p.id}/tasks/list?pageSize=50`);
          if (!r.ok) continue;
          const j = await r.json();
          for (const t of (j.items || j || [])) if (!q || String(t.title).toLowerCase().includes(q.toLowerCase()) || String(t.id).startsWith(q.toLowerCase())) out.push({ id: t.id, title: t.title, status: t.status, project: p.name, projectId: p.id, updatedAt: t.updatedAt, people: (t.assignees || []).map(a => a.name) });
        } catch { /* skipped */ }
        if (out.length >= 40) break;
      }
      // most recently changed first: what you touched last is what you want on the board
      out.sort((a, b) => String(b.updatedAt || '').localeCompare(String(a.updatedAt || '')));
      return out.slice(0, 40);
    },
    // "+ Create new" in the Task picker: a task in the project last used for the board (or the first one you can add to)
    drawTaskCreate: async title => {
      let projects = [];
      try { const r = await api('/api/projects/writable'); if (r.ok) projects = await r.json(); } catch { /* none */ }
      if (!projects.length) return null;
      let last = null; try { last = localStorage.getItem('ipop.wb.lastProject'); } catch { /* blocked */ }
      const p = projects.find(x => x.id === last) || projects[0];
      const r = await api(`/api/projects/${p.id}/tasks`, { method: 'POST', json: { projectId: p.id, parentTaskId: null, title: String(title || 'New task').slice(0, 300), description: null, status: 'todo', priority: null, startDate: null, dueDate: null, estimatedHours: null, dependencies: null, tags: null } });
      if (!r.ok) return null;
      const t = await r.json();
      projectNames.set(p.id, p.name);
      return { id: t.id, title: t.title, status: t.status, project: p.name, projectId: p.id };
    },
    drawLastProjectName: async () => {
      let projects = [];
      try { const r = await api('/api/projects/writable'); if (r.ok) projects = await r.json(); } catch { /* none */ }
      let last = null; try { last = localStorage.getItem('ipop.wb.lastProject'); } catch { /* blocked */ }
      return (projects.find(x => x.id === last) || projects[0])?.name || null;
    },
    // Before an old drawing becomes the new canvas, the page as it is now is kept as a named version (once per page).
    beforeCanvasConvert: async () => {
      if (beforeConvertDone) return beforeConvertDone;
      beforeConvertDone = (async () => {
        await flush(); scheduleSnapshot.cancel(); await snapshot();
        await api(`/api/notes/${pageId}/versions`, { method: 'POST', json: { name: 'Before the new canvas' } });
      })();
      return beforeConvertDone;
    },
    // Pictures and files on a whiteboard: uploaded into the page like any Notes upload (NAS, page access).
    drawFiles: {
      async upload(file) {
        if (!canEdit) throw new Error('You can only view this page');
        const form = new FormData(); form.append('file', file, file.name);
        const r = await uploadXhr(`${apiBase}/api/notes/${pageId}/uploads`, form, await token());
        if (!r.ok) throw new Error(r.json?.message || `Couldn't upload ${file.name}`);
        return r.json;
      },
      load: id => loadBlob(host, id),
      async open(id, name) {
        try { const url = await loadBlob(host, id); const a = document.createElement('a'); a.href = url; a.download = name || 'file'; a.click(); }
        catch { host.toast("Couldn't open the file"); }
      },
    },
    // Emoji list shared with Chat (same file, cached by the browser), loaded once for the board's emoji picker.
    drawEmojiData: () => (emojiData ||= fetch('/data/emoji/emoji-18.0.json').then(r => (r.ok ? r.json() : null)).catch(() => null)),
    // Stickers: the built-in set (static files) and the team's own (through the API, with the token).
    drawStickers: {
      async list() {
        const own = [];
        try {
          const r = await api('/api/chat/stickers');
          if (r.ok) { const j = await r.json(); for (const s of [...(j.mine || []), ...(j.official || [])]) own.push({ key: `custom:${s.id}`, name: 'Sticker' }); }
        } catch { /* only the built-in ones */ }
        const seen = new Set();
        const custom = own.filter(s => (seen.has(s.key) ? false : seen.add(s.key)));
        for (const s of custom) s.url = await host.drawStickers.url(s.key).catch(() => '');
        return [...custom.filter(s => s.url), ...FLUENT_STICKERS.map(n => ({ key: `fluent:${n}`, name: n.replace(/-/g, ' '), url: `/stickers/fluent/${n}.webp` }))];
      },
      async url(key) {
        if (key.startsWith('fluent:')) return `/stickers/fluent/${key.slice(7).replace(/[^a-z0-9-]/g, '')}.webp`;
        if (key.startsWith('custom:')) {
          if (stickerUrls.has(key)) return stickerUrls.get(key);
          const p = (async () => { const r = await fetch(`${apiBase}/api/chat/stickers/${key.slice(7)}/image`); if (!r.ok) throw new Error(String(r.status)); return URL.createObjectURL(await r.blob()); })();
          stickerUrls.set(key, p); p.catch(() => stickerUrls.delete(key));
          return p;
        }
        return '';
      },
    },
    // Web link cards: the same preview service as Chat (title, site, picture). Internal iPop links come back without a fetch.
    drawLinkPreview: async url => {
      const r = await api(`/api/link-preview?url=${encodeURIComponent(url)}`);
      if (r.status !== 200) return null;
      const p = await r.json();
      return { title: p.title || p.internal?.title || null, site: p.siteName || (p.kind === 'internal' ? 'iPop' : null), image: p.image || null };
    },
    drawPreviewImage: async path => { const r = await fetch(apiBase + path); if (!r.ok) throw new Error(String(r.status)); const b = await r.blob(); return await new Promise(ok => { const fr = new FileReader(); fr.onload = () => ok(fr.result); fr.readAsDataURL(b); }); },
    // The whiteboard page is the board: locking it is the page lock (owner and editors; everyone keeps view/comment/vote).
    drawBoardLock: {
      canChange: () => canEditAccess && opts.kind === 'board',
      isLocked: () => pageLocked,
      toggle: () => call('OnBoardLockToggle'),
    },
    // Whiteboard round 4. Directory for org charts: names, department and photo only (office rule: no NIK or position).
    drawPeople: {
      async search(q) {
        const r = await api(`/api/directory/member-search?q=${encodeURIComponent(q)}`);
        if (!r.ok) return [];
        return (await r.json()).map(p => ({ key: p.nik, name: p.name, dept: p.department || '' }));
      },
      /// the person, their manager and everyone who reports to them (one level), as tree rows
      async team(person) {
        const key = person?.key; if (!key) return [];
        const [rel, photos] = await Promise.all([api(`/api/directory/employees/${encodeURIComponent(key)}/relations`).then(r => (r.ok ? r.json() : null)).catch(() => null), photoMap()]);
        if (!rel) return [];
        const rows = [];
        const row = (nik, name, dept, managerKey) => rows.push({ key: nik, name, dept: dept || '', managerKey, photo: photos?.[nik] || null });
        if (rel.manager) row(rel.manager.nik, rel.manager.name, rel.manager.department, null);
        row(key, person.name, person.dept, rel.manager?.nik || null);
        for (const p of rel.reports || []) row(p.nik, p.name, p.department, key);
        return rows;
      },
      photo: id => (id ? `${apiBase}/api/photos/${id}` : null),
    },
    // Version history for the board: the page's saved versions, this board's scene in each, and Restore (page restore).
    drawHistory: {
      async list() {
        const r = await api(`/api/notes/${pageId}/versions`);
        if (!r.ok) return [];
        return (await r.json()).map(v => ({ id: v.id, at: v.createdAt, who: (v.contributors || []).join(', ') })).reverse();
      },
      async scene(versionId, boardId = 'board') {
        const r = await api(`/api/notes/${pageId}/versions/${versionId}`);
        if (!r.ok) return [];
        const { state } = await r.json();
        const tmp = new Y.Doc();
        try {
          Y.applyUpdate(tmp, unb64(state));
          const out = [];
          tmp.getMap(`wb:${boardId}`).forEach(v => { if (v && typeof v === 'object' && v.id && v.type) out.push(v); });
          return out;
        } finally { tmp.destroy(); }
      },
      restore: versionId => call('OnRestoreVersionId', versionId),
    },
    // "Send to Chat": the page's own Share dialog (sends a live card of the page to a chat)
    drawShareToChat: opts.access === 'owner' ? () => call('OnShareFromBoard') : null,
    isPoppedOut: () => !!opts.boardOnly,
    drawPopOut: id => { const u = `${location.origin}/notes/${pageId}/board${id && id !== 'board' ? `?d=${encodeURIComponent(id)}` : ''}`; window.open(u, '_blank', 'noopener'); },
    allPages: () => [...pages.values()],
    currentPageId: () => pageId,
    pagesKnown: () => pages.size > 0,
    listPopup: (render, empty) => listPopup(render, empty, host),
    openLink: url => { if (/^https?:\/\//i.test(url || '')) window.open(url, '_blank', 'noopener,noreferrer'); },
    drawFullscreen: id => openDrawFullscreen(id),
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
    // the drag handle exists only for people who can edit
    lockHandle: on => editor && !closing && (on ? editor.commands.lockDragHandle?.() : editor.commands.unlockDragHandle?.()),
  };

  // ── Yjs doc + awareness ──────────────────────────────────────────────────
  const ydoc = new Y.Doc();
  const awareness = new Awareness(ydoc);
  // QA hook: with localStorage['ipop.qa'] set, the live document can be inspected from the console (never set for real users).
  try { if (localStorage.getItem('ipop.qa') === '1') window.__ipopQa = { ydoc }; } catch { /* storage blocked */ }
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
  let lostEarly = false; // access lost while the page was still loading (stopEditing isn't ready yet)
  hub.on('noteAccessLost', pid => { if (pid !== pageId) return; if (loaded) stopEditing('not_found'); else lostEarly = true; });
  hub.onreconnecting(() => { if (loaded) status('offline'); });
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
  const status = s => { if (s !== saveState && (saveState !== 'locked' || s === 'locked')) { saveState = s; call('OnStatus', s); } };
  ydoc.on('update', (update, origin) => {
    if (origin === 'remote' || !canEdit) return;
    queue.push(update);
    status('saving');
    flush();
  });
  // The server refused for good (access removed, page gone, update too large): stop pushing and make the editor
  // read-only. Unsent edits stay in `queue` only so the user can still copy them out of the page.
  let refused = null;
  function stopEditing(reason) {
    if (refused || destroyed) return;
    refused = reason;
    try { if (editor && !closing) editor.setEditable(false); } catch { /* editor already gone */ }
    status('locked');
    call('OnRefused', reason, queue.length > 0);
  }
  const unsent = () => queue.length ? (queue.length === 1 ? queue[0] : Y.mergeUpdates(queue)) : null;
  const parkUnsent = () => { const u = unsent(); if (u) savePending(localStorage, pageId, b64(u)); };
  // Closing the tab with edits the server has not confirmed: park them, and ask the browser to warn first.
  const onBeforeUnload = e => { if (queue.length || pushing) { parkUnsent(); e.preventDefault(); e.returnValue = ''; } };
  window.addEventListener('beforeunload', onBeforeUnload);
  async function flush() {
    if (pushing || !queue.length || destroyed || refused) return;
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
    } catch (err) {
      const reason = refusedReason(err);
      if (reason) { pushing = false; queue = [merged, ...queue]; stopEditing(reason); return; }
      queue = [merged, ...queue];
      parkUnsent();
      status('offline');
      pushing = false;
      setTimeout(flush, 3000);
      return;
    }
    pushing = false;
    if (queue.length) flush(); else { clearPending(localStorage, pageId); status('saved'); }
  }

  // ── Snapshots (fold the update tail into the stored state) ──────────────
  let snapping = false;
  const scheduleSnapshot = debounce(() => snapshot(), SNAPSHOT_IDLE_MS);
  async function snapshot() {
    if (!canEdit || snapping || !unmerged.size || destroyed || refused) return;
    snapping = true;
    const ids = [...unmerged];
    const { done, total } = countTodos(editor.state.doc);
    try {
      const r = await api(`/api/notes/${pageId}/snapshot`, { method: 'POST', json: {
        state: b64(Y.encodeStateAsUpdate(ydoc)), baseVersion: version, mergedUpdateIds: ids,
        plainText: editor.getText({ blockSeparator: '\n' }), todoDone: done, todoTotal: total, todoDue: todoDueItems(editor.state.doc), linksTo: pageLinkIds(editor.state.doc),
      } });
      if (r.ok) { version = (await r.json()).version; ids.forEach(i => unmerged.delete(i)); }
      else if (r.status === 403 || r.status === 404) stopEditing(r.status === 404 ? 'not_found' : 'forbidden');
      else if (r.status === 409) await refetch(); // someone else compacted first; take theirs and retry later
    } catch { /* retried on the next edit */ }
    snapping = false;
  }

  // ── Make task(s) from stickies ─────────────────────────────────────────
  // Title = the sticky's text without @names and dates; an @name that matches a project member becomes the assignee;
  // a date in the text (25 Nov, 2026-11-25, 25/11) becomes the due date. The project list holds only projects the person can add to.
  const LAST_PROJECT = 'ipop.wb.lastProject';
  async function makeTasksDialog(stickies, { fromSteps = false } = {}) {
    let projects = [];
    try { const r = await api('/api/projects/writable'); if (r.ok) projects = await r.json(); } catch { /* none */ }
    for (const p of projects) projectNames.set(p.id, p.name);
    if (!projects.length) { host.toast('You are not a member of a project you can add tasks to.'); return []; }
    let last = null; try { last = localStorage.getItem(LAST_PROJECT); } catch { /* blocked */ }
    // diagram steps already carry PIC (shape data) and a due date (chained from durations); stickies are parsed
    const parsed = fromSteps
      ? stickies.map(s => ({ ...s, title: s.text, who: s.pic || null, due: s.dueIso ? { iso: s.dueIso, label: new Date(`${s.dueIso}T00:00:00`).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) } : null }))
      : stickies.map(s => ({ ...s, ...parseSticky(s.text) }));
    return new Promise(resolve => {
      const layer = document.createElement('div');
      layer.className = 'nt-taskdlg-layer';
      layer.innerHTML = `<div class="nt-taskdlg" role="dialog" aria-modal="true" aria-label="Make tasks">
        <h4>Make ${parsed.length === 1 ? 'a task' : `${parsed.length} tasks`}</h4>
        <label class="nt-taskdlg-f">Project<select data-project>${projects.map(p => `<option value="${p.id}" ${p.id === last ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select></label>
        <ul class="nt-taskdlg-list">${parsed.map(t => `<li><b>${esc(t.title || 'Untitled task')}</b><span>${[t.who ? '@' + esc(t.who) : '', t.due ? esc(t.due.label) : ''].filter(Boolean).join(' · ') || 'No one yet'}</span></li>`).join('')}</ul>
        <div class="nt-taskdlg-acts"><button type="button" data-x>Cancel</button><button type="button" data-ok class="pri">Create ${parsed.length === 1 ? 'task' : `${parsed.length} tasks`}</button></div>
      </div>`;
      document.body.appendChild(layer);
      const close = v => { layer.remove(); document.removeEventListener('keydown', onKey, true); resolve(v); };
      const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); close([]); } };
      document.addEventListener('keydown', onKey, true);
      layer.addEventListener('click', async e => {
        if (e.target === layer || e.target.closest('[data-x]')) { close([]); return; }
        if (!e.target.closest('[data-ok]')) return;
        const btn = e.target.closest('[data-ok]'); btn.disabled = true; btn.textContent = 'Creating…';
        const projectId = layer.querySelector('[data-project]').value;
        try { localStorage.setItem(LAST_PROJECT, projectId); } catch { /* blocked */ }
        let members = [];
        try { const r = await api(`/api/projects/${projectId}/members`); if (r.ok) members = await r.json(); } catch { /* none */ }
        const out = [];
        for (const t of parsed) {
          try {
            const r = await api(`/api/projects/${projectId}/tasks`, { method: 'POST', json: { projectId, parentTaskId: null, title: (t.title || 'Untitled task').slice(0, 300), description: fromSteps ? (t.description || null) : t.text !== t.title ? t.text : null, status: 'todo', priority: null, startDate: null, dueDate: t.due?.iso ?? null, estimatedHours: null, dependencies: null, tags: null } });
            if (!r.ok) continue;
            const task = await r.json();
            const who = t.who && members.find(m => String(m.name).toLowerCase().startsWith(t.who.toLowerCase()) || String(m.name).toLowerCase().split(' ').some(w => w === t.who.toLowerCase()));
            if (who) await api(`/api/projects/tasks/${task.id}/assignees`, { method: 'POST', json: { userId: who.userId, role: 'assignee' } }).catch(() => {});
            out.push({ stickyId: t.id, task: { id: task.id, title: task.title, status: task.status, due: t.due?.label ?? null, people: who ? [String(who.name).split(' ')[0]] : [], project: projectNames.get(projectId), projectId } });
          } catch { /* skipped */ }
        }
        if (out.length < parsed.length) host.toast(`${parsed.length - out.length} could not be created`);
        close(out);
      });
    });
  }

  // ── Full-screen drawing ────────────────────────────────────────────────
  // The same scene (same Y.Map) mounted a second time over the page: edits made here show up in the inline block live.
  let fsClose = null;
  async function openDrawFullscreen(id) {
    if (fsClose || closing || !id) return;
    const layer = document.createElement('div');
    layer.className = 'nt-draw-fs'; layer.setAttribute('role', 'dialog'); layer.setAttribute('aria-modal', 'true'); layer.setAttribute('aria-label', 'Drawing');
    const stage = document.createElement('div'); stage.className = 'nt-draw-fs-stage';
    const close = document.createElement('button'); close.type = 'button'; close.className = 'nt-draw-fs-close'; close.textContent = 'Close';
    layer.append(stage, close); document.body.appendChild(layer);
    let inst = null;
    const finish = () => { if (!fsClose) return; fsClose = null; document.removeEventListener('keydown', onKey, true); inst?.destroy(); layer.remove(); };
    const onKey = e => { if (e.key === 'Escape') { e.stopPropagation(); finish(); } };
    fsClose = finish;
    close.addEventListener('click', finish);
    document.addEventListener('keydown', onKey, true);
    try { inst = await mountScene(stage, { ydoc, id, readOnly: !canEdit, mode: 'board', toast: m => host.toast(m), library: host.drawLibrary, files: host.drawFiles, beforeConvert: host.beforeCanvasConvert, awareness, comments: host.drawComments, tasks: host.drawTasks, pages: host.drawPages, taskSearch: host.drawTaskSearch, extras: host, popOutId: id }); if (!fsClose) inst.destroy(); }
    catch { host.toast('Couldn’t load the drawing tools. Check your connection.'); finish(); }
  }

  // ── Editor ───────────────────────────────────────────────────────────────
  let editor = null;
  let handlePos = -1;
  // Pictures in and out of the clipboard (clip-images.js): pasted web pictures are saved into the page.
  const pics = pictureClipboard(() => editor, {
    loadBlob: id => loadBlob(host, id),
    toast: m => host.toast(m),
    async upload(file) {
      const form = new FormData(); form.append('file', file, file.name);
      const r = await uploadXhr(`${apiBase}/api/notes/${pageId}/uploads`, form, await token());
      return r.ok ? r.json : null;
    },
    async importUrl(url) {
      const r = await api(`/api/notes/${pageId}/uploads/from-url`, { method: 'POST', json: { url } });
      return r.status === 200 ? r.json() : null;
    },
  });
  host.copyImage = node => pics.copy(node);
  const handleEl = createHandle(host);
  const Keys = Extension.create({
    name: 'noteKeys',
    addKeyboardShortcuts() {
      // the block the caret is in (top level of its list / column / callout), for duplicate and move
      const blockAt = () => {
        const { $from } = this.editor.state.selection;
        for (let d = $from.depth; d > 0; d--) {
          const parent = $from.node(d - 1).type.name;
          if (['doc', 'column', 'callout', 'detailsContent', 'blockquote'].includes(parent)) return { pos: $from.before(d), node: $from.node(d) };
        }
        return null;
      };
      const move = dir => { const b = blockAt(); const tr = b && moveBlockTr(this.editor.state, b.pos, dir); if (tr) this.editor.view.dispatch(tr.scrollIntoView()); return !!b; };
      return {
        'Mod-Shift-m': () => { commentSelection(); return true; },
        'Mod-Alt-0': () => this.editor.commands.setParagraph(),
        'Mod-Alt-1': () => this.editor.commands.toggleHeading({ level: 1 }),
        'Mod-Alt-2': () => this.editor.commands.toggleHeading({ level: 2 }),
        'Mod-Alt-3': () => this.editor.commands.toggleHeading({ level: 3 }),
        'Mod-Shift-9': () => this.editor.commands.toggleTaskList(),
        'Mod-Alt-c': () => this.editor.commands.toggleCodeBlock(),
        'Mod-d': () => { const b = blockAt(); if (b) this.editor.chain().insertContentAt(b.pos + b.node.nodeSize, b.node.toJSON()).run(); return true; },
        'Mod-Shift-ArrowUp': () => move(-1),
        'Mod-Shift-ArrowDown': () => move(1),
      };
    },
  });

  editor = new Editor({
    element: el,
    editable: canEdit,
    extensions: [
      ...baseExtensions(host, { rich: { onMenu: (pos, a) => rich?.openTodoMenu(pos, a), onChip: (pos, a, k) => rich?.onChip(pos, a, k) } }),
      Collaboration.configure({ document: ydoc, field: FIELD }),
      CollaborationCaret.configure({ provider: { awareness }, user: { name: opts.user.name, nik: opts.user.nik, color: opts.user.color } }),
      NotePlaceholder,
      SlashMenu.configure({ host }),
      FindReplace,
      CommentHighlights.configure({ resolve: resolveAnchor, onClick: id => call('OnThreadClick', id) }),
      Keys,
      ...(canEdit ? [DragHandle.configure({
        render: () => handleEl,
        onNodeChange: ({ pos }) => { handlePos = pos ?? -1; },
      })] : []),
    ],
    editorProps: {
      attributes: { class: 'nt-editor', spellcheck: 'true' },
      // Text other apps get from a copy: one line per paragraph, list markers kept (not a blank line between every block).
      clipboardTextSerializer: slice => sliceText(slice.content),
      // Word / Outlook HTML: real lists instead of "·" paragraphs, without Office's styles.
      transformPastedHTML: html => { try { return cleanOfficeHtml(html); } catch { return html; } },
      transformPasted: slice => pics.editorProps.transformPasted(slice),
      handleDOMEvents: pics.editorProps.handleDOMEvents,
      // A copied picture or screenshot is a file; a Word/Excel copy also carries a picture of itself, so text wins there.
      handlePaste: (view, event) => (preferFiles(event.clipboardData) && dropFiles(event.clipboardData?.files, todoInsertPos(view.state.doc, view.state.selection.from)))
        || !!rich?.handlePasteUrl(event),
      handleDrop: (view, event, _slice, moved) => {
        if (moved || !event.dataTransfer?.files?.length) return false;
        const at = view.posAtCoords({ left: event.clientX, top: event.clientY });
        const pos = at?.pos ?? view.state.selection.from;
        return dropFiles(event.dataTransfer.files, todoInsertPos(view.state.doc, pos));
      },
    },
  });

  // A whiteboard page: the same document and sync, but the page body is one endless canvas (scene "draw:board") instead of text.
  let board = null, boardFsExit = null;
  if (opts.kind === 'board' && opts.boardEl) {
    const boardEl = opts.boardEl;
    // In a pop-out tab (/notes/{id}/board) the board is the whole page; ?d= opens a drawing block of a normal page.
    const sceneId = opts.boardOnly && opts.drawingId ? opts.drawingId : 'board';
    let fsHome = null;
    const onBoardFs = on => {
      if (on && !fsHome) {
        fsHome = { parent: boardEl.parentNode, next: boardEl.nextSibling };
        document.body.appendChild(boardEl);
        boardEl.classList.add('nt-board-fs');
        document.documentElement.classList.add('nt-board-fs-open');
      } else if (!on && fsHome) {
        boardEl.classList.remove('nt-board-fs');
        document.documentElement.classList.remove('nt-board-fs-open');
        if (fsHome.parent?.isConnected) fsHome.parent.insertBefore(boardEl, fsHome.next?.parentNode === fsHome.parent ? fsHome.next : null); else boardEl.remove();
        fsHome = null;
      }
      return !!fsHome;
    };
    boardFsExit = () => onBoardFs(false);
    mountScene(boardEl, { ydoc, id: sceneId, readOnly: !canEdit, mode: 'board', templates: host.drawTemplates, prefs: host.drawPrefs, taskCreate: host.drawTaskCreate, lastProject: host.drawLastProjectName, onLinkOpen: url => host.openLink(url), toast: m => host.toast(m), onFullscreen: opts.boardOnly ? null : onBoardFs, library: host.drawLibrary, files: host.drawFiles, beforeConvert: host.beforeCanvasConvert, awareness, comments: host.drawComments, tasks: host.drawTasks, pages: host.drawPages, taskSearch: host.drawTaskSearch, extras: host, popOutId: sceneId, title: () => titleNow() })
      .then(inst => {
        if (closing) { inst.destroy(); return; }
        board = inst;
        startBoardThumbnail(inst);
        // "Use template → As a new board page": the new page puts the chosen template on itself once (within 2 minutes)
        try {
          const pend = JSON.parse(sessionStorage.getItem('ipop.wb.pendingTemplate') || 'null');
          if (pend?.key && Date.now() - pend.at < 120000 && canEdit && inst.count() === 0) { sessionStorage.removeItem('ipop.wb.pendingTemplate'); inst.applyTemplate(pend.key, { howTo: pend.howTo !== false, examples: pend.examples !== false }); }
          // "Templates" on the Whiteboards list: a blank board that opens with the Template Center showing
          const wantCenter = JSON.parse(sessionStorage.getItem('ipop.wb.openCenter') || 'null');
          if (wantCenter && Date.now() - wantCenter.at < 120000 && canEdit) { sessionStorage.removeItem('ipop.wb.openCenter'); setTimeout(() => inst.openCenter?.(), 300); }
        } catch { /* blocked */ }
      })
      .catch(() => host.toast('Couldn’t load the whiteboard tools. Check your connection and reload the page.'));
  }

  // The Whiteboards list shows a small picture of each board. Drawn here (the board knows how), a few seconds after the last edit
  // by someone who can edit, then uploaded; the server keeps one per board. Skipped for an empty board, a pop-out tab and viewers.
  let thumbTimer = 0, thumbBusy = false, thumbSig = '';
  function startBoardThumbnail(inst) {
    if (!canEdit || opts.boardOnly) return;
    const sceneMap = () => ydoc.getMap('wb:board');
    const schedule = () => { clearTimeout(thumbTimer); thumbTimer = setTimeout(draw, 6000); };
    async function draw() {
      if (closing || thumbBusy || !board) return;
      const count = board.count();
      if (!count) return;
      // changed since the last picture? (element count + the doc size is enough to skip idle re-uploads)
      const sig = `${count}:${Y.encodeStateVector(ydoc).length}:${[...sceneMap().keys()].length}`;
      if (sig === thumbSig) return;
      thumbBusy = true;
      try {
        const png = await board.png({ dark: false, scale: 0.5 });
        if (!png || closing) return;
        const form = new FormData(); form.append('file', png, 'board.png');
        const r = await api(`/api/notes/${pageId}/thumbnail`, { method: 'POST', body: form });
        if (r.ok) thumbSig = sig;
      } catch { /* the list keeps the previous picture */ }
      finally { thumbBusy = false; }
    }
    ydoc.on('update', (_u, origin) => { if (origin !== 'remote') schedule(); });
    // a board opened and left without edits still gets its first picture
    setTimeout(() => { if (!closing && !pageHasThumbnail) draw(); }, 4000);
  }
  const pageHasThumbnail = !!opts.hasThumbnail;

  const toolbar = createToolbar(editor, host);
  // Find & replace (Ctrl+F while the page has focus) and the word-count pill.
  host.copyBlockLink = pos => {
    const bid = ensureBlockId(editor, pos);
    if (!bid) return false;
    const url = `${location.origin}/notes/${pageId}?b=${bid}`;
    copyText(url).then(ok => host.toast(ok ? 'Link to this block copied' : url));
    return true;
  };
  // Opened from a block link (?b=<id>): wait for the page to arrive, then scroll to the block and flash it.
  const wantBlock = new URLSearchParams(location.search).get('b');
  if (wantBlock) {
    let tries = 0;
    const seek = () => {
      if (closing || editor.isDestroyed) return;
      const pos = findBlock(editor.state.doc, wantBlock);
      if (pos >= 0) {
        const node = editor.state.doc.nodeAt(pos);
        editor.view.dispatch(editor.state.tr.setMeta(flashKey, { from: pos, to: pos + node.nodeSize }).setMeta('addToHistory', false));
        const dom = editor.view.nodeDOM(pos);
        (dom?.nodeType === 1 ? dom : dom?.parentElement)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
        setTimeout(() => { try { if (!editor.isDestroyed) editor.view.dispatch(editor.state.tr.setMeta(flashKey, null).setMeta('addToHistory', false)); } catch { /* closed */ } }, 1800);
      } else if (++tries < 25) setTimeout(seek, 300);
    };
    seek();
  }
  const findBar = createFindBar(editor, host);
  el.before(findBar.dom);
  const stats = document.createElement('div');
  stats.className = 'nt-stats'; stats.setAttribute('aria-live', 'off');
  el.after(stats);
  const paintStats = debounce(() => {
    if (closing) return;
    const w = wordStats(editor.state.doc);
    stats.textContent = w.words ? `${w.words.toLocaleString()} word${w.words === 1 ? '' : 's'} · ${w.chars.toLocaleString()} characters · ${w.minutes} min read` : '';
  }, 400);
  editor.on('update', paintStats); paintStats();
  const onFindKey = e => {
    if (closing || !(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 'f' || e.shiftKey || e.altKey) return;
    if (!el.contains(document.activeElement) && !findBar.dom.contains(document.activeElement)) return;   // Ctrl+F elsewhere stays the browser's
    e.preventDefault(); findBar.open();
  };
  document.addEventListener('keydown', onFindKey, true);
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
        if (!j?.a || !j?.b) return null;          // a comment pinned to a whiteboard, not to text
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
    // The notification quotes the paragraph the mention sits in (not wherever the cursor happens to be by now).
    // Typed then deleted (or undone) before the batch went out: nobody to notify.
    const contexts = mentionContexts(editor.state.doc, new Set(mentionQueue.keys()));
    mentionQueue.clear();
    for (const [context, ids] of contexts) {
      await api(`/api/notes/${pageId}/mentions`, { method: 'POST', json: { userIds: ids, context } }).catch(() => {});
    }
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
  const upTray = createUploadTray();
  const upAborts = new Set();
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
        const ctl = new AbortController(); upAborts.add(ctl);
        const row = upTray.add(f.name, () => ctl.abort());
        try {
          const r = await uploadXhr(`${apiBase}/api/notes/${pageId}/uploads`, form, await token(), { onProgress: row.progress, signal: ctl.signal });
          if (!r.ok) { row.fail(r.json?.message || 'Failed'); host.toast(r.json?.message || `Couldn't upload ${f.name}`); failed++; continue; }
          row.done();
          const u = r.json;
          const node = { type: u.contentType?.startsWith('image/') ? 'noteImage' : 'noteFile', attrs: { id: u.id, name: u.fileName, size: u.size } };
          if (closing) return;
          const at = Math.min(toAbs(rel) ?? editor.state.doc.content.size, editor.state.doc.content.size);
          const before = editor.state.doc.content.size;
          editor.chain().insertContentAt(at, node).run();
          rel = toRel(Math.min(at + (editor.state.doc.content.size - before), editor.state.doc.content.size));
          if (node.type === 'noteImage') images.push(u.id);
          done++;
        } catch (err) {
          if (err?.name === 'AbortError') { row.done(); host.toast(`Cancelled ${f.name}`); } else { row.fail('Failed'); host.toast(`Couldn't upload ${f.name}`); }
          failed++;
        } finally { upAborts.delete(ctl); }
      }
      if (images.length >= 3 && !closing) rich?.offerGallery(images);
      if (list.length > 1 && !closing) host.toast(failed ? `Uploaded ${done} of ${list.length} files` : `Uploaded ${done} files`);
    })();
    return true;
  }

  // ── API for Blazor ───────────────────────────────────────────────────────
  if (refused) editor.setEditable(false); else if (lostEarly) stopEditing('not_found');
  if (canEdit && !refused) {
    const parked = takePending(localStorage, pageId);
    if (parked) { try { Y.applyUpdate(ydoc, unb64(parked)); host.toast('Restored edits that were not saved last time'); } catch { /* corrupt entry: dropped */ } }
  }

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
        case 'redo': chain.redo().run(); break;
      }
    },
    setThreads(threads, focus) {
      if (closing) return;
      boardThreads = threads || [];
      drawThreadListeners.forEach(f => f(boardThreads));
      const open = (threads || []).filter(t => !t.resolvedAt && t.anchor).map(t => ({ id: t.id, anchor: t.anchor }));
      editor.view.dispatch(editor.state.tr.setMeta(commentKey, { threads: open, focus: focus ?? null }));
    },
    focusThread(id) {
      if (closing) return;
      drawFocusListeners.forEach(f => f(String(id)));
      const st = commentKey.getState(editor.state);
      editor.view.dispatch(editor.state.tr.setMeta(commentKey, { threads: st.threads, focus: id }));
      const t = st.threads.find(x => x.id === id);
      const r = t && resolveAnchor(t.anchor, editor.state);
      if (r) editor.view.domAtPos(r.from)?.node?.parentElement?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    },
    setAccent(k) { accent = k; accentListeners.forEach(f => f()); },
    /// Comments in the page margin (margin-comments.js): measures where each commented text is and where the cards fit.
    /// scroller = the page's scroll area, page = the page column, cards = the margin column; net gets OnMarginMode('margin'|'bubbles').
    margin(scroller, page, cards, netRef) {
      const root = () => editor?.view?.dom;
      const lay = createMarginLayout({
        scroller, page, cards, editorRoot: root,
        draftTop: () => { const { from } = editor.state.selection; try { const c = editor.view.coordsAtPos(from); return c.top - scroller.getBoundingClientRect().top + scroller.scrollTop - 8; } catch { return 0; } },
        onMode: m => { try { netRef.invokeMethodAsync('OnMarginMode', m).catch(() => {}); } catch { /* disposed */ } },
      });
      // anything that changes the text moves the cards
      const onUpd = () => lay.schedule();
      editor.on('update', onUpd);
      return {
        layout: () => lay.layout(),
        reveal: id => lay.reveal(id),
        /// where a thread's popover goes: just below its highlighted text, kept on screen
        bubbleAt: id => {
          const el = root()?.querySelector(`[data-thread="${CSS.escape(String(id))}"]`);
          if (!el) return [Math.round(innerWidth / 2 - 160), 120];
          const r = el.getBoundingClientRect();
          return [Math.max(12, Math.min(Math.round(r.left), innerWidth - 332)), Math.max(60, Math.min(Math.round(r.bottom + 8), innerHeight - 260))];
        },
        destroy: () => { editor?.off?.('update', onUpd); lay.destroy(); },
      };
    },
    /// Page locked / unlocked (own action or someone else's): the text becomes read-only or editable again in place.
    setLocked(on) {
      if (closing) return;
      pageLocked = !!on;
      canEdit = canEditAccess && !pageLocked;
      try { editor.setEditable(canEdit); } catch { /* editor already gone */ }
      setAllReadOnly(!canEdit);
      refreshBoards();
    },
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
    /// Tidy up: suggestions for the current page. `title` is the page title (an empty one gets a "give it a title" suggestion).
    tidySuggestions(title) { return closing ? [] : tidyAnalyze(editor.getJSON(), { title }); },
    /// Applies the named rules in order as ONE editor step (a single Ctrl Z undoes the lot). Returns { applied, title? }.
    tidyApply(rules, title) {
      if (closing || !canEdit) return { applied: 0 };
      let d = editor.getJSON(), newTitle = null, applied = 0;
      for (const r of rules) { const out = tidyApply(d, r, { title: newTitle ?? title }); d = out.doc; if (out.title) newTitle = out.title; applied++; }
      if (rules.some(r => r !== 'title')) editor.chain().setContent(d, { emitUpdate: true }).run();
      return { applied, title: newTitle };
    },
    undo() { if (!closing && canEdit) editor.chain().focus().undo().run(); },
    /// Whiteboard: add a starter layout (placed beside whatever is already there). Returns how many elements were added.
    boardTemplate(name) { return board && canEdit ? board.applyTemplate(name, { keep: true }) : 0; },
    async boardPng(dark) { return board ? await board.png({ dark }) : null; },
    boardCount() { return board ? board.count() : 0; },
    isEmpty() { return !closing && editor.isEmpty; },
    /// The page as it is right now, as a base64 Yjs state (for comparing with a saved version).
    currentState() { return b64(Y.encodeStateAsUpdate(ydoc)); },
    focus() { if (!closing) editor.commands.focus('start'); },
    restore(state) {
      if (closing || !canEdit) return;
      const tmp = new Y.Doc();
      Y.applyUpdate(tmp, unb64(state));
      ydoc.transact(() => {
        editor.commands.setContent(yXmlFragmentToProsemirrorJSON(tmp.getXmlFragment(FIELD)));
        restoreScenes(ydoc, tmp);
      });
      tmp.destroy();
    },
    async flushNow() { await flush(); scheduleSnapshot.cancel(); await snapshot(); },
    async destroy() {
      if (closing) return;
      closing = true;
      scheduleSnapshot.cancel(); clearTimeout(thumbTimer);
      try { await flush(); await snapshot(); } catch { /* best effort */ }
      destroyed = true;
      removeAwarenessStates(awareness, [ydoc.clientID], 'local');
      await hub.invoke('LeavePage', pageId).catch(() => {});
      await hub.stop().catch(() => {});
      fsClose?.();
      board?.destroy(); board = null;
      closeLightboxes(host);
      window.removeEventListener('focus', host.accessChanged);
      window.removeEventListener('beforeunload', onBeforeUnload);
      document.querySelectorAll('.nt-share-ask').forEach(e => e.remove());
      toolbar.destroy(); blockMenu.destroy(); rich?.destroy();
      for (const c of upAborts) c.abort(); upTray.destroy();
      boardFsExit?.();
      findBar.destroy(); stats.remove(); paintStats.cancel(); document.removeEventListener('keydown', onFindKey, true);
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
    // Ctrl S: Notes saves by itself; instead of the browser's "Save page as HTML", push anything pending and say so.
    if (k === 's' && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey) {
      e.preventDefault(); e.stopImmediatePropagation(); net.invokeMethodAsync('OnHotkey', 'save').catch(() => {});
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

/// Photo cover of the open page (covers.js). opts: { apiBase, dotnet } — dotnet provides GetToken(), OnCoverProgress, OnCoverDragged.
export function covers(opts) { return createCovers(opts); }

export function copyText(text) { return navigator.clipboard?.writeText(text).then(() => true, () => false) ?? false; }
export { sidebar, place, openTab, focusSelect } from './sidebar.js';
