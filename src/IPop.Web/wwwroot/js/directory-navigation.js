// Keep Space activation from scrolling, without changing nested contact controls.
document.addEventListener('keydown', e => {
    if ((e.key === ' ' || e.key === 'Enter') && e.target.matches('.dir-card[tabindex], .dir-people tr[tabindex], .fav-row[tabindex]')) e.preventDefault();
});
let restoreFocus, listener;
const madeInert = [];
export function open(dotnet) {
    if (!matchMedia('(max-width: 767px)').matches) return;
    const sheet = document.getElementById('directory-departments');
    if (!sheet) return;
    restoreFocus = document.activeElement;
    sheet.setAttribute('role', 'dialog');
    sheet.setAttribute('aria-modal', 'true');
    sheet.setAttribute('aria-label', 'Departments');
    for (let current = sheet; current.parentElement; current = current.parentElement) {
        for (const sibling of current.parentElement.children) {
            if (sibling === current || sibling.classList.contains('dir-tree-backdrop') || sibling.inert) continue;
            sibling.inert = true; madeInert.push(sibling);
        }
    }
    listener = e => {
        if (e.key === 'Escape') { e.preventDefault(); dotnet.invokeMethodAsync('CloseDepartments'); }
        if (e.key !== 'Tab') return;
        const focusable = [...sheet.querySelectorAll('button:not(:disabled),input,[tabindex="0"]')].filter(el => el.getClientRects().length);
        const first = focusable[0], last = focusable.at(-1);
        if (e.shiftKey && (document.activeElement === first || document.activeElement === sheet)) { e.preventDefault(); last?.focus(); }
        else if (!e.shiftKey && (document.activeElement === last || !sheet.contains(document.activeElement))) { e.preventDefault(); first?.focus(); }
    };
    document.addEventListener('keydown', listener);
    sheet.querySelector('button')?.focus({ preventScroll: true });
}
export function close() {
    if (listener) document.removeEventListener('keydown', listener);
    listener = null;
    for (const el of madeInert.splice(0)) el.inert = false;
    const sheet = document.getElementById('directory-departments');
    sheet?.removeAttribute('role'); sheet?.removeAttribute('aria-modal'); sheet?.removeAttribute('aria-label');
    if (restoreFocus?.isConnected) restoreFocus.focus({ preventScroll: true });
    restoreFocus = null;
}
