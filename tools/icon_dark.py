# Drawn by Codex from tools/make_icon.py's brief, reviewed and chosen by the user.
# The icon for dark (teal harbour) surroundings. Imported by make_icon.py.
"""Signal Harbor: teal telemetry dock on a deep ocean monitor."""

from __future__ import annotations

from PIL import Image, ImageDraw


SIZES = (16, 20, 24, 32, 48, 128, 256)
BODY = "#102A33"
EDGE = "#3A6972"
BAR_SHADOW = "#0A6670"
BAR = "#35D7C5"
INK = "#09252D"
AMBER = "#FFB547"

SMALL = {
    16: (1, 2, 15, 7, (4.25, 8, 11.75), 3),
    20: (1, 2, 19, 9, (5.25, 10, 14.75), 4),
    24: (2, 3, 22, 11, (6.25, 12, 17.75), 5),
}


def draw(size: int) -> Image.Image:
    scale = 8
    s = size * scale
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    q = lambda n: round(n * scale)

    margin = max(1, round(size * 0.045))
    d.rounded_rectangle(
        (q(margin), q(margin), q(size - margin) - 1, q(size - margin) - 1),
        radius=q(size * 0.205), fill=BODY, outline=EDGE,
        width=max(scale, q(size * 0.032)),
    )

    if size in SMALL:
        left, top, right, bottom, centers, mark = SMALL[size]
    else:
        left, top, right, bottom = size * 0.075, size * 0.105, size * 0.925, size * 0.405
        centers = (size * 0.245, size * 0.50, size * 0.755)
        mark = size * 0.105

    # A narrow dark keel makes the aqua strip feel physically docked to the screen.
    d.rounded_rectangle((q(left), q(top + size * 0.025), q(right), q(bottom + size * 0.045)),
                        radius=q(size * 0.075), fill=BAR_SHADOW)
    d.rounded_rectangle((q(left), q(top), q(right), q(bottom)),
                        radius=q(size * 0.075), fill=BAR)

    cy = (top + bottom) / 2
    if size <= 24:
        # Three deliberately different, heavy silhouettes: dial, level, processor.
        x = centers[0]
        d.ellipse((q(x - mark / 2), q(cy - mark / 2), q(x + mark / 2), q(cy + mark / 2)), fill=INK)
        d.pieslice((q(x - mark * .31), q(cy - mark * .31), q(x + mark * .31), q(cy + mark * .31)),
                   270, 360, fill=AMBER)
        x = centers[1]
        w = max(scale, q(mark * (.32 if size == 16 else .25)))
        levels = ((-.24, .56), (.28, 1.0)) if size == 16 else ((-.34, .48), (0, .76), (.34, 1.0))
        for dx, h in levels:
            d.rounded_rectangle((q(x + dx * mark) - w // 2, q(cy + mark / 2 - h * mark),
                                 q(x + dx * mark) + w // 2, q(cy + mark / 2)),
                                radius=max(1, w // 3), fill=INK)
        x = centers[2]
        d.rounded_rectangle((q(x - mark / 2), q(cy - mark / 2), q(x + mark / 2), q(cy + mark / 2)),
                            radius=max(1, q(mark * .12)), fill=INK)
        d.rectangle((q(x - mark * .18), q(cy - mark * .18), q(x + mark * .18), q(cy + mark * .18)), fill=AMBER)
    else:
        r = mark
        line = max(scale, q(size * .025))
        x = centers[0]
        d.ellipse((q(x-r), q(cy-r), q(x+r), q(cy+r)), outline=INK, width=line)
        d.line((q(x), q(cy), q(x), q(cy-r*.58)), fill=INK, width=line)
        d.line((q(x), q(cy), q(x+r*.52), q(cy)), fill=AMBER, width=line)
        x = centers[1]
        bw = q(r * .38)
        for dx, h in ((-.62, .65), (0, 1.18), (.62, 1.72)):
            d.rounded_rectangle((q(x+dx*r)-bw//2, q(cy+r*.82-h*r), q(x+dx*r)+bw//2, q(cy+r*.82)),
                                radius=bw//3, fill=INK)
        x = centers[2]
        d.rounded_rectangle((q(x-r*.72), q(cy-r*.72), q(x+r*.72), q(cy+r*.72)),
                            radius=q(r*.16), outline=INK, width=line)
        d.rectangle((q(x-r*.22), q(cy-r*.22), q(x+r*.22), q(cy+r*.22)), fill=AMBER)
        pin = q(r * .33)
        for off in (-.38, .38):
            d.line((q(x+off*r), q(cy-r*.72), q(x+off*r), q(cy-r*.72)-pin), fill=INK, width=line)
            d.line((q(x+off*r), q(cy+r*.72), q(x+off*r), q(cy+r*.72)+pin), fill=INK, width=line)

    if size >= 32:
        # A quiet horizon keeps the lower field recognisable as a display.
        y = q(size * .72)
        d.rounded_rectangle((q(size*.23), y, q(size*.77), y + max(scale, q(size*.018))),
                            radius=scale, fill="#24505A")
        d.ellipse((q(size*.47), q(size*.69), q(size*.53), q(size*.75)), fill=AMBER)

    return im.resize((size, size), Image.Resampling.LANCZOS)
