// "Type '/' for commands…" on the empty line the caret is on, and nowhere else.
// TipTap's Placeholder with includeChildren keeps a mapped decoration set and only rescans the blocks a transaction touched.
// A Yjs update from someone else replaces the whole document, so old decorations were mapped onto other empty lines and stayed
// there ("placeholder left behind" on five lines). This one is computed fresh from the current state every time: one caret,
// one placeholder, plus the labels of empty headings and toggle titles (those always show, like Notion).
import { Extension } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';

export const placeholderKey = new PluginKey('notePlaceholder');

export function placeholderText(node) {
  if (node.type.name === 'heading') return `Heading ${node.attrs.level}`;
  if (node.type.name === 'detailsSummary') return 'Toggle';
  return "Type '/' for commands…";
}

/// The decorations for a state: pure, so it can be tested without a view.
export function placeholderDecorations(state, { focused = true, editable = true } = {}) {
  if (!editable) return DecorationSet.empty;
  const decos = [];
  const add = (node, pos) => decos.push(Decoration.node(pos, pos + node.nodeSize, { class: 'is-empty', 'data-placeholder': placeholderText(node) }));
  // empty headings and toggle titles keep their label wherever they are
  state.doc.descendants((node, pos) => {
    if (!node.isTextblock) return true;
    if (node.content.size === 0 && (node.type.name === 'heading' || node.type.name === 'detailsSummary')) add(node, pos);
    return false;
  });
  // the caret's own empty line (paragraphs inside lists, columns and callouts too)
  const { selection } = state;
  if (focused && selection.empty) {
    const $a = selection.$anchor;
    const node = $a.parent;
    if (node.isTextblock && node.content.size === 0 && node.type.name !== 'heading' && node.type.name !== 'detailsSummary' && !node.type.spec.code) {
      add(node, $a.before());
    }
  }
  // a brand-new page with nothing in it shows the hint even before it gets focus
  if (!focused && state.doc.childCount === 1 && state.doc.firstChild.isTextblock && state.doc.firstChild.content.size === 0 && state.doc.firstChild.type.name === 'paragraph') {
    add(state.doc.firstChild, 0);
  }
  return DecorationSet.create(state.doc, decos);
}

export const NotePlaceholder = Extension.create({
  name: 'notePlaceholder',
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [new Plugin({
      key: placeholderKey,
      props: {
        decorations: state => placeholderDecorations(state, { focused: editor.isFocused, editable: editor.isEditable }),
      },
    })];
  },
  onFocus() { this.editor.view.dispatch(this.editor.state.tr.setMeta(placeholderKey, 'focus').setMeta('addToHistory', false)); },
  onBlur() { if (!this.editor.isDestroyed) this.editor.view.dispatch(this.editor.state.tr.setMeta(placeholderKey, 'blur').setMeta('addToHistory', false)); },
});
