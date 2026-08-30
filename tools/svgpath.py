"""Rewrites SVG path data into a form WinUI's parser understands.

Iconify serves paths squeezed as small as they will go: separators dropped
wherever a minus sign can stand in for one, and arc flags run together with the
coordinates that follow them, so that "a1 1 0 100-2" means an arc with
large-arc=1, sweep=0, and an endpoint of (0, -2).

WinUI accepts the same commands as SVG but not that packing. Fed the compressed
form it stops at the first thing it cannot read and silently draws the part it
managed - which is why the processor icon arrived with no legs and the network
icon as a single stroke. Nothing logs a warning; the picture is just wrong.

So every path is tokenised here, where the rules can be applied properly, and
written back out with one space between every number.
"""

from __future__ import annotations

import re

# How many numbers each command takes, per repetition.
ARITY = {
    "M": 2, "L": 2, "H": 1, "V": 1,
    "C": 6, "S": 4, "Q": 4, "T": 2,
    "A": 7, "Z": 0,
}

_NUMBER = re.compile(r"[+-]?(?:\d*\.\d+|\d+\.?)(?:[eE][+-]?\d+)?")


class _Reader:
    def __init__(self, text: str) -> None:
        self.text = text
        self.at = 0

    def skip(self) -> None:
        while self.at < len(self.text) and self.text[self.at] in " ,\t\r\n":
            self.at += 1

    def command(self) -> str | None:
        self.skip()
        if self.at >= len(self.text):
            return None
        c = self.text[self.at]
        if c.isalpha():
            self.at += 1
            return c
        return ""

    def number(self) -> float:
        self.skip()
        match = _NUMBER.match(self.text, self.at)
        if not match:
            raise ValueError(f"expected a number at {self.at} in {self.text!r}")
        self.at = match.end()
        return float(match.group())

    def flag(self) -> int:
        """An arc flag: exactly one character, 0 or 1, with no separator needed."""
        self.skip()
        c = self.text[self.at]
        if c not in "01":
            raise ValueError(f"expected an arc flag at {self.at} in {self.text!r}")
        self.at += 1
        return int(c)


def _fmt(value: float) -> str:
    text = f"{value:.4f}".rstrip("0").rstrip(".")
    return text if text not in ("", "-0") else "0"


def normalise(data: str) -> str:
    """
    The same path, with every token separated by a space.

    The opening moveto is always emitted in absolute form. On its own that
    changes nothing - SVG treats a path's first "m" as absolute anyway, because
    there is no current point yet - but these paths get joined end to end, and a
    lower-case m in the middle of the joined string means "relative to wherever
    the last one finished". That silently slid half of every multi-part icon off
    to one side.
    """
    reader = _Reader(data)
    out: list[str] = []
    command: str | None = None
    first = True

    while True:
        found = reader.command()

        if found is None:
            break

        if found:
            command = found
        elif command is None:
            raise ValueError(f"path starts without a command: {data!r}")
        elif command in "Mm":
            # A repeated moveto is a lineto, as SVG defines it.
            command = "L" if command == "M" else "l"

        out.append("M" if first and command == "m" else command)
        first = False
        upper = command.upper()

        if upper == "Z":
            command = None if found else command
            continue

        if upper == "A":
            out += [
                _fmt(reader.number()), _fmt(reader.number()), _fmt(reader.number()),
                str(reader.flag()), str(reader.flag()),
                _fmt(reader.number()), _fmt(reader.number()),
            ]
        else:
            out += [_fmt(reader.number()) for _ in range(ARITY[upper])]

    return " ".join(out)


def _numbers(data: str):
    """Walks a normalised path, handing back (command, absolute, numbers)."""
    parts = data.split(" ")
    at = 0

    while at < len(parts):
        command = parts[at]
        at += 1
        upper = command.upper()
        take = 7 if upper == "A" else ARITY[upper]
        yield command, command.isupper(), [float(p) for p in parts[at:at + take]]
        at += take


