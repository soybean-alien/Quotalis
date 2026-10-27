"""Quotalis by soybean_alien. Builds docs/assets/heatmap-demo.gif: the usage graph being hovered and clicked (demo data).

    Quotalis.exe --demo --snapshot=<dir> --culture=en-US        # renders 80-walk-NN-<action>.png among the states
    python tools/make-heatmap-gif.py <dir>                    # needs Pillow

Each walk frame is the app's own render. A cursor is drawn on the hovered day, found by its white ring; on "leave"
frames the cursor rests below the graph. Clicks show a short ripple. 1270 x 840 on a black stage.
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "docs" / "assets" / "heatmap-demo.gif"
CANVAS = (1270, 840)
TOP = 0  # the render is placed so the graph area sits in view
SCALE = 1.0


def ring_center(img, box):
    """Center of the bright white hover ring inside the heatmap area, or None."""
    px = img.load()
    xs, ys = [], []
    left, top, right, bottom = box
    for y in range(top, bottom):
        for x in range(left, right):
            r, g, b, a = px[x, y]
            if a > 200 and r > 235 and g > 235 and b > 235:
                xs.append(x)
                ys.append(y)
    if len(xs) < 8:
        return None
    return (min(xs) + max(xs)) // 2, (min(ys) + max(ys)) // 2


def cursor(draw, x, y, pressed=False):
    """A classic arrow pointer with its tip at (x, y)."""
    pts = [(x, y), (x, y + 22), (x + 6, y + 17), (x + 10, y + 26), (x + 14, y + 24), (x + 10, y + 15), (x + 17, y + 15)]
    draw.polygon(pts, fill=(255, 255, 255), outline=(0, 0, 0))
    if pressed:
        draw.ellipse((x - 12, y - 12, x + 12, y + 12), outline=(255, 214, 10), width=3)


def font(size):
    for name in ("SegUIVar.ttf", "segoeui.ttf"):
        p = Path("C:/Windows/Fonts") / name
        if p.exists():
            return ImageFont.truetype(str(p), size)
    return ImageFont.load_default()


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    snaps = Path(sys.argv[1])
    walk = sorted(snaps.glob("80-walk-*.png"))
    if not walk:
        sys.exit("No 80-walk-*.png renders in " + str(snaps))

    first = Image.open(walk[0]).convert("RGBA")
    # Show the lower part of the island (graph, hours block, day readout), centered on the stage.
    crop_top = max(0, first.height - CANVAS[1] + 40)
    heat_box = (30, int(first.height * 0.55), first.width - 30, int(first.height * 0.72))
    caption_font = font(24)
    frames, durations = [], []
    last_pos = None
    captions = {"hover": "Hover a day", "pin": "Click to pin it", "leave": "Move away: the pinned day stays"}
    for path in walk:
        action = path.stem.split("-")[-1]
        img = Image.open(path).convert("RGBA")
        pos = ring_center(img, heat_box) if action != "leave" else None
        if pos is None:
            pos = (img.width // 2 + 90, int(img.height * 0.80)) if action == "leave" else last_pos
        last_pos = pos if action != "leave" else last_pos

        stage = Image.new("RGBA", CANVAS, (0, 0, 0, 255))
        x0 = (CANVAS[0] - img.width) // 2
        y0 = 20 - crop_top
        stage.alpha_composite(img, (x0, y0))
        draw = ImageDraw.Draw(stage)
        if pos:
            cursor(draw, x0 + pos[0], y0 + pos[1], pressed=action == "pin")
        text = captions.get(action, "")
        draw.text((60, CANVAS[1] - 70), text, font=caption_font, fill=(134, 134, 139))
        draw.text((60, CANVAS[1] - 40), "Quotalis usage graph (sample data)", font=font(16), fill=(90, 90, 95))
        frames.append(stage.convert("RGB"))
        durations.append(1600 if action != "hover" else 1100)

    sample = Image.new("RGB", (CANVAS[0], CANVAS[1] * len(frames)))
    for i, f in enumerate(frames):
        sample.paste(f, (0, CANVAS[1] * i))
    palette = sample.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    quantized = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    quantized[0].save(OUT, save_all=True, append_images=quantized[1:], duration=durations, loop=0, optimize=True, disposal=1)
    print(f"{OUT.name}: {len(frames)} frames, {sum(durations) / 1000:.1f} s, {OUT.stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
