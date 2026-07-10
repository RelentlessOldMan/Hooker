"""Generate the widget's four tiles from Mascot.png.

Two decoupled axes:
  * background tint  -> needs-you:  green = working, yellow = waiting
  * mascot body      -> enabled:    salmon = hooking, muted grey = manual

So: tile-<work|wait>_<on|off>.png  (on = hooking = salmon, off = manual = grey).
Each: recolor exterior background to the tint, keep/greyscale the body with a
black outline and black eyes, crop tight, and pad to a square with a small margin.
"""
from collections import deque
from pathlib import Path
from PIL import Image, ImageFilter

HERE = Path(__file__).parent
SRC = HERE.parent / "Mascot.png"

TINTS = {"work": (232, 176, 28), "wait": (43, 166, 82)}   # working=yellow (busy), waiting=green (your turn)
BODIES = {"on": (215, 119, 87), "off": (170, 168, 164)}   # hooking=salmon, manual=muted grey

DARK_MAX = 60
HMARGIN = 0.035
OUTLINE = 7


def content_bbox(img, tint):
    w, h = img.size
    px = img.load()
    mask = Image.new("L", (w, h), 0)
    mp = mask.load()
    for y in range(h):
        for x in range(w):
            if px[x, y][:3] != tint:
                mp[x, y] = 255
    return mask.getbbox()


def recolor(src, tint, body_color):
    src = src.convert("RGBA")
    w, h = src.size
    px = src.load()

    def dark(x, y):
        r, g, b, a = px[x, y]
        return a > 0 and max(r, g, b) <= DARK_MAX

    ext = [[False] * w for _ in range(h)]
    dq = deque()
    for x in range(w):
        for y in (0, h - 1):
            if dark(x, y) and not ext[y][x]:
                ext[y][x] = True; dq.append((x, y))
    for y in range(h):
        for x in (0, w - 1):
            if dark(x, y) and not ext[y][x]:
                ext[y][x] = True; dq.append((x, y))
    while dq:
        x, y = dq.popleft()
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if 0 <= nx < w and 0 <= ny < h and not ext[ny][nx] and dark(nx, ny):
                ext[ny][nx] = True; dq.append((nx, ny))

    body = Image.new("L", (w, h), 0)
    bpx = body.load()
    for y in range(h):
        for x in range(w):
            if px[x, y][3] > 0 and not dark(x, y):
                bpx[x, y] = 255
    ring = body.filter(ImageFilter.MaxFilter(2 * OUTLINE + 1)).load()

    out = Image.new("RGBA", (w, h))
    opx = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if a > 0 and not dark(x, y):
                opx[x, y] = (*body_color, 255)
            elif ring[x, y] > 0:
                opx[x, y] = (0, 0, 0, 255)
            elif ext[y][x]:
                opx[x, y] = (*tint, 255)
            else:
                opx[x, y] = (0, 0, 0, 255)

    bbox = content_bbox(out, tint)
    crop = out.crop(bbox)
    cw, ch = crop.size
    pad = max(1, round(cw * HMARGIN))
    side = cw + 2 * pad
    canvas = Image.new("RGBA", (side, side), (*tint, 255))
    canvas.paste(crop, (pad, (side - ch) // 2), crop)
    return canvas


def build(tk, tint, bk, body):
    img = recolor(Image.open(SRC), tint, body)
    name = f"tile-{tk}_{bk}.png"
    img.save(HERE / name)
    print("wrote", name, img.size)


if __name__ == "__main__":
    if not SRC.exists():
        raise SystemExit(f"Mascot source not found: {SRC}")
    for tk, tint in TINTS.items():
        for bk, body in BODIES.items():
            build(tk, tint, bk, body)
