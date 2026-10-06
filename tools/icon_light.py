# Drawn by Codex from tools/make_icon.py's brief, reviewed and chosen by the user.
# The icon for light (ivory console) surroundings. Imported by make_icon.py.
"""Ivory Console: a light instrument body with a dark telemetry rail."""

from __future__ import annotations

from PIL import Image, ImageDraw


SIZES = (16, 20, 24, 32, 48, 128, 256)
BODY = "#F5E9D4"
EDGE = "#A88F72"
SCREEN_SHADE = "#E2D1B8"
BAR_SHADOW = "#A85035"
BAR = "#232A33"
BLUE = "#63D1FF"
ORANGE = "#FF7043"

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

    margin = max(1, round(size*.045))
    d.rounded_rectangle((q(margin), q(margin), q(size-margin)-1, q(size-margin)-1),
                        radius=q(size*.205), fill=BODY, outline=EDGE,
                        width=max(scale, q(size*.034)))

    if size in SMALL:
        left, top, right, bottom, centers, mark = SMALL[size]
    else:
        left, top, right, bottom = size*.07, size*.105, size*.93, size*.405
        centers = (size*.245, size*.50, size*.755)
        mark = size*.105

    # A warm orange sliver under the charcoal rail is visible even at tray sizes.
    d.rounded_rectangle((q(left), q(top+size*.03), q(right), q(bottom+size*.055)),
                        radius=q(size*.07), fill=BAR_SHADOW)
    d.rounded_rectangle((q(left), q(top), q(right), q(bottom)),
                        radius=q(size*.07), fill=BAR)
    cy = (top + bottom) / 2

    if size <= 24:
        x = centers[0]
        d.ellipse((q(x-mark/2), q(cy-mark/2), q(x+mark/2), q(cy+mark/2)), fill=BLUE)
        d.pieslice((q(x-mark*.30), q(cy-mark*.30), q(x+mark*.30), q(cy+mark*.30)),
                   275, 365, fill=ORANGE)
        x = centers[1]
        w = max(scale, q(mark*(.32 if size == 16 else .25)))
        levels = ((-.24, .55), (.28, 1.0)) if size == 16 else ((-.34, .45), (0, .72), (.34, 1.0))
        for i, (dx, h) in enumerate(levels):
            d.rounded_rectangle((q(x+dx*mark)-w//2, q(cy+mark/2-h*mark),
                                 q(x+dx*mark)+w//2, q(cy+mark/2)),
                                radius=w//3, fill=BLUE if i < 2 else ORANGE)
        x = centers[2]
        d.rounded_rectangle((q(x-mark*.55), q(cy-mark*.55), q(x+mark*.55), q(cy+mark*.55)),
                            radius=max(1, q(mark*.12)), fill=ORANGE)
        d.rectangle((q(x-mark*.18), q(cy-mark*.18), q(x+mark*.18), q(cy+mark*.18)), fill=BLUE)
    else:
        r = mark
        line = max(scale, q(size*.025))
        x = centers[0]
        d.ellipse((q(x-r), q(cy-r), q(x+r), q(cy+r)), outline=BLUE, width=line)
        d.line((q(x), q(cy), q(x), q(cy-r*.60)), fill=BLUE, width=line)
        d.line((q(x), q(cy), q(x+r*.52), q(cy)), fill=ORANGE, width=line)
        x = centers[1]
        bw = q(r*.4)
        for i, (dx, h) in enumerate(((-.63, .65), (0, 1.15), (.63, 1.7))):
            d.rounded_rectangle((q(x+dx*r)-bw//2, q(cy+r*.82-h*r),
                                 q(x+dx*r)+bw//2, q(cy+r*.82)),
                                radius=bw//3, fill=BLUE if i < 2 else ORANGE)
        x = centers[2]
        d.rounded_rectangle((q(x-r*.73), q(cy-r*.73), q(x+r*.73), q(cy+r*.73)),
                            radius=q(r*.14), outline=ORANGE, width=line)
        d.rectangle((q(x-r*.24), q(cy-r*.24), q(x+r*.24), q(cy+r*.24)), fill=BLUE)
        pin = q(r*.32)
        for off in (-.38, .38):
            d.line((q(x+off*r), q(cy-r*.72), q(x+off*r), q(cy-r*.72)-pin), fill=ORANGE, width=line)
            d.line((q(x+off*r), q(cy+r*.72), q(x+off*r), q(cy+r*.72)+pin), fill=ORANGE, width=line)

    if size >= 32:
        d.rounded_rectangle((q(size*.23), q(size*.67), q(size*.77), q(size*.75)),
                            radius=q(size*.04), fill=SCREEN_SHADE)
        d.rounded_rectangle((q(size*.30), q(size*.695), q(size*.59), q(size*.725)),
                            radius=q(size*.015), fill="#B7A389")
        d.ellipse((q(size*.67), q(size*.685), q(size*.72), q(size*.735)), fill=ORANGE)

    return im.resize((size, size), Image.Resampling.LANCZOS)
