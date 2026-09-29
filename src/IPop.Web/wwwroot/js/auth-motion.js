// Login -> app hand-off. The login page plays its exit, then this flags <html> so the shell
// (sidebar + main) animates in on its first paint; the flag clears itself afterwards.
window.ipopAuthMotion = {
    reduced: () => matchMedia('(prefers-reduced-motion: reduce)').matches,
    // Resolves when the login exit animation has had time to play.
    exit(ms) { return new Promise((r) => setTimeout(r, this.reduced() ? 0 : ms)); },
    enterApp() {
        if (this.reduced()) return;
        const html = document.documentElement;
        html.classList.add('ipop-entering');
        setTimeout(() => html.classList.remove('ipop-entering'), 1400);
    },
};
