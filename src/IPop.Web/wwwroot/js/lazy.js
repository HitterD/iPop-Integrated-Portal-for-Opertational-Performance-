// On-demand loading for the heavy third-party bundles, so first load only pays for the shell.
//   LiveKit (~600 KB): needed only when a group call or meeting joins.
//   SheetJS (~900 KB, CDN): needed only for Excel export / .xls preview.
// The entry points Blazor calls are wrapped so each waits for its library first; callers don't change.
(function () {
    const pending = {};
    function load(src, ready) {
        if (ready()) return Promise.resolve(true);
        return pending[src] ??= new Promise((resolve) => {
            const s = document.createElement('script');
            s.src = src; s.async = true;
            s.onload = () => resolve(!!ready());
            s.onerror = () => { delete pending[src]; resolve(false); };
            document.head.appendChild(s);
        });
    }
    const livekit = () => load('js/livekit-client.umd.js?v=20261001-nik', () => window.LivekitClient?.Room);
    const xlsx = () => load('https://cdn.jsdelivr.net/npm/xlsx@0.18.5/dist/xlsx.full.min.js', () => window.XLSX);

    function wrap(obj, name, need) {
        const fn = obj?.[name];
        if (typeof fn !== 'function') return;
        obj[name] = async function (...args) { await need(); return fn.apply(this, args); };
    }
    wrap(window.ipopGroupCall, 'join', livekit);
    wrap(window.ipopMeeting, 'join', livekit);
    wrap(window.ipop, 'exportXlsx', xlsx);
    wrap(window.ipop, 'renderOffice', xlsx);
    wrap(window.ipop, 'renderXls', xlsx);

    window.ipopLazy = { livekit, xlsx };
    // Warm the call bundle once the app is idle, so the first call doesn't wait on the download.
    (window.requestIdleCallback || ((f) => setTimeout(f, 4000)))(() => setTimeout(livekit, 8000));
})();
