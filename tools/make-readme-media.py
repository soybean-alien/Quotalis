"""Builds the README media from the app's own offscreen renders (demo data only).

    Claudex.exe --demo --snapshot=<dir>       # renders every state to PNG
    python tools/make-readme-media.py <dir>           # needs Pillow (pip install pillow)

Writes to docs/assets/:
  - the screenshots, copied under stable names (no editing, no mockups)
  - hero.gif: 1270 x 840, about 20 s: the notch, the capsule, a live activity, Claude and Codex usage,
    Music, Focus, System and Awake, each with a one-line caption, joined by short crossfades (the live
    app morphs with springs instead; see docs/RECORDING.md to record that)
  - hero.png: the expanded usage view on the same stage
The banner and social preview come from tools/banner/render.ps1.
"""

import shutil
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "docs" / "assets"
STAGE = (0, 0, 0)
CAPTION = (134, 134, 139)

# Snapshot name -> asset name.
SCREENSHOTS = {
    "01-compact": "compact",
    "50-compact-claude": "compact-featured-claude",
    "50-compact-system": "compact-featured-system",
    "03-activity-music": "activity-music",
    "04-activity-claude": "activity-claude",
    "10-expanded-usage": "expanded-usage",
    "11-usage-tooltip": "usage-tooltip",
    "10-expanded-music": "expanded-music",
    "10-expanded-system": "expanded-system",
    "10-expanded-focus": "expanded-focus",
    "10-expanded-calendar": "expanded-calendar",
    "10-expanded-git": "expanded-git",
    "10-expanded-github": "expanded-github",
    "10-expanded-tasks": "expanded-tasks",
    "20-notch": "notch",
    "60-privacy-compact-mic": "privacy-compact",
    "30-settings-modules": "settings-modules",
    "30-settings-position": "settings-position",
    "30-settings-position-end": "settings-auto-hide",
}

# The hero animation: (snapshot, seconds on screen, caption).
SEQUENCE = [
    ("20-notch", 1.4, "Out of the way, as a notch"),
    ("50-compact-claude", 1.8, "Your Claude limit, at rest"),
    ("04-activity-claude", 1.6, "Claude Code at work"),
    ("10-expanded-usage", 3.2, "Claude and Codex: plan limits and today's tokens"),
    ("11-usage-tooltip", 2.6, "Every day, both tools, on one graph"),
    ("10-expanded-music", 1.8, "Now playing"),
    ("10-expanded-focus", 1.6, "Focus"),
    ("10-expanded-system", 2.0, "Your PC"),
    ("72-awake-expanded", 1.6, "Keep awake"),
    ("50-compact-codex", 1.4, "Codex, at rest"),
    ("50-compact-claude", 1.0, "Claudex"),
]
FADE_FRAMES = 2
FADE_SECONDS = 0.24
CANVAS = (1270, 840)
TOP = 22  # where the capsule's top edge sits, like the island under the screen edge


def capsule_box(image):
    """Bounding box of the opaque capsule, without its soft shadow."""
    alpha = image.getchannel("A").point(lambda a: 255 if a > 230 else 0)
    return alpha.getbbox()


def place(image, caption=None, canvas_size=CANVAS, top=TOP):
    """The render on the black stage, capsule centered and its top at `top`, with an optional caption."""
    left, upper, right, _ = capsule_box(image)
    canvas = Image.new("RGBA", canvas_size, STAGE + (255,))
    x = canvas_size[0] // 2 - (left + right) // 2
    canvas.alpha_composite(image, (x, top - upper))
    if caption:
        draw = ImageDraw.Draw(canvas)
        face = font(["SegUIVar.ttf", "segoeui.ttf"], 26)
        width = draw.textlength(caption, font=face)
        draw.text(((canvas_size[0] - width) / 2, canvas_size[1] - 58), caption, font=face, fill=CAPTION)
    return canvas.convert("RGB")


def hero_gif(snaps, out):
    frames, durations = [], []
    stills = [(place(Image.open(snaps / f"{name}.png").convert("RGBA"), caption), seconds) for name, seconds, caption in SEQUENCE]
    for i, (still, seconds) in enumerate(stills):
        frames.append(still)
        durations.append(int(seconds * 1000))
        if i + 1 < len(stills):
            nxt = stills[i + 1][0]
            for step in range(1, FADE_FRAMES + 1):
                frames.append(Image.blend(still, nxt, step / (FADE_FRAMES + 1)))
                durations.append(int(FADE_SECONDS * 1000 / FADE_FRAMES))

    # One shared palette, taken from every state, keeps the file small and the colors stable between frames.
    sample = Image.new("RGB", (CANVAS[0], CANVAS[1] * len(stills)))
    for i, (still, _) in enumerate(stills):
        sample.paste(still, (0, CANVAS[1] * i))
    palette = sample.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    quantized = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    quantized[0].save(out, save_all=True, append_images=quantized[1:], duration=durations, loop=0, optimize=True, disposal=1)


def font(names, size):
    for name in names:
        path = Path("C:/Windows/Fonts") / name
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    snaps = Path(sys.argv[1])
    ASSETS.mkdir(parents=True, exist_ok=True)
    for snapshot, asset in SCREENSHOTS.items():
        source = snaps / f"{snapshot}.png"
        if source.exists():
            shutil.copyfile(source, ASSETS / f"{asset}.png")
        else:
            print(f"skipped {asset}.png: {source.name} was not rendered")

    hero_gif(snaps, ASSETS / "hero.gif")
    place(Image.open(snaps / "10-expanded-usage.png").convert("RGBA")).save(ASSETS / "hero.png", optimize=True)
    for name in ("hero.gif", "hero.png"):
        print(f"{name}: {(ASSETS / name).stat().st_size / 1024:.0f} KB")


if __name__ == "__main__":
    main()
