// Starter content for new pages (ProseMirror JSON). The server hands each seeded page its template
// exactly once (POST /seed); the first editor to claim it writes this into the shared Yjs doc.

const txt = (text, ...marks) => ({ type: 'text', text, ...(marks.length ? { marks: marks.map(m => typeof m === 'string' ? { type: m } : m) } : {}) });
const b = s => txt(s, 'bold');
const i = s => txt(s, 'italic');
const code = s => txt(s, 'code');
const color = (s, c) => txt(s, { type: 'textStyle', attrs: { color: `var(--nt-${c})` } });
const hl = (s, c) => txt(s, { type: 'textStyle', attrs: { backgroundColor: `var(--nb-${c})` } });
const inl = parts => parts.filter(x => x !== '').map(x => typeof x === 'string' ? txt(x) : x);

export const p = (...parts) => ({ type: 'paragraph', ...(parts.length ? { content: inl(parts) } : {}) });
export const h = (level, ...parts) => ({ type: 'heading', attrs: { level }, content: inl(parts) });
const todos = items => ({ type: 'taskList', content: items.map(it => ({ type: 'taskItem', attrs: { checked: false }, content: [p(...[].concat(it))] })) });
const bullets = items => ({ type: 'bulletList', content: items.map(it => ({ type: 'listItem', content: [p(...[].concat(it))] })) });
const numbers = items => ({ type: 'orderedList', attrs: { start: 1 }, content: items.map(it => ({ type: 'listItem', content: [p(...[].concat(it))] })) });
const callout = (emoji, colorKey, ...paras) => ({ type: 'callout', attrs: { emoji, color: colorKey }, content: paras });
const toggle = (summary, ...content) => ({ type: 'details', content: [
  { type: 'detailsSummary', content: inl([].concat(summary)) },
  { type: 'detailsContent', content },
] });
const widget = kind => ({ type: 'noteWidget', attrs: { kind } });
const hr = { type: 'horizontalRule' };
const quote = (...parts) => ({ type: 'blockquote', content: [p(...parts)] });
const doc = (...content) => ({ type: 'doc', content });

/// The phrase the tutorial's example comment is anchored to.
export const GUIDE_COMMENT_TEXT = 'click this yellow text to see the comment';

const guide = () => doc(
  callout('👋', 'orange', p('Welcome to ', b('Notes'), ' — the place for notes, SOPs, meeting minutes, and anything else. This page is ',
    b('a hands-on example you can try right away'), ': tick each step once you have tried it. Don\'t worry, nothing can break — this page is yours alone.')),
  widget('progress'),

  h(2, '1️⃣ Writing'),
  todos([
    ['Click anywhere and start typing. ', b('Saved automatically'), ' — there is no Save button.'],
    ['Click the 👋 emoji above the title to change the page icon.'],
    ['Type ', code('/'), ' on an empty line to pick a block type: heading, list, to-do, image, code, and more.'],
  ]),
  p(i('Try it here → ')),
  toggle(['Faster with Markdown shortcuts ', color('— click the arrow to open', 'gray')],
    bullets([
      [code('# '), ' large heading · ', code('## '), ' medium heading · ', code('### '), ' small heading'],
      [code('- '), ' bulleted list · ', code('1. '), ' numbered list · ', code('[ ] '), ' to-do'],
      [code('> '), ' quote · ', code('```'), ' code block · ', code('---'), ' divider'],
      [code('@'), ' mention a colleague — they get a notification'],
    ])),

  h(2, '2️⃣ Organizing pages'),
  todos([
    ['Hover over a block — a ⠿ appears on its left. ', b('Drag'), ' to move it, ', b('click'), ' to change its type or background colour, or delete it. Try it on this line!'],
    ['Create a page inside a page — type ', code('/page'), ' or click ', b('+'), ' next to the page name in the sidebar.'],
  ]),

  h(2, '3️⃣ Colours & style'),
  p('Every page has its own ', b('accent colour, cover, and font'), ' — it doesn\'t all have to be green 🙂 The accent colour is used for checklists, mentions, quotes, and numbering.'),
  todos([['Change this page\'s accent colour — click one:']]),
  widget('accents'),
  todos([
    ['Click ', b('Style'), ' in the top right to change the cover, font (Default · Serif · Mono), and page width.'],
    [b('Select this text'), ', then click ', b('A'), ' in the toolbar that appears to add ', color('text colour', 'red'), ' or a ', hl('highlight', 'yellow'), '.'],
  ]),
  p(color('Coloured callouts flag important things — change the colour via ⠿:', 'gray')),
  callout('💡', 'blue', p(b('Tip'), ' — helpful info')),
  callout('⚠️', 'yellow', p(b('Caution'), ' — check before continuing')),
  callout('⛔', 'red', p(b('Important'), ' — don\'t skip this')),
  callout('✅', 'green', p(b('Done'), ' — already handled')),

  h(2, '4️⃣ Sharing & editing together'),
  p('Notes can be sent to ', b('direct or group chats'), '. Everyone in the chat can open them right away — and with ', i('Can edit'), ' permission, you can write ', b('together in real time'), '.'),
  numbers([
    ['Click ', b('Share'), ' in the top right.'],
    ['Pick the ', b('Send to chat'), ' tab, then choose a DM or group.'],
    ['Set the permission: ', b('View'), ', ', b('Comment'), ', or ', b('Edit'), '.'],
    ['Click ', b('Send'), ' — the note shows up as a card in the chat.'],
  ]),
  widget('perms'),
  callout('👥', 'green', p(b('Access follows chat membership.'), ' New group members can open the note automatically; people who leave the group lose access. Sub-pages are shared too.')),
  p('When others are editing, you see their cursor and name live. Their avatars also appear in the top right.'),
  todos([['Send a note to a chat, then open it again from its card in the chat.']]),

  h(2, '5️⃣ Comments & mentions'),
  todos([
    ['Select text and click ', b('Comment'), ' (or ', code('Ctrl Shift M'), '). Example: ', GUIDE_COMMENT_TEXT, '.'],
    ['Type ', code('@'), ' to mention a colleague — they will get a notification in iPop.'],
  ]),

  h(2, '6️⃣ Images, files & history'),
  todos([
    ['Drag or paste images/files straight into the page — stored safely on the office NAS.'],
    ['Deleted something by mistake? Open ', b('⋯ → Version history'), ' to view and ', b('restore'), ' older versions.'],
  ]),
  hr,
  toggle('⌨️ All keyboard shortcuts', bullets([
    [code('Ctrl K'), ' search notes · ', code('Ctrl B'), ' bold · ', code('Ctrl I'), ' italic · ', code('Ctrl U'), ' underline'],
    [code('Ctrl Shift M'), ' comment · ', code('Ctrl Z'), ' undo · ', code('Tab'), ' indent'],
  ])),
  callout('🙋', 'gray', p('Still unsure? Ask the ICT team via chat.')),
);

