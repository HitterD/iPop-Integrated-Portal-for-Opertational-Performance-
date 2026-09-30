// Sticker editor: a 512×512 canvas stage with zoom/pan/rotate/flip, in-browser person
// segmentation (MediaPipe selfie segmenter, self-hosted under lib/mediapipe and loaded only when
// an image is opened), background removal + white outline, text/emoji overlays, freehand drawing
// and undo/redo. Loaded as an ES module by Components/Chat/StickerEditor.razor.
//
// Blazor owns the markup; this module only draws into the <canvas> it is given and reports state
// back through the DotNetObjectReference (OnEditorState / OnSourceLoaded).

const S = 512;                 // output size
const PAD = 20;                // breathing room around the fitted subject
const OUTLINE = 8;             // white outline width at 512
const MAX_SRC = 1600;          // source images are downscaled to this on load
const MP_BASE = new URL("../lib/mediapipe/", import.meta.url).href;
const EMOJI_FONT = '"Segoe UI Emoji","Apple Color Emoji","Noto Color Emoji",sans-serif';
const TEXT_FONT = 'system-ui,"Segoe UI",Roboto,Helvetica,Arial,sans-serif';

let segmenterPromise = null;

/** Lazily creates (once per page) the MediaPipe ImageSegmenter. Rejects when unavailable. */
function getSegmenter() {
  if (!segmenterPromise) {
    segmenterPromise = (async () => {
      const vision = await import(MP_BASE + "tasks-vision.esm.js");
      const model = new Uint8Array(await (await fetch(MP_BASE + "selfie_segmenter.bin")).arrayBuffer());
      return await vision.ImageSegmenter.createFromOptions(
        { wasmLoaderPath: MP_BASE + "wasm/vision_wasm_internal.js", wasmBinaryPath: MP_BASE + "wasm/vision_wasm_internal.wasm" },
        { baseOptions: { modelAssetBuffer: model, delegate: "CPU" }, runningMode: "IMAGE", outputConfidenceMasks: true, outputCategoryMask: false });
    })();
    segmenterPromise.catch(() => { segmenterPromise = null; });
  }
  return segmenterPromise;
}

/** True for a GIF with more than one frame or a WebP with an ANIM chunk. */
export function isAnimated(bytes) {
  const b = bytes;
  if (b.length > 6 && b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46) {
    let frames = 0;
    for (let i = 0; i < b.length - 3; i++) {
      // Graphic Control Extension (21 F9 04) precedes each frame of an animation.
      if (b[i] === 0x21 && b[i + 1] === 0xF9 && b[i + 2] === 0x04 && ++frames > 1) return true;
    }
    return false;
  }
  if (b.length > 30 && b[0] === 0x52 && b[1] === 0x49 && b[8] === 0x57 && b[9] === 0x45) {
    // RIFF....WEBP VP8X with the animation flag
    if (b[12] === 0x56 && b[13] === 0x50 && b[14] === 0x38 && b[15] === 0x58) return (b[20] & 0x02) !== 0;
  }
  return false;
}

function canvas(w, h) {
  const c = document.createElement("canvas");
  c.width = Math.max(1, Math.round(w)); c.height = Math.max(1, Math.round(h));
  return c;
}

const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

class Editor {
  constructor(stage, dotnet) {
    this.stage = stage;               // the visible <canvas> (512×512 backing store)
    this.stage.width = S; this.stage.height = S;
    this.ctx = stage.getContext("2d");
    this.dn = dotnet;
    this.src = null;                  // canvas with the decoded source image
    this.cut = null;                  // source with the person mask applied as alpha
    this.box = null;                  // person bbox in source-normalized coords [x0,y0,x1,y1]
    this.seg = "idle";                // idle | loading | person | none | failed
    this.st = this.blankState();
    this.hist = []; this.hi = -1;
    this.sel = null;                  // selected overlay id
    this.tool = "move";
    this.brush = { color: "#ffffff", width: 10 };
    this.ptrs = new Map();
    this.gesture = null;
    this.oriKey = ""; this.ori = null;
    this.dirty = true;
    this.origBytes = null; this.origName = null;
    this.bind();
    this.loop = () => { if (this.dirty) { this.dirty = false; this.draw(this.ctx, false); } this.raf = requestAnimationFrame(this.loop); };
    this.raf = requestAnimationFrame(this.loop);
  }

