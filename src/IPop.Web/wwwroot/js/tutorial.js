// Coach-mark tours on the real pages (driver.js). Steps whose target isn't on screen —
// an empty inbox, a role-gated button, a column hidden on mobile — are skipped, so one
// tour works for every user and every screen size.
window.ipopTutorial = {
  _drv: null,
  start(steps, color, dotnet) {
    const make = window.driver && window.driver.js && window.driver.js.driver;
    if (!make) return;
    this._drv?.destroy();
    const find = (sel) => [...document.querySelectorAll(sel)].find((e) => e.getClientRects().length > 0);
    const list = steps.filter((s) => !s.el || find(s.el)).map((s) => ({
      element: s.el ? find(s.el) : undefined,
      popover: { title: s.title, description: s.body, popoverClass: "tut-pop" + (s.limits ? " tut-limits" : "") },
    }));
    if (!list.length) return;
    document.documentElement.style.setProperty("--tut-m", color);
    let finished = false;
    const drv = make({
      steps: list,
      showProgress: true,
      progressText: "Step {{current}} of {{total}}",
      nextBtnText: "Next",
      prevBtnText: "Back",
      doneBtnText: "Done",
      overlayOpacity: 0.52,
      stagePadding: 6,
      stageRadius: 14,
      smoothScroll: true,
      onPopoverRender: (pop, { state }) => {
        // "Skip tour" beside the step counter, and progress dots, like the mockup.
        const skip = document.createElement("button");
        skip.className = "tut-skip"; skip.type = "button"; skip.textContent = "Skip tour";
        skip.onclick = () => drv.destroy();
        pop.wrapper.appendChild(skip);
        const dots = document.createElement("div");
        dots.className = "tut-dots";
        dots.innerHTML = list.map((_, i) => '<i class="' + (i === state.activeIndex ? "on" : "") + '"></i>').join("");
        pop.footer.insertBefore(dots, pop.footer.firstChild);
        if (state.activeIndex === list.length - 1) finished = true;
      },
      onDestroyed: () => {
        this._drv = null;
        dotnet?.invokeMethodAsync("TourEnded", finished);
      },
    });
    this._drv = drv;
    drv.drive();
  },
  stop() { this._drv?.destroy(); },
};
