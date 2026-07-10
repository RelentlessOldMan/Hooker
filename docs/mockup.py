"""Render clean promo mockups of the widget from the real tile art (no desktop needed)."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).parent
A = HERE.parent / "assets"
BAR = (30, 30, 34, 255)
TILE = 56
GAP = 8
GRIP = 18
PAD = 10
RAD = 16
SCALE = 2   # supersample for crispness


def font(size):
    for name in ("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def tile(name):
    return Image.open(A / f"tile-{name}.png").convert("RGBA").resize((TILE * SCALE, TILE * SCALE), Image.LANCZOS)


def strip(tiles):
    n = len(tiles)
    w = (PAD * 2 + GRIP + GAP + n * TILE + (n - 1) * GAP) * SCALE
    h = (PAD * 2 + TILE) * SCALE
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=RAD * SCALE, fill=BAR)
    # grip dots
    gx = (PAD + 3) * SCALE
    for cx in range(2):
        for cy in range(3):
            x = gx + cx * 6 * SCALE
            y = (PAD + TILE // 2 - 11 + cy * 8) * SCALE
            d.ellipse([x, y, x + 4 * SCALE, y + 4 * SCALE], fill=(200, 200, 205, 210))
    x = (PAD + GRIP + GAP) * SCALE
    for t in tiles:
        img.alpha_composite(t, (x, PAD * SCALE))
        x += (TILE + GAP) * SCALE
    return img


def tooltip(lines):
    f = font(15 * SCALE)
    d0 = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    tw = max(d0.textlength(l, font=f) for l in lines)
    lh = (f.getbbox("Ay")[3] - f.getbbox("Ay")[1]) + 4 * SCALE
    padx, pady = 10 * SCALE, 7 * SCALE
    w = int(tw + padx * 2)
    h = int(lh * len(lines) + pady * 2)
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=6 * SCALE, fill=(24, 24, 28, 255))
    y = pady
    for l in lines:
        d.text((padx, y), l, font=f, fill=(240, 240, 245, 255))
        y += lh
    return img


# Hero: a strip with a variety of states + a tooltip beneath one tile.
s = strip([tile("wait_on"), tile("work_off"), tile("wait_off"), tile("work_on")])
tip = tooltip(["MusicMaker", "manual · waiting on you", "0 auto-approvals"])
gap = 14 * SCALE
hero = Image.new("RGBA", (max(s.width, tip.width) + 20 * SCALE, s.height + gap + tip.height + 20 * SCALE), (0, 0, 0, 0))
hero.alpha_composite(s, (10 * SCALE, 10 * SCALE))
hero.alpha_composite(tip, (10 * SCALE + (TILE + GRIP) * SCALE, 10 * SCALE + s.height + gap))
hero.save(HERE / "hero.png")
print("wrote hero.png", hero.size)
