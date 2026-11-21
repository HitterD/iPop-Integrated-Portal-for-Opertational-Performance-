// Numbered lists that keep counting.
//  - A numbered list split by a picture, video, drawing, table or code block continues where the previous one stopped (4, picture, 5),
//    instead of starting again at 1. A normal paragraph in between still starts a new list, like everywhere else.
//  - "Continue numbering" (block menu) continues from the previous numbered list even across text; "Restart at 1" and
//    "Start at…" fix the number.
//  - The number takes the colour of the item's text when the whole first line has one colour.
// Numbers are shown through the rendered list's start attribute (a decoration), never written into the shared document, so
// two people editing at once can't fight over them.
import { Extension } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';

/// Blocks that can sit between two parts of one numbered list without breaking the count.
export const PASS_THROUGH = new Set(['noteImage', 'noteGallery', 'noteEmbed', 'noteFile', 'noteDraw', 'noteBookmark', 'table', 'codeBlock']);

/// Numbering of every ordered list: [{ pos, first }] where `first` is the number its first item shows.
export function listNumbers(doc) {
  const out = [];
  const visit = (parent, base) => {
    let prevEnd = null;          // the number the previous ordered list (same parent) ended on
    let passOnly = true;          // only pass-through blocks since that list
    parent.forEach((node, offset) => {
      const pos = base + offset;
      if (node.type.name === 'orderedList') {
        const mode = node.attrs.numbering || 'auto';
        const own = node.attrs.start ?? 1;
        let first = own;
        if (own === 1 && mode !== 'restart' && prevEnd != null && (mode === 'continue' || passOnly)) first = prevEnd + 1;
        out.push({ pos, first });
        prevEnd = first + node.childCount - 1;
        passOnly = true;
      } else if (!PASS_THROUGH.has(node.type.name)) {
        passOnly = false;
      }
      // lists inside list items, columns, callouts, toggles count on their own
      if (!node.isTextblock && !node.isAtom && node.childCount) visit(node, pos + 1);
    });
  };
  visit(doc, 0);
  return out;
}

/// The colour of a list item's first line when all of its text has one colour, else null.
export function itemColor(item) {
  const first = item.firstChild;
  if (!first?.isTextblock || !first.content.size) return null;
  let color, mixed = false;
  first.forEach(ch => {
    if (!ch.isText) return;
    const c = ch.marks.find(m => m.type.name === 'textStyle' && m.attrs.color)?.attrs.color ?? null;
    if (color === undefined) color = c; else if (c !== color) mixed = true;
  });
  return mixed ? null : color ?? null;
}

const safeColor = c => (/^(var\(--nt-[a-z]+\)|#[0-9a-f]{3,8}|rgba?\([\d\s.,%]+\))$/i.test(c || '') ? c : null);

export function listDecorations(doc) {
  const decos = [];
  for (const { pos, first } of listNumbers(doc)) {
    const node = doc.nodeAt(pos);
    // the <ol start> the browser counts from (shown only, the stored list keeps its own start)
    if (first !== (node.attrs.start ?? 1)) decos.push(Decoration.node(pos, pos + node.nodeSize, { start: String(first), 'data-continued': '' }));
    node.forEach((item, off) => {
      const c = safeColor(itemColor(item));
      if (c) decos.push(Decoration.node(pos + 1 + off, pos + 1 + off + item.nodeSize, { style: `--nt-marker:${c}` }));
    });
  }
  return DecorationSet.create(doc, decos);
}

export const listKey = new PluginKey('noteListNumbers');

export const ListNumbering = Extension.create({
  name: 'listNumbering',
  addGlobalAttributes() {
    return [{
      types: ['orderedList'],
      attributes: {
        numbering: {
          default: 'auto',
          parseHTML: e => (['continue', 'restart'].includes(e.getAttribute('data-numbering')) ? e.getAttribute('data-numbering') : 'auto'),
          renderHTML: a => (a.numbering && a.numbering !== 'auto' ? { 'data-numbering': a.numbering } : {}),
        },
      },
    }];
  },
  addProseMirrorPlugins() {
    return [new Plugin({
      key: listKey,
      state: {
        init: (_, state) => listDecorations(state.doc),
        apply: (tr, old) => (tr.docChanged ? listDecorations(tr.doc) : old),
      },
      props: { decorations: state => listKey.getState(state) },
    })];
  },
});