def _arc_points(x0, y0, rx, ry, turn, large, sweep, x1, y1, steps=24):
    """
    Points along an SVG arc.

    Endpoint parameterisation to centre parameterisation, straight out of the
    SVG specification's implementation notes, then sampled. Bounds taken from
    the ends alone miss the whole bulge - a circle measured that way is a
    horizontal line - and bounds taken from the radii round the ends over-
    measure it by a factor of two, which is what made the first attempt at
    this enlarge drawings off the edge of the grid.
    """
    import math

    if rx == 0 or ry == 0 or (x0 == x1 and y0 == y1):
        return [(x1, y1)]

    rx, ry = abs(rx), abs(ry)
    phi = math.radians(turn)
    cos, sin = math.cos(phi), math.sin(phi)

    dx, dy = (x0 - x1) / 2, (y0 - y1) / 2
    x1p, y1p = cos * dx + sin * dy, -sin * dx + cos * dy

    # A radius too small for the distance is scaled up until it fits.
    lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)

    if lam > 1:
        rx, ry = rx * math.sqrt(lam), ry * math.sqrt(lam)

    top = (rx * rx * ry * ry) - (rx * rx * y1p * y1p) - (ry * ry * x1p * x1p)
    bottom = (rx * rx * y1p * y1p) + (ry * ry * x1p * x1p)
    factor = math.sqrt(max(0.0, top / bottom)) if bottom else 0.0

    if large == sweep:
        factor = -factor

    cxp, cyp = factor * rx * y1p / ry, -factor * ry * x1p / rx
    cx = cos * cxp - sin * cyp + (x0 + x1) / 2
    cy = sin * cxp + cos * cyp + (y0 + y1) / 2

    def angle(ux, uy, vx, vy):
        dot = ux * vx + uy * vy
        size = math.hypot(ux, uy) * math.hypot(vx, vy)
        found = math.acos(max(-1.0, min(1.0, dot / size))) if size else 0.0

        return -found if ux * vy - uy * vx < 0 else found

    start = angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    sweep_angle = angle(
        (x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)

    if not sweep and sweep_angle > 0:
        sweep_angle -= 2 * math.pi
    elif sweep and sweep_angle < 0:
        sweep_angle += 2 * math.pi

    points = []

    for i in range(1, steps + 1):
        t = start + sweep_angle * (i / steps)
        points.append((
            cos * rx * math.cos(t) - sin * ry * math.sin(t) + cx,
            sin * rx * math.cos(t) + cos * ry * math.sin(t) + cy,
        ))

    return points


def _curve_points(points, steps=16):
    """Points along a Bezier of any order, by de Casteljau."""
    out = []

    for i in range(1, steps + 1):
        t = i / steps
        work = list(points)

        while len(work) > 1:
            work = [
                (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
                for a, b in zip(work, work[1:])
            ]

        out.append(work[0])

    return out


def extent(data: str) -> tuple[float, float, float, float]:
    """
    The box the ink actually fits inside.

    Curves and arcs are walked rather than guessed at: a drawing is enlarged
    to fill its grid from this number, and a guess in either direction either
    leaves the drawing small or pushes it off the edge.
    """
    x = y = 0.0
    start_x = start_y = 0.0
    last_control = None
    left = top = 1e9
    right = bottom = -1e9

    def touch(px: float, py: float) -> None:
        nonlocal left, top, right, bottom
        left, right = min(left, px), max(right, px)
        top, bottom = min(top, py), max(bottom, py)

    for command, absolute, numbers in _numbers(data):
        upper = command.upper()

        def point(i):
            return (
                numbers[i] if absolute else x + numbers[i],
                numbers[i + 1] if absolute else y + numbers[i + 1],
            )

        if upper == "Z":
            x, y = start_x, start_y
            last_control = None
            continue

        if upper == "H":
            x = numbers[0] if absolute else x + numbers[0]
            touch(x, y)
            last_control = None
            continue

        if upper == "V":
            y = numbers[0] if absolute else y + numbers[0]
            touch(x, y)
            last_control = None
            continue

        if upper == "A":
            end = point(5)
            touch(x, y)

            for px, py in _arc_points(
                    x, y, numbers[0], numbers[1], numbers[2],
                    int(numbers[3]), int(numbers[4]), end[0], end[1]):
                touch(px, py)

            x, y = end
            last_control = None
            continue

        if upper in ("C", "S", "Q", "T"):
            if upper == "C":
                c1, c2, end = point(0), point(2), point(4)
                run = [(x, y), c1, c2, end]
                last_control = c2
            elif upper == "S":
                c1 = (2 * x - last_control[0], 2 * y - last_control[1]) if last_control else (x, y)
                c2, end = point(0), point(2)
                run = [(x, y), c1, c2, end]
                last_control = c2
            elif upper == "Q":
                c1, end = point(0), point(2)
                run = [(x, y), c1, end]
                last_control = c1
            else:
                c1 = (2 * x - last_control[0], 2 * y - last_control[1]) if last_control else (x, y)
                end = point(0)
                run = [(x, y), c1, end]
                last_control = c1

            touch(x, y)

            for px, py in _curve_points(run):
                touch(px, py)

            x, y = end
            continue

        # M and L, and the runs of pairs that follow them.
        for i in range(0, len(numbers), 2):
            px, py = point(i)
            touch(px, py)
            x, y = px, py

        if upper == "M":
            start_x, start_y = x, y

        last_control = None

    return left, top, right, bottom


def scale(data: str, factor: float, centre: float) -> str:
    """
    The same drawing, enlarged about the middle of the grid.

    Absolute coordinates move towards or away from the centre; relative ones
    are lengths and only stretch. An arc's radii are lengths too; its rotation
    is not, and a uniform scale leaves it alone.
    """
    out: list[str] = []

    for command, absolute, numbers in _numbers(data):
        out.append(command)
        upper = command.upper()

        if upper == "Z":
            continue

        def at(value: float) -> str:
            return _fmt(((value - centre) * factor) + centre if absolute else value * factor)

        if upper in ("H", "V"):
            out.append(at(numbers[0]))
            continue

        if upper == "A":
            out += [
                _fmt(numbers[0] * factor), _fmt(numbers[1] * factor), _fmt(numbers[2]),
                str(int(numbers[3])), str(int(numbers[4])),
                at(numbers[5]), at(numbers[6]),
            ]
            continue

        out += [at(n) for n in numbers]

    return " ".join(out)
