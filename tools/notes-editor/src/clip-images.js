// Pictures in and out of the clipboard.
//  - Copy one picture (Ctrl C on a selected picture, or the Copy button): the clipboard gets the picture itself (PNG) for
//    Chat, Mail, Word and WhatsApp, plus the note's own block so pasting into another note reuses the same file.
//  - Paste a picture copied from a web page (<img src="https://…">): the server fetches it and keeps a copy on the NAS
//    (POST /uploads/from-url). If the site refuses, the picture stays as an "External" picture shown from that site.
//  - Paste a data: picture (some apps put the bytes in the HTML): uploaded like a file.
import { NodeSelection } from '@tiptap/pm/state';

/// <img> sources we accept on paste; everything else (blob:, file:, javascript:) is dropped by the schema.
export const isWebImageSrc = s => /^https?:\/\//i.test(s || '');
export const isDataImageSrc = s => /^data:image\/(png|jpe?g|gif|webp);base64,/i.test(s || '');

/// Positions of pictures in the document that still need importing (src set, not yet saved here), for the given srcs.
export function pendingImages(doc, srcs) {
  const out = [];
  doc.descendants((n, pos) => {
    if (n.type.name === 'noteImage' && !n.attrs.id && n.attrs.src && (!srcs || srcs.has(n.attrs.src))) out.push({ pos, src: n.attrs.src });
  });
  return out;
}

/// Clipboard HTML that our own editor reads back as the same block (data-id) and other apps read as a picture.
export function figureHtml(attrs, dataUrl) {
  const q = s => String(s ?? '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
  return `<figure data-image="" data-id="${q(attrs.id)}" data-name="${q(attrs.name)}" data-size="${q(attrs.size)}" data-width="${q(attrs.width)}" data-align="${q(attrs.align)}" data-caption="${q(attrs.caption)}"><img src="${q(dataUrl)}" alt="${q(attrs.caption || attrs.name)}"></figure>`;
}

async function toPng(blob) {
  if (blob.type === 'image/png') return blob;
  const bmp = await createImageBitmap(blob);
  const c = document.createElement('canvas'); c.width = bmp.width; c.height = bmp.height;
  c.getContext('2d').drawImage(bmp, 0, 0);
  return await new Promise(ok => c.toBlob(ok, 'image/png'));
}
const asDataUrl = blob => new Promise((ok, no) => { const r = new FileReader(); r.onload = () => ok(r.result); r.onerror = no; r.readAsDataURL(blob); });

/// Puts one picture on the clipboard. `loadUrl` gives an object URL (or web URL) for the picture. Must be called while the
/// click / key press is being handled (browsers only allow clipboard writes then); the bytes are filled in afterwards.
export function copyPicture(attrs, loadUrl) {
  if (!navigator.clipboard?.write || typeof ClipboardItem === 'undefined') return Promise.resolve(false);
  const blobP = (async () => { const u = await loadUrl(); const r = await fetch(u); if (!r.ok) throw new Error(String(r.status)); return r.blob(); })();
  const pngP = blobP.then(toPng);
  const htmlP = pngP.then(asDataUrl).then(d => new Blob([figureHtml(attrs, d)], { type: 'text/html' }));
  const textP = Promise.resolve(new Blob([attrs.caption || attrs.name || 'Picture'], { type: 'text/plain' }));
  try {
    return navigator.clipboard.write([new ClipboardItem({ 'image/png': pngP, 'text/html': htmlP, 'text/plain': textP })]).then(() => true, () => false);
  } catch { return Promise.resolve(false); }
}

/// Wires picture copy/paste into a live editor. host: { fetch, upload(file) -> {id,fileName,size}, importUrl(url) -> dto|null,
/// loadBlob(id), toast }. Returns the editorProps to merge and a destroy().
export function pictureClipboard(getEditor, host) {
  const queued = new Set();
  let timer = 0;
  const run = async () => {
    const editor = getEditor();
    if (!editor || editor.isDestroyed || !editor.isEditable) return;
    const srcs = new Set(queued); queued.clear();
    const seen = new Map();
    for (const { src } of pendingImages(editor.state.doc, srcs)) {
      if (seen.has(src)) continue;
      let dto = null;
      try {
        if (isDataImageSrc(src)) {
          const b = await (await fetch(src)).blob();
          dto = await host.upload(new File([b], `pasted-image.${(b.type.split('/')[1] || 'png').replace('jpeg', 'jpg')}`, { type: b.type }));
        } else if (isWebImageSrc(src)) dto = await host.importUrl(src);
      } catch { dto = null; }
      seen.set(src, dto);
    }
    const ed = getEditor();
    if (!ed || ed.isDestroyed) return;
    let tr = ed.state.tr, kept = 0, saved = 0;
    for (const { pos, src } of pendingImages(ed.state.doc, srcs)) {
      const dto = seen.get(src);
      const n = tr.doc.nodeAt(pos);
      if (!n) continue;
      if (dto?.id) { tr = tr.setNodeMarkup(pos, undefined, { ...n.attrs, id: dto.id, src: null, name: n.attrs.name || dto.fileName, size: dto.size || 0 }); saved++; }
      else if (isDataImageSrc(src)) tr = tr.delete(pos, pos + n.nodeSize);   // bytes we couldn't store: don't keep megabytes in the page
      else kept++;
    }
    if (tr.docChanged) ed.view.dispatch(tr.setMeta('addToHistory', false));
    if (kept) host.toast(kept === 1 ? "That picture couldn't be copied here, so it is shown from the website (marked External)" : `${kept} pictures are shown from their website (marked External)`);
    else if (saved > 1) host.toast(`Saved ${saved} pasted pictures`);
  };
  const editorProps = {
    // remember which pasted pictures need saving here; the import runs right after the paste lands
    transformPasted(slice) {
      slice.content.descendants(n => { if (n.type.name === 'noteImage' && !n.attrs.id && n.attrs.src) queued.add(n.attrs.src); });
      if (queued.size) { clearTimeout(timer); timer = setTimeout(run, 0); }
      return slice;
    },
    handleDOMEvents: {
      // Ctrl C on one selected picture: the picture itself for other apps (our own block rides along in the HTML)
      copy(view, event) {
        const sel = view.state.selection;
        if (!(sel instanceof NodeSelection) || sel.node.type.name !== 'noteImage') return false;
        const a = sel.node.attrs;
        if (!a.id && !a.src) return false;
        const ok = copyPicture(a, () => (a.id ? host.loadBlob(a.id) : Promise.resolve(a.src)));
        // the async write replaces what the browser copies; until it lands the normal copy is there
        ok.then(done => { if (!done) host.toast("Copied inside Notes. To paste the picture elsewhere, right-click it and choose Copy image"); });
        return false;
      },
    },
  };
  return { editorProps, copy: node => copyPicture(node.attrs, () => (node.attrs.id ? host.loadBlob(node.attrs.id) : Promise.resolve(node.attrs.src))), destroy: () => clearTimeout(timer) };
}
