// Typed symbols: "->" becomes "→" while you type (Backspace right after puts the text back), and `:deg:`-style names for the
// word-like ones so ordinary words never change. A small chip says what happened the first few times; /symbol lists them all.
import { Extension, InputRule } from '@tiptap/core';
import { Plugin, PluginKey } from '@tiptap/pm/state';

/// [typed, symbol, label]. Order matters: longer sequences that end like a shorter one come first ("<->" before "->").
export const SYMBOL_RULES = [
  // arrows
  ['<=>', '⇔', 'Double arrow both ways'], ['<->', '↔', 'Arrow both ways'], ['-->', '⟶', 'Long arrow'], ['->', '→', 'Arrow right'],
  ['<-', '←', 'Arrow left'], ['=>', '⇒', 'Double arrow'],
  // punctuation
  ['...', '…', 'Ellipsis'], ['<<', '«', 'Left quote mark'], ['>>', '»', 'Right quote mark'],
  // maths
  ['!=', '≠', 'Not equal'], ['<=', '≤', 'Less or equal'], ['>=', '≥', 'Greater or equal'], ['+-', '±', 'Plus-minus'], ['~=', '≈', 'About equal'],
  // legal
  ['(c)', '©', 'Copyright'], ['(r)', '®', 'Registered'], ['(tm)', '™', 'Trademark'], ['(p)', '§', 'Section'],
  // status
  ['(v)', '✓', 'Check'], ['(x)', '✗', 'Cross'], ['(!)', '⚠', 'Warning'], ['(?)', '❓', 'Question'], ['(*)', '★', 'Star'],
];
/// "--" → em dash only between words ("a -- b" / "a--b"), so "---" can still become a divider at the start of a line.
export const DASH_RULE = ['--', '—', 'Long dash'];
/// Fractions only when they stand alone (space before and after), never inside dates like 1/2/2026.
export const FRACTIONS = [['1/2', '½', 'One half'], ['1/4', '¼', 'One quarter'], ['3/4', '¾', 'Three quarters']];
/// `:name:` symbols (shown in the ":" popup together with emoji).
export const NAMED_SYMBOLS = {
  deg: '°', inf: '∞', sqrt: '√', times: '×', div: '÷', approx: '≈', eur: '€', usd: '$', yen: '¥', pound: '£', micro: 'µ', pi: 'π',
  sum: '∑', delta: 'Δ', ohm: 'Ω', alpha: 'α', beta: 'β', check_mark: '✓', cross_mark: '✗', bullet: '•', middot: '·', para: '¶', section: '§',
  arrow: '→', larrow: '←', uarrow: '↑', darrow: '↓', star2: '★', degc: '℃', permil: '‰', half: '½', quarter: '¼',
};

/// Rules fire on every key, so "<=>" is typed as "<=" (already "≤") then ">": these finish the longer ones.
const CHAINS = [['≤>', '⇔', '<=>'], ['←>', '↔', '<->'], ['-→', '⟶', '-->'], ['—>', '⟶', '-->']];

const esc = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/// Pure text helper (tests, and anything that wants the same mapping outside the editor): applies every rule to a string the
/// way typing it would.
export function replaceSymbols(text) {
  let out = text;
  for (const [from, to] of SYMBOL_RULES) out = out.split(from).join(to);
  return out;
}

const HINT_KEY = 'ipop.notes.symbolHints';
const HINT_TIMES = 5;
function hintsLeft() { try { return HINT_TIMES - (+localStorage.getItem(HINT_KEY) || 0); } catch { return 0; } }
function useHint() { try { localStorage.setItem(HINT_KEY, String((+localStorage.getItem(HINT_KEY) || 0) + 1)); } catch { /* blocked */ } }

let chip = null, chipTimer = 0;
/// "→ replaced automatically · Backspace to undo" next to the caret for 2 s, the first few times only.
export function showSymbolHint(view, typed, symbol) {
  if (typeof document === 'undefined' || hintsLeft() <= 0) return;
  useHint();
  chip?.remove(); clearTimeout(chipTimer);
  chip = document.createElement('div');
  chip.className = 'nt-sym-hint';
  chip.setAttribute('role', 'status');
  chip.innerHTML = `<b></b> replaced <code></code> · <kbd>Backspace</kbd> to undo`;
  chip.querySelector('b').textContent = symbol; chip.querySelector('code').textContent = typed;
  document.body.appendChild(chip);
  try {
    const c = view.coordsAtPos(view.state.selection.from);
    chip.style.top = `${c.bottom + 6}px`; chip.style.left = `${Math.max(8, Math.min(c.left - 12, innerWidth - chip.offsetWidth - 8))}px`;
  } catch { chip.style.bottom = '20px'; chip.style.left = '20px'; }
  const mine = chip;
  chipTimer = setTimeout(() => { mine.remove(); if (chip === mine) chip = null; }, 2200);
}
const hintKey = new PluginKey('symbolHint');

function inCode(state, pos) {
  const $p = state.doc.resolve(pos);
  if ($p.parent.type.spec.code) return true;
  return $p.marks().some(m => m.type.name === 'code');
}

function rule(find, symbol, typed, offset) {
  return new InputRule({
    find,
    handler: ({ state, range, match }) => {
      if (inCode(state, range.from)) return null;
      const start = range.from + offset(match);
      state.tr.insertText(symbol, start, range.to);
      state.tr.setMeta(hintKey, { typed, symbol });
    },
  });
}

