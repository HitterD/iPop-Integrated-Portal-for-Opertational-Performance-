// Chat micro-interactions that need the DOM (ES module, loaded by Pages/Chat via import()).
// Decoration only: nothing here changes chat state. Every effect checks reduced motion.
//  #42 voice note: a live waveform from the recording microphone (ipop._audioStream).
//  #40 photo viewer: the opened image grows out of the thumbnail that was clicked.

const reduced = () => !!(window.ipopMicro ? ipopMicro.reduced() : matchMedia("(prefers-reduced-motion: reduce)").matches);

// ── #42 waveform ──
let wave = null;
export function startWave(host) {
  stopWave();
  if (!host) return;
  const BARS = 28;
  host.replaceChildren(...Array.from({ length: BARS }, () => document.createElement("i")));
  const stream = window.ipop && ipop._audioStream;
  if (reduced() || !stream || !(window.AudioContext || window.webkitAudioContext)) return;
  try {
    const ctx = new (window.AudioContext || window.webkitAudioContext)();
    const src = ctx.createMediaStreamSource(stream);
    const an = ctx.createAnalyser();
    an.fftSize = 256;
    src.connect(an);
    const data = new Uint8Array(an.fftSize);
    const bars = [...host.children];
    const levels = new Array(BARS).fill(0.08);
    let last = 0, raf = 0;
    const draw = t => {
      raf = requestAnimationFrame(draw);
      if (!host.isConnected) { stopWave(); return; }
      if (t - last < 90) return;
      last = t;
      an.getByteTimeDomainData(data);
      let peak = 0;
      for (let i = 0; i < data.length; i++) peak = Math.max(peak, Math.abs(data[i] - 128));
      levels.shift();
      levels.push(Math.max(0.08, Math.min(1, peak / 64)));
      bars.forEach((b, i) => { b.style.transform = `scaleY(${levels[i].toFixed(2)})`; });
    };
    raf = requestAnimationFrame(draw);
    wave = { stop: () => { cancelAnimationFrame(raf); try { src.disconnect(); ctx.close(); } catch { } } };
  } catch { /* no analyser: the static bars stay */ }
}
export function stopWave() {
  if (wave) { wave.stop(); wave = null; }
}

// ── #40 zoom from thumbnail ──
let lastThumb = null, lastAt = 0;
document.addEventListener("click", e => {
  const t = e.target.closest && e.target.closest(".chat-area .att-image, .chat-detail-panel .media-thumb-box");
  if (t) { lastThumb = t.querySelector("img") || t; lastAt = performance.now(); }
}, true);

export function armZoom() {
  const thumb = lastThumb;
  if (!thumb || performance.now() - lastAt > 1500 || reduced() || !window.ipopMicro) return;
  lastThumb = null;
  const deadline = performance.now() + 4000;
  const tryZoom = () => {
    const img = document.querySelector(".att-viewer img.av-image");
    if (!img) return false;
    const go = () => { if (thumb.isConnected) { img.classList.add("mi-zooming"); ipopMicro.zoomFrom(thumb, img); setTimeout(() => img.classList.remove("mi-zooming"), 340); } };
    if (img.complete && img.naturalWidth) go(); else img.addEventListener("load", go, { once: true });
    return true;
  };
  if (tryZoom()) return;
  const mo = new MutationObserver(() => { if (tryZoom() || performance.now() > deadline) mo.disconnect(); });
  mo.observe(document.body, { childList: true, subtree: true });
  setTimeout(() => mo.disconnect(), 4000);
}
