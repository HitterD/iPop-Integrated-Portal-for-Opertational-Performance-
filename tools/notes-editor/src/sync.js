/// A PushUpdate refusal arrives as a HubException whose message carries "note_refused:<reason>" (see NotesHub).
/// Returns the reason, or null for transient failures (network, reconnect) that are worth retrying.
export function refusedReason(err) {
  const m = /note_refused:([a-z_]+)/.exec(String(err?.message ?? err ?? ''));
  return m ? m[1] : null;
}

/// Groups the people mentioned in `doc` by the paragraph that mentions them: Map(context text -> [userId]). Only ids in
/// `wanted` that are still present in the document appear (a mention deleted before the batch went out is dropped).
/// A mention or assignee counts for the textblock it sits in; the first place a person is mentioned wins.
export function mentionContexts(doc, wanted) {
  const byContext = new Map();
  const seen = new Set();
  const add = (id, block) => {
    if (!id || !wanted.has(id) || seen.has(id)) return;
    seen.add(id);
    const context = block.textContent.slice(0, 200);
    if (!byContext.has(context)) byContext.set(context, []);
    byContext.get(context).push(id);
  };
  doc.descendants(node => {
    if (node.type.name === 'taskItem') add(node.attrs.assigneeId, node);
    else if (node.isTextblock) node.descendants(n => { if (n.type.name === 'mention') add(n.attrs.id, node); });
    return true;
  });
  return byContext;
}

/// Edits that were typed but never reached the server (offline, tab closed) are parked in localStorage and replayed the
/// next time the page opens. Yjs updates are idempotent, so replaying one the server already has is harmless.
const pendingKey = pageId => `ipop.notes.pending.${pageId}`;
export function savePending(store, pageId, b64Update) {
  try { store.setItem(pendingKey(pageId), b64Update); return true; } catch { return false; }
}
export function takePending(store, pageId) {
  try { const v = store.getItem(pendingKey(pageId)); if (v) store.removeItem(pendingKey(pageId)); return v || null; } catch { return null; }
}
export function clearPending(store, pageId) {
  try { store.removeItem(pendingKey(pageId)); } catch { /* storage unavailable */ }
}