  blankState() {
    return { rot: 0, flip: false, px: S / 2, py: S / 2, scale: 1, removeBg: false, outline: false, items: [], strokes: [], nextId: 1 };
  }

  // ---------------------------------------------------------------- loading

  async loadBlob(blob, name) {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    this.origBytes = bytes; this.origName = name || "sticker";
    if (isAnimated(bytes)) { this.src = null; this.dn.invokeMethodAsync("OnSourceLoaded", "animated"); return; }
    let bmp;
    try { bmp = await createImageBitmap(blob); }
    catch {
      try { bmp = await new Promise((ok, fail) => { const i = new Image(); i.onload = () => ok(i); i.onerror = fail; i.src = URL.createObjectURL(blob); }); }
      catch { this.dn.invokeMethodAsync("OnSourceLoaded", "unsupported"); return; }
    }
    const w = bmp.width || bmp.naturalWidth, h = bmp.height || bmp.naturalHeight;
    const k = Math.min(1, MAX_SRC / Math.max(w, h));
    this.src = canvas(w * k, h * k);
    this.src.getContext("2d").drawImage(bmp, 0, 0, this.src.width, this.src.height);
    if (bmp.close) bmp.close();
    this.cut = null; this.box = null; this.sel = null;
    this.st = this.blankState();
    this.fit();
    this.resetHistory();
    this.seg = "loading";
    this.dn.invokeMethodAsync("OnSourceLoaded", "image");
    this.notify();
    this.segment();
  }

  async loadUrl(url, name) {
    const res = await fetch(url);
    if (!res.ok) { this.dn.invokeMethodAsync("OnSourceLoaded", "error"); return; }
    await this.loadBlob(await res.blob(), name);
  }

  async segment() {
    const src = this.src;
    try {
      const seg = await getSegmenter();
      if (src !== this.src) return; // another image was opened meanwhile
      const k = Math.min(1, 512 / Math.max(src.width, src.height));
      const small = canvas(src.width * k, src.height * k);
      small.getContext("2d").drawImage(src, 0, 0, small.width, small.height);
      const result = seg.segment(small);
      const masks = result.confidenceMasks || [];
      const m = masks[masks.length - 1];
      const w = m.width, h = m.height;
      const conf = m.getAsFloat32Array().slice();
      if (result.close) result.close();
      const found = maskInfo(conf, w, h);
      if (!found) { this.seg = "none"; this.notify(); return; }
      this.box = found.box;
      // Alpha from confidence (soft ramp), upscaled to the source with a slight feather.
      const mc = canvas(w, h);
      const id = mc.getContext("2d").createImageData(w, h);
      for (let i = 0; i < conf.length; i++) { id.data[i * 4 + 3] = Math.round(smooth(0.3, 0.7, conf[i]) * 255); }
      mc.getContext("2d").putImageData(id, 0, 0);
      const cut = canvas(src.width, src.height);
      const cc = cut.getContext("2d");
      cc.drawImage(src, 0, 0);
      cc.globalCompositeOperation = "destination-in";
      cc.imageSmoothingQuality = "high";
      if ("filter" in cc) cc.filter = `blur(${Math.max(0.6, (src.width / w) * 0.6).toFixed(2)}px)`;
      cc.drawImage(mc, 0, 0, src.width, src.height);
      this.cut = cut;
      this.seg = "person";
      this.st.removeBg = true; this.st.outline = true;
      this.fit();
      this.resetHistory();
      this.redraw();
    } catch (e) {
      console.warn("sticker segmentation unavailable", e);
      this.seg = "failed";
      this.notify();
    }
  }

