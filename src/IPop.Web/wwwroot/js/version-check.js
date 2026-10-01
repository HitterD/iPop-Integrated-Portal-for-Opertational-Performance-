// Picks up a new deploy without a manual hard refresh. Every few minutes and whenever the tab comes back
// into view it fetches index.html and the runtime entry (dotnet.js, which embeds the assembly hashes) with
// no-store; if either changed since this tab loaded, the tab reloads — straight away when that's harmless,
// otherwise (call running, mail being written, user typing) a "New version" bar asks first and the reload
// waits until the call/compose is over.
(function () {
    const EVERY_MS = 4 * 60 * 1000, IDLE_MS = 90 * 1000;
    let base = null, pending = false, lastInput = Date.now(), bar = null;

    async function signature() {
        const opt = { cache: 'no-store', credentials: 'same-origin' };
        const [html, rt] = await Promise.all([
            fetch('index.html', opt).then(r => r.ok ? r.text() : null),
            fetch('_framework/dotnet.js', { ...opt, method: 'HEAD' })
                .then(r => r.ok ? (r.headers.get('etag') || r.headers.get('last-modified') || r.headers.get('content-length') || '') : '')
                .catch(() => ''),
        ]);
        return html == null ? null : html.length + ':' + hash(html) + '|' + rt;
    }
    function hash(s) { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return h.toString(36); }

    // Unsafe to reload: a call UI is up, the mail composer is open, or a page flags unsaved work.
    const busy = () => !!document.querySelector('[class*="callx-"], .mc-sheet, [data-unsaved="true"]');

    function reload() { location.reload(); }

    function decide() {
        if (!pending) return;
        if (busy()) { showBar(); return; }
        if (document.visibilityState === 'hidden' || Date.now() - lastInput > IDLE_MS) reload();
        else showBar();
    }

    function showBar() {
        if (bar) return;
        bar = document.createElement('div');
        bar.className = 'ipop-update-bar';
        bar.setAttribute('role', 'status');
        bar.innerHTML = '<span>A new version of iPop is available.</span><button type="button">Reload</button>';
        bar.querySelector('button').onclick = reload;
        document.body.appendChild(bar);
    }

    async function check() {
        if (pending) { decide(); return; }
        try {
            const sig = await signature();
            if (sig == null) return;
            if (base == null) { base = sig; return; }
            if (sig !== base) { pending = true; decide(); }
        } catch { /* offline or server restarting: try again next round */ }
    }

    for (const ev of ['keydown', 'pointerdown', 'wheel', 'touchstart'])
        addEventListener(ev, () => { lastInput = Date.now(); }, { passive: true, capture: true });
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible' || pending) check(); });
    addEventListener('focus', check);
    setInterval(check, EVERY_MS);
    // Once the bar is up, reload by itself as soon as the call/compose ends and the user goes idle.
    setInterval(() => { if (pending && !busy() && (document.visibilityState === 'hidden' || Date.now() - lastInput > IDLE_MS)) reload(); }, 15 * 1000);
    check();
    window.ipopVersion = { check };
})();
