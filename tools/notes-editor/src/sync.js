/// A PushUpdate refusal arrives as a HubException whose message carries "note_refused:<reason>" (see NotesHub).
/// Returns the reason, or null for transient failures (network, reconnect) that are worth retrying.
export function refusedReason(err) {
  const m = /note_refused:([a-z_]+)/.exec(String(err?.message ?? err ?? ''));
  return m ? m[1] : null;
}
