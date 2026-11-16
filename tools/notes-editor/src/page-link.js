// A link to another Notes page, written inline: type [[ and pick a page (or use the slash menu). It is a small chip showing the
// page's current icon and title (so a rename shows up everywhere), clicking it opens the page, and the server remembers the
// link so the target page can list "Linked from" (backlinks).
import { Node } from '@tiptap/core';
import Suggestion from '@tiptap/suggestion';
import { PluginKey } from '@tiptap/pm/state';
import { el, esc } from './nodes.js';

export const pageLinkKey = new PluginKey('pageLink');

/// Pages whose title matches `query`, best matches first: title starts with it, then a word starts with it, then contains it.
/// The page being edited and the guide page are left out. Pure, so it can be tested without an editor.
export function matchPages(pages, query, { exclude = [] } = {}) {
  const q = String(query || '').trim().toLowerCase();
  const rank = t => { const s = t.toLowerCase(); return s.startsWith(q) ? 0 : s.split(/\s+/).some(w => w.startsWith(q)) ? 1 : s.includes(q) ? 2 : 9; };
  return [...pages]
    .filter(p => p && p.id && !exclude.includes(p.id))
    .map(p => ({ p, r: q ? rank(p.title || 'Untitled') : 3 }))
    .filter(x => x.r < 9)
    .sort((a, b) => a.r - b.r || String(a.p.title).localeCompare(String(b.p.title)))
    .slice(0, 8)
    .map(x => x.p);
}

/// Page ids a document links to, in reading order without duplicates (used when the page is saved).
export function pageLinkIds(doc) {
  const ids = [];
  doc.descendants(n => { if (n.type.name === 'pageLink' && n.attrs.id && !ids.includes(n.attrs.id)) ids.push(n.attrs.id); });
  return ids;
}

export const PageLink = Node.create({
  name: 'pageLink',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  addAttributes() {
    return {
      id: { default: null, parseHTML: e => e.getAttribute('data-id') },
      title: { default: 'Untitled', parseHTML: e => e.getAttribute('data-title') || 'Untitled' },
      emoji: { default: '📄', parseHTML: e => e.getAttribute('data-emoji') || '📄' },
    };
  },
  parseHTML() { return [{ tag: 'span[data-page-link]' }]; },
  renderHTML({ node: { attrs: a } }) { return ['span', { 'data-page-link': '', 'data-id': a.id, 'data-title': a.title, 'data-emoji': a.emoji }, a.title]; },
  renderText({ node }) { return node.attrs.title || 'Untitled'; },
  addOptions() { return { host: null }; },

  addNodeView() {
    return ({ node }) => {
      const host = this.options.host?.() ?? {};
      const dom = el('span', 'nt-pagelink');
      dom.contentEditable = 'false';
      dom.tabIndex = 0;
      dom.setAttribute('role', 'link');
      const paint = n => {
        const live = host.pageInfo?.(n.attrs.id);
        const gone = host.pageInfo && !live && host.pagesKnown?.();
        dom.classList.toggle('gone', !!gone);
        dom.title = gone ? 'This page was deleted or you no longer have access' : 'Open page';
        dom.innerHTML = `<span class="emo">${esc(live?.emoji || n.attrs.emoji || '📄')}</span><span class="t">${esc(live?.title || n.attrs.title || 'Untitled')}</span>`;
      };
      paint(node);
      const open = () => node.attrs.id && host.openPage?.(node.attrs.id);
      dom.addEventListener('click', e => { e.preventDefault(); open(); });
      dom.addEventListener('keydown', e => { if (e.key === 'Enter') open(); });
      return { dom, update(n) { if (n.type.name !== 'pageLink') return false; node = n; paint(n); return true; }, ignoreMutation: () => true };
    };
  },

  addProseMirrorPlugins() {
    const host = this.options.host?.() ?? {};
    return [Suggestion({
      editor: this.editor,
      pluginKey: pageLinkKey,
      char: '[[',
      allowSpaces: true,
      allowedPrefixes: null,
      items: ({ query }) => matchPages(host.allPages?.() ?? [], query, { exclude: [host.currentPageId?.()].filter(Boolean) }),
      command: ({ editor, range, props }) => {
        editor.chain().focus().insertContentAt(range, [
          { type: 'pageLink', attrs: { id: props.id, title: props.title || 'Untitled', emoji: props.emoji || '📄' } }, { type: 'text', text: ' ' },
        ]).run();
      },
      render: () => host.listPopup?.(p => `<span class="bx">${esc(p.emoji || '📄')}</span><div><div class="nm">${esc(p.title || 'Untitled')}</div></div>`,
        q => (q.trim() ? `No page called “${esc(q.trim())}”` : 'Type to find a page')),
    })];
  },
});
