// Launch videos: admin upload (probe duration, capture a poster frame, XHR upload with progress)
// and the shared player chrome used by the login popup and the News detail.
window.ipopVideo = (() => {
  const up = { file: null, url: null, probe: null, poster: null, posterUrl: null, xhr: null };
  const fmt = (s) => isFinite(s) ? `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}` : '0:00';

  function reset() {
    if (up.url) URL.revokeObjectURL(up.url);
    if (up.posterUrl) URL.revokeObjectURL(up.posterUrl);
    Object.assign(up, { file: null, url: null, probe: null, poster: null, posterUrl: null });
  }

  // Reads the chosen file into a hidden <video> for duration + poster frames. Nothing is uploaded yet.
  async function pick(inputId) {
    reset();
    const f = document.getElementById(inputId)?.files?.[0];
    if (!f) return null;
    up.file = f;
    up.url = URL.createObjectURL(f);
    const v = document.createElement('video');
    v.muted = true; v.preload = 'auto'; v.playsInline = true; v.src = up.url;
    try {
      await new Promise((res, rej) => { v.onloadedmetadata = res; v.onerror = () => rej(new Error('unreadable')); });
    } catch {
      return { name: f.name, size: f.size, duration: 0, readable: false };
    }
    up.probe = v;
    const at = Math.min(3, (v.duration || 0) / 3);
    const poster = await frame(at);
    return { name: f.name, size: f.size, duration: Math.round(v.duration || 0), readable: true, poster, at };
  }

  // Captures the frame at `sec` as the poster (JPEG) and returns a preview URL.
  async function frame(sec) {
    const v = up.probe;
    if (!v) return null;
    await new Promise((res) => { v.onseeked = res; v.currentTime = Math.max(0, Math.min(sec, (v.duration || 1) - 0.1)); });
    const w = Math.min(1280, v.videoWidth || 1280), h = Math.round(w * (v.videoHeight || 720) / (v.videoWidth || 1280));
    const c = document.createElement('canvas'); c.width = w; c.height = h;
    c.getContext('2d').drawImage(v, 0, 0, w, h);
    const blob = await new Promise((res) => c.toBlob(res, 'image/jpeg', 0.85));
    return setPoster(blob);
  }

  function posterFromInput(inputId) {
    const f = document.getElementById(inputId)?.files?.[0];
    return f ? setPoster(f) : null;
  }

  function setPoster(blob) {
    if (!blob) return null;
    if (up.posterUrl) URL.revokeObjectURL(up.posterUrl);
    up.poster = blob;
    up.posterUrl = URL.createObjectURL(blob);
    return up.posterUrl;
  }

  // XHR because fetch has no upload progress. Retries once with a refreshed token on 401.
  async function upload(url, fields, dotnet) {
    if (!up.file) return { status: 0, error: 'No file selected' };
    let res = await send(url, fields, dotnet, await dotnet.invokeMethodAsync('UploadToken', false));
    if (res.status === 401) res = await send(url, fields, dotnet, await dotnet.invokeMethodAsync('UploadToken', true));
    if (res.status >= 200 && res.status < 300) reset();
    return res;
  }

  function send(url, fields, dotnet, token) {
    return new Promise((resolve) => {
      const xhr = new XMLHttpRequest();
      up.xhr = xhr;
      xhr.open('POST', url);
      if (token) xhr.setRequestHeader('Authorization', 'Bearer ' + token);
      let last = 0;
      xhr.upload.onprogress = (e) => {
        const now = performance.now();
        if (now - last < 150 && e.loaded < e.total) return;
        last = now;
        dotnet.invokeMethodAsync('UploadProgress', e.loaded, e.total);
      };
      xhr.onload = () => {
        let error = '';
        try { error = JSON.parse(xhr.responseText || '{}').code || ''; } catch { /* not JSON */ }
        resolve({ status: xhr.status, error });
      };
      xhr.onerror = () => resolve({ status: 0, error: 'network' });
      xhr.onabort = () => resolve({ status: 0, error: 'cancelled' });
      const form = new FormData();
      for (const [k, val] of Object.entries(fields)) if (val !== null && val !== undefined) form.append(k, val);
      if (up.poster) form.append('poster', up.poster, 'poster.jpg');
      form.append('file', up.file, up.file.name);
      xhr.send(form);
    });
  }

  function cancel() { up.xhr?.abort(); }

  // ── Player chrome ────────────────────────────────────────────────────────────
  // Wires the custom controls inside `.an-stage`. `autoplay`: try with sound, fall back to muted +
  // the "Turn sound on" pill. Calls dotnet.Played() once on the first real playback.
  const players = new WeakMap();
  function mount(stage, dotnet, autoplay) {
    if (!stage || players.has(stage)) return;
    const v = stage.querySelector('video');
    const $ = (s) => stage.querySelector(s);
    const icons = () => window.lucide && lucide.createIcons();
    const ic = (b, n) => { if (b) { b.innerHTML = `<i data-lucide="${n}"></i>`; icons(); } };
    const play = $('[data-play]'), mute = $('[data-mute]');
    let played = false;
    const sync = () => stage.classList.toggle('muted', v.muted);
    play.onclick = () => v.paused ? v.play() : v.pause();
    v.onplay = () => {
      stage.classList.remove('paused'); ic(play, 'pause');
      if (!played) { played = true; dotnet?.invokeMethodAsync('Played'); }
    };
    v.onpause = () => { stage.classList.add('paused'); ic(play, 'play'); };
    mute.onclick = () => { v.muted = !v.muted; };
    v.addEventListener('volumechange', () => { ic(mute, v.muted ? 'volume-x' : 'volume-2'); sync(); });
    const pill = $('.an-unmute');
    if (pill) pill.onclick = () => { v.muted = false; v.play(); };
    $('[data-fs]').onclick = () => toggleFs(stage, v);
    const bar = $('.an-bar'), fill = $('.an-bar i'), time = $('.an-time');
    v.ontimeupdate = () => { fill.style.width = (v.currentTime / v.duration * 100 || 0) + '%'; time.textContent = `${fmt(v.currentTime)} / ${fmt(v.duration)}`; };
    bar.onclick = (e) => { const r = bar.getBoundingClientRect(); v.currentTime = (e.clientX - r.left) / r.width * v.duration; };
    v.onloadedmetadata = () => v.ontimeupdate();
    v.onclick = () => play.click();
    // Landscape on a phone → full screen, like the native players.
    const onOrient = () => { if (innerWidth < 900 && screen.orientation?.type.startsWith('landscape') && !v.paused && !document.fullscreenElement) toggleFs(stage, v); };
    screen.orientation?.addEventListener('change', onOrient);
    players.set(stage, { v, onOrient });
    if (autoplay) {
      v.muted = false;
      v.play().catch(() => { v.muted = true; sync(); v.play().catch(() => stage.classList.add('paused')); });
    } else {
      stage.classList.add('paused');
    }
  }

  function toggleFs(stage, v) {
    if (document.fullscreenElement) return document.exitFullscreen();
    if (stage.requestFullscreen) return stage.requestFullscreen();
    v.webkitEnterFullscreen?.(); // iOS Safari only full-screens the video element itself
  }

  function unmount(stage) {
    const p = stage && players.get(stage);
    if (!p) return;
    p.v.pause();
    screen.orientation?.removeEventListener('change', p.onOrient);
    if (document.fullscreenElement === stage) document.exitFullscreen();
    players.delete(stage);
  }

  function isFullscreen() { return !!document.fullscreenElement; }

  return { pick, frame, posterFromInput, upload, cancel, reset, mount, unmount, isFullscreen };
})();
