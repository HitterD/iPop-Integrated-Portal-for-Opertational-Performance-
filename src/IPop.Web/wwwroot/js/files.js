// Files page (Finder layout): keyboard, drag & drop, uploads with byte progress.
// Loaded as an ES module by Files.razor. One page instance at a time, so module state is fine.
//
// Rows carry data-fx-item="<path>"; anything that accepts a drop (folder rows, sidebar shares
// and sites) carries data-drop-server + data-drop-path. Internal drags move/copy on the NAS,
// external (desktop) drags upload into the drop target.

const INTERNAL = "application/x-ipop-files";
let s = null;

const typing = el => !!el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
const overlayOpen = () => !!document.querySelector(".modal-backdrop, .ctx-menu-backdrop, .drawer.on:not([aria-label='Preview'])");
const previewOpen = () => !!document.querySelector(".drawer.on[aria-label='Preview']");

export function attach(root, dotnet) {
  detach();
  s = { root, dotnet, xhr: null, cancelled: false, dragOver: null };

  s.onKey = e => {
    if (!s || !document.body.contains(s.root)) return;
    const mod = e.ctrlKey || e.metaKey;
    // Search focus works from anywhere on the page.
    if ((mod && e.key.toLowerCase() === "f") || (e.key === "/" && !typing(e.target))) {
      const box = s.root.querySelector("[data-fx-search]");
      if (box) { e.preventDefault(); box.focus(); box.select(); }
      return;
    }
    if (typing(e.target)) {
      if (e.key === "Escape" && e.target.matches("[data-fx-search]")) { e.preventDefault(); dotnet.invokeMethodAsync("OnKey", "Escape", false, false, 1); e.target.blur(); }
      return;
    }
    // Quick Look: Space toggles the preview closed again.
    if (previewOpen()) { if (e.key === " ") { e.preventDefault(); dotnet.invokeMethodAsync("OnKey", " ", false, false, 1); } return; }
    if (overlayOpen()) return;
    const handled = ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Enter", " ", "Delete", "F2", "Escape", "Home", "End"];
    const k = e.key;
    const isBack = k === "Backspace" || (e.altKey && k === "ArrowLeft");
    const isFwd = e.altKey && k === "ArrowRight";
    if (!(handled.includes(k) || isBack || (mod && ["a", "i", "o"].includes(k.toLowerCase())))) return;
    e.preventDefault();
    const key = isBack ? "Back" : isFwd ? "Forward" : (mod && k.length === 1 ? k.toLowerCase() : k);
    dotnet.invokeMethodAsync("OnKey", key, mod, e.shiftKey, columns()).then(() => requestAnimationFrame(reveal));
  };

  s.onDragStart = e => {
    const row = e.target.closest && e.target.closest("[data-fx-item]");
    if (!row || !s.root.contains(row)) return;
    e.dataTransfer.setData(INTERNAL, row.dataset.fxItem);
    e.dataTransfer.setData("text/plain", row.dataset.fxItem);
    e.dataTransfer.effectAllowed = "copyMove";
    row.classList.add("fx-dragging");
    dotnet.invokeMethodAsync("OnDragStart", row.dataset.fxItem);
  };
  s.onDragEnd = e => {
    const row = e.target.closest && e.target.closest("[data-fx-item]");
    if (row) row.classList.remove("fx-dragging");
    clearDrop();
  };
  s.onDragOver = e => {
    const types = Array.from(e.dataTransfer?.types || []);
    const internal = types.includes(INTERNAL);
    const external = types.includes("Files");
    if (!internal && !external) return;
    let target = e.target.closest && e.target.closest("[data-drop-path]");
    // A folder can't be dropped onto itself; external files fall back to the open folder.
    if (target && internal && target.dataset.fxItem && target.classList.contains("fx-dragging")) target = null;
    if (!target && external) target = s.root.querySelector("[data-fx-dropzone]");
    if (!target || target.dataset.dropPath === "") { clearDrop(); return; }
    e.preventDefault();
    e.dataTransfer.dropEffect = internal && (e.ctrlKey || e.altKey) ? "copy" : internal ? "move" : "copy";
    if (s.dragOver !== target) { clearDrop(); s.dragOver = target; target.classList.add("fx-drop"); }
  };
  s.onDragLeave = e => {
    if (s.dragOver && !s.dragOver.contains(e.relatedTarget)) clearDrop();
  };
  s.onDrop = e => {
    const target = s.dragOver;
    clearDrop();
    if (!target) return;
    e.preventDefault();
    const destServer = target.dataset.dropServer || "";
    const destPath = target.dataset.dropPath || "";
    const src = e.dataTransfer.getData(INTERNAL);
    if (src) {
      dotnet.invokeMethodAsync("OnDropItems", src, destServer, destPath, e.ctrlKey || e.altKey);
    } else if (e.dataTransfer.files?.length) {
      upload(Array.from(e.dataTransfer.files), destServer, destPath);
    }
  };
  s.onPick = e => {
    const input = e.target;
    if (!input.matches || !input.matches("[data-fx-upload]")) return;
    const files = Array.from(input.files || []);
    input.value = "";
    if (files.length) upload(files, "", "");
  };

  document.addEventListener("keydown", s.onKey);
  root.addEventListener("dragstart", s.onDragStart);
  root.addEventListener("dragend", s.onDragEnd);
  root.addEventListener("dragover", s.onDragOver);
  root.addEventListener("dragleave", s.onDragLeave);
  root.addEventListener("drop", s.onDrop);
  root.addEventListener("change", s.onPick);
  // Touch layouts open on tap instead of select-then-double-click.
  return window.matchMedia("(max-width: 767px), (pointer: coarse)").matches;
}

