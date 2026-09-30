// Dashboard KPI count-up. Blazor renders each .v empty with data-count (and re-keys it when the
// value changes); this fills it in. Only the first render of a visit animates, the rest just set.
window.ipopDashCount = function (animate) {
  const still = !animate || matchMedia('(prefers-reduced-motion: reduce)').matches;
  document.querySelectorAll('.dw-kpi .v[data-count]:not([data-done])').forEach((el) => {
    el.dataset.done = '1';
    const target = +el.dataset.count || 0;
    if (still || target === 0) { el.textContent = target; return; }
    const t0 = performance.now(), dur = 700;
    const step = (t) => {
      const p = Math.min(1, (t - t0) / dur);
      el.textContent = Math.round(target * (1 - Math.pow(1 - p, 3)));
      if (p < 1) requestAnimationFrame(step);
    };
    el.textContent = '0';
    requestAnimationFrame(step);
  });
};
