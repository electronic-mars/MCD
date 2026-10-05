"""Adds the icons ticked on the panel's Icons page to the program.

    python tools/panel/add_icons.py          # what would be added
    python tools/panel/add_icons.py --apply  # add them

Reads ``assets/icons-selected.json``, adds each icon to ``ICONS`` in
``tools/fetch_icons.py`` under a name made from its Hugeicons name
(``coffee-02`` becomes ``Coffee02``), caches the drawing in
``assets/icons-src`` and regenerates ``IconLibrary.g.cs``. Icons the program
already has are left exactly as they are.
"""

from __future__ import annotations

import io
import json
import pathlib
import re
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SELECTION = ROOT / "assets" / "icons-selected.json"
FETCH = ROOT / "tools" / "fetch_icons.py"
SOURCES = ROOT / "assets" / "icons-src"
CACHE = HERE / "cache" / "hugeicons.json"

SVG = (
    '<svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em" viewBox="0 0 24 24">'
    "{body}</svg>"
)


def pascal(name: str) -> str:
    return "".join(part[:1].upper() + part[1:] for part in name.split("-"))


def carried() -> dict[str, str]:
    source = FETCH.read_text(encoding="utf-8-sig")
    block = source.split("ICONS: dict[str, str] = {", 1)[1].split("\n}", 1)[0]
    return dict(re.findall(r'"(\w+)":\s*"([^"]+)"', block))


def main() -> None:
    apply = "--apply" in sys.argv

    if not SELECTION.exists():
        sys.exit("nothing ticked yet: assets/icons-selected.json does not exist")

    chosen = json.loads(SELECTION.read_text(encoding="utf-8"))["selected"]
    have = carried()
    taken = set(have)
    already = set(have.values())

    if not CACHE.exists():
        sys.exit("the icon set is not cached: run python tools/panel/build.py first")

    iconify = json.loads(CACHE.read_text(encoding="utf-8"))["icons"]

    added: list[tuple[str, str]] = []

    for huge in chosen:
        if huge in already:
            continue

        if huge not in iconify:
            print(f"skipped {huge}: not in the set")
            continue

        name = pascal(huge)

        # A name that is taken by another drawing - Mouse is "mouse-22" in
        # the program, and the set also has a plain "mouse" - gets a suffix
        # rather than replacing what somebody may already have chosen.
        while name in taken:
            name += "Alt"

        taken.add(name)
        added.append((name, huge))

    print(f"{len(chosen)} ticked, {len(chosen) - len(added)} already in the program or not available, "
          f"{len(added)} to add")

    for name, huge in added[:40]:
        print(f"  {name:32} <- {huge}")

    if len(added) > 40:
        print(f"  ... and {len(added) - 40} more")

    if not apply or not added:
        if not apply:
            print("\nrun with --apply to add them")
        return

    source = io.open(FETCH, encoding="utf-8-sig", newline="").read()
    newline = "\r\n" if "\r\n" in source else "\n"
    text = source.replace("\r\n", "\n")

    block = "    # Chosen on the control panel (tools/panel), from the whole free set.\n"
    block += "".join(f'    "{name}": "{huge}",\n' for name, huge in added)

    end = text.index("\n}", text.index("ICONS: dict[str, str] = {"))
    text = text[: end + 1] + block + text[end + 1:]
    io.open(FETCH, "w", encoding="utf-8-sig", newline="").write(text.replace("\n", newline))

    SOURCES.mkdir(parents=True, exist_ok=True)

    for _, huge in added:
        (SOURCES / f"{huge}.svg").write_text(SVG.format(body=iconify[huge]["body"]), encoding="utf-8")

    sys.path.insert(0, str(HERE))
    import groups

    print(f"{groups.write_program_groups()} icons placed in tools/icon-groups.json")

    subprocess.run([sys.executable, str(FETCH)], cwd=ROOT, check=True)
    print(f"\nadded {len(added)}; IconLibrary.g.cs regenerated")


if __name__ == "__main__":
    main()
