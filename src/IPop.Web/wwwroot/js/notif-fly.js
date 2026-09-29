// Banner -> bell: when a notification banner times out, it shrinks and flies along a curve
// into the topbar bell, which then rings and pops its badge. Called from NotificationState
// before the banner is removed; resolves once the ghost lands (or immediately when it can't fly).
window.ipopNotifFly = (() => {
    const reduced = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;

    function visibleBell() {
        for (const el of document.querySelectorAll('.bell-btn')) {
            const r = el.getBoundingClientRect();
            if (r.width > 0 && r.height > 0 && r.bottom > 0 && r.top < innerHeight) return el;
        }
        return null;
    }

    function ring(bell) {
        bell.classList.remove('nf-ring');
        void bell.offsetWidth;
        bell.classList.add('nf-ring');
        setTimeout(() => bell.classList.remove('nf-ring'), 900);
    }

    async function fly(id) {
        const card = document.querySelector(`.apple-banner[data-nid="${id}"]`);
        const bell = visibleBell();
        if (!card || !bell || document.hidden) return false;
        if (reduced() || !card.animate) { ring(bell); return false; }

        const from = card.getBoundingClientRect();
        const to = bell.getBoundingClientRect();
        const ghost = card.cloneNode(true);
        ghost.removeAttribute('data-nid');
        ghost.classList.add('nf-ghost');
        Object.assign(ghost.style, {
            left: from.left + 'px', top: from.top + 'px',
            width: from.width + 'px', height: from.height + 'px'
        });
        document.body.appendChild(ghost);
        card.classList.add('nf-gone');

        // Curve: first lift a little and shrink into a pill, then dive into the bell centre.
        const dx = to.left + to.width / 2 - (from.left + from.width / 2);
        const dy = to.top + to.height / 2 - (from.top + from.height / 2);
        const s = Math.max(14 / from.width, 0.04);
        const anim = ghost.animate([
            { transform: 'translate(0,0) scale(1)', opacity: 1, borderRadius: '16px' },
            { transform: `translate(${dx * 0.35}px, ${dy * 0.35 - 28}px) scale(0.42)`, opacity: 1, borderRadius: '22px', offset: 0.45 },
            { transform: `translate(${dx}px, ${dy}px) scale(${s})`, opacity: 0.2, borderRadius: '999px' }
        ], { duration: 720, easing: 'cubic-bezier(.5,0,.25,1)', fill: 'forwards' });

        try { await anim.finished; } catch { /* cancelled */ }
        ghost.remove();
        ring(bell);
        return true;
    }

    return { fly, ring };
})();