  // ---------------------------------------------------------------- geometry

  /** The base layer after background removal, rotation and flip. Cached by its inputs. */
  oriented() {
    const useCut = this.st.removeBg && this.cut;
    const key = `${this.st.rot}|${this.st.flip}|${useCut ? 1 : 0}`;
    if (this.ori && this.oriKey === key) return this.ori;
    const img = useCut ? this.cut : this.src;
    const odd = this.st.rot % 2 === 1;
    const c = canvas(odd ? img.height : img.width, odd ? img.width : img.height);
    const x = c.getContext("2d");
    x.translate(c.width / 2, c.height / 2);
    x.scale(this.st.flip ? -1 : 1, 1);
    x.rotate(this.st.rot * Math.PI / 2);
    x.drawImage(img, -img.width / 2, -img.height / 2);
    this.ori = c; this.oriKey = key;
    return c;
  }

  /** Fits the person (if cut out) or the whole image into the square with a little padding. */
  fit() {
    const o = this.oriented();
    let bx = 0, by = 0, bw = o.width, bh = o.height;
    if (this.box && this.st.removeBg) {
      // Map the source-normalized box through rotate+flip into oriented pixel coords.
      const pts = [[this.box[0], this.box[1]], [this.box[2], this.box[1]], [this.box[0], this.box[3]], [this.box[2], this.box[3]]]
        .map(([nx, ny]) => this.mapPoint(nx, ny));
      const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
      bx = Math.min(...xs); by = Math.min(...ys); bw = Math.max(...xs) - bx; bh = Math.max(...ys) - by;
    }
    const pad = PAD + (this.st.outline ? OUTLINE : 0);
    const scale = (S - 2 * pad) / Math.max(bw, bh, 1);
    this.st.scale = scale;
    this.st.px = S / 2 + (o.width / 2 - (bx + bw / 2)) * scale;
    this.st.py = S / 2 + (o.height / 2 - (by + bh / 2)) * scale;
  }

  /** Source-normalized point → oriented pixel point. */
  mapPoint(nx, ny) {
    const w = this.src.width, h = this.src.height;
    let u = (nx - 0.5) * w, v = (ny - 0.5) * h;
    for (let i = 0; i < this.st.rot; i++) { const t = u; u = -v; v = t; }
    if (this.st.flip) u = -u;
    const o = this.oriented();
    return [u + o.width / 2, v + o.height / 2];
  }

  // ---------------------------------------------------------------- drawing

  redraw() { this.dirty = true; }

