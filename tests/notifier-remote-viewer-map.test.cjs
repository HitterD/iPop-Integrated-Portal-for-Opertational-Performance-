// Run with: node tests/notifier-remote-viewer-map.test.cjs
// Hotfix 1.8.2 (prod 2026-11-27, IT10): the helper's mouse was above the user's cursor, more so towards the bottom/
// right. 1.8.1 normalised the click by the <video> ELEMENT box; the picture inside it is letterboxed (Fit), cropped
// (Fill) or shown at its own size (1:1), so every point was off by up to ~100 px.
const assert = require("node:assert/strict");
const { toPicture } = require("../src/IPop.Notifier/Ui/remote-viewer-map.js");

const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-6, `${a} ≈ ${b}`);
const px = (p, w, h) => [Math.round(p.x * w), Math.round(p.y * h)];

// The owner's layout: stage 1725×1005, 1920×1080 picture, Fit → bars of 17.5 px above and below.
{
  const r = { left: 0, top: 97, width: 1725, height: 1005 };
  const s = 1725 / 1920, top = 97 + (1005 - 1080 * s) / 2;
  const p = toPicture(r, 1920, 1080, "contain", 0.05 * 1725, top + 0.05 * 1080 * s);
  near(p.x, 0.05); near(p.y, 0.05);
  // 1.8.1 math for the same click: y = 0.0647 → 16 px lower on the user's screen.
  const old = (top + 0.05 * 1080 * s - r.top) / r.height;
  assert.ok(Math.round((old - 0.05) * 1080) >= 15, "the old mapping was ≥ 15 px off");
  assert.equal(toPicture(r, 1920, 1080, "contain", 100, r.top + 5), null, "a click on the black bar is ignored");
}

// Tall window (bars top/bottom) and wide window (bars left/right) both land on the same picture point.
for (const r of [{ left: 10, top: 20, width: 1400, height: 1000 }, { left: 0, top: 0, width: 2200, height: 900 }]) {
  const s = Math.min(r.width / 1920, r.height / 1080), pw = 1920 * s, ph = 1080 * s;
  const L = r.left + (r.width - pw) / 2, T = r.top + (r.height - ph) / 2;
  for (const [fx, fy] of [[0, 0], [0.5, 0.5], [1, 1], [0.25, 0.9]]) {
    const p = toPicture(r, 1920, 1080, "contain", L + fx * pw, T + fy * ph);
    near(p.x, fx); near(p.y, fy);
  }
}

// Fill (cover): the picture is cropped; the centre stays the centre and the visible edges map inside 0..1.
{
  const r = { left: 0, top: 0, width: 1400, height: 1000 };
  const c = toPicture(r, 1920, 1080, "cover", 700, 500); near(c.x, 0.5); near(c.y, 0.5);
  const s = Math.max(1400 / 1920, 1000 / 1080), pw = 1920 * s;
  const leftEdge = toPicture(r, 1920, 1080, "cover", 0, 500);
  near(leftEdge.x, ((pw - 1400) / 2) / pw);
}

// 1:1 (none): one screen pixel per CSS pixel, centred in the element.
{
  const r = { left: 0, top: 0, width: 1000, height: 600 };
  const p = toPicture(r, 1920, 1080, "none", 500, 300);
  near(p.x, 0.5); near(p.y, 0.5);
  assert.deepEqual(px(toPicture(r, 1920, 1080, "none", 500 + 100, 300 + 50), 1920, 1080), [1060, 590]);
}

// Not ready yet → no input.
assert.equal(toPicture({ left: 0, top: 0, width: 100, height: 100 }, 0, 0, "contain", 50, 50), null);
console.log("notifier remote viewer mapping (hotfix 1.8.2): ok");
