"""Draws the application icon and writes it out at every size Windows asks for.

The icon is drawn rather than stored as a binary so that a change is a diff, and
so that the small sizes are drawn on their own grid instead of being resampled
down from one large image - at 16 pixels a resampled rounded corner turns into
grey mush.

Run: python tools/make_icon.py
"""

from __future__ import annotations

import pathlib

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "MCD.App" / "Assets"

# Sizes Windows actually asks for: the tray takes 16-24 depending on scaling,
# the taskbar and Alt+Tab take 32-48, the Store and the shell take the rest.
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

SCREEN = (31, 36, 48, 255)
SCREEN_EDGE = (78, 86, 104, 255)
ACCENT = (76, 194, 255, 255)
WIDGET = (150, 160, 178, 255)


def draw(size: int) -> Image.Image:
    """One icon, drawn at its own size."""
    # Drawn at four times the target and reduced once. Straight lines stay
    # straight, and only the curves get antialiased.
    scale = 4
    s = size * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)

    margin = max(1, round(s * 0.06))
    radius = round(s * 0.18)
    box = (margin, margin, s - margin - 1, s - margin - 1)

    pen.rounded_rectangle(box, radius=radius, fill=SCREEN, outline=SCREEN_EDGE,
                          width=max(scale, round(s * 0.035)))

    # The dock itself: a bar along the bottom edge, which is the whole idea of
    # the program and the one shape that still reads at 16 pixels.
    bar_height = round(s * 0.20)
    bar_inset = round(s * 0.17)
    bar_bottom = s - margin - round(s * 0.09)
    pen.rounded_rectangle(
        (bar_inset, bar_bottom - bar_height, s - bar_inset - 1, bar_bottom),
        radius=round(bar_height * 0.35),
        fill=ACCENT,
    )

    # Widgets on the screen above it. Below 24 pixels they would merge into a
    # smudge, so they are simply left out.
    if size >= 24:
        dot = round(s * 0.09)
        gap = round(s * 0.07)
        top = round(s * 0.30)
        left = bar_inset
        for _ in range(3):
            pen.rounded_rectangle(
                (left, top, left + dot * 2, top + dot),
                radius=round(dot * 0.4),
                fill=WIDGET,
            )
            left += dot * 2 + gap

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    images = [draw(size) for size in ICO_SIZES]
    images[0].save(
        ASSETS / "icon.ico",
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=images[1:],
    )

    # Kept alongside for the tray, the About page and the Store tiles later.
    for size in (16, 24, 32, 48, 256):
        draw(size).save(ASSETS / f"icon-{size}.png")

    print(f"wrote {ASSETS / 'icon.ico'} at {', '.join(str(s) for s in ICO_SIZES)}")


if __name__ == "__main__":
    main()
