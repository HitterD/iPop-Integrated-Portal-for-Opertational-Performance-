// Projects v2 table: after a status change the cell flashes (spring), Done adds a burst ring.
(function () {
  const reduced = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  function replay(el, cls) {
    el.classList.remove(cls);
    void el.offsetWidth;
    el.classList.add(cls);
    el.addEventListener('animationend', () => el.classList.remove(cls), { once: true });
  }
  window.ipopPv2 = {
    statusChanged(taskId, stage) {
      if (reduced()) return;
      const el = document.querySelector(`.pv2 [data-pv2-st="${CSS.escape(taskId)}"]`);
      if (!el) return;
      replay(el, 'pv2-flash');
      if (stage === 'done') replay(el.parentElement, 'pv2-burst');
    },
  };
})();
