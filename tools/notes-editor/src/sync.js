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
