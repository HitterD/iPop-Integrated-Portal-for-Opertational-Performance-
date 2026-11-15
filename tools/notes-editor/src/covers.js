// Photo cover of a Notes page. The picture is a bearer-protected attachment, so bytes are fetched with the token and shown
// from an object URL; this module owns those URLs (one per attachment, revoked on dispose).

export const MAX_COVER_BYTES = 10 * 1024 * 1024;
const OK_TYPES = new Set(['image/jpeg', 'image/png', 'image/webp']);

/// Client-side check before any bytes are sent, with the same wording the server answers with.
export function checkCoverFile(file) {
  if (!file) return 'Choose an image first.';
  if (file.size > MAX_COVER_BYTES) return `“${file.name}” is ${(file.size / 1048576).toFixed(1)} MB. Covers can be up to 10 MB — try a smaller photo.`;
  const ext = /\.(jpe?g|png|webp)$/i.test(file.name || '');
  if (!OK_TYPES.has(file.type) && !ext) return 'Use a JPG, PNG or WebP image.';
  return null;
}

/// Where the photo sits vertically: the drag distance (px) maps onto 0..100 of the part of the photo that is hidden.
export function nextPos(start, dragPx, boxHeight, imgHeight) {
  const hidden = Math.max(1, imgHeight - boxHeight);
  return Math.max(0, Math.min(100, Math.round(start - (dragPx / hidden) * 100)));
}

export function createCovers(opts) {
  const apiBase = (opts.apiBase || '').replace(/\/$/, '');
  const net = opts.dotnet;
  const token = () => net.invokeMethodAsync('GetToken');
  const urls = new Map();
  const authFetch = async (path, init = {}) => fetch(apiBase + path, { ...init, headers: { ...(init.headers || {}), Authorization: `Bearer ${await token()}` } });

  async function load(id) {
    if (urls.has(id)) return urls.get(id);
    const p = (async () => {
      const res = await authFetch(`/api/attachments/${id}`);
      if (!res.ok) throw new Error(String(res.status));
      return URL.createObjectURL(await res.blob());
    })();
    urls.set(id, p);
    p.catch(() => urls.delete(id));
    return p;
  }

  let drag = null;
  let teardown = null;

  return {
    /// Paints the cover band: sets --img and the focus position. Resolves false when the picture can't be loaded.
    async paint(el, id, posY) {
      if (!el) return false;
      try {
        el.style.setProperty('--img', `url("${await load(id)}")`);
        el.style.setProperty('--y', `${posY}%`);
        el.classList.add('photo');
        return true;
      } catch { el.classList.remove('photo'); return false; }
    },
    /// Thumbnails for the "this page" tab.
    async thumbs(container, ids) {
      for (const id of ids) {
        const b = container?.querySelector(`[data-img="${id}"]`);
        if (!b) continue;
        try { b.style.backgroundImage = `url("${await load(id)}")`; } catch { b.classList.add('broken'); }
      }
    },
    /// Drag the band to move the photo. Reports the position on every move; the page saves on "Save position".
    async startReposition(el, id, startPos) {
      this.stopReposition();
      try {
        const img = new Image();
        img.src = await load(id);
        await img.decode();
        el._ratio = img.naturalHeight / img.naturalWidth;
      } catch { /* keep the default ratio */ }
      let pos = startPos;
      const onDown = e => {
        if (e.target.closest('button')) return;
        drag = { y: e.clientY, start: pos };
        el.setPointerCapture?.(e.pointerId);
      };
      const onMove = e => {
        if (!drag) return;
        const box = el.clientHeight;
        const img = el.clientWidth * (el._ratio || 0.5625);
        pos = nextPos(drag.start, e.clientY - drag.y, box, Math.max(img, box + 1));
        el.style.setProperty('--y', pos + '%');
        net.invokeMethodAsync('OnCoverDragged', pos).catch(() => {});
      };
      const onUp = () => { drag = null; };
      el.addEventListener('pointerdown', onDown);
      el.addEventListener('pointermove', onMove);
      el.addEventListener('pointerup', onUp);
      el.addEventListener('pointercancel', onUp);
      teardown = () => {
        el.removeEventListener('pointerdown', onDown);
        el.removeEventListener('pointermove', onMove);
        el.removeEventListener('pointerup', onUp);
        el.removeEventListener('pointercancel', onUp);
      };
    },
    stopReposition() { teardown?.(); teardown = null; drag = null; },
    /// Opens the browser file picker; resolves with the chosen File, or null when cancelled.
    pick() {
      return new Promise(resolve => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = 'image/jpeg,image/png,image/webp';
        input.onchange = () => resolve(input.files?.[0] || null);
        input.oncancel = () => resolve(null);
        input.click();
      });
    },
    /// Uploads with a progress callback to .NET. Resolves { ok } or { ok:false, message } with a sentence to show.
    async upload(pageId, file) {
      const problem = checkCoverFile(file);
      if (problem) return { ok: false, message: problem };
      const body = new FormData();
      body.append('file', file, file.name);
      let t;
      try { t = await token(); } catch { return { ok: false, message: 'You were signed out. Sign in again.' }; }
      return await new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        xhr.open('POST', `${apiBase}/api/notes/${pageId}/cover`);
        xhr.setRequestHeader('Authorization', `Bearer ${t}`);
        xhr.upload.onprogress = e => { if (e.lengthComputable) net.invokeMethodAsync('OnCoverProgress', Math.round(e.loaded / e.total * 100)).catch(() => {}); };
        xhr.onload = () => {
          if (xhr.status >= 200 && xhr.status < 300) return resolve({ ok: true });
          let message = 'Couldn’t upload the cover. Try again.';
          try { message = JSON.parse(xhr.responseText).message || message; } catch { /* keep the default */ }
          resolve({ ok: false, message });
        };
        xhr.onerror = () => resolve({ ok: false, message: 'Couldn’t reach the server. Check your connection and try again.' });
        xhr.send(body);
      });
    },
    /// The whole "Choose image…" flow: pick, check, upload with progress, tell .NET how it went. Resolves true when a cover was set.
    async pickAndUpload(pageId) {
      const file = await this.pick();
      if (!file) return false;
      const problem = checkCoverFile(file);
      if (problem) { await net.invokeMethodAsync('OnCoverUploadState', false, problem).catch(() => {}); return false; }
      await net.invokeMethodAsync('OnCoverUploadState', true, null).catch(() => {});
      const r = await this.upload(pageId, file);
      await net.invokeMethodAsync('OnCoverUploadState', false, r.ok ? null : r.message).catch(() => {});
      if (r.ok) await net.invokeMethodAsync('OnCoverUploaded').catch(() => {});
      return !!r.ok;
    },
    dispose() {
      this.stopReposition();
      for (const p of urls.values()) p.then(u => URL.revokeObjectURL(u), () => {});
      urls.clear();
    },
  };
}
