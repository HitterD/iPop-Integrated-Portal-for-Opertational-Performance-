// Store CSS-pixel width so the same pasted screenshot has the same size for every collaborator.
export function originalImageWidth(pixelWidth, pixelRatio = 1) {
  if (!Number.isFinite(pixelWidth) || pixelWidth <= 0) return null;
  const ratio = Number.isFinite(pixelRatio) && pixelRatio > 0 ? pixelRatio : 1;
  return Math.max(1, Math.round(pixelWidth / ratio));
}

export async function pastedImageWidth(file, pixelRatio = globalThis.devicePixelRatio || 1) {
  try {
    const bitmap = await createImageBitmap(file);
    try { return originalImageWidth(bitmap.width, pixelRatio); }
    finally { bitmap.close(); }
  } catch { return null; }
}