export const Symbols = Extension.create({
  name: 'symbols',
  addInputRules() {
    const rules = CHAINS.map(([from, to, typed]) => rule(new RegExp(`${esc(from)}$`), to, typed, () => 0));
    for (const [from, to] of SYMBOL_RULES) {
      // "(c)", "(r)", "(v)", "(x)", "(p)" inside an "(a) (b) (c)" list stay letters
      if (/^\([a-z]\)$/.test(from)) {
        rules.push(new InputRule({ find: new RegExp(`${esc(from)}$`), handler: ({ state, range }) => {
          const before = state.doc.resolve(range.from).parent.textContent;
          if (inCode(state, range.from) || /\([a-z]\)/i.test(before)) return null;
          state.tr.insertText(to, range.from, range.to); state.tr.setMeta(hintKey, { typed: from, symbol: to });
        } }));
        continue;
      }
      rules.push(rule(new RegExp(`${esc(from)}$`), to, from, () => 0));
    }
    // "--" after a word ("a--" / "a --"): at the start of a line "---" must still become a divider.
    rules.push(rule(/[^\s-]\s?(--)$/, DASH_RULE[1], DASH_RULE[0], m => m[0].length - 2));
    for (const [from, to] of FRACTIONS) rules.push(rule(new RegExp(`(?:^|\\s)(${esc(from)})\\s$`), `${to} `, from, m => m[0].length - from.length - 1));
    rules.push(new InputRule({
      find: /(?:^|\s):([a-z0-9_]{1,12}):$/,
      handler: ({ state, range, match }) => {
        const sym = NAMED_SYMBOLS[match[1]];
        if (!sym || inCode(state, range.from)) return null;
        const start = range.from + match[0].indexOf(':');
        state.tr.insertText(sym, start, range.to);
      },
    }));
    return rules;
  },
  // Backspace right after a replacement already puts the typed text back (TipTap's core keymap runs undoInputRule).
  addProseMirrorPlugins() {
    const editor = this.editor;
    return [new Plugin({
      key: hintKey,
      appendTransaction: trs => {
        for (const tr of trs) {
          const m = tr.getMeta(hintKey);
          if (m) setTimeout(() => { if (!editor.isDestroyed) showSymbolHint(editor.view, m.typed, m.symbol); }, 0);
        }
        return null;
      },
    })];
  },
});

/// The full list for the /symbol panel and the "?" card: [{ typed, symbol, label }].
export function symbolCatalog() {
  return [
    ...SYMBOL_RULES.map(([typed, symbol, label]) => ({ typed, symbol, label })),
    { typed: DASH_RULE[0], symbol: DASH_RULE[1], label: DASH_RULE[2] },
    ...FRACTIONS.map(([typed, symbol, label]) => ({ typed: ` ${typed} `, symbol, label })),
    ...Object.entries(NAMED_SYMBOLS).map(([k, symbol]) => ({ typed: `:${k}:`, symbol, label: k.replace(/_/g, ' ') })),
  ];
}

const CATALOG_GROUPS = [
  ['Arrows', ['→', '←', '↔', '⇒', '⇔', '⟶', '↑', '↓']],
  ['Text', ['—', '…', '«', '»', '•', '·', '¶', '§']],
  ['Maths', ['≠', '≤', '≥', '±', '≈', '×', '÷', '°', '√', '∞', '∑', 'π', 'Δ', 'Ω', 'µ', '‰', '½', '¼', '¾', '℃']],
  ['Status', ['✓', '✗', '⚠', '❓', '★']],
  ['Legal & money', ['©', '®', '™', '€', '$', '£', '¥']],
];

/// "/symbol": every symbol in a grid with what to type for it. Click (or Enter) inserts it at the caret.
export function openSymbolPanel(editor) {
  if (typeof document === 'undefined') return;
  document.querySelectorAll('.nt-sym-panel').forEach(x => x.remove());
  const how = new Map();
  for (const c of symbolCatalog()) if (!how.has(c.symbol)) how.set(c.symbol, c);
  const pop = document.createElement('div');
  pop.className = 'nt-pop nt-sym-panel show';
  pop.setAttribute('role', 'dialog'); pop.setAttribute('aria-label', 'Symbols');
  const cell = s => { const c = how.get(s); const t = c ? `${c.label} · type ${c.typed.trim()}` : s;
    return `<button type="button" class="sy" data-s="${s}" title="${t.replace(/"/g, '&quot;')}"><span>${s}</span>${c ? `<kbd>${c.typed.trim().replace(/</g, '&lt;')}</kbd>` : ''}</button>`; };
  pop.innerHTML = `<div class="hd"><b>Symbols</b><span>Tip: type the code and it changes by itself · Backspace undoes</span></div>`
    + CATALOG_GROUPS.map(([g, list]) => `<div class="grp">${g}</div><div class="grid">${list.map(cell).join('')}</div>`).join('');
  document.body.appendChild(pop);
  try {
    const c = editor.view.coordsAtPos(editor.state.selection.from);
    const h = pop.offsetHeight, w = pop.offsetWidth;
    let top = c.bottom + 6; if (top + h > innerHeight - 10) top = Math.max(10, c.top - h - 6);
    pop.style.top = `${top}px`; pop.style.left = `${Math.max(8, Math.min(c.left, innerWidth - w - 8))}px`;
  } catch { /* stays where CSS puts it */ }
  const close = () => { pop.remove(); document.removeEventListener('mousedown', outside, true); document.removeEventListener('keydown', key, true); };
  const outside = e => { if (!pop.contains(e.target)) close(); };
  const key = e => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); editor.commands.focus(); } };
  pop.addEventListener('mousedown', e => e.preventDefault());
  pop.addEventListener('click', e => {
    const b = e.target.closest('[data-s]'); if (!b) return;
    editor.chain().focus().insertContent(b.dataset.s).run();
    if (!e.shiftKey) close();   // Shift-click keeps the panel open to add several
  });
  setTimeout(() => { document.addEventListener('mousedown', outside, true); document.addEventListener('keydown', key, true); }, 0);
  return close;
}
