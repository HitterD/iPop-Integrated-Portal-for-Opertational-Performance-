// Markdown markers on a line made with Shift+Enter.
//
// Shift+Enter inside a paragraph (a list item's text too) adds a line break, not a new paragraph, so the markdown shortcuts of TipTap
// ("1. " "- " "[] " "# ") never fire there: they only match at the very start of a paragraph. People expect them on every visible line.
// When the space that completes such a marker is typed right after a line break, the break and the marker are removed and the text
// after them becomes what the marker says: a numbered / bulleted list or to-do (nested under the item when the paragraph is a list
// item's text) or a heading. Backspace right after puts the marker text back (it is one undoable step, so Ctrl+Z does too).
import { Extension } from '@tiptap/core';
import { Plugin, PluginKey, TextSelection } from '@tiptap/pm/state';

const KINDS = [
  { re: /^(\d{1,3})[.)]$/, make: m => ({ type: 'orderedList', start: +m[1] }) },
  { re: /^[-*•]$/, make: () => ({ type: 'bulletList' }) },
  { re: /^\[[ xX]?\]$/, make: m => ({ type: 'taskList', checked: /x/i.test(m[0]) }) },
  { re: /^(#{1,3})$/, make: m => ({ type: 'heading', level: m[1].length }) },
];

/// What the marker (without its trailing space) stands for, or null.
export function markerKind(text) {
  for (const k of KINDS) { const m = k.re.exec(text); if (m) return k.make(m); }
  return null;
}

/// The marker typed on the last line of a paragraph, given the paragraph and its start position: the text after the last hard break
/// (it must be only plain text, ending at the cursor) and where the break is. Null when there is no break or the line holds more.
export function markerAfterBreak(paragraph, paraPos, cursor) {
  let breakOffset = -1, breakSize = 0;
  paragraph.forEach((child, offset) => { if (child.type.name === 'hardBreak') { breakOffset = offset; breakSize = child.nodeSize; } });
  if (breakOffset < 0) return null;
  const textFrom = paraPos + 1 + breakOffset + breakSize;
  if (cursor < textFrom) return null;
  let text = '', plain = true;
  paragraph.forEach((child, offset) => {
    if (paraPos + 1 + offset < textFrom) return;
    if (child.isText) text += child.text; else plain = false;
  });
  if (!plain) return null;
  return { text, breakFrom: paraPos + 1 + breakOffset, textFrom };
}

function blockFor(schema, kind) {
  const para = () => schema.nodes.paragraph.create();
  if (kind.type === 'heading') return schema.nodes.heading?.create({ level: kind.level });
  if (kind.type === 'orderedList') return schema.nodes.orderedList?.create({ start: kind.start }, schema.nodes.listItem.create(null, para()));
  if (kind.type === 'bulletList') return schema.nodes.bulletList?.create(null, schema.nodes.listItem.create(null, para()));
  if (kind.type === 'taskList') return schema.nodes.taskList?.create(null, schema.nodes.taskItem.create({ checked: !!kind.checked }, para()));
  return null;
}

/// Pure transaction builder (tested without a view): the state, the typed space position; returns a transaction or null.
export function markerTransaction(state, at) {
  const { selection } = state;
  if (!selection.empty) return null;
  const $at = state.doc.resolve(at);
  const para = $at.parent;
  if (para.type.name !== 'paragraph' || para.type.spec.code) return null;
  const paraPos = $at.before();
  const found = markerAfterBreak(para, paraPos, at);
  if (!found || found.text.length === 0 || at !== $at.end()) return null;
  const kind = markerKind(found.text);
  if (!kind) return null;
  const block = blockFor(state.schema, kind);
  if (!block) return null;

  const tr = state.tr;
  // remove the line break and the typed marker (the space has not been inserted yet: it is the key that triggered this)
  tr.delete(found.breakFrom, at);
  const paraStart = tr.mapping.map(paraPos);
  const afterPara = paraStart + tr.doc.nodeAt(paraStart).nodeSize;
  tr.insert(afterPara, block);
  // the caret goes into the new block's first (empty) paragraph / heading
  const inside = kind.type === 'heading' ? afterPara + 1 : afterPara + 3;
  tr.setSelection(TextSelection.near(tr.doc.resolve(Math.min(inside, tr.doc.content.size)), 1));
  return tr.scrollIntoView();
}

const markerKey = new PluginKey('lineBreakMarkers');

export const LineBreakMarkers = Extension.create({
  name: 'lineBreakMarkers',
  priority: 1100,
  addProseMirrorPlugins() {
    return [new Plugin({
      key: markerKey,
      state: {
        init: () => null,
        apply(tr, previous) {
          const marker = tr.getMeta(markerKey);
          if (marker) return marker;
          if (previous && tr.getMeta('appendedTransaction')) {
            return { ...previous, steps: [...previous.steps, ...tr.steps], docs: [...previous.docs, ...tr.docs] };
          }
          return tr.docChanged || tr.selectionSet ? null : previous;
        },
      },
      props: {
        handleKeyDown(view, event) {
          if (event.key !== 'Backspace' || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return false;
          const previous = markerKey.getState(view.state);
          if (!previous) return false;
          const tr = view.state.tr;
          for (let i = previous.steps.length - 1; i >= 0; i--) tr.step(previous.steps[i].invert(previous.docs[i]));
          tr.setSelection(TextSelection.create(tr.doc, previous.at));
          tr.insertText(' ');
          view.dispatch(tr.scrollIntoView());
          return true;
        },
        handleTextInput(view, from, to, text) {
          if (text !== ' ' || from !== to) return false;
          const tr = markerTransaction(view.state, from);
          if (!tr) return false;
          tr.setMeta(markerKey, { steps: [...tr.steps], docs: [...tr.docs], at: from });
          view.dispatch(tr);
          return true;
        },
      },
    })];
  },
});
