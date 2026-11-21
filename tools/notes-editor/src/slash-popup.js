// Categorised insert picker. Does not take focus from the editor or scroll the document.
const categories = ['Recent', 'Text', 'Lists', 'Media', 'Structure', 'Insert'];
const escape = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const key = 'ipop.notes.slash.recent';
export function readRecent(storage) {
  try { const names = JSON.parse(storage.getItem(key)); return Array.isArray(names) ? names.filter(n => typeof n === 'string').slice(0, 5) : []; } catch { return []; }
}
export function rememberRecent(storage, name) {
  const names = [name, ...readRecent(storage).filter(n => n !== name)].slice(0, 5);
  try { storage.setItem(key, JSON.stringify(names)); } catch { /* optional convenience */ }
  return names;
}
export function categoryFor(name) {
  if (['To-do list', 'Bulleted list', 'Numbered list'].includes(name)) return 'Lists';
  if (['Text', 'Heading 1', 'Heading 2', 'Heading 3', 'Quote', 'Code'].includes(name)) return 'Text';
  if (['Drawing', 'Image', 'File', 'Bookmark', 'Embed video'].includes(name)) return 'Media';
  if (['Page', 'Whiteboard', 'Link to page', 'Mention person', 'Date', 'Today', 'Symbol'].includes(name)) return 'Insert';
  return 'Structure';
}
const samples = {
  Text: 'Write down your ideas.\nKeep the next steps clear.',
  'Heading 1': 'Meeting decisions', 'Heading 2': 'Next steps', 'Heading 3': 'Resources needed',
  'To-do list': '☑ Review the plan\n☐ Share the meeting notes',
  'Bulleted list': '• Risks to discuss\n• Ideas from the team', 'Numbered list': '1. Prepare the brief\n2. Review together\n   a. Capture feedback',
  Toggle: '▸ Supporting details\n▾ Decisions\n   Start with a small pilot',
  '2 columns': 'Pros         │ Cons\nFast         │ Training', '3 columns': 'Plan    │ Do    │ Review',
  Callout: '💡 Decision\nStart with the ICT pilot.', Divider: 'Decisions\n────────────────\nActions',
  Table: 'Task       │ Owner\nReview     │ ICT', Image: '▧ Screenshot\nOriginal size · fits the page',
  File: '▤ Project brief.pdf\nOpen or download', Drawing: 'Idea → Sketch → Discuss', Whiteboard: '[ Idea ] → [ Plan ]\n               ↓\n          [ Deliver ]',
  Page: '▤ Rollout checklist →', 'Link to page': '↗ Rollout checklist', 'Table of contents': '1. Overview\n2. Decisions\n3. Next steps',
  Code: 'const nextStep = "Review";', Quote: '“Make the next step clear.”', Bookmark: '↗ Project reference\nTitle, picture and link', 'Embed video': '▶ Watch the walkthrough',
  'Mention person': '@Alex · invite someone to the discussion', Date: '▣ 19 Nov 2026', Today: '▣ Today', Symbol: '→  ✓  °  ±  ≠',
};
export function slashPopup(place) {
  let pop, all = [], items = [], selected = 0, category = 'Recent', query = '', command, rect, painted = '';
  const storage = () => { try { return window.localStorage; } catch { return null; } };
  const preview = () => {
    const item = items[selected], panel = pop?.querySelector('.nt-slash-preview');
    if (!panel) return;
    panel.innerHTML = item ? `<div class="nt-slash-sample sample-${escape(item.name.replace(/\s/g, '-'))}">${escape(samples[item.name] || `${item.box}\n${item.name}`)}</div><strong>${escape(item.name)}</strong><p>${escape(item.desc)}</p><small>${item.kbd ? `Shortcut: ${escape(item.kbd)} + Space` : 'Select to insert into this note'}</small>` : '<p>No matching blocks. Try another word.</p>';
  };
  const highlight = (reveal = false) => {
    pop.querySelectorAll('.it').forEach(el => { const active = +el.dataset.i === selected; el.classList.toggle('sel', active); el.setAttribute('aria-selected', String(active)); });
    if (reveal) {
      const list = pop.querySelector('.nt-slash-items'), current = list.querySelector('.sel');
      if (current) { if (current.offsetTop < list.scrollTop) list.scrollTop = current.offsetTop; else if (current.offsetTop + current.offsetHeight > list.scrollTop + list.clientHeight) list.scrollTop = current.offsetTop + current.offsetHeight - list.clientHeight; }
    }
    preview();
  };
  const paint = () => {
    const recent = readRecent(storage());
    items = query.trim() ? all : category === 'Recent' ? recent.map(name => all.find(c => c.name === name)).filter(Boolean) : all.filter(c => c.group === category);
    if (!query.trim() && category === 'Recent' && !items.length) items = ['Text', 'To-do list', 'Heading 2', 'Image', 'Whiteboard'].map(name => all.find(c => c.name === name)).filter(Boolean);
    selected = Math.min(selected, Math.max(0, items.length - 1));
    const html = `<div class="nt-slash-search">${query.trim() ? `Results for “${escape(query)}”` : 'Insert a block · type to search all categories'}</div><div class="nt-slash-tabs" role="tablist" aria-label="Block categories">${categories.map(c => `<button type="button" role="tab" aria-selected="${!query.trim() && c === category}" data-category="${c}">${c}</button>`).join('')}</div><div class="nt-slash-body"><div class="nt-slash-items" role="listbox" aria-label="Blocks">${items.map((c, i) => `<div class="it" role="option" aria-selected="false" data-i="${i}"><span class="bx">${escape(c.box)}</span><div><div class="nm">${escape(c.name)}</div><div class="ds">${escape(c.desc)}</div></div></div>`).join('') || '<div class="empty">No matching blocks</div>'}</div><aside class="nt-slash-preview" aria-live="polite"></aside></div><div class="nt-slash-foot">↑ ↓ Explore · Enter Insert · ← → Categories · Esc Close</div>`;
    if (html !== painted) { pop.innerHTML = html; painted = html; }
    highlight();
    if (rect) place(pop, rect);
  };
  const choose = () => { const item = items[selected]; if (!item) return; rememberRecent(storage(), item.name); command(item); };
  return {
    onStart(props) {
      pop = document.createElement('div'); pop.className = 'nt-pop nt-list nt-slash'; pop.setAttribute('aria-label', 'Insert a block');
      pop.addEventListener('mousedown', e => { e.preventDefault(); const tab = e.target.closest('[data-category]'); if (tab) { category = tab.dataset.category; selected = 0; paint(); return; } const item = e.target.closest('[data-i]'); if (item) { selected = +item.dataset.i; choose(); } });
      pop.addEventListener('mousemove', e => { const item = e.target.closest('[data-i]'); if (item && selected !== +item.dataset.i) { selected = +item.dataset.i; highlight(); } });
      document.body.append(pop); this.onUpdate(props);
    },
    onUpdate(props) {
      if (!pop) return;
      command = props.command; rect = props.clientRect?.();
      if (props.loading && painted) { if (rect) place(pop, rect); return; }
      if (query !== (props.query || '')) selected = 0;
      query = props.query || ''; all = props.items; paint();
    },
    onKeyDown({ event }) {
      if (event.key === 'Escape') { pop?.remove(); pop = null; return true; }
      if (!pop) return false;
      if ((event.key === 'ArrowLeft' || event.key === 'ArrowRight') && !query.trim()) { category = categories[(categories.indexOf(category) + (event.key === 'ArrowRight' ? 1 : categories.length - 1)) % categories.length]; selected = 0; paint(); return true; }
      if (!items.length) return false;
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { selected = (selected + (event.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length; highlight(true); return true; }
      if (event.key === 'Enter' || event.key === 'Tab') { choose(); return true; }
      return false;
    },
    onExit() { pop?.remove(); pop = null; painted = ''; },
  };
}
