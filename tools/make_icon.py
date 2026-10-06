"""Draws the application icon and writes it out at every size Windows asks for.

The icon is drawn rather than stored as a binary so that a change is a diff, and
so that the small sizes are drawn on their own grid instead of being resampled
down from one large image - at 16 pixels a resampled rounded corner turns into
grey mush.

Run: python tools/make_icon.py
"""

from __future__ import annotations

import io
import pathlib
import struct

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "MCD.App" / "Assets"

# Sizes Windows actually asks for: the tray takes 16-24 depending on scaling,
# the taskbar and Alt+Tab take 32-48, the Store and the shell take the rest.
ICO_SIZES = (16, 20, 24, 28, 32, 40, 48, 56, 60, 64, 72, 80, 96, 128, 256)

import icon_dark
import icon_light


def draw(size: int, light: bool = False) -> Image.Image:
    """One icon at its own size; the dark one by default."""
    return (icon_light if light else icon_dark).draw(size)


def write_ico(path: pathlib.Path, light: bool) -> None:
    # Written by hand. Pillow's ICO writer keeps only the frames no larger than
    # the image it is called on, and called on the 16-pixel one it wrote an icon
    # with a single 16-pixel frame - every larger size Windows asked for was
    # that frame stretched, which is the blur on the desktop shortcut. Each size
    # here is its own drawing, stored as a PNG frame.
    frames = []
    for size in ICO_SIZES:
        buffer = io.BytesIO()
        draw(size, light).save(buffer, format="PNG")
        frames.append((size, buffer.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(frames))
    entries = b""
    data = b""
    offset = 6 + 16 * len(frames)

    for size, png in frames:
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset + len(data))
        data += png

    path.write_bytes(header + entries + data)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    # The dark one is the program's own icon (the file, the shortcuts, the
    # installer); the light one is what the windows wear under a light Windows.
    write_ico(ASSETS / "icon.ico", light=False)
    write_ico(ASSETS / "icon-light.ico", light=True)

    for size in (16, 24, 32, 48, 128, 256):
        draw(size).save(ASSETS / f"icon-{size}.png")
        draw(size, light=True).save(ASSETS / f"icon-light-{size}.png")

    print(f"wrote icon.ico and icon-light.ico at {', '.join(str(s) for s in ICO_SIZES)}")


if __name__ == "__main__":
    main()
