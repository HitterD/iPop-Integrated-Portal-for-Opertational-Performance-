// News composer rich editor + reader renderer.
//
// Loaded as an ES module by Components/News/NewsRichEditor.razor (authoring) and
// Components/News/NewsBody.razor (reading). Mail's `ipop.rte` adapter and its Jodit usage are
// untouched: this module has its own editor registry, its own Jodit config and its own toolbar.
//
// ── Canonical media markup (the only thing ever stored / sent to the API) ────────────────────
//   <figure data-news-media="GUID" data-news-kind="image|video" contenteditable="false">
//     <figcaption>…</figcaption></figure>
//   <figure data-news-youtube="11charid" data-news-kind="youtube" contenteditable="false">…</figure>
//   <figure data-news-video-url="https://…mp4|webm" data-news-kind="video-url" contenteditable="false">…</figure>
//
// No `src`, no stream ticket and no base64 is ever stored: an ephemeral URL exists only in the
// editor DOM (on the `[data-nce-chrome]` node) and on the rendered reader page, and it is removed
// again by serialize() on every value change. The GUID is the canonical reference; resolving it to
// a URL is the caller's job (NewsRichEditor.MediaUrls / NewsBody.Media).
//
// Media blocks in the editor carry two kinds of children:
//   [data-nce-chrome]  editor-only chrome (thumbnail / <img> / <video>) — dropped on serialize
//   <figcaption>       user content — kept


const IMAGE_MAX = 5 * 1024 * 1024;      // 5 MB  — server re-checks signature and size
const VIDEO_MAX = 100 * 1024 * 1024;    // 100 MB — one video per post, server re-checks
const IMAGE_MIME = /^image\/(png|jpeg|jpg|gif|webp)$/i;
const VIDEO_MIME = /^video\/(mp4|webm)$/i;
const IMAGE_ACCEPT = "image/png,image/jpeg,image/gif,image/webp";
const VIDEO_ACCEPT = "video/mp4,video/webm";
const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const MEDIA_SEL = "[data-news-media],[data-news-youtube],[data-news-video-url]";
const DATA_IMG = /^data:image\//i;

/** Absolute URLs for the vendored assets, derived from this script's own <script> tag so the
 *  file needs no `import.meta.url` (it is loaded as a classic script, not a module). */
function assetUrl(rel) {
  const self = document.currentScript?.src;
  if (!self) return rel;
  try { return new URL(rel, self).href; } catch { return rel; }
}
const JODIT_CSS = assetUrl("../lib/jodit/jodit.fat.min.css");
const JODIT_JS = assetUrl("../lib/jodit/jodit.fat.min.js");

const editors = new Map();
let nextId = 1;
let joditPromise = null;

const esc = s => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const fmtSize = b => b < 1024 ? b + " B" : b < 1048576 ? (b / 1024).toFixed(1) + " KB" : (b / 1048576).toFixed(1) + " MB";
const playSpan = cls => `<span class="${cls}" aria-hidden="true"></span>`;

function loadScript(src) {
  return new Promise(resolve => {
    const s = document.createElement("script");
    s.src = src; s.async = true;
    s.onload = () => resolve(true);
    s.onerror = () => resolve(false);
    document.head.appendChild(s);
  });
}

/** Jodit UMD + its stylesheet, loaded once per page (shares the data-jodit marker with ipop.rte). */
function loadJodit() {
  if (window.Jodit) return Promise.resolve(true);
  if (!joditPromise) {
    if (!document.querySelector("link[data-jodit]")) {
      const l = document.createElement("link");
      l.rel = "stylesheet"; l.href = JODIT_CSS; l.setAttribute("data-jodit", "1");
      document.head.appendChild(l);
    }
    joditPromise = loadScript(JODIT_JS);
  }
  return joditPromise;
}

// ── media ids / urls ────────────────────────────────────────────────────────────────────────

/** Normalizes a caller-supplied media map to `{ idLower: {kind, url} }`.
 *  Accepts `{ "guid": "url" }` (kind unknown) or `{ "guid": { kind, url } }` (kind authoritative).
 *  Ids are trimmed and lowercased; HTML attribute names are already lowercased by the parser.
 *  Relative URLs (the API's `/api/news/composer/media/{id}?t=…`) are resolved against `base` —
 *  the API origin, which is usually a different origin from the page. */
function resolveUrl(u, base) {
  if (!u) return u;
  try { return new URL(u, base || document.baseURI).href; } catch { return u; }
}

function normalizeMedia(map, base) {
  const out = {};
  for (const k of Object.keys(map || {})) {
    const key = String(k).trim().toLowerCase();
    const v = map[k];
    if (!key || v == null) continue;
    out[key] = typeof v === "string"
      ? { kind: null, url: resolveUrl(v, base) }
      : { kind: v.kind || v.Kind || null, url: resolveUrl(v.url || v.Url || v.streamUrl || v.StreamUrl, base) };
  }
  return out;
}
const entryFor = (media, raw) => media[String(raw || "").trim().toLowerCase()] || null;

function isYouTubeId(v) { return /^[A-Za-z0-9_-]{11}$/.test(String(v || "")); }

