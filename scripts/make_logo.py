"""Draw the Windows Weather logo: a window with snow piled on its top edge, daylight sky in the glass, the sun
peeking from behind a cloud. Writes brand/ (PNG mark, wordmark, previews) and app/icon.ico (exe + tray icon).

Two panes on purpose (four square panes would read as Microsoft's logo). Colours come from the app itself:
night-navy frame, daylight-blue glass, snow white, warm sun. Sizes of 24 px and below drop the sun and cloud so the
tray icon stays legible.

Run: python execution/make_logo.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import io
import math
import random
import struct

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "brand"

NAVY = (30, 53, 80, 255)          # frame
NAVY_DARK = (22, 40, 62, 255)     # mullion
SKY_TOP = (84, 160, 232)          # glass, top
SKY_BOT = (190, 226, 250)         # glass, bottom
SNOW = (255, 255, 255, 255)
SNOW_SHADE = (214, 230, 246, 255)
SNOW_EDGE = (150, 182, 214, 255)  # thin outline so the snow still reads on a white background
SUN = (255, 200, 74, 255)
CLOUD = (255, 255, 255, 255)
CLOUD_SHADE = (226, 238, 250, 255)


def mark(size: int, detail: bool = True) -> Image.Image:
    """The mark on a transparent square canvas, drawn 4x and downsampled."""
    ss = 4
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = S / 100.0  # one unit = 1% of the canvas

    # The window: frame, glass with a sky gradient, one vertical mullion. Small sizes use a tighter crop.
    if detail:
        x0, y0, x1, y1 = 14 * u, 26 * u, 86 * u, 90 * u
    else:
        x0, y0, x1, y1 = 7 * u, 22 * u, 93 * u, 97 * u
    r = 11 * u
    d.rounded_rectangle((x0, y0, x1, y1), radius=r, fill=NAVY)
    fw = 6.5 * u if detail else 9 * u
    gx0, gy0, gx1, gy1 = x0 + fw, y0 + fw, x1 - fw, y1 - fw
    glass = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glass)
    for yy in range(int(gy0), int(gy1) + 1):
        t = (yy - gy0) / max(1, gy1 - gy0)
        c = tuple(int(SKY_TOP[i] + (SKY_BOT[i] - SKY_TOP[i]) * t) for i in range(3)) + (255,)
        gd.line([(gx0, yy), (gx1, yy)], fill=c)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((gx0, gy0, gx1, gy1), radius=r - fw * 0.8, fill=255)
    img.paste(glass, (0, 0), mask)

    if detail:
        # Sun peeking from behind a cloud, in the right pane.
        sun_c = (64 * u, 52 * u)
        d.ellipse((sun_c[0] - 9 * u, sun_c[1] - 9 * u, sun_c[0] + 9 * u, sun_c[1] + 9 * u), fill=SUN)
        cloud = [(63, 66, 8.5), (71, 62, 9.5), (78.5, 67, 6.5), (58, 69.5, 5.5)]
        for cx, cy, cr in cloud:
            d.ellipse(((cx - cr) * u, (cy + 1.2 - cr) * u, (cx + cr) * u, (cy + 1.2 + cr) * u), fill=CLOUD_SHADE)
        for cx, cy, cr in cloud:
            d.ellipse(((cx - cr) * u, (cy - cr) * u, (cx + cr) * u, (cy + cr) * u), fill=CLOUD)
        d.rectangle((56 * u, 66 * u, 81 * u, 74.5 * u), fill=CLOUD)
        # Re-clip the cloud to the glass.
        clip = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        clip.paste(img, (0, 0), mask)
        frame_only = img.copy()
        inv = Image.eval(mask, lambda v: 255 - v)
        base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        base.paste(frame_only, (0, 0), inv)
        ImageDraw.Draw(base)  # noqa: keep frame
        img = Image.alpha_composite(base, clip)
        d = ImageDraw.Draw(img)

    # Mullion (drawn after the sky so it sits on top).
    mw = 4.2 * u if detail else 6 * u
    mx = (x0 + x1) / 2
    d.rectangle((mx - mw / 2, gy0, mx + mw / 2, gy1), fill=NAVY_DARK)

    # Snow piled on the top edge: lumpy, overhanging both ends, a soft shaded underside.
    rnd = random.Random(7)
    top = y0 + 1.5 * u
    lumps = []
    n = 9
    for i in range(n):
        t = i / (n - 1)
        cx = (x0 - 2.5 * u) + t * (x1 - x0 + 5 * u)
        peak = math.sin(t * math.pi) ** 0.6
        cr = (5.2 + 3.4 * peak + rnd.uniform(-0.6, 0.6)) * u
        lumps.append((cx, top - (1.2 + 3.6 * peak) * u, cr))
    e = 1.3 * u if detail else 2.4 * u
    for cx, cy, cr in lumps:
        d.ellipse((cx - cr - e, cy - cr - e, cx + cr + e, cy + cr + 1.6 * u + e), fill=SNOW_EDGE)
    d.rounded_rectangle((x0 - 2.5 * u - e, top - 3.5 * u - e, x1 + 2.5 * u + e, top + 5.5 * u + e), radius=4 * u + e, fill=SNOW_EDGE)
    for ex, dr in ((x0 + 1.5 * u, 3.6 * u), (x1 - 2.5 * u, 3.0 * u)):
        d.ellipse((ex - dr - e, top + 2 * u - e, ex + dr + e, top + 2 * u + 2.4 * dr + e), fill=SNOW_EDGE)
    for cx, cy, cr in lumps:
        d.ellipse((cx - cr, cy - cr + 1.6 * u, cx + cr, cy + cr + 1.6 * u), fill=SNOW_SHADE)
    d.rounded_rectangle((x0 - 2.5 * u, top - 3 * u, x1 + 2.5 * u, top + 5.5 * u), radius=4 * u, fill=SNOW_SHADE)
    for cx, cy, cr in lumps:
        d.ellipse((cx - cr, cy - cr, cx + cr, cy + cr), fill=SNOW)
    d.rounded_rectangle((x0 - 2.5 * u, top - 3.5 * u, x1 + 2.5 * u, top + 4 * u), radius=4 * u, fill=SNOW)
    # Drips over the ends of the frame.
    for ex, dr in ((x0 + 1.5 * u, 3.6 * u), (x1 - 2.5 * u, 3.0 * u)):
        d.ellipse((ex - dr, top + 2 * u, ex + dr, top + 2 * u + 2.4 * dr), fill=SNOW)

    if detail:
        # Two flakes on their way down.
        # Three small six-armed flakes, ice blue so they read on dark and light backgrounds alike.
        for fx, fy, fr in ((29, 10, 4.2), (77, 7, 3.4), (54, 14, 2.6)):
            for k in range(3):
                a = math.radians(90 + 60 * k)
                dx, dy = math.cos(a) * fr * u, math.sin(a) * fr * u
                d.line((fx * u - dx, fy * u - dy, fx * u + dx, fy * u + dy), fill=(150, 196, 236, 255), width=max(1, int(1.1 * u)))

    return img.resize((size, size), Image.LANCZOS)


def save_ico(path: Path, sizes):
    frames = []
    for s in sizes:
        im = mark(s, detail=s > 24)
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        frames.append((s, buf.getvalue()))
    out = struct.pack("<HHH", 0, 1, len(frames))
    off = 6 + 16 * len(frames)
    for s, data in frames:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), off)
        off += len(data)
    out += b"".join(dt for _, dt in frames)
    path.write_bytes(out)


def font(size, weight="Semibold"):
    for name in ("SegUIVar.ttf", "segoeuib.ttf", "segoeui.ttf"):
        p = Path("C:/Windows/Fonts") / name
        if p.exists():
            f = ImageFont.truetype(str(p), size)
            try:
                f.set_variation_by_name(weight)
            except Exception:
                pass
            return f
    return ImageFont.load_default()


def wordmark(height: int, dark_bg: bool) -> Image.Image:
    m = mark(height)
    f = font(int(height * 0.40))
    text = "Windows Weather"
    tw = int(f.getlength(text))
    w = height + int(height * 0.18) + tw + int(height * 0.12)
    img = Image.new("RGBA", (w, height), (0, 0, 0, 0))
    img.paste(m, (0, 0), m)
    d = ImageDraw.Draw(img)
    color = (240, 245, 250, 255) if dark_bg else (30, 53, 80, 255)
    asc, desc = f.getmetrics()
    d.text((height + int(height * 0.18), (height - (asc + desc)) // 2 + int(height * 0.05)), text, font=f, fill=color)
    return img


def main():
    BRAND.mkdir(exist_ok=True)
    big = mark(1024)
    big.save(BRAND / "logo-1024.png")
    mark(512).save(BRAND / "logo-512.png")
    for dark in (True, False):
        wordmark(256, dark).save(BRAND / f"wordmark-{'dark' if dark else 'light'}-bg.png")

    # A preview sheet: the mark on dark and light, the wordmark, and the small sizes the tray actually uses.
    W, H = 1600, 900
    sheet = Image.new("RGBA", (W, H), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    d.rectangle((0, 0, W // 2, H), fill=(28, 28, 32, 255))
    for i, bg_dark in enumerate((True, False)):
        ox = i * W // 2
        m = mark(380)
        sheet.paste(m, (ox + (W // 2 - 380) // 2, 60), m)
        wm = wordmark(110, bg_dark)
        sheet.paste(wm, (ox + (W // 2 - wm.width) // 2, 480), wm)
        x = ox + 120
        for s in (16, 20, 24, 32, 48, 64):
            small = mark(s, detail=s > 24)
            sheet.paste(small, (x, 700 - s // 2), small)
            d.text((x, 760), f"{s}px", font=font(20, "Regular"), fill=(200, 200, 205, 255) if bg_dark else (90, 90, 95, 255))
            x += max(s, 40) + 50
    sheet.convert("RGB").save(BRAND / "logo-preview.png")

    save_ico(ROOT / "app" / "icon.ico", [16, 20, 24, 32, 40, 48, 64, 128, 256])
    print("wrote", BRAND / "logo-1024.png", BRAND / "logo-preview.png", ROOT / "app" / "icon.ico")


if __name__ == "__main__":
    main()
