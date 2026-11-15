import test from 'node:test';
import assert from 'node:assert/strict';
import { refusedReason } from '../src/sync.js';

// SignalR wraps a server HubException as "An unexpected error occurred invoking 'PushUpdate' on the server. HubException: note_refused:forbidden".
test('server refusals are final and carry their reason', () => {
  assert.equal(refusedReason(new Error("An unexpected error occurred invoking 'PushUpdate' on the server. HubException: note_refused:forbidden")), 'forbidden');
  assert.equal(refusedReason(new Error('HubException: note_refused:not_found')), 'not_found');
  assert.equal(refusedReason(new Error('note_refused:invalid_update')), 'invalid_update');
});

test('network failures are not refusals and keep retrying', () => {
  assert.equal(refusedReason(new Error('Invocation canceled due to the underlying connection being closed.')), null);
  assert.equal(refusedReason(new Error('refused')), null);
  assert.equal(refusedReason(undefined), null);
});

import { mentionContexts } from '../src/sync.js';

// Minimal stand-in for a ProseMirror doc: descendants(cb) walks nodes depth-first.
const mk = (type, attrs = {}, kids = [], text = '') => {
  const n = { type: { name: type }, attrs, content: kids, isTextblock: type === 'paragraph', _text: text };
  n.textContent = text + kids.map(k => k.textContent).join('');
  n.descendants = cb => { for (const k of kids) { if (cb(k) !== false) k.descendants(cb); } };
  return n;
};

test('a mention is reported with its own paragraph, not the cursor paragraph', () => {
  const doc = mk('doc', {}, [
    mk('paragraph', {}, [mk('text', {}, [], 'Hi '), mk('mention', { id: 'u1' }, [], '@Yudi')], ''),
    mk('paragraph', {}, [mk('text', {}, [], 'Unrelated later paragraph')]),
    mk('paragraph', {}, [mk('mention', { id: 'u2' }, [], '@Ana')]),
  ]);
  const r = mentionContexts(doc, new Set(['u1', 'u2', 'gone']));
  assert.deepEqual([...r.values()].flat().sort(), ['u1', 'u2']);   // 'gone' (deleted) is dropped
  assert.equal(r.size, 2);
  assert.ok([...r.keys()].some(k => k.startsWith('Hi ')));
});