/** Strict YouTube URL → 11-char video id, or null. Anything else is rejected. */
function youtubeIdFromUrl(u) {
  let url;
  try { url = new URL(String(u || "").trim()); } catch { return null; }
  if (url.protocol !== "https:" && url.protocol !== "http:") return null;
  const host = url.hostname.toLowerCase().replace(/^(www|m|music)\./, "");
  let id = "";
  if (host === "youtu.be") id = url.pathname.slice(1);
  else if (host === "youtube.com" || host === "youtube-nocookie.com") {
    if (url.pathname === "/watch") id = url.searchParams.get("v") || "";
    else if (url.pathname.startsWith("/embed/") || url.pathname.startsWith("/shorts/") || url.pathname.startsWith("/live/"))
      id = url.pathname.split("/")[2] || "";
  }
  id = String(id).split(/[?&#/]/)[0];
  return isYouTubeId(id) ? id : null;
}

/** Direct HTTPS MP4/WebM only — browser playback, never fetched server-side. */
function directVideoUrl(u) {
  const s = String(u || "").trim();
  if (!/^https:\/\//i.test(s)) return null;
  return /\.(mp4|webm)([?#].*)?$/i.test(s) ? s : null;
}

// ── serialize / hydrate ─────────────────────────────────────────────────────────────────────

/** One media block → its canonical markup. Returns null when the block is not storable.
 *  Only the id attributes are authoritative; `data-news-kind` is a UX hint and is re-derived
 *  from the element that is actually present. */
function canonicalMedia(el) {
  const fig = el.closest("figure") || el;
  const cap = fig.querySelector("figcaption");
  const capTxt = cap ? cap.textContent.replace(/\s+/g, " ").trim() : "";
  const capHtml = capTxt ? `<figcaption>${esc(capTxt)}</figcaption>` : "";
  // Internal media is keyed by the GUID and nothing else: never re-emit a caller-supplied kind.
  const rawId = fig.getAttribute("data-news-media");
  if (rawId != null) {
    const g = rawId.trim().toLowerCase();
    if (!GUID_RE.test(g)) return null;
    return `<figure data-news-media="${g}" contenteditable="false">${capHtml}</figure>`;
  }
  const yt = (fig.getAttribute("data-news-youtube") || "").trim();
  if (yt) {
    if (!isYouTubeId(yt)) return null;
    return `<figure data-news-youtube="${yt}" data-news-kind="youtube" contenteditable="false">${capHtml}</figure>`;
  }
  const url = directVideoUrl(fig.getAttribute("data-news-video-url"));
  if (!url) return null;
  return `<figure data-news-video-url="${esc(url)}" data-news-kind="video-url" contenteditable="false">${capHtml}</figure>`;
}

/** Serializes a DocumentFragment. There is NO `fragment.innerHTML` in the DOM — Chromium returns
 *  undefined for it, which silently turned every serialize/render into the string "undefined".
 *  `<template>` is still used to parse (its parser semantics are what we want), but the result is
 *  read back through a real element. */
function fragHtml(frag) {
  const out = document.createElement("div");
  out.appendChild(frag.cloneNode(true));
  return out.innerHTML;
}

/** Editor DOM → canonical HTML. Chrome, every ephemeral URL and any non-allow-listed node is
 *  dropped here, so what leaves the editor is already the store shape. */
function clean(html) {
  const t = document.createElement("template");
  t.innerHTML = String(html || "");
  // Jodit's own paste pipeline already strips these; this is the belt to its braces, and it also
  // covers media chrome written by older drafts.
  t.content.querySelectorAll("script,style,link,meta,noscript,iframe,object,embed,form,input,button,select,textarea,svg,math,video,audio,source").forEach(n => n.remove());
  t.content.querySelectorAll("*").forEach(n => {
    for (const a of Array.from(n.attributes)) {
      const name = a.name.toLowerCase();
      if (name.startsWith("on") || name === "srcdoc") n.removeAttribute(a.name);
      if (name === "src" && /^\s*(javascript|data):/i.test(a.value)) n.removeAttribute(a.name);
      if (name === "contenteditable") n.removeAttribute(a.name);
    }
    if (n.classList && n.classList.length === 0) n.removeAttribute("class");
  });
  t.content.querySelectorAll("[data-nce-chrome]").forEach(n => n.remove());
  t.content.querySelectorAll(MEDIA_SEL).forEach(el => {
    const fig = el.closest("figure") || el;
    const canon = canonicalMedia(fig);
    if (canon) {
      const holder = document.createElement("div");
      holder.innerHTML = canon;
      fig.replaceWith(holder.firstElementChild);
    } else {
      fig.remove();
    }
  });
  // Jodit's cleanHTML fillEmptyParagraph leaves <br> inside empty blocks; that is the stored shape.
  return fragHtml(t.content);
}

function serialize(st) {
  return clean(st.ed.editor.cloneNode(true).innerHTML);
}

function videoChrome(extra) {
  return `<div data-nce-chrome contenteditable="false" class="nce-vp">` +
    `<div class="nce-vp-thumb${extra && extra.cls ? " " + extra.cls : ""}">${playSpan("nce-play")}</div>` +
    `<div class="nce-vp-meta"><b>${esc(extra ? extra.title : "Video")}</b>` +
    `<span>${esc(extra ? extra.sub : "Video attachment · plays in the reader")}</span></div></div>`;
}

/** Canonical HTML → editor DOM (chrome re-added, media resolved from `media`).
 *  The manifest decides which element a GUID becomes — the stored `data-news-kind` is only a hint. */
function hydrate(html, media, base) {
  const t = document.createElement("template");
  t.innerHTML = String(html || "");
  const map = normalizeMedia(media, base);
  t.content.querySelectorAll(MEDIA_SEL).forEach(el => {
    const fig = el.closest("figure") || el;
    fig.removeAttribute("contenteditable");
    const hint = (fig.getAttribute("data-news-kind") || "").toLowerCase();
    let chrome;
    if (fig.hasAttribute("data-news-youtube") || hint === "youtube") {
      const id = (fig.getAttribute("data-news-youtube") || "").trim();
      chrome = videoChrome({ cls: "nce-yt", title: "YouTube", sub: "youtube.com/watch?v=" + id });
    } else if (fig.hasAttribute("data-news-video-url")) {
      const url = directVideoUrl(fig.getAttribute("data-news-video-url")) || "";
      chrome = videoChrome({ title: "Video link", sub: url });
    } else {
      const entry = entryFor(map, fig.getAttribute("data-news-media"));
      const kind = entry && entry.kind ? String(entry.kind).toLowerCase() : (hint === "video" ? "video" : "image");
      const url = entry ? entry.url : null;
      chrome = kind === "video"
        ? videoChrome({ title: "Video", sub: url ? "Video attachment · plays in the reader" : "Video not available" })
        : `<img data-nce-chrome contenteditable="false" alt=""${url ? ` src="${esc(url)}"` : ""}>`;
    }
    const holder = document.createElement("div");
    holder.innerHTML = chrome;
    fig.insertBefore(holder.firstElementChild, fig.firstChild);
  });
  return fragHtml(t.content);
}

/** Canonical HTML → readable markup. `media` maps GUID → ephemeral URL (or { kind, url }). */
function resolveBody(html, media, base) {
  const t = document.createElement("template");
  t.innerHTML = String(html || "");
  const map = normalizeMedia(media, base);
  // Defence in depth: the server sanitizes, but the renderer never trusts incoming markup either.
  t.content.querySelectorAll("script,style,link,meta,iframe,object,embed,form,input,button,svg,math").forEach(n => n.remove());
  t.content.querySelectorAll("*").forEach(n => {
    for (const a of Array.from(n.attributes)) {
      const name = a.name.toLowerCase();
      if (name.startsWith("on") || name === "srcdoc" || (name === "src" && /^\s*(javascript|data):/i.test(a.value))) n.removeAttribute(a.name);
    }
  });
  t.content.querySelectorAll(MEDIA_SEL).forEach(el => {
    const fig = el.closest("figure") || el;
    fig.removeAttribute("contenteditable");
    const hint = (fig.getAttribute("data-news-kind") || "").toLowerCase();
    let node = null;
    if (fig.hasAttribute("data-news-youtube")) {
      const id = (fig.getAttribute("data-news-youtube") || "").trim();
      if (isYouTubeId(id)) {
        // External media is outside iPop access control: nothing loads until the reader clicks.
        node = document.createElement("div");
        node.className = "nc-embed nc-embed-yt";
        node.setAttribute("data-nc-yt", id);
        node.setAttribute("role", "button");
        node.setAttribute("tabindex", "0");
        node.setAttribute("aria-label", "Play YouTube video (loads from youtube.com)");
        node.innerHTML = `<span class="nc-play-circle" aria-hidden="true"></span>` +
          `<span class="nc-embed-note">YouTube · loads when you press play</span>`;
      }
    } else if (fig.hasAttribute("data-news-video-url")) {
      const url = directVideoUrl(fig.getAttribute("data-news-video-url"));
      if (url) node = mediaElement("video", url, fig);
    } else if (fig.hasAttribute("data-news-media")) {
      const entry = entryFor(map, fig.getAttribute("data-news-media"));
      const kind = entry && entry.kind ? String(entry.kind).toLowerCase() : (hint === "video" ? "video" : "image");
      const url = entry ? entry.url : null;
      if (kind === "video") {
        node = mediaElement("video", url, fig);
        if (!url) node.setAttribute("data-news-unresolved", "1");
      } else {
        node = document.createElement("img");
        node.alt = fig.querySelector("figcaption")?.textContent?.trim() || "";
        node.loading = "lazy";
        node.decoding = "async";
        node.referrerPolicy = "no-referrer";
        if (url) node.src = url;
        else node.setAttribute("data-news-unresolved", "1");
      }
    }
    if (node) fig.insertBefore(node, fig.firstChild);
  });
  return fragHtml(t.content);
}

function mediaElement(tag, url, fig) {
  const node = document.createElement(tag);
  node.controls = true; node.preload = "metadata"; node.playsInline = true;
  node.setAttribute("controlslist", "nodownload");
  node.setAttribute("referrerpolicy", "no-referrer");
  node.setAttribute("aria-label", fig.querySelector("figcaption")?.textContent?.trim() || "Video");
  if (url) node.src = url;
  return node;
}

/** Wires the click-to-load YouTube posters inside a rendered body. */
function wireEmbeds(root) {
  root.querySelectorAll("[data-nc-yt]").forEach(el => {
    if (el.dataset.ncWired === "1") return;
    el.dataset.ncWired = "1";
    const load = () => {
      const id = el.getAttribute("data-nc-yt");
      if (!isYouTubeId(id)) return;
      const frame = document.createElement("iframe");
      frame.className = "nc-yt-frame";
      frame.src = `https://www.youtube-nocookie.com/embed/${encodeURIComponent(id)}?autoplay=1&rel=0`;
      frame.title = "YouTube video";
      frame.setAttribute("allow", "accelerometer; clipboard-write; encrypted-media; gyroscope; picture-in-picture");
      frame.setAttribute("allowfullscreen", "");
      frame.setAttribute("referrerpolicy", "strict-origin-when-cross-origin");
      el.replaceWith(frame);
    };
    el.addEventListener("click", load);
    el.addEventListener("keydown", e => {
      if (e.key === "Enter" || e.key === " ") { e.preventDefault(); load(); }
    });
  });
}

/** Renders canonical HTML into `root`. Called by NewsBody.razor. */
async function renderBody(root, html, media, base) {
  if (!root) return;
  root.innerHTML = resolveBody(html, media, base);
  wireEmbeds(root);
}

/** Tears down a rendered body: closes any player it started. */
function unmountBody(root) {
  if (root) root.querySelectorAll("iframe.nc-yt-frame").forEach(f => f.remove());
}

// ── `ipop.*` entry points ───────────────────────────────────────────────────────────────────
// Blazor calls through window.ipop (the established convention in this app) rather than through
// the module reference, so the JS is reached the same way Mail's ipop.rte is.
//
// ipop.newsEditor.* is id-addressed: mount(host, dotNet, opts) → id (0 when the bundling
// unavailable), then every later call uses that id. ipop.newsBody.* is element-addressed and is
// what NewsBody.razor calls.

function register() {
  const api = window.ipop || (window.ipop = {});
  api.newsEditor = {
    mount,
    unmount,
    cancelUpload,
    chooseFile,
    insertMedia,
    insertYouTube,
    insertVideoUrl,
    insertHtml,
    setHtml,
    setMedia,
    setReadOnly,
    getHtml,
    focus,
    exec,
    link,
    linkInfo,
    clean,
    resolveBody,
    isYouTubeId,
    youtubeIdFromUrl,
    directVideoUrl,
  };
  api.newsBody = {
    render: renderBody,
    resolve: resolveBody,
    unmount: unmountBody,
  };
  return true;
}

// index.html loads this file with a plain <script> tag, so registration happens on load. The
// guard keeps a second evaluation (or a host that imports it as a module) from wiping a registry
// that already holds live editors.
if (!window.ipop?.newsEditor) register();



// ── editor ──────────────────────────────────────────────────────────────────────────────────

function notify(st, payload) {
  st.dotNet?.invokeMethodAsync("OnUploadProgress", payload).catch(() => { });
}
function notice(st, message) {
  st.dotNet?.invokeMethodAsync("OnNotice", message).catch(() => { });
}

function saveRange(st) {
  try {
    const sel = st.ed.ew.getSelection();
    if (sel && sel.rangeCount && st.ed.editor.contains(sel.getRangeAt(0).commonAncestorContainer))
      st.range = sel.getRangeAt(0).cloneRange();
  } catch { }
}

function focusWithRange(st) {
  const ed = st.ed;
  if (!ed.s.isFocused()) ed.s.focus();
  if (st.range) {
    // A range saved before a re-render can point at a detached node; restoring it silently
    // throws and the insert then lands nowhere. Drop it and use the current caret instead.
    const node = st.range.startContainer;
    if (node && node.isConnected) { try { ed.s.selectRange(st.range); } catch { st.range = null; } }
    else st.range = null;
  }
}

/** A media block is atomic. If the caret sits inside one (the author clicked its caption, or a
 *  previous insert left it there), inserting would nest the new block as a child of the old one —
 *  and the serializer, which resolves each figure to its nearest ancestor, would then collapse
 *  both to the outer id and silently lose the new media. Move the caret out first. */
function escapeMediaBlock(st) {
  const ed = st.ed;
  const cur = ed.s.current();
  if (!cur) return;
  const fig = Jodit.modules.Dom.closest(cur, "figure", ed.editor);
  if (!fig) return;
  const after = fig.nextSibling;
  try {
    if (after) ed.s.setCursorBefore(after);
    else ed.s.setCursorAfter(fig);
  } catch { /* leave the caret alone rather than throw mid-insert */ }
}

function flush(id) {
  const st = editors.get(id);
  if (!st) return;
  st.ed.synchronizeValues();
  st.ed.e.fire("change");
  queueState(id);
}

/** Removes any base64 image Jodit may have produced on its own (belt and braces). */
function dropDataImages(st) {
  let dropped = 0;
  st.ed.editor.querySelectorAll("img").forEach(img => {
    const src = img.getAttribute("src") || "";
    if (DATA_IMG.test(src)) { img.remove(); dropped++; }
  });
  if (dropped) notice(st, "Images are uploaded to iPop, not pasted as data. That image was dropped — use the image button or drag the file in.");
}

/** The upload URL for one kind. A single configured URL is enough: when it does not already
 *  carry an explicit `kind`, the kind is appended as a query parameter, so image and video can
 *  share one endpoint (the composer media route takes its kind from the query string). */
function uploadUrlFor(st, kind) {
  const base = kind === "video" ? (st.opts.videoUploadUrl || st.opts.uploadUrl) : st.opts.uploadUrl;
  if (!base) return null;
  try {
    const u = new URL(base, document.baseURI);
    if (!u.searchParams.has("kind")) u.searchParams.set("kind", kind);
    return u.href;
  } catch {
    return base + (base.includes("?") ? "&" : "?") + "kind=" + encodeURIComponent(kind);
  }
}

/** The bearer for an upload. Asked just-in-time rather than kept from mount, because a composer
 *  session easily outlives the access token. `refresh` asks .NET to renew first (used once after
 *  a 401); the mount-time token is only a fallback for a host that supplies no callback. */
async function currentToken(st, refresh) {
  try {
    const t = await st.dotNet?.invokeMethodAsync(refresh ? "RefreshUploadToken" : "GetUploadToken");
    if (t) return t;
  } catch { /* fall through to the mount-time token */ }
  return st.opts.token || null;
}

/** POSTs one file to the composer media endpoint. Progress is reported on the way. */
function postFile(st, file, kind, token) {
  const url = uploadUrlFor(st, kind);
  if (!url) return Promise.reject(new Error("no-upload-endpoint"));
  const name = file.name || (kind === "video" ? "video" : "image");
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open("POST", url, true);
    if (token) xhr.setRequestHeader("Authorization", "Bearer " + token);
    xhr.upload.onprogress = e => {
      if (e.lengthComputable) notify(st, { kind, name, state: "uploading", percent: Math.round(e.loaded / e.total * 100) });
    };
    xhr.onload = () => {
      let data = null;
      try { data = JSON.parse(xhr.responseText); } catch { }
      if (xhr.status >= 200 && xhr.status < 300 && data) { resolve(data); return; }
      const err = new Error((data && (data.message || data.error || data.detail)) || `Upload failed (${xhr.status})`);
      err.status = xhr.status;   // so the caller can tell "session expired" from "refused"
      reject(err);
    };
    xhr.onerror = () => reject(new Error("Network error while uploading."));
    xhr.onabort = () => reject(new Error("Upload cancelled."));
    st.lastXhr = xhr;   // so cancelUpload() can abort the one in flight
    const fd = new FormData();
    // The File object goes straight into the FormData: no ArrayBuffer, no byte[] hop.
    fd.append("file", file, name);
    xhr.send(fd);
  });
}

/** Uploads with one silent renewal on 401: the token is refreshed through .NET and the file is
 *  re-sent. Buffering the file twice is fine — the browser keeps it on disk, not in WASM memory. */
async function uploadWithRefresh(st, file, kind) {
  try {
    return await postFile(st, file, kind, await currentToken(st, false));
  } catch (err) {
    if (err && err.status !== 401) throw err;
    const fresh = await currentToken(st, true);
    if (!fresh) throw err;
    return await postFile(st, file, kind, fresh);
  }
}

/** Media descriptor from the upload response. Tolerates the field-name variants still in flux. */
function readMedia(d, kind, file) {
  const mediaId = d.mediaId ?? d.MediaId ?? d.id ?? d.Id;
  return {
    mediaId: mediaId ? String(mediaId).toLowerCase() : null,
    kind,
    url: d.streamUrl ?? d.StreamUrl ?? d.url ?? d.Url ?? d.previewUrl ?? d.PreviewUrl ?? null,
    name: d.fileName ?? d.FileName ?? d.name ?? d.Name ?? file.name,
    contentType: d.contentType ?? d.ContentType ?? file.type,
    size: d.size ?? d.Size ?? file.size,
  };
}

async function handleFiles(st, files, kind) {
  const isVideo = kind === "video";
  const max = isVideo ? (st.opts.maxVideoBytes || VIDEO_MAX) : (st.opts.maxImageBytes || IMAGE_MAX);
  const okMime = isVideo ? VIDEO_MIME : IMAGE_MIME;

  for (const file of files) {
    const mime = file.type || "";
    if (!okMime.test(mime)) {
      notice(st, isVideo
        ? `${file.name}: only MP4 or WebM video can be inserted. Other files go in the Attachments panel.`
        : `${file.name}: only PNG, JPEG, GIF or WebP images can be inserted.`);
      continue;
    }
    if (file.size > max) {
      notice(st, `${file.name} is ${fmtSize(file.size)} — the limit is ${fmtSize(max)}.`);
      notify(st, { kind, name: file.name, state: "failed", percent: 0, message: "Too large" });
      continue;
    }
    if (!st.opts.uploadUrl) {
      // Foundation default: no endpoint configured, so the host owns the upload. The blob is
      // handed over as an object URL; the host must revoke it after fetching.
      // Object URLs cannot be revoked here, or an upload that is still in flight would break.
      const objectUrl = URL.createObjectURL(file);
      try {
        await st.dotNet?.invokeMethodAsync("OnBlobReady", {
          kind, name: file.name, contentType: mime, size: file.size, objectUrl,
        });
      } catch { }
      notice(st, `${file.name} is ready for upload.`);
      continue;
    }
    saveRange(st);
    // Resolve the editor id once: the DOM lookups below run from listeners that outlive this call.
    const edId = id_of(st);
    notify(st, { kind, name: file.name, state: "uploading", percent: 0 });
    st.uploads = (st.uploads || 0) + 1;
    const ph = isVideo ? null : uploadPlaceholder(st);
    try {
      const raw = await uploadWithRefresh(st, file, kind);
      const m = readMedia(raw, kind, file);
      if (!m.mediaId || !GUID_RE.test(m.mediaId)) throw new Error("The server did not return a media id.");
      notify(st, { kind, name: file.name, state: "done", percent: 100 });
      takePlaceholder(st, ph);
      insertMedia(edId, { mediaId: m.mediaId, kind, url: m.url, name: m.name, sizeText: fmtSize(m.size) });
      revealMedia(st, m.mediaId);
      st.dotNet?.invokeMethodAsync("OnMediaUploaded", {
        mediaId: m.mediaId, kind, url: m.url, name: m.name,
        contentType: m.contentType, size: m.size,
      }).catch(() => { });
    } catch (err) {
      const message = err && err.message === "no-upload-endpoint"
        ? "Uploads are not available in this editor yet."
        : (err && err.message) || "Upload failed.";
      notify(st, { kind, name: file.name, state: "failed", percent: 0, message });
      notice(st, `${file.name}: ${message}`);
    } finally {
      if (ph && ph.isConnected) ph.remove();
      st.uploads = Math.max(0, (st.uploads || 1) - 1);
      queueState(edId);
    }
  }
}

// ── Micro-interaction #14: a shimmering placeholder holds the spot while an image uploads ──
// It is editor chrome ([data-nce-chrome]), so serialize() never stores it.
function uploadPlaceholder(st) {
  if (!window.ipopMicro) return null;
  try {
    focusWithRange(st);
    escapeMediaBlock(st);
    const ph = st.ed.editor.ownerDocument.createElement("div");
    ph.className = "mi-paste-ph";
    ph.setAttribute("data-nce-chrome", "");
    ph.contentEditable = "false";
    ph.textContent = "Adding image…";
    st.ed.s.insertNode(ph, true, false);
    return ph;
  } catch { return null; }
}
/** The upload finished: the image goes exactly where its placeholder was. */
function takePlaceholder(st, ph) {
  if (!ph || !ph.isConnected) return;
  try {
    const r = ph.ownerDocument.createRange();
    r.setStartBefore(ph);
    r.collapse(true);
    st.range = r;
  } catch { }
  ph.remove();
}
function revealMedia(st, mediaId) {
  if (!window.ipopMicro || ipopMicro.reduced()) return;
  const img = st.ed.editor.querySelector(`figure[data-news-media="${String(mediaId).toLowerCase()}"] img`);
  if (!img) return;
  img.classList.add("mi-revealed");
  setTimeout(() => img.classList.remove("mi-revealed"), 500);
}

function id_of(st) {
  for (const [k, v] of editors) if (v === st) return k;
  return 0;
}

// ── public API ──────────────────────────────────────────────────────────────────────────────

async function mount(host, dotNet, opts) {
  if (!host) return 0;
  // index.html loads this script and the module stylesheet directly; nothing to inject here.
  if (!await loadJodit() || !window.Jodit) return 0;

  const id = nextId++;
  host.innerHTML = "";
  const area = document.createElement("textarea");
  host.appendChild(area);
  host.classList.add("nce-jodit-host");

  const ed = Jodit.make(area, {
    toolbar: false, statusbar: false, showCharsCounter: false, showWordsCounter: false,
    showXPathInStatusbar: false, showPlaceholder: true, placeholder: opts.placeholder || "",
    language: "en", height: "auto", minHeight: 340, iframe: false, beautifyHTML: false,
    defaultFontSizePoints: "pt", spellcheck: ipop.spellcheck(), toolbarInlineForSelection: false,
    askBeforePasteFromWord: false, askBeforePasteHTML: false,
    defaultActionOnPasteFromWord: "insert_as_html", defaultActionOnPaste: "insert_as_html",
    processPasteFromWord: true, nl2brInPlainText: true,
    // News never stores base64 and never lets Jodit upload on its own: the module owns both paths.
    uploader: { insertImageAsBase64URI: false, imagesExtensions: ["jpg", "jpeg", "png", "gif", "webp"] },
    enableDragAndDropFileToEditor: false,
    disablePlugins: ["about", "print", "preview", "powered-by-jodit", "speech-recognize", "ai-assistant", "xpath", "stat", "source", "fullsize", "filebrowser"],
    cleanHTML: {
      // Object form: getHash() uppercases the tag keys and leaves the attribute keys alone.
      allowTags: {
        P: { style: true }, DIV: { style: true, class: true, "data-nce-chrome": true, contenteditable: true },
        SPAN: { style: true, class: true }, BR: true, HR: true,
        H1: { style: true }, H2: { style: true }, H3: { style: true }, H4: { style: true }, H5: { style: true }, H6: { style: true },
        STRONG: true, B: true, EM: true, I: true, U: true, S: true, DEL: true, INS: true, SUB: true, SUP: true, MARK: true, SMALL: true,
        A: { href: true, title: true, target: true, rel: true },
        UL: true, OL: { start: true }, LI: true, BLOCKQUOTE: true, PRE: true, CODE: true,
        DL: true, DT: true, DD: true, CITE: true, Q: true, ABBR: true, TIME: true,
        FIGURE: { class: true, contenteditable: true, "data-news-media": true, "data-news-kind": true, "data-news-youtube": true, "data-news-video-url": true },
        FIGCAPTION: { style: true },
        IMG: { src: true, alt: true, width: true, height: true, "data-nce-chrome": true, contenteditable: true },
        TABLE: { style: true }, THEAD: true, TBODY: true, TFOOT: true, TR: true,
        TD: { colspan: true, rowspan: true, style: true }, TH: { colspan: true, rowspan: true, style: true }, CAPTION: true,
      },
      denyTags: { SCRIPT: true, IFRAME: true, OBJECT: true, EMBED: true, SVG: true, MATH: true, STYLE: true, FORM: true, INPUT: true, BUTTON: true, TEXTAREA: true, SELECT: true, LINK: true, META: true, VIDEO: true, AUDIO: true, SOURCE: true },
      replaceOldTags: { i: "em", b: "strong" },
      removeEventAttributes: true, removeOnError: true, safeJavaScriptLink: true, safeLinksTarget: true,
      convertUnsafeEmbeds: false, sandboxIframesInContent: false,
    },
  });

  const st = { ed, dotNet, opts: opts || {}, base: opts?.apiBase || document.baseURI, media: normalizeMedia(opts && opts.media, opts && opts.apiBase), range: null, lastValue: "", lastState: "", changeT: 0, stateT: 0, uploads: 0, lastXhr: null };
  editors.set(id, st);

  ed.value = hydrate(opts && opts.html, st.media, st.base);
  try { ed.history.clear(); } catch { }
  st.lastValue = serialize(st);

  ed.e.on("beforePaste", ev => {
    const dt = ev && ev.clipboardData;
    if (!dt) return;
    const types = Array.from(dt.types || []);
    if (types.includes("text/html") || types.includes("Files")) return;
    const text = dt.getData("text/plain");
    if (!text) return;
    const escT = l => l.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/\t/g, "\u00a0\u00a0\u00a0\u00a0").replace(/ {2}/g, " \u00a0");
    const lines = text.replace(/\r\n?/g, "\n").split("\n");
    if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();
    ev.preventDefault();
    ed.s.insertHTML(lines.length === 1 ? escT(lines[0]) : lines.map(l => l ? `<p>${escT(l)}</p>` : "<p><br></p>").join(""));
    return false;
  });

  // Paste / drop of files: captured before Jodit so nothing becomes base64 and nothing is dropped.
  const takeFiles = ev => {
    const dt = ev.clipboardData || ev.dataTransfer;
    const files = dt ? Array.from(dt.files || []) : [];
    if (!files.length) return false;
    ev.preventDefault();
    ev.stopPropagation();
    saveRange(st);
    const images = files.filter(f => IMAGE_MIME.test(f.type));
    const videos = files.filter(f => VIDEO_MIME.test(f.type)).slice(0, 1);
    if (!images.length && !videos.length) {
      notice(st, "Only PNG, JPEG, GIF or WebP images and MP4/WebM video can be inserted in the body. Other files belong in the Attachments panel.");
      return true;
    }
    if (images.length) handleFiles(st, images, "image");
    if (videos.length) handleFiles(st, videos, "video");
    return true;
  };
  ed.editor.addEventListener("paste", takeFiles, true);
  ed.editor.addEventListener("drop", takeFiles, true);
  // #12: dragging a file from Windows over the body shows the shared drop target.
  if (window.ipopMicro) ipopMicro.registerDrop(host, "Drop to insert", "PNG, JPEG, GIF or WebP images · MP4 or WebM video");
  ed.editor.addEventListener("dragover", ev => {
    if (ev.dataTransfer && Array.from(ev.dataTransfer.types || []).includes("Files")) ev.preventDefault();
  }, true);

  ed.e.on("change", () => {
    clearTimeout(st.changeT);
    st.changeT = setTimeout(() => {
      dropDataImages(st);
      const v = serialize(st);
      if (v === st.lastValue) return;
      st.lastValue = v;
      dotNet.invokeMethodAsync("OnChange", v).catch(() => { });
      queueState(id);
    }, 200);
    queueState(id);
  });
  ed.e.on("changeSelection keyup mouseup focus", () => queueState(id));
  ed.e.on("blur", () => saveRange(st));
  ed.e.on("keydown", e => {
    if (!(e.ctrlKey || e.metaKey) || e.isComposing) return;
    if (e.key === "k" || e.key === "K") {
      e.preventDefault();
      saveRange(st);
      dotNet.invokeMethodAsync("OnLinkShortcut").catch(() => { });
      return false;
    }
  });

  if (opts && opts.readOnly) { try { ed.setReadOnly(true); } catch { } }
  queueState(id);
  return id;
}

/** Default foundation: opens the OS picker and hands the blob to the host. */
function chooseFile(id, kind) {
  const st = editors.get(id);
  if (!st) return;
  const input = document.createElement("input");
  input.type = "file";
  input.accept = kind === "video" ? VIDEO_ACCEPT : IMAGE_ACCEPT;
  input.style.display = "none";
  document.body.appendChild(input);
  input.addEventListener("change", () => {
    const file = input.files && input.files[0];
    input.remove();
    if (file) {
      saveRange(st);
      handleFiles(st, [file], kind === "video" ? "video" : "image");
    }
  });
  input.click();
}

function unmount(id) {
  const st = editors.get(id);
  if (!st) return;
  clearTimeout(st.changeT); clearTimeout(st.stateT);
  try { st.lastXhr?.abort(); } catch { }
  try { st.ed.destruct(); } catch { }
  editors.delete(id);
}

/** Aborts the upload in flight, if any. The XHR's onabort rejects the pending promise, so the
 *  failure path and the progress reset happen exactly as they would on a network error. */
function cancelUpload(id) {
  const st = editors.get(id);
  try { st?.lastXhr?.abort(); } catch { }
}

/** The editor overrides serialization so `st.ed.value` is never the store shape. Every entry
 *  point that reads HTML out of an editor must come through here. */
function readHtml(id) {
  const st = editors.get(id);
  return st ? serialize(st) : "";
}

function exec(id, cmd, arg) {
  const st = editors.get(id);
  if (!st) return;
  const ed = st.ed;
  focusWithRange(st);
  ed.execCommand(cmd, false, arg ?? null);
  flush(id);
}

function insertDivider(id) { exec(id, "insertHorizontalRule"); }

function link(id, url, text) {
  const st = editors.get(id);
  if (!st || !url) return;
  const ed = st.ed;
  focusWithRange(st);
  const cur = ed.s.current();
  const a = cur && Jodit.modules.Dom.closest(cur, "a", ed.editor);
  if (a) {
    a.setAttribute("href", url);
    if (text && text !== a.textContent) a.textContent = text;
  } else if (ed.s.isCollapsed()) {
    const el = ed.createInside.element("a");
    el.setAttribute("href", url);
    el.textContent = text || url;
    ed.s.insertNode(el, false, true);
    ed.s.setCursorAfter(el);
  } else {
    ed.execCommand("createLink", false, url);
  }
  flush(id);
}

function linkInfo(id) {
  const st = editors.get(id);
  if (!st) return null;
  const ed = st.ed;
  let range = null;
  try { const sel = ed.ew.getSelection(); if (sel && sel.rangeCount) range = sel.getRangeAt(0); } catch { }
  if (!range || !ed.editor.contains(range.commonAncestorContainer)) range = st.range;
  else st.range = range.cloneRange();
  const node = range ? range.commonAncestorContainer : null;
  const a = node ? Jodit.modules.Dom.closest(node, "a", ed.editor) : null;
  return { href: a ? a.getAttribute("href") || "" : "", text: a ? a.textContent : (range ? range.toString() : "") };
}

/** Inserts an uploaded image/video block at the caret. Kind: "image" | "video". */
function insertMedia(id, m) {
  const st = editors.get(id);
  if (!st || !m || !GUID_RE.test(String(m.mediaId || ""))) return;
  const g = String(m.mediaId).toLowerCase();
  const name = m.name || (m.kind === "video" ? "video" : "image");
  const meta = [name, m.sizeText].filter(Boolean).join(" · ");
  const html = m.kind === "video"
    ? `<figure data-news-media="${g}" data-news-kind="video">${videoChrome({ title: name, sub: [m.sizeText, "video", "plays in the reader"].filter(Boolean).join(" · ") })}` +
      `<figcaption>${esc(name)}${m.sizeText ? " · " + esc(m.sizeText) : ""}</figcaption></figure><p><br></p>`
    : `<figure data-news-media="${g}" data-news-kind="image">` +
      `<img data-nce-chrome contenteditable="false" alt=""${m.url ? ` src="${esc(m.url)}"` : ""}>` +
      `<figcaption>${esc(meta)}</figcaption></figure><p><br></p>`;
  focusWithRange(st);
  escapeMediaBlock(st);
  st.ed.s.insertHTML(html);
  flush(id);
}

/** Inserts a YouTube block from a validated 11-char id. */
function insertYouTube(id, payload) {
  const st = editors.get(id);
  const vid = payload && payload.videoId;
  if (!st || !isYouTubeId(vid)) return;
  const title = (payload && payload.title) || "YouTube video";
  focusWithRange(st);
  st.ed.s.insertHTML(
    `<figure data-news-youtube="${vid}" data-news-kind="youtube">` +
    `${videoChrome({ cls: "nce-yt", title, sub: "youtube.com/watch?v=" + vid })}` +
    `<figcaption>${esc(title)}</figcaption></figure><p><br></p>`);
  flush(id);
}

/** Inserts a direct HTTPS MP4/WebM block. */
function insertVideoUrl(id, payload) {
  const st = editors.get(id);
  const url = directVideoUrl(payload && payload.url);
  if (!st || !url) return;
  focusWithRange(st);
  st.ed.s.insertHTML(
    `<figure data-news-video-url="${esc(url)}" data-news-kind="video-url">` +
    `${videoChrome({ title: "Video link", sub: url })}` +
    `<figcaption>${esc(url)}</figcaption></figure><p><br></p>`);
  flush(id);
}

function insertHtml(id, html) {
  const st = editors.get(id);
  if (!st || !html) return;
  focusWithRange(st);
  st.ed.s.insertHTML(html);
  flush(id);
}

/** Replaces the document. `media` re-resolves every media placeholder. */
function setHtml(id, html, media) {
  const st = editors.get(id);
  if (!st) return;
  st.media = normalizeMedia(media, st.base);
  const next = hydrate(html, st.media, st.base);
  if (st.ed.value === next) return;
  st.ed.value = next;
  st.lastValue = serialize(st);
  try { st.ed.history.clear(); } catch { }
  queueState(id);
}

/** Re-resolves media URLs in place. Never touches the caret and never fires a change. */
function setMedia(id, media) {
  const st = editors.get(id);
  if (!st) return;
  st.media = normalizeMedia(media, st.base);
  st.ed.editor.querySelectorAll(MEDIA_SEL).forEach(fig => {
    const entry = entryFor(st.media, fig.getAttribute("data-news-media"));
    if (!entry || !entry.url) return;
    const url = entry.url;
    const kind = entry.kind ? String(entry.kind).toLowerCase() : (fig.getAttribute("data-news-kind") || "image").toLowerCase();
    if (kind === "image") {
      const img = fig.querySelector("img[data-nce-chrome]");
      if (img && img.getAttribute("src") !== url) img.src = url;
    } else if (kind === "video") {
      const v = fig.querySelector("video[data-nce-chrome-media]");
      if (v && v.getAttribute("src") !== url) v.src = url;
    }
  });
}

function getHtml(id) {
  return readHtml(id);
}

function focus(id) {
  const st = editors.get(id);
  if (st) focusWithRange(st);
}

/** Read-only state can change while mounted (the host disables the form during publish). */
function setReadOnly(id, on) {
  const st = editors.get(id);
  if (!st) return;
  try { st.ed.setReadOnly(!!on); } catch { }
}

function queueState(id) {
  const st = editors.get(id);
  if (!st) return;
  clearTimeout(st.stateT);
  st.stateT = setTimeout(() => reportState(id), 120);
}

function reportState(id) {
  const st = editors.get(id);
  if (!st) return;
  const ed = st.ed;
  const q = c => { try { return document.queryCommandState(c); } catch { return false; } };
  const cur = ed.s.current();
  const el = cur ? (cur.nodeType === 1 ? cur : cur.parentElement) : null;
  const inside = !!(el && ed.editor.contains(el));
  let block = "p";
  if (inside) {
    const b = Jodit.modules.Dom.closest(el, Jodit.modules.Dom.isBlock, ed.editor);
    if (b) block = b.nodeName.toLowerCase();
  }
  const canon = serialize(st);
  const text = canon.replace(/<[^>]*>/g, " ").replace(/&[a-z#0-9]+;/gi, " ");
  const state = {
    bold: q("bold"), italic: q("italic"), underline: q("underline"), strike: q("strikeThrough"),
    ul: q("insertUnorderedList"), ol: q("insertOrderedList"),
    align: q("justifyCenter") ? "center" : q("justifyRight") ? "right" : q("justifyFull") ? "full" : "left",
    block,
    inLink: !!(inside && Jodit.modules.Dom.closest(el, "a", ed.editor)),
    words: text.split(/\s+/).filter(Boolean).length,
    mediaCount: ed.editor.querySelectorAll(MEDIA_SEL).length,
    uploadsInFlight: st.uploads || 0,
    canUndo: !!(ed.history && ed.history.canUndo && ed.history.canUndo()),
    canRedo: !!(ed.history && ed.history.canRedo && ed.history.canRedo()),
  };
  const json = JSON.stringify(state);
  if (json === st.lastState) return;
  st.lastState = json;
  st.dotNet?.invokeMethodAsync("OnState", state).catch(() => { });
}