export function detach() {
  if (!s) return;
  document.removeEventListener("keydown", s.onKey);
  s.root.removeEventListener("dragstart", s.onDragStart);
  s.root.removeEventListener("dragend", s.onDragEnd);
  s.root.removeEventListener("dragover", s.onDragOver);
  s.root.removeEventListener("dragleave", s.onDragLeave);
  s.root.removeEventListener("drop", s.onDrop);
  s.root.removeEventListener("change", s.onPick);
  cancelUpload();
  s = null;
}

export function pickFiles() {
  s?.root.querySelector("[data-fx-upload]")?.click();
}

export function focusSearch() {
  const box = s?.root.querySelector("[data-fx-search]");
  if (box) { box.focus(); box.select(); }
}

export function reveal() {
  const el = s?.root.querySelector(".fx-item.is-focus");
  if (el) el.scrollIntoView({ block: "nearest" });
}

export function cancelUpload() {
  if (!s) return;
  s.cancelled = true;
  try { s.xhr?.abort(); } catch { /* already done */ }
}

function clearDrop() {
  if (s?.dragOver) s.dragOver.classList.remove("fx-drop");
  if (s) s.dragOver = null;
}

// Grid columns, so ArrowUp/Down can move a whole row in grid view.
function columns() {
  const items = s?.root.querySelectorAll(".fx-grid .fx-item");
  if (!items || items.length < 2) return 1;
  const top = items[0].offsetTop;
  let n = 0;
  for (const it of items) { if (it.offsetTop !== top) break; n++; }
  return Math.max(1, n);
}

// Sequential XHR uploads (fetch has no upload progress). The page hands out the endpoint and a
// bearer token per file so an access token that expires mid-batch is refreshed, not fatal.
async function upload(files, destServer, destPath) {
  if (!s) return;
  const d = s.dotnet;
  const plan = await d.invokeMethodAsync("BeginUpload", files.map(f => f.name), files.map(f => f.size), destServer, destPath);
  if (!plan) return;
  s.cancelled = false;
  for (let i = 0; i < files.length; i++) {
    if (!s || s.cancelled) break;
    let res = await send(plan, files[i], i, await d.invokeMethodAsync("UploadToken", false));
    if (res.status === 401 && s && !s.cancelled) res = await send(plan, files[i], i, await d.invokeMethodAsync("UploadToken", true));
    if (!s) return;
    await d.invokeMethodAsync("UploadFileDone", i, res.status >= 200 && res.status < 300, res.status, res.error || "");
  }
  if (s) await d.invokeMethodAsync("EndUpload", s.cancelled);
}

function send(plan, file, index, token) {
  return new Promise(resolve => {
    const xhr = new XMLHttpRequest();
    s.xhr = xhr;
    xhr.open("POST", plan.url);
    if (token) xhr.setRequestHeader("Authorization", "Bearer " + token);
    let last = 0;
    xhr.upload.onprogress = e => {
      const now = performance.now();
      if (now - last < 120 && e.loaded < e.total) return;
      last = now;
      s?.dotnet.invokeMethodAsync("UploadProgress", index, e.loaded);
    };
    xhr.onload = () => {
      let error = "";
      try { error = JSON.parse(xhr.responseText || "{}").message || ""; } catch { /* not JSON */ }
      resolve({ status: xhr.status, error });
    };
    xhr.onerror = () => resolve({ status: 0, error: "Network error" });
    xhr.onabort = () => resolve({ status: 0, error: "Cancelled" });
    const form = new FormData();
    form.append("server", plan.server);
    form.append("path", plan.path);
    form.append("file", file, file.name);
    xhr.send(form);
  });
}