  draw(ctx, forExport) {
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, S, S);
    if (!this.src) { ctx.restore(); return; }
    const st = this.st, o = this.oriented();
    const layer = canvas(S, S), lc = layer.getContext("2d");
    lc.imageSmoothingQuality = "high";
    lc.translate(st.px, st.py); lc.scale(st.scale, st.scale);
    lc.drawImage(o, -o.width / 2, -o.height / 2);
    if (st.outline && st.removeBg && this.cut) {
      const sil = canvas(S, S), sc = sil.getContext("2d");
      sc.drawImage(layer, 0, 0);
      sc.globalCompositeOperation = "source-in";
      sc.fillStyle = "#ffffff"; sc.fillRect(0, 0, S, S);
      for (const r of [OUTLINE / 2, OUTLINE]) {
        const n = r > 4 ? 32 : 16;
        for (let i = 0; i < n; i++) {
          const a = (i / n) * Math.PI * 2;
          ctx.drawImage(sil, Math.cos(a) * r, Math.sin(a) * r);
        }
      }
    }
    ctx.drawImage(layer, 0, 0);
    for (const s of st.strokes) this.drawStroke(ctx, s);
    for (const it of st.items) this.drawItem(ctx, it);
    if (!forExport && this.sel) {
      const it = st.items.find(i => i.id === this.sel);
      if (it) this.drawSelection(ctx, it);
    }
    ctx.restore();
  }

  drawStroke(ctx, s) {
    if (!s.pts.length) return;
    ctx.save();
    ctx.strokeStyle = s.color; ctx.fillStyle = s.color; ctx.lineWidth = s.width;
    ctx.lineCap = "round"; ctx.lineJoin = "round";
    if (s.pts.length === 1) { ctx.beginPath(); ctx.arc(s.pts[0][0], s.pts[0][1], s.width / 2, 0, Math.PI * 2); ctx.fill(); }
    else {
      ctx.beginPath(); ctx.moveTo(s.pts[0][0], s.pts[0][1]);
      for (let i = 1; i < s.pts.length - 1; i++) {
        const [x0, y0] = s.pts[i], [x1, y1] = s.pts[i + 1];
        ctx.quadraticCurveTo(x0, y0, (x0 + x1) / 2, (y0 + y1) / 2);
      }
      const l = s.pts[s.pts.length - 1]; ctx.lineTo(l[0], l[1]);
      ctx.stroke();
    }
    ctx.restore();
  }

  font(it) { return it.kind === "emoji" ? `${it.size}px ${EMOJI_FONT}` : `800 ${it.size}px ${TEXT_FONT}`; }

  /** Local-space half extents of an overlay. */
  extent(it) {
    const c = this.ctx; c.save(); c.font = this.font(it);
    const w = c.measureText(it.text).width; c.restore();
    return { hw: w / 2 + it.size * 0.12, hh: it.size * 0.62 };
  }

  drawItem(ctx, it) {
    ctx.save();
    ctx.translate(it.x, it.y); ctx.rotate(it.angle);
    ctx.font = this.font(it); ctx.textAlign = "center"; ctx.textBaseline = "middle";
    if (it.kind === "text") {
      if (it.stroke) {
        ctx.lineJoin = "round"; ctx.miterLimit = 2;
        ctx.lineWidth = Math.max(3, it.size * 0.18);
        ctx.strokeStyle = isLight(it.color) ? "#111111" : "#ffffff";
        ctx.strokeText(it.text, 0, 0);
      }
      ctx.fillStyle = it.color;
    }
    ctx.fillText(it.text, 0, it.kind === "emoji" ? it.size * 0.06 : 0);
    ctx.restore();
  }

  drawSelection(ctx, it) {
    const { hw, hh } = this.extent(it);
    ctx.save();
    ctx.translate(it.x, it.y); ctx.rotate(it.angle);
    ctx.setLineDash([6, 5]); ctx.lineWidth = 2; ctx.strokeStyle = "#0A84FF";
    ctx.strokeRect(-hw, -hh, hw * 2, hh * 2);
    ctx.setLineDash([]);
    ctx.fillStyle = "#ffffff"; ctx.strokeStyle = "#0A84FF"; ctx.lineWidth = 2.5;
    ctx.beginPath(); ctx.arc(hw, -hh, 11, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
    ctx.beginPath(); ctx.arc(hw, -hh, 4, -Math.PI * 0.9, Math.PI * 0.4); ctx.stroke();
    ctx.restore();
  }

  // ---------------------------------------------------------------- hit testing

  toLocal(it, x, y) {
    const dx = x - it.x, dy = y - it.y, c = Math.cos(-it.angle), s = Math.sin(-it.angle);
    return [dx * c - dy * s, dx * s + dy * c];
  }

  hitHandle(x, y) {
    const it = this.st.items.find(i => i.id === this.sel);
    if (!it) return null;
    const { hw, hh } = this.extent(it), [lx, ly] = this.toLocal(it, x, y);
    return Math.hypot(lx - hw, ly + hh) <= 18 ? it : null;
  }

  hitItem(x, y) {
    for (let i = this.st.items.length - 1; i >= 0; i--) {
      const it = this.st.items[i], { hw, hh } = this.extent(it), [lx, ly] = this.toLocal(it, x, y);
      if (Math.abs(lx) <= hw + 6 && Math.abs(ly) <= hh + 6) return it;
    }
    return null;
  }

  // ---------------------------------------------------------------- input

  pt(e) {
    const r = this.stage.getBoundingClientRect();
    return [(e.clientX - r.left) * S / r.width, (e.clientY - r.top) * S / r.height];
  }

  bind() {
    const el = this.stage;
    el.style.touchAction = "none";
    this.onDown = e => this.down(e);
    this.onMove = e => this.move(e);
    this.onUp = e => this.up(e);
    this.onWheel = e => this.wheel(e);
    this.onPaste = e => this.paste(e);
    this.onKey = e => this.key(e);
    el.addEventListener("pointerdown", this.onDown);
    el.addEventListener("pointermove", this.onMove);
    el.addEventListener("pointerup", this.onUp);
    el.addEventListener("pointercancel", this.onUp);
    el.addEventListener("wheel", this.onWheel, { passive: false });
    document.addEventListener("paste", this.onPaste, true);
    document.addEventListener("keydown", this.onKey);
  }

  down(e) {
    if (!this.src) return;
    e.preventDefault();
    this.stage.setPointerCapture(e.pointerId);
    const p = this.pt(e);
    this.ptrs.set(e.pointerId, p);
    if (this.ptrs.size === 2) { this.startPinch(); return; }
    if (this.ptrs.size > 2) return;
    if (this.tool === "draw") {
      const s = { color: this.brush.color, width: this.brush.width, pts: [p] };
      this.st.strokes.push(s);
      this.gesture = { kind: "draw", stroke: s };
      this.redraw();
      return;
    }
    const h = this.hitHandle(p[0], p[1]);
    if (h) {
      this.gesture = { kind: "handle", it: h, a0: Math.atan2(p[1] - h.y, p[0] - h.x), d0: Math.hypot(p[0] - h.x, p[1] - h.y), angle0: h.angle, size0: h.size };
      return;
    }
    const it = this.hitItem(p[0], p[1]);
    if (it) {
      this.sel = it.id;
      // Bring to front so it stays on top while dragged.
      this.st.items = this.st.items.filter(i => i !== it).concat(it);
      this.gesture = { kind: "item", it, ox: p[0] - it.x, oy: p[1] - it.y };
    } else {
      this.sel = null;
      this.gesture = { kind: "pan", ox: p[0] - this.st.px, oy: p[1] - this.st.py };
    }
    this.redraw(); this.notify();
  }

  startPinch() {
    const [a, b] = [...this.ptrs.values()];
    const it = this.st.items.find(i => i.id === this.sel);
    this.gesture = {
      kind: "pinch", it, d0: Math.hypot(b[0] - a[0], b[1] - a[1]), a0: Math.atan2(b[1] - a[1], b[0] - a[0]),
      size0: it?.size, angle0: it?.angle, scale0: this.st.scale, px0: this.st.px, py0: this.st.py,
      mx: (a[0] + b[0]) / 2, my: (a[1] + b[1]) / 2,
    };
    // A second finger turns a stroke-in-progress into a gesture, not a scribble.
    if (this.tool === "draw" && this.st.strokes.length && this.st.strokes[this.st.strokes.length - 1].pts.length < 4) this.st.strokes.pop();
  }

  move(e) {
    if (!this.ptrs.has(e.pointerId)) return;
    const p = this.pt(e);
    this.ptrs.set(e.pointerId, p);
    const g = this.gesture;
    if (!g) return;
    if (g.kind === "pinch" && this.ptrs.size >= 2) {
      const [a, b] = [...this.ptrs.values()];
      const k = Math.hypot(b[0] - a[0], b[1] - a[1]) / (g.d0 || 1);
      const da = Math.atan2(b[1] - a[1], b[0] - a[0]) - g.a0;
      if (g.it) { g.it.size = clamp(g.size0 * k, 12, 400); g.it.angle = g.angle0 + da; }
      else {
        const s = clamp(g.scale0 * k, 0.02, 40), f = s / g.scale0;
        const mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2;
        this.st.scale = s;
        this.st.px = mx + (g.px0 - g.mx) * f; this.st.py = my + (g.py0 - g.my) * f;
      }
    } else if (g.kind === "draw") {
      const l = g.stroke.pts[g.stroke.pts.length - 1];
      if (Math.hypot(p[0] - l[0], p[1] - l[1]) > 1.5) g.stroke.pts.push(p);
    } else if (g.kind === "item") { g.it.x = p[0] - g.ox; g.it.y = p[1] - g.oy; }
    else if (g.kind === "pan") { this.st.px = p[0] - g.ox; this.st.py = p[1] - g.oy; }
    else if (g.kind === "handle") {
      const it = g.it, d = Math.hypot(p[0] - it.x, p[1] - it.y);
      it.size = clamp(g.size0 * d / (g.d0 || 1), 12, 400);
      it.angle = g.angle0 + Math.atan2(p[1] - it.y, p[0] - it.x) - g.a0;
    } else return;
    this.redraw();
  }

  up(e) {
    if (!this.ptrs.delete(e.pointerId)) return;
    if (this.ptrs.size === 1 && this.gesture?.kind === "pinch") {
      // Lifting one finger of a pinch: stop, and let the remaining finger start fresh on its next down.
      this.gesture = null;
      this.commit();
      return;
    }
    if (this.ptrs.size === 0 && this.gesture) { this.gesture = null; this.commit(); }
  }

  wheel(e) {
    if (!this.src) return;
    e.preventDefault();
    const p = this.pt(e);
    const k = Math.exp(-e.deltaY * 0.0015);
    const it = this.st.items.find(i => i.id === this.sel);
    if (it) {
      if (e.shiftKey) it.angle += e.deltaY * 0.004;
      else it.size = clamp(it.size * k, 12, 400);
    } else {
      const s = clamp(this.st.scale * k, 0.02, 40), f = s / this.st.scale;
      this.st.scale = s;
      this.st.px = p[0] + (this.st.px - p[0]) * f; this.st.py = p[1] + (this.st.py - p[1]) * f;
    }
    this.redraw();
    clearTimeout(this.wheelTimer);
    this.wheelTimer = setTimeout(() => this.commit(), 300);
  }

  async paste(e) {
    // Capture phase: while the editor is open an image paste is always for it, never the composer.
    const file = [...(e.clipboardData?.items || [])].find(i => i.kind === "file" && i.type.startsWith("image/"))?.getAsFile();
    if (!file) return;           // plain text paste stays with the caption box
    e.preventDefault(); e.stopPropagation();
    await this.loadBlob(file, file.name || "pasted.png");
  }

  key(e) {
    const t = e.target;
    if (t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.isContentEditable)) return;
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === "z") { e.preventDefault(); e.shiftKey ? this.redo() : this.undo(); }
    else if (mod && e.key.toLowerCase() === "y") { e.preventDefault(); this.redo(); }
    else if ((e.key === "Delete" || e.key === "Backspace") && this.sel) { e.preventDefault(); this.deleteSelected(); }
  }

  // ---------------------------------------------------------------- history

  snapshot() { return JSON.stringify(this.st); }

  resetHistory() { this.hist = [this.snapshot()]; this.hi = 0; this.redraw(); this.notify(); }

  commit() {
    const s = this.snapshot();
    if (this.hist[this.hi] === s) { this.notify(); return; }
    this.hist = this.hist.slice(0, this.hi + 1);
    this.hist.push(s);
    if (this.hist.length > 60) this.hist.shift();
    this.hi = this.hist.length - 1;
    this.notify();
  }

  restore(i) {
    this.hi = i;
    this.st = JSON.parse(this.hist[i]);
    if (this.sel && !this.st.items.some(it => it.id === this.sel)) this.sel = null;
    this.redraw(); this.notify();
  }

  undo() { if (this.hi > 0) this.restore(this.hi - 1); }
  redo() { if (this.hi < this.hist.length - 1) this.restore(this.hi + 1); }

  notify() {
    if (this.notifyQueued) return;
    this.notifyQueued = true;
    queueMicrotask(() => {
      this.notifyQueued = false;
      const it = this.st.items.find(i => i.id === this.sel);
      this.dn.invokeMethodAsync("OnEditorState", {
        hasImage: !!this.src, seg: this.seg, removeBg: this.st.removeBg, outline: this.st.outline,
        canUndo: this.hi > 0, canRedo: this.hi < this.hist.length - 1, tool: this.tool,
        selected: it ? { kind: it.kind, text: it.text, color: it.color || null, stroke: !!it.stroke } : null,
      }).catch(() => { });
    });
  }

  // ---------------------------------------------------------------- commands (from Blazor)

  rotate() { this.st.rot = (this.st.rot + 1) % 4; this.refitAfterOrientation(); }
  flip() { this.st.flip = !this.st.flip; this.refitAfterOrientation(); }

  refitAfterOrientation() {
    // Keep the zoom the user chose relative to the fit, re-centred.
    const before = this.st.scale;
    const fitBefore = this.fitScaleProbe();
    this.oriKey = "";
    this.fit();
    if (fitBefore) this.st.scale *= before / fitBefore;
    this.redraw(); this.commit();
  }

  fitScaleProbe() { const save = { ...this.st }; this.fit(); const s = this.st.scale; Object.assign(this.st, { px: save.px, py: save.py, scale: save.scale }); return s; }

  setRemoveBg(on) {
    if (!this.cut) return;
    this.st.removeBg = on;
    if (!on) this.st.outline = false;
    this.oriKey = ""; this.fit(); this.redraw(); this.commit();
  }

  setOutline(on) { if (!this.cut) return; this.st.outline = on && this.st.removeBg; this.redraw(); this.commit(); }

  resetView() { this.fit(); this.redraw(); this.commit(); }

  setTool(tool) { this.tool = tool === "draw" ? "draw" : "move"; if (this.tool === "draw") this.sel = null; this.redraw(); this.notify(); }

  setBrush(color, width) { this.brush = { color, width }; }

  addText(text, color, stroke) {
    text = (text || "").trim();
    if (!text) return;
    const it = { id: this.st.nextId++, kind: "text", text: text.slice(0, 60), color, stroke: !!stroke, x: S / 2, y: S * 0.84, size: 64, angle: 0 };
    // Long captions start smaller so they fit the width.
    const { hw } = this.extent(it);
    if (hw * 2 > S - 40) it.size = Math.max(20, it.size * (S - 40) / (hw * 2));
    this.st.items.push(it); this.sel = it.id; this.tool = "move";
    this.redraw(); this.commit();
  }

  addEmoji(emoji) {
    const n = this.st.items.filter(i => i.kind === "emoji").length;
    const it = { id: this.st.nextId++, kind: "emoji", text: emoji, x: S * 0.78 - (n % 3) * 40, y: S * 0.2 + (n % 3) * 40, size: 96, angle: -0.15 };
    this.st.items.push(it); this.sel = it.id; this.tool = "move";
    this.redraw(); this.commit();
  }

  updateSelected(text, color, stroke) {
    const it = this.st.items.find(i => i.id === this.sel);
    if (!it) return;
    if (it.kind === "text") {
      if (text != null && text.trim()) it.text = text.trim().slice(0, 60);
      if (color) it.color = color;
      if (stroke != null) it.stroke = !!stroke;
    }
    this.redraw(); this.commit();
  }

  deleteSelected() {
    if (!this.sel) return;
    this.st.items = this.st.items.filter(i => i.id !== this.sel);
    this.sel = null; this.redraw(); this.commit();
  }

  deselect() { this.sel = null; this.redraw(); this.notify(); }

  /** Renders the sticker (no selection chrome) and returns its bytes. WebP, PNG if unsupported. */
  async export() {
    const out = canvas(S, S);
    this.draw(out.getContext("2d"), true);
    let blob = await new Promise(r => out.toBlob(r, "image/webp", 0.92));
    if (!blob || blob.type !== "image/webp") blob = await new Promise(r => out.toBlob(r, "image/png"));
    this.lastMime = blob.type;
    return new Uint8Array(await blob.arrayBuffer());
  }

  original() { return this.origBytes; }

  dispose() {
    cancelAnimationFrame(this.raf);
    const el = this.stage;
    el.removeEventListener("pointerdown", this.onDown);
    el.removeEventListener("pointermove", this.onMove);
    el.removeEventListener("pointerup", this.onUp);
    el.removeEventListener("pointercancel", this.onUp);
    el.removeEventListener("wheel", this.onWheel);
    document.removeEventListener("paste", this.onPaste, true);
    document.removeEventListener("keydown", this.onKey);
    this.src = this.cut = this.ori = null; this.origBytes = null;
  }
}

