// Per-account localStorage. Settings that used to live under one shared key (mail signature,
// notification and sound preferences, calendar view, …) leaked from one account to the next on a
// shared browser. Each is now stored as "<key>@<nik>". A value saved under the old shared key goes
// to the first account that reads it after the update and is then removed, so nobody else sees it.
(function () {
    let current = null;

    const scoped = (nik, key) => `${key}@${nik}`;

    function get(nik, key) {
        if (!nik) return null;
        try {
            const own = localStorage.getItem(scoped(nik, key));
            if (own !== null) return own;
            const legacy = localStorage.getItem(key);
            if (legacy === null) return null;
            localStorage.setItem(scoped(nik, key), legacy);
            localStorage.removeItem(key);
            return legacy;
        } catch { return null; }
    }

    function set(nik, key, value) {
        if (!nik) return;
        try { localStorage.setItem(scoped(nik, key), value); } catch { /* quota or blocked */ }
    }

    window.ipopUserStore = {
        /// The signed-in NIK, for scripts that read their own settings (sounds.js). null = signed out.
        setUser(nik) { current = nik || null; },
        get user() { return current; },
        get,
        set,
        getMine: key => get(current, key),
        setMine: (key, value) => set(current, key, value),
    };
})();
