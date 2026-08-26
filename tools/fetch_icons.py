"""Downloads the icon set and turns it into a C# lookup.

The icons are Hugeicons, the same free set Master Audio Switcher uses, fetched
through Iconify. They are drawn as strokes on a 24 by 24 grid, which is why the
dock draws them with a Path and a stroke rather than with PathIcon, which fills.

The SVG sources are kept in assets/icons-src so the build needs no network and
so a change to the set is a diff rather than a mystery. Re-run after editing
ICONS below:

    python tools/fetch_icons.py

Licence: Hugeicons free set, MIT. See THIRD-PARTY.md.
"""

from __future__ import annotations

import pathlib
import re
import sys
import urllib.request

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from svgpath import normalise

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCES = ROOT / "assets" / "icons-src"
GENERATED = ROOT / "src" / "MCD.App" / "Widgets" / "IconLibrary.g.cs"

API = "https://api.iconify.design/hugeicons/{name}.svg"

# name in the program -> icon in the set.
# The first six are what the built-in widgets use; the rest are there so that
# someone can pick something else.
ICONS: dict[str, str] = {
    "Cpu": "cpu",
    "Gpu": "gpu",
    "Memory": "ram-memory",
    "Disk": "hard-drive",
    "Network": "internet",
    "Temperature": "temperature",
    "Brush": "thermometer",
    "Fan": "fan-01",
    "Battery": "battery-medium-01",
    "Power": "power",
    "Computer": "computer",
    "Activity": "activity-01",
    "Pulse": "activity-03",
    "Chart": "chart-01",
    "ChartUp": "chart-up",
    "PieChart": "pie-chart",
    "BarChart": "bar-chart",
    "Speedometer": "dashboard-speed-01",
    "Gauge": "dashboard-speed-02",
    "Clock": "clock-01",
    "Wifi": "wifi-02",
    "Download": "download-01",
    "ArrowUp": "arrow-up-01",
    "Previous": "previous",
    "Play": "play",
    "Pause": "pause",
    "Next": "next",
    "Music": "music-note-01",
    "Spacer": "arrow-horizontal",
    "ArrowDown": "arrow-down-01",
    "Folder": "folder-01",
    "Rocket": "rocket-01",
    "Star": "star",
    "Shield": "shield-01",
    "Network2": "neural-network",
    "CpuCharge": "cpu-charge",
}


def fetch(name: str) -> str:
    """The SVG source, from disk if it is already there."""
    cached = SOURCES / f"{name}.svg"

    if cached.exists():
        return cached.read_text(encoding="utf-8")

    # Iconify refuses the default urllib user agent with 403.
    request = urllib.request.Request(
        API.format(name=name),
        headers={"User-Agent": "master-control-dock/icon-fetch"},
    )

    with urllib.request.urlopen(request, timeout=30) as response:
        svg = response.read().decode("utf-8")

    SOURCES.mkdir(parents=True, exist_ok=True)
    cached.write_text(svg, encoding="utf-8")
    return svg


def _attrs(tag: str) -> dict[str, str]:
    return dict(re.findall(r'([a-zA-Z-]+)="([^"]*)"', tag))