/** Person coverage + bbox (normalized) of a confidence mask, or null when no person is present. */
export function maskInfo(conf, w, h, threshold = 0.5, minCoverage = 0.025) {
  let n = 0, x0 = w, y0 = h, x1 = -1, y1 = -1;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      if (conf[y * w + x] > threshold) {
        n++;
        if (x < x0) x0 = x; if (x > x1) x1 = x;
        if (y < y0) y0 = y; if (y > y1) y1 = y;
      }
    }
  }
  const coverage = n / (w * h);
  if (coverage < minCoverage) return null;
  return { coverage, box: [x0 / w, y0 / h, (x1 + 1) / w, (y1 + 1) / h] };
}

function clamp(v, a, b) { return Math.min(b, Math.max(a, v)); }

function isLight(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex || "");
  if (!m) return false;
  const v = parseInt(m[1], 16), r = v >> 16, g = (v >> 8) & 255, b = v & 255;
  return (0.299 * r + 0.587 * g + 0.114 * b) > 170;
}

// ---------------------------------------------------------------- Blazor-facing API

const editors = new Map();
let seq = 0;

export function mount(stage, dotnet, fileInput) {
  const id = ++seq;
  const ed = new Editor(stage, dotnet);
  editors.set(id, ed);
  if (fileInput) {
    ed.onFile = () => { const f = fileInput.files && fileInput.files[0]; if (f) ed.loadBlob(f, f.name); fileInput.value = ""; };
    fileInput.addEventListener("change", ed.onFile);
    ed.fileInput = fileInput;
  }
  // Warm the segmenter while the user is still picking a file.
  getSegmenter().catch(() => { });
  return id;
}

export function chooseFile(id) { const ed = editors.get(id); if (ed?.fileInput) { ed.fileInput.value = ""; ed.fileInput.click(); } }
export function loadUrl(id, url, name) { return editors.get(id)?.loadUrl(url, name); }
export function call(id, method, ...args) {
  const ed = editors.get(id);
  if (!ed || typeof ed[method] !== "function") return null;
  return ed[method](...args);
}
export async function exportSticker(id) { return await editors.get(id)?.export() ?? null; }
export function exportMime(id) { return editors.get(id)?.lastMime || "image/png"; }
export function original(id) { return editors.get(id)?.original() ?? null; }
export function originalName(id) { return editors.get(id)?.origName ?? "sticker"; }
export function unmount(id) {
  const ed = editors.get(id);
  if (!ed) return;
  if (ed.fileInput) ed.fileInput.removeEventListener("change", ed.onFile);
  ed.dispose();
  editors.delete(id);
}
