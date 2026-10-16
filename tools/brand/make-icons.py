"""Generate PWA / TWA icons and iOS splash screens from the iPop brand assets.

Run from the repo root:  python -I tools/brand/make-icons.py
Inputs  (src/IPop.Web/wwwroot/img/brand): icon-512.png (the mark), batik-kopi-oval.svg is
         approximated in code, ipop-word-dark.webp, kapal-api-dark.webp.
Outputs (same folder): icon-maskable-192/512.png, shortcut-*.png and splash-<w>x<h>.png,
         plus android/twa/splash-512.png.
"""
import math
import os
import sys

from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BRAND = os.path.join(ROOT, "src", "IPop.Web", "wwwroot", "img", "brand")
FOREST = (0x12, 0x35, 0x28)
FOREST_DARK = (0x0D, 0x2A, 0x1F)
FOREST_LIGHT = (0x1A, 0x4A, 0x37)
BATIK = (0xE3, 0xAD, 0x3C)


def mark() -> Image.Image:
    im = Image.open(os.path.join(BRAND, "icon-512.png")).convert("RGBA")
    return im.crop(im.getbbox())


def maskable(size: int) -> Image.Image:
    """Mark on white; the mark fills the central 60 % so every launcher mask (circle 80 %) keeps it whole."""
    out = Image.new("RGBA", (size, size), (255, 255, 255, 255))
    m = mark()
    side = int(size * 0.60)
    scale = side / max(m.size)
    m = m.resize((max(1, int(m.width * scale)), max(1, int(m.height * scale))), Image.LANCZOS)
    out.alpha_composite(m, ((size - m.width) // 2, (size - m.height) // 2))
    return out.convert("RGB")


def shortcut(color, glyph: str, size: int = 96) -> Image.Image:
    """Simple module tile for manifest shortcuts (rounded square + white glyph)."""
    s = size * 4
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((0, 0, s - 1, s - 1), radius=s // 4, fill=color)
    w = max(4, s // 22)
    c = s // 2
    if glyph == "chat":
        d.ellipse((s * .22, s * .24, s * .78, s * .70), outline="white", width=w)
        d.polygon([(s * .30, s * .62), (s * .26, s * .80), (s * .44, s * .68)], fill="white")
    elif glyph == "mail":
        d.rounded_rectangle((s * .20, s * .28, s * .80, s * .72), radius=s // 24, outline="white", width=w)
        d.line([(s * .22, s * .32), (c, s * .54), (s * .78, s * .32)], fill="white", width=w, joint="curve")
    else:  # calendar
        d.rounded_rectangle((s * .22, s * .26, s * .78, s * .78), radius=s // 20, outline="white", width=w)
        d.line([(s * .22, s * .42), (s * .78, s * .42)], fill="white", width=w)
        for x in (s * .36, s * .64):
            d.line([(x, s * .18), (x, s * .32)], fill="white", width=w)
    return im.resize((size, size), Image.LANCZOS)


def batik(img: Image.Image, opacity: float = 0.05, tile: int = 180):
    """Faint batik kopi pattern (rotated ovals), as css/v6 and the mockup splash use."""
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    a = int(255 * opacity)
    w = max(2, tile // 40)
    rx, ry = tile * 13 / 60, tile * 6.2 / 60
    for y0 in range(0, img.height + tile, tile):
        for x0 in range(0, img.width + tile, tile):
            for cx, cy, ang in ((.25, .25, 45), (.75, .25, -45), (.25, .75, -45), (.75, .75, 45)):
                pts = []
                for k in range(48):
                    t = 2 * math.pi * k / 48
                    x, y = rx * math.cos(t), ry * math.sin(t)
                    r = math.radians(ang)
                    pts.append((x0 + cx * tile + x * math.cos(r) - y * math.sin(r),
                                y0 + cy * tile + x * math.sin(r) + y * math.cos(r)))
                d.line(pts + [pts[0]], fill=BATIK + (a,), width=w)
    img.alpha_composite(layer)


def radial(w: int, h: int) -> Image.Image:
    """radial-gradient(120% 70% at 50% 45%, #1a4a37 0%, #123528 55%, #0d2a1f 100%)."""
    small = Image.new("RGB", (120, int(120 * h / w)))
    px = small.load()
    sw, sh = small.size
    for y in range(sh):
        for x in range(sw):
            dx = (x - sw * .5) / (sw * 1.2 / 2)
            dy = (y - sh * .45) / (sh * .7 / 2)
            t = min(1.0, math.hypot(dx, dy))
            if t < .55:
                f = t / .55
                c = tuple(int(FOREST_LIGHT[i] + (FOREST[i] - FOREST_LIGHT[i]) * f) for i in range(3))
            else:
                f = (t - .55) / .45
                c = tuple(int(FOREST[i] + (FOREST_DARK[i] - FOREST[i]) * f) for i in range(3))
            px[x, y] = c
    return small.resize((w, h), Image.BICUBIC).convert("RGBA")


def splash(w: int, h: int, scale: float = 3.0) -> Image.Image:
    img = radial(w, h)
    batik(img, tile=int(60 * scale))
    # Mark on a white rounded tile (132 css px), wordmark below, Kapal Api Group at the bottom.
    tile = int(132 * scale)
    t = Image.new("RGBA", (tile, tile), (0, 0, 0, 0))
    ImageDraw.Draw(t).rounded_rectangle((0, 0, tile - 1, tile - 1), radius=int(36 * scale), fill=(255, 255, 255, 245))
    m = mark()
    side = int(104 * scale)
    k = side / max(m.size)
    m = m.resize((int(m.width * k), int(m.height * k)), Image.LANCZOS)
    t.alpha_composite(m, ((tile - m.width) // 2, (tile - m.height) // 2))
    word = Image.open(os.path.join(BRAND, "ipop-word-dark.webp")).convert("RGBA")
    wh = int(44 * scale)
    word = word.resize((int(word.width * wh / word.height), wh), Image.LANCZOS)
    gap = int(16 * scale)
    block = tile + gap + word.height
    top = (h - block) // 2 - int(20 * scale)
    img.alpha_composite(t, ((w - tile) // 2, top))
    img.alpha_composite(word, ((w - word.width) // 2, top + tile + gap))
    logo = Image.open(os.path.join(BRAND, "kapal-api-dark.webp")).convert("RGBA")
    lh = int(48 * scale)
    logo = logo.resize((int(logo.width * lh / logo.height), lh), Image.LANCZOS)
    img.alpha_composite(logo, ((w - logo.width) // 2, h - int(56 * scale) - lh))
    return img.convert("RGB")


def main():
    for size in (192, 512):
        maskable(size).save(os.path.join(BRAND, f"icon-maskable-{size}.png"), optimize=True)
    for name, color, glyph in (("chat", (0x2E, 0x9E, 0x5B), "chat"), ("mail", (0x0A, 0x84, 0xFF), "mail"),
                               ("calendar", (0xFF, 0x9F, 0x0A), "cal")):
        shortcut(color, glyph).save(os.path.join(BRAND, f"shortcut-{name}.png"), optimize=True)
    for w, h in ((1170, 2532), (1290, 2796), (1080, 2340)):
        splash(w, h).save(os.path.join(BRAND, f"splash-{w}x{h}.png"), optimize=True)
    twa = os.path.join(ROOT, "android", "twa")
    os.makedirs(twa, exist_ok=True)
    s = radial(512, 512)
    m = maskable(512).resize((256, 256), Image.LANCZOS).convert("RGBA")
    s.alpha_composite(m, (128, 128))
    s.convert("RGB").save(os.path.join(twa, "splash-512.png"), optimize=True)
    print("icons + splash written to", BRAND)


if __name__ == "__main__":
    sys.exit(main())