def _shapes(svg: str) -> list[str]:
    """
    Every drawn element, as path data.

    Not every icon is made of paths. Several are drawn with <circle>,
    <ellipse> or <rect>, and a reader that only looks for d="..." throws those
    away without a word - which is how the network icon arrived as a single
    horizontal stroke where a globe should have been.
    """
    found: list[str] = []

    for match in re.finditer(r"<(path|circle|ellipse|rect|line|polyline|polygon)\b([^>]*)>", svg):
        kind, body = match.group(1), match.group(2)
        a = _attrs(body)

        if kind == "path":
            found.append(a["d"])

        elif kind in ("circle", "ellipse"):
            cx, cy = float(a.get("cx", 0)), float(a.get("cy", 0))
            rx = float(a.get("rx", a.get("r", 0)))
            ry = float(a.get("ry", a.get("r", 0)))

            # Two half-turns: one arc cannot describe a whole ellipse, because
            # its start and end points would coincide.
            found.append(
                f"M {cx - rx} {cy} A {rx} {ry} 0 1 0 {cx + rx} {cy} "
                f"A {rx} {ry} 0 1 0 {cx - rx} {cy} Z"
            )

        elif kind == "rect":
            x, y = float(a.get("x", 0)), float(a.get("y", 0))
            w, h = float(a["width"]), float(a["height"])
            found.append(f"M {x} {y} H {x + w} V {y + h} H {x} Z")

        elif kind == "line":
            found.append(
                f"M {float(a['x1'])} {float(a['y1'])} L {float(a['x2'])} {float(a['y2'])}"
            )

        else:
            points = re.findall(r"[-+0-9.eE]+", a["points"])
            pairs = list(zip(points[0::2], points[1::2]))
            body = " L ".join(f"{x} {y}" for x, y in pairs[1:])
            close = " Z" if kind == "polygon" else ""
            found.append(f"M {pairs[0][0]} {pairs[0][1]} L {body}{close}")

    return found


def combine(svg: str) -> str:
    """
    Every shape in one string.

    All of them are strokes of the same width in the same group, so joining them
    loses nothing. They differ only in their line cap, and round caps throughout
    read better at fifteen pixels than the mixture does.
    """
    shapes = _shapes(svg)

    if not shapes:
        raise ValueError("nothing drawn in the icon")

    # Normalised, because WinUI reads the same commands as SVG but not the
    # compressed spelling Iconify serves. See tools/svgpath.py.
    return " ".join(normalise(s.strip()) for s in shapes)


def _self_check() -> None:
    """
    A handful of shapes that used to come out wrong.

    Every one of these was a real bug, and every one of them was invisible in
    the code and obvious on screen. They cost a rebuild and a squint each time,
    so they are checked here instead.
    """
    # Arc flags run together with the coordinates after them.
    assert normalise("a2 2 0 100-4") == "a 2 2 0 1 0 0 -4"

    # A path's own first moveto is absolute, and stays absolute when joined.
    assert normalise("m5 5l1 1").startswith("M 5 5")

    # A repeated moveto is a lineto.
    assert normalise("M1 1 2 2") == "M 1 1 L 2 2"

    # Circles and ellipses are shapes too, not just paths.
    assert "A 10 10" in combine('<circle cx="12" cy="12" r="10"/>')
    assert "A 4 10" in combine('<ellipse cx="12" cy="12" rx="4" ry="10"/>')


def main() -> None:
    _self_check()

    rows: list[str] = []

    for key, name in ICONS.items():
        data = combine(fetch(name))
        rows.append(f'        ["{key}"] =\n            "{data}",\n')

    GENERATED.write_text(
        '// Generated by tools/fetch_icons.py. Do not edit by hand.\n'
        '// Hugeicons free set, MIT. Sources in assets/icons-src.\n'
        '\n'
        'namespace Mcd.App.Widgets;\n'
        '\n'
        '/// <summary>The icons a widget can be drawn with, as path data on a 24 by 24 grid.</summary>\n'
        'public static class IconLibrary\n'
        '{\n'
        '    /// <summary>The grid the paths are drawn on. Icons are scaled from this.</summary>\n'
        '    public const double Grid = 24;\n'
        '\n'
        '    /// <summary>Every icon, by the name stored in config.json.</summary>\n'
        '    public static IReadOnlyDictionary<string, string> Paths { get; } =\n'
        '        new Dictionary<string, string>(StringComparer.Ordinal)\n'
        '    {\n'
        + "".join(rows)
        + '    };\n'
        '}\n',
        encoding="utf-8",
    )

    print(f"{len(ICONS)} icons -> {GENERATED}")


if __name__ == "__main__":
    main()
