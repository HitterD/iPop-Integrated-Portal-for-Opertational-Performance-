// Plain text out, Office HTML in.
//  - sliceText: what other apps get as text when you copy from a note. One line per paragraph (no blank line between every
//    block, which is what made a copied note "jadi banyak line"), list items keep a marker, to-dos keep their box.
//  - cleanOfficeHtml: Word/Outlook lists arrive as paragraphs with a "·" or "1." typed in front; turn them back into real lists
//    and drop Office's styles, comments and <o:p> tags. Excel tables pass through as tables.

/// Text for a ProseMirror Fragment/Node content (the copied slice).
export function sliceText(fragment) {
  const lines = [];
  const walk = (node, prefix, indent) => {
    const t = node.type.name;
    if (node.isTextblock) {
      let text = '';
      node.forEach(ch => { text += ch.isText ? ch.text : (ch.type.spec.leafText?.(ch) ?? ch.type.spec.renderText?.(ch) ?? textOfLeaf(ch)); });
      lines.push(indent + prefix + text);
      return;
    }
    if (node.isLeaf) {
      if (t === 'horizontalRule') lines.push('---');
      else if (t === 'noteImage') lines.push(indent + (node.attrs.caption || node.attrs.name ? `[Picture: ${node.attrs.caption || node.attrs.name}]` : '[Picture]'));
      else if (t === 'hardBreak') lines.push('');
      else { const lt = textOfLeaf(node); if (lt) lines.push(indent + lt); }
      return;
    }
    if (t === 'bulletList' || t === 'orderedList' || t === 'taskList') {
      let n = node.attrs.start ?? 1;
      node.forEach(item => {
        const mark = t === 'bulletList' ? '- ' : t === 'orderedList' ? `${n++}. ` : (item.attrs.checked ? '[x] ' : '[ ] ');
        let first = true;
        item.forEach(child => {
          if (child.type.name === 'bulletList' || child.type.name === 'orderedList' || child.type.name === 'taskList') walk(child, '', indent + '  ');
          else { walk(child, first ? mark : '  ', indent); first = false; }
        });
      });
      return;
    }
    if (t === 'table') {
      node.forEach(row => { const cells = []; row.forEach(cell => cells.push(cellText(cell))); lines.push(indent + cells.join('\t')); });
      return;
    }
    node.forEach(child => walk(child, '', indent));
  };
  fragment.forEach(n => walk(n, '', ''));
  return lines.join('\n');
}
function textOfLeaf(n) {
  if (n.type.name === 'mention') return `@${n.attrs.label ?? ''}`;
  if (n.type.name === 'dateChip') return n.attrs.date || '';
  return n.attrs?.title || n.attrs?.url || '';
}
function cellText(cell) { const parts = []; cell.forEach(b => parts.push(b.textContent)); return parts.join(' ').replace(/\s+/g, ' ').trim(); }

const BULLET_RE = /^\s*([·•▪◦o§\-–]|\u00b7)\s+/;
const NUMBER_RE = /^\s*(\d{1,3}|[a-z]|[ivx]{1,4})[.)]\s+/i;
export const isOfficeHtml = html => /urn:schemas-microsoft-com:office|class="?Mso|<o:p>|mso-list/i.test(html);

/// Office HTML → clean HTML (string in, string out). Needs a DOMParser (browser or happy-dom).
export function cleanOfficeHtml(html, parser = new DOMParser()) {
  if (!isOfficeHtml(html)) return html;
  const src = html.replace(/<!--\[if[\s\S]*?<!\[endif\]-->/gi, '').replace(/<!--[\s\S]*?-->/g, '').replace(/<\/?o:p>/gi, '');
  const doc = parser.parseFromString(src, 'text/html');
  doc.querySelectorAll('style,meta,link,xml,script').forEach(e => e.remove());
  // Word list paragraphs → <ul>/<ol>
  const paras = [...doc.body.querySelectorAll('p')];
  let list = null, kind = null;
  for (const p of paras) {
    const style = p.getAttribute('style') || '', cls = p.getAttribute('class') || '';
    const isList = /mso-list/i.test(style) || /MsoListParagraph/i.test(cls);
    // Word puts the marker in a span with mso-list:Ignore
    const ignore = p.querySelector('span[style*="mso-list"]');
    const markerText = ignore ? ignore.textContent : '';
    const text = p.textContent || '';
    const k = isList ? (NUMBER_RE.test(markerText || text) ? 'ol' : 'ul') : null;
    if (!k) { list = null; kind = null; continue; }
    if (ignore) ignore.remove(); else p.innerHTML = p.innerHTML.replace(k === 'ol' ? NUMBER_RE : BULLET_RE, '');
    if (!list || kind !== k || list.nextElementSibling !== p) {
      list = doc.createElement(k); kind = k;
      p.before(list);
    }
    const li = doc.createElement('li');
    const np = doc.createElement('p'); np.innerHTML = p.innerHTML.replace(/^(\s|&nbsp;)+/, '');
    li.append(np); list.append(li); p.remove();
  }
  // Office styles fight the page's own look; keep only colour, bold, italic, underline that are on the inline tags
  doc.body.querySelectorAll('[style]').forEach(e => {
    const st = e.getAttribute('style') || '';
    const keep = [];
    const color = /(?:^|;)\s*color\s*:\s*([^;]+)/i.exec(st);
    if (color && !/windowtext|black|#000000|auto/i.test(color[1]) && e.tagName === 'SPAN') keep.push(`color:${color[1].trim()}`);
    if (/font-weight\s*:\s*(bold|[6-9]00)/i.test(st) && e.tagName === 'SPAN') keep.push('font-weight:bold');
    if (/font-style\s*:\s*italic/i.test(st) && e.tagName === 'SPAN') keep.push('font-style:italic');
    if (keep.length) e.setAttribute('style', keep.join(';')); else e.removeAttribute('style');
  });
  doc.body.querySelectorAll('[class]').forEach(e => e.removeAttribute('class'));
  // empty Word paragraphs (&nbsp;) between blocks
  doc.body.querySelectorAll('p').forEach(p => { if (!p.textContent.replace(/\u00a0/g, '').trim() && !p.querySelector('img')) p.remove(); });
  return doc.body.innerHTML;
}

/// Office copies a table or text AND a picture of it. Use the files only when the HTML has no text (a real picture copy).
export function preferFiles(clipboardData) {
  if (!clipboardData?.files?.length) return false;
  const html = clipboardData.getData?.('text/html') || '';
  if (!html) return true;
  const text = html.replace(/<style[\s\S]*?<\/style>/gi, '').replace(/<!--[\s\S]*?-->/g, '').replace(/<[^>]+>/g, '').replace(/&nbsp;/g, ' ').trim();
  return text.length === 0;
}
