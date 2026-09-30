// Profile photo: centre-crop the picked file to a square, scale to 256px (plus an optional larger
// copy for the viewer) and re-encode as WebP
// (JPEG where the browser cannot encode WebP). The server only checks headers, so all pixel
// work happens here. Returns { base64, type, preview } or { error }.
window.ipopProfilePhoto = {
  open(input) { input?.click(); },
  async crop(input, size, largeSize) {
    const file = input?.files?.[0];
    if (!file) return null;
    input.value = "";
    if (!/^image\/(jpeg|png|webp)$/.test(file.type)) return { error: "Use a JPG, PNG or WebP image." };
    if (file.size > 5 * 1024 * 1024) return { error: "The photo must be 5 MB or smaller." };

    let bmp;
    try { bmp = await createImageBitmap(file); }
    catch { return { error: "This image could not be read." }; }
    if (bmp.width < 64 || bmp.height < 64) return { error: "The photo must be at least 64 × 64 pixels." };

    const side = Math.min(bmp.width, bmp.height);
    const small = this._encode(bmp, side, Math.min(size, side));
    // A bigger copy for the "view photo" viewer (the avatar itself stays small).
    const large = largeSize ? this._encode(bmp, side, Math.min(largeSize, side)) : null;
    bmp.close?.();
    return {
      base64: small.base64, type: small.type, preview: small.url,
      largeBase64: large?.base64 ?? null, largeType: large?.type ?? null,
    };
  },
  _encode(bmp, side, out) {
    const canvas = document.createElement("canvas");
    canvas.width = canvas.height = out;
    const ctx = canvas.getContext("2d");
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(bmp, (bmp.width - side) / 2, (bmp.height - side) / 2, side, side, 0, 0, out, out);
    let url = canvas.toDataURL("image/webp", 0.86);
    if (!url.startsWith("data:image/webp")) url = canvas.toDataURL("image/jpeg", 0.88);
    return { url, type: url.slice(5, url.indexOf(";")), base64: url.slice(url.indexOf(",") + 1) };
  },
};
