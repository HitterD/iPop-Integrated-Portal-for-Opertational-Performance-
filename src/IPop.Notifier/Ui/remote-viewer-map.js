// Viewer → user's screen coordinates (hotfix 1.8.2). The <video> element keeps the picture's aspect ratio inside a
// box of another shape: "contain" (Fit) adds bars, "cover" (Fill) crops, "none" (1:1) shows it at its own size. The
// point the helper clicks must be measured on the picture itself, never on the element box.
(function () {
  function toPicture(rect, vw, vh, fit, clientX, clientY) {
    if (!vw || !vh || !rect.width || !rect.height) return null;
    let s;
    if (fit === 'cover') s = Math.max(rect.width / vw, rect.height / vh);
    else if (fit === 'none') s = 1;
    else s = Math.min(rect.width / vw, rect.height / vh); // contain (default)
    const pw = vw * s, ph = vh * s;
    const left = rect.left + (rect.width - pw) / 2, top = rect.top + (rect.height - ph) / 2;
    const x = (clientX - left) / pw, y = (clientY - top) / ph;
    return x < 0 || y < 0 || x > 1 || y > 1 ? null : { x, y };
  }
  const api = { toPicture };
  if (typeof module !== 'undefined') module.exports = api; else window.viewerMap = api;
})();
