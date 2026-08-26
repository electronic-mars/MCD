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
