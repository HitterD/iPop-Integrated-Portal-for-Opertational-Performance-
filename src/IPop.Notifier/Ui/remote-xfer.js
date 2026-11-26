// Streaming file transfer for remote help (BIGIS #8 v3). Used by the ICT viewer and the user's hidden host page.
//   send:    a File (picked or dropped in the page) or a URL the app serves from disk (http://files.notifier.ipop/<token>)
//            → LiveKit byte stream, chunk by chunk; never the whole file in memory.
//   receive: a LiveKit byte stream → small batches posted to the app, which appends them to a ".part" file on disk.
// Both sides announce cancel / done / fail on the "xfer" data topic so each row shows the same state.
(function () {
  const R = window.remote, { post } = window.app;
  const CHUNK = 64 * 1024, BATCH = 256 * 1024;
  const out = new Map();   // id → { cancelled }
  const inc = new Map();   // id → { cancelled, failed }
  const say = (id, op, msg) => R.room && R.sendReliable({ t: 'xfer', id, op, msg }, 'xfer');

  /// src: File | { url, name, size }. onProgress(sent, size). Resolves 'done' | 'cancel'; throws on failure.
  async function send(src, { id, topic = 'file', onProgress }) {
    const name = src.name, size = src.size;
    const st = { cancelled: false }; out.set(id, st);
    const writer = await R.room.localParticipant.streamBytes({ streamId: id, name, totalSize: size, mimeType: src.type || 'application/octet-stream', topic });
    let sent = 0;
    try {
      if (src instanceof Blob) {
        for (let off = 0; off < size && !st.cancelled; off += CHUNK) {
          const buf = new Uint8Array(await src.slice(off, off + CHUNK).arrayBuffer());
          await writer.write(buf); sent += buf.length; onProgress && onProgress(sent, size);
        }
      } else {
        const res = await fetch(src.url);
        if (!res.ok || !res.body) throw new Error('The file could not be read');
        const reader = res.body.getReader();
        for (;;) {
          if (st.cancelled) { try { await reader.cancel(); } catch { } break; }
          const { done, value } = await reader.read();
          if (done) break;
          for (let o = 0; o < value.length; o += CHUNK) { const part = value.subarray(o, o + CHUNK); await writer.write(part); sent += part.length; }
          onProgress && onProgress(sent, size);
        }
      }
    } finally {
      try { await writer.close(); } catch { }
      out.delete(id);
    }
    if (st.cancelled) { say(id, 'cancel'); return 'cancel'; }
    return 'done';
  }

  function cancelSend(id) { const s = out.get(id); if (s) s.cancelled = true; }
  function cancelReceive(id) { const s = inc.get(id); if (s) s.cancelled = true; say(id, 'cancel'); post({ type: 'xferEnd', id, ok: false, reason: 'cancel' }); }

  /// Reads a byte stream into the app in batches. max: bytes allowed. Posts xferBegin / xferChunk / xferEnd.
  async function receive(reader, { max, topic, from }) {
    const info = reader.info, id = info.id, size = info.size || 0;
    const st = { cancelled: false, failed: null }; inc.set(id, st);
    if (max && size > max) {
      st.failed = 'big';
      post({ type: 'xferRefused', id, topic, name: info.name, size, reason: 'big' });
      say(id, 'fail', 'big');
      for await (const _ of reader) { /* drain without keeping anything */ }
      inc.delete(id); return;
    }
    post({ type: 'xferBegin', id, topic, name: info.name, size, mime: info.mimeType, from });
    let n = 0, buf = [], len = 0;
    const flush = () => { if (!len) return; const all = new Uint8Array(len); let o = 0; for (const b of buf) { all.set(b, o); o += b.length; } post({ type: 'xferChunk', id, data: R.b64(all), n }); buf = []; len = 0; };
    try {
      for await (const c of reader) {
        if (st.cancelled || st.failed) continue; // keep draining so LiveKit doesn't stall, but write nothing
        n += c.length;
        if (max && n > max) { st.failed = 'big'; continue; }
        buf.push(c); len += c.length;
        if (len >= BATCH) flush();
      }
      flush();
    } catch (e) { st.failed = st.failed || 'interrupted'; }
    inc.delete(id);
    if (st.cancelled) return;
    const ok = !st.failed && (!size || n === size);
    post({ type: 'xferEnd', id, ok, reason: st.failed || (ok ? '' : 'interrupted'), n });
    say(id, ok ? 'done' : 'fail', ok ? '' : (st.failed || 'interrupted'));
  }

  // The other side cancelled: stop whichever direction this id is.
  function onPeer(m) {
    if (m.op === 'cancel') { const s = out.get(m.id); if (s) s.cancelled = true; const r = inc.get(m.id); if (r) { r.cancelled = true; post({ type: 'xferEnd', id: m.id, ok: false, reason: 'cancel' }); } }
  }

  window.xfer = { send, cancelSend, cancelReceive, receive, onPeer, newId: () => 'x' + Date.now().toString(36) + Math.random().toString(36).slice(2, 8) };
})();