const meeting = () => {
  const today = new Date().toLocaleDateString('en-US', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
  return doc(
    callout('📅', 'blue', p(b('Date: '), today), p(b('Attendees: '), '@')),
    h(2, 'Agenda'), numbers([[''], [''], ['']]),
    h(2, 'Notes'), bullets([['']]),
    h(2, 'Decisions'), callout('✅', 'green', p('')),
    h(2, 'Action items'), todos([[''], ['']]),
  );
};

const todo = () => {
  const day = d => new Date(Date.now() + d * 864e5).toLocaleDateString('en-US', { weekday: 'long', day: 'numeric', month: 'short' });
  return doc(
    h(2, `Today · ${day(0)}`), todos([[''], ['']]),
    h(2, `Tomorrow · ${day(1)}`), todos([['']]),
    h(2, 'Later'), todos([['']]),
  );
};

const brief = () => doc(
  callout('🎯', 'purple', p(b('Goal: '), 'what we want to achieve and why it matters.')),
  h(2, 'Scope'), bullets([['In scope: '], ['Out of scope: ']]),
  h(2, 'Timeline'), todos([['Week 1 — '], ['Week 2 — '], ['Week 3 — ']]),
  h(2, 'Owners'), bullets([['Owner: @'], ['Team: ']]),
  h(2, 'Risks & open questions'), bullets([['']]),
);

const sop = () => doc(
  callout('📖', 'orange', p(b('SOP purpose: '), 'briefly explain what this procedure is for.')),
  h(2, 'Before you start'), todos([['Prepare '], ['Make sure ']]),
  h(2, 'Steps'), numbers([[''], [''], ['']]),
  callout('⚠️', 'yellow', p(b('Caution: '), 'things that are often missed.')),
  h(2, 'If something goes wrong'), bullets([['Contact ']]),
);

// Plain-text table: first row is the header.
const cell = (type, text) => ({ type, content: [text ? p(text) : p()] });
const table = (head, rows) => ({ type: 'table', content: [
  { type: 'tableRow', content: head.map(t => cell('tableHeader', t)) },
  ...rows.map(r => ({ type: 'tableRow', content: r.map(t => cell('tableCell', t)) })),
] });

const inspection = () => doc(
  callout('🔍', 'teal', p(b('Tip: '), 'tick each point on site and add a photo to it with ⋯ → Add photo. Assign an owner and a due date to anything that needs fixing.')),
  h(2, 'Details'), bullets([[b('Area: ')], [b('Inspector: ')], [b('Date: ')]]),
  h(2, 'Checklist'), todos([['Floor and walkways clear'], ['Fire extinguisher in place and checked'], ['Emergency exits free'], ['Lighting working'], ['Cleanliness OK']]),
  h(2, 'Findings'), table(['#', 'Finding', 'Owner', 'Due'], [['1', '', '', ''], ['2', '', '', '']]),
  h(2, 'Notes'), p(),
);
const weekly = () => doc(
  callout('📊', 'blue', p(b('Week: '), 'fill in the dates, then keep each section short.')),
  h(2, 'Highlights'), bullets([[''], ['']]),
  h(2, 'Numbers'), table(['KPI', 'Target', 'Actual', 'Note'], [['', '', '', ''], ['', '', '', ''], ['', '', '', '']]),
  h(2, 'Issues and blockers'), bullets([['']]),
  h(2, 'Next week'), todos([[''], ['']]),
);
const incident = () => doc(
  callout('🧯', 'red', p(b('Report as soon as possible. '), 'Write facts first; the cause can be filled in later.')),
  h(2, 'What happened'), bullets([[b('When: ')], [b('Where: ')], [b('Who was involved: ')], [b('What happened: ')]]),
  h(2, 'Photos'), p('Drop photos here.'),
  h(2, 'Cause'), p(),
  h(2, 'Corrective actions'), todos([[''], ['']]),
  callout('⚠️', 'yellow', p(b('Prevent it happening again: '), 'what will change?')),
);

export const TEMPLATES = { guide, meeting, todo, brief, sop, inspection, weekly, incident };
