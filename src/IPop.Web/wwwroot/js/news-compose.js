// News composer page guard — the browser tab-close / reload warning.
//
// Blazor's NavigationLock covers in-app navigation (the composer asks through ConfirmService), but
// it cannot intercept the browser closing or reloading the tab. `beforeunload` cannot await a .NET
// call, so the composer pushes its dirty flag here after every render and this module answers
// synchronously. Loaded by Pages/NewsCompose.razor through a dynamic ES-module import, so no
// index.html <script> tag is involved.
//
// Its own module on purpose: ipop.js is shared by every page, and news-editor.js belongs to the
// rich editor.

let dirty = false;
let handler = null;

/** The composer's latest unsaved-changes state. Called after each render. */
export function setDirty(value) {
    dirty = !!value;
}

export function install() {
    if (handler) return;
    handler = event => {
        if (!dirty) return;
        // The two lines are the cross-browser contract for showing the native "leave site?" prompt.
        event.preventDefault();
        event.returnValue = "";
        return "";
    };
    window.addEventListener("beforeunload", handler);
}

export function remove() {
    if (handler) {
        window.removeEventListener("beforeunload", handler);
        handler = null;
    }
    dirty = false;
}

// ── drag & drop + keyboard file pick ─────────────────────────────────────────────────────────
//
// Blazor's DragEventArgs only carries file NAMES, so a real drop has to read the bytes here and
// hand them to .NET. The same helper wires Enter/Space on the drop zone to a file picker.

async function handFiles(dotNet, files, method) {
    if (!files.length) return;
    const payloads = [];
    for (const f of files) {
        const buf = new Uint8Array(await f.arrayBuffer());
        payloads.push({ name: f.name, contentType: f.type, data: buf });
    }
    dotNet.invokeMethodAsync(method, payloads).catch(() => { });
}

function acceptFilter(accept) {
    const tokens = (accept || "").split(",").map(s => s.trim().toLowerCase()).filter(Boolean);
    const exts = tokens.filter(s => s.startsWith("."));
    const mimes = tokens.filter(s => s.includes("/"));
    if (!exts.length && !mimes.length) return () => true;
    return (name, type) =>
        exts.some(e => name.toLowerCase().endsWith(e)) ||
        mimes.some(m => (type || "").toLowerCase().startsWith(m));
}

/** Wires drag/drop (and Enter/Space) on an element. `multiple` picks the .NET callback. */
export function installDrop(dotNet, elementId, multiple, accept) {
    const el = document.getElementById(elementId);
    if (!el || el.dataset.ncDrop === "1") return;
    el.dataset.ncDrop = "1";
    const ok = acceptFilter(accept);
    const stop = e => { e.preventDefault(); e.stopPropagation(); };
    // #12: the shared drop target appears while a file from Windows is over the zone.
    if (window.ipopMicro) ipopMicro.registerDrop(el, multiple ? "Drop to attach" : "Drop to set the cover",
        multiple ? "PDF, Office files or images" : "PNG, JPEG, GIF or WebP");

    el.addEventListener("dragover", e => { stop(e); el.classList.add("drag"); });
    el.addEventListener("dragenter", e => { stop(e); el.classList.add("drag"); });
    el.addEventListener("dragleave", e => { stop(e); el.classList.remove("drag"); });
    el.addEventListener("drop", e => {
        stop(e);
        el.classList.remove("drag");
        const all = [...(e.dataTransfer?.files || [])].filter(f => ok(f.name, f.type));
        handFiles(dotNet, multiple ? all.slice(0, 10) : all.slice(0, 1), multiple ? "OnDroppedFile" : "OnDroppedCover");
    });
    el.addEventListener("keydown", e => {
        if (e.key !== "Enter" && e.key !== " ") return;
        e.preventDefault();
        if (multiple) dotNet.invokeMethodAsync("OnPickFiles").catch(() => { });
    });
}

/** Temporary file input → bytes. Used by the drop zone's keyboard path. */
export function pickFiles(dotNet, accept) {
    const input = document.createElement("input");
    input.type = "file";
    input.multiple = true;
    if (accept) input.accept = accept;
    input.style.display = "none";
    document.body.appendChild(input);
    input.addEventListener("change", () => {
        const files = [...(input.files || [])];
        input.remove();
        handFiles(dotNet, files, "OnDroppedFile");
    });
    input.click();
}