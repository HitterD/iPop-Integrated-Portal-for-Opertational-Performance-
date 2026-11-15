// "Tidy up page": rule-based suggestions (no AI, nothing leaves the browser). Each rule looks at the page's ProseMirror JSON
// and returns suggestions; applying a suggestion returns a NEW document, which the editor swaps in as one undoable step.
//
// Rules are deliberately conservative: they only touch structure (empty blocks, heading levels, split lists, a bare
// link line, a missing title / contents block) and never rewrite what a person wrote.

const textOf = n => (n.text ?? '') + (n.content ?? []).map(textOf).join('');
const isEmptyPara = n => n.type === 'paragraph' && !textOf(n).trim() && !(n.content ?? []).some(c => c.type !== 'text');
const clone = x => JSON.parse(JSON.stringify(x));
const BARE_URL = /^\s*(https?:\/\/[^\s<>"']+)\s*$/i;

export const RULES = ['title', 'blank', 'heading', 'list', 'link', 'toc'];

function countExtraBlanks(b) {
  let run = 0, extra = 0;
  for (const n of b) { if (isEmptyPara(n)) { run++; if (run > 1) extra++; } else run = 0; }
  return extra;
}

/// Blocks at the top level only: nested content (list items, toggles, callouts) is left alone.
function blocks(doc) { return doc.content ?? []; }

function findTitle(doc) {
  for (const n of blocks(doc)) {
    if (n.type === 'heading') { const t = textOf(n).trim(); if (t) return t; }
    if (n.type === 'paragraph') { const t = textOf(n).trim(); if (t) return t.length > 80 ? t.slice(0, 79) + '…' : t; }
  }
  return '';
}

export function analyze(doc, { title = '' } = {}) {
  const out = [];
  const b = blocks(doc);

  // 1. Untitled page → use the first heading / line.
  if (!String(title).trim()) {
    const t = findTitle(doc);
    if (t) out.push({ id: 'title', rule: 'title', label: 'Give the page a title', detail: `“${t}”, from the first heading`, value: t });
  }

  // 2. Runs of empty paragraphs (2+) → keep one. A single empty line is normal and never flagged.
  const removable = countExtraBlanks(b);
  if (removable > 0) out.push({ id: 'blank', rule: 'blank', label: `Remove ${removable} extra empty line${removable === 1 ? '' : 's'}`, detail: 'Keeps one blank line where there were several' });

  // 3. Heading levels that skip (H1 → H3): lower the jump to the next level.
  let prev = 0; const fix = [];
  b.forEach((n, i) => {
    if (n.type !== 'heading') return;
    const lvl = n.attrs?.level ?? 1;
    const want = prev === 0 ? lvl : Math.min(lvl, prev + 1);
    if (want !== lvl) fix.push([i, lvl, want]);
    prev = want;
  });
  if (fix.length) {
    const [i, from, to] = fix[0];
    out.push({ id: 'heading', rule: 'heading', label: fix.length === 1 ? 'Fix a heading that skips a level' : `Fix ${fix.length} headings that skip a level`,
      detail: `“${textOf(b[i]).trim().slice(0, 40)}” is level ${from} right after a level ${to - 1} → make it level ${to}` });
  }

  // 4. Two lists of the same kind separated only by empty paragraphs → one list.
  let joins = 0;
  for (let i = 0; i + 1 < b.length; i++) {
    const a = b[i];
    if (!['bulletList', 'orderedList', 'taskList'].includes(a.type)) continue;
    let j = i + 1;
    while (j < b.length && isEmptyPara(b[j])) j++;
    if (j > i + 1 && b[j]?.type === a.type) joins++;
  }
  if (joins) out.push({ id: 'list', rule: 'list', label: joins === 1 ? 'Join a list that was split in two' : `Join ${joins} split lists`, detail: 'Lists separated only by empty lines become one list' });

  // 5. A paragraph that is only a URL → bookmark card.
  const urls = b.filter(n => n.type === 'paragraph' && BARE_URL.test(textOf(n)) && !(n.content ?? []).some(c => c.type === 'hardBreak'));
  if (urls.length) {
    const u = BARE_URL.exec(textOf(urls[0]))[1];
    out.push({ id: 'link', rule: 'link', label: urls.length === 1 ? 'Turn a bare link into a bookmark' : `Turn ${urls.length} bare links into bookmarks`, detail: u.replace(/^https?:\/\//, '').slice(0, 48) });
  }

  // 6. Long page (3+ headings, no contents block yet) → table of contents.
  const headings = b.filter(n => n.type === 'heading').length;
  if (headings >= 3 && !b.some(n => n.type === 'noteToc')) out.push({ id: 'toc', rule: 'toc', label: 'Add a table of contents', detail: `This page has ${headings} headings` });

  return out;
}

/// Applies one rule. Returns { doc, title? }: title is set only by the 'title' rule (the title is not part of the document).
export function apply(doc, rule, { title = '' } = {}) {
  const d = clone(doc);
  const b = d.content ?? [];
  switch (rule) {
    case 'title': return { doc: d, title: findTitle(d) };
    case 'blank': {
      const keep = []; let run = 0;
      for (const n of b) {
        if (isEmptyPara(n)) { run++; if (run === 1) keep.push(n); }
        else { run = 0; keep.push(n); }
      }
      d.content = keep; return { doc: d };
    }
    case 'heading': {
      let prev = 0;
      for (const n of b) {
        if (n.type !== 'heading') continue;
        const lvl = n.attrs?.level ?? 1;
        const want = prev === 0 ? lvl : Math.min(lvl, prev + 1);
        n.attrs = { ...n.attrs, level: want }; prev = want;
      }
      return { doc: d };
    }
    case 'list': {
      const LISTS = ['bulletList', 'orderedList', 'taskList'];
      const out = [];
      let i = 0;
      while (i < b.length) {
        const a = b[i];
        if (!LISTS.includes(a.type)) { out.push(a); i++; continue; }
        const merged = { ...a, content: [...(a.content ?? [])] };
        i++;
        for (;;) {
          let k = i; while (k < b.length && isEmptyPara(b[k])) k++;
          if (k > i && b[k]?.type === a.type) { merged.content.push(...(b[k].content ?? [])); i = k + 1; }
          else break;
        }
        out.push(merged);
      }
      d.content = out; return { doc: d };
    }
    case 'link': {
      d.content = b.map(n => {
        if (n.type !== 'paragraph' || !BARE_URL.test(textOf(n))) return n;
        const url = BARE_URL.exec(textOf(n))[1];
        let host = ''; try { host = new URL(url).hostname; } catch { /* keep empty */ }
        return { type: 'noteBookmark', attrs: { url, title: '', desc: '', site: host } };
      });
      return { doc: d };
    }
    case 'toc': {
      const at = b.findIndex(n => n.type === 'heading');
      d.content = [...b.slice(0, Math.max(0, at)), { type: 'noteToc' }, ...b.slice(Math.max(0, at))];
      return { doc: d };
    }
    default: return { doc: d };
  }
}

/// Headings of the page for the table of contents: [{ level, text, pos }] with pos = document position of the heading.
export function outline(pmDoc) {
  const out = [];
  pmDoc.descendants((node, pos) => {
    if (node.type.name === 'heading') { const text = node.textContent.trim(); if (text) out.push({ level: node.attrs.level, text, pos }); return false; }
    return true;
  });
  return out;
}
