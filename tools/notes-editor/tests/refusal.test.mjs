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
