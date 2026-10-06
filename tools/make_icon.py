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
SCREEN_EDGE = (92, 101, 122, 255)
BAR = (255, 122, 26, 255)        # the "Instrument" orange of the program's own look
GLYPH = (31, 36, 48, 255)


def draw(size: int) -> Image.Image:
    """
    One icon, drawn at its own size: a screen with the bar along its top edge,
    and the bar carrying what the program is for - readings. Below 48 pixels
    the readings are plain marks; at 48 and up they are small pictures of a
    clock, a level and a chip.
    """
    # Drawn at eight times the target and reduced once. Straight lines stay
    # straight, and only the curves get antialiased.
    scale = 8
    s = size * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)

    margin = max(1, round(s * 0.04))
    pen.rounded_rectangle(
        (margin, margin, s - margin - 1, s - margin - 1),
        radius=round(s * 0.20),
        fill=SCREEN,
        outline=SCREEN_EDGE,
        width=max(scale, round(s * 0.04)),
    )

    # The bar: nearly the width of the screen, a third of its height.
    left = round(s * 0.11)
    right = s - left - 1
    top = round(s * 0.15)
    bottom = round(s * 0.47)
    pen.rounded_rectangle((left, top, right, bottom), radius=round((bottom - top) * 0.22), fill=BAR)

    height = bottom - top
    centre = (top + bottom) // 2
    mark = round(height * 0.52)
    cells = [left + round((right - left) * f) for f in (0.20, 0.50, 0.80)]

    if size < 48:
        # Three marks; at sixteen pixels that is what a reading can be.
        for x in cells:
            half = max(scale, mark // 2)
            pen.rounded_rectangle((x - half, centre - half, x + half, centre + half),
                                  radius=round(half * 0.45), fill=GLYPH)
    else:
        line = max(scale, round(height * 0.11))
        half = mark // 2

        # A clock: ring and two hands.
        x = cells[0]
        pen.ellipse((x - half, centre - half, x + half, centre + half), outline=GLYPH, width=line)
        pen.line((x, centre, x, centre - round(half * 0.65)), fill=GLYPH, width=line)
        pen.line((x, centre, x + round(half * 0.5), centre), fill=GLYPH, width=line)

        # A level: three bars rising.
        x = cells[1]
        w = max(scale, round(half * 0.42))
        for i, h in enumerate((0.45, 0.8, 1.15)):
            bx = x - round(half * 0.95) + i * round(half * 0.7)
            pen.rounded_rectangle((bx, centre + half - round(2 * half * h * 0.8), bx + w, centre + half),
                                  radius=max(1, w // 3), fill=GLYPH)

        # A chip: a square with pins.
        x = cells[2]
        inner = round(half * 0.72)
        pen.rounded_rectangle((x - inner, centre - inner, x + inner, centre + inner),
                              radius=round(inner * 0.25), outline=GLYPH, width=line)
        pin = round(half * 0.32)
        for d in (-round(inner * 0.5), round(inner * 0.5)):
            pen.line((x + d, centre - inner, x + d, centre - inner - pin), fill=GLYPH, width=line)
            pen.line((x + d, centre + inner, x + d, centre + inner + pin), fill=GLYPH, width=line)

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
