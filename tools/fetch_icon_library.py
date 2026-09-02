"""Lays the whole free Hugeicons set out on disk, sorted by category.

This is the picking table, not the program's icon set. The program carries
only the drawings named in ``tools/fetch_icons.py``; this puts every free one
somewhere they can be looked at, so choosing the next one is browsing rather
than guessing at names.

Two sources, because neither has both halves:

* the drawings come from Iconify's copy of the free set - the same place
  ``fetch_icons.py`` fetches from, so a drawing chosen here arrives in the
  program exactly as it looked while being chosen;
* the categories and the search words come from hugeicons.com's own
  catalogue, which is what the site's pages are built from.

    python tools/fetch_icon_library.py

Writes ``assets/icons-free/<category>/<name>.svg`` and an ``index.html``
beside them that shows the lot with a search box. The folder is not
committed: it is five thousand files to pick from, and what survives the
picking is named in ``fetch_icons.py`` instead.

Run it again once the folder has been thinned and it leaves the drawings
alone and rebuilds the page from what is left - which is the point of
thinning by hand: the page becomes the shortlist. Drawings the program
already carries are marked, and "used" finds them.

Licence: Hugeicons free set, MIT. See THIRD-PARTY.md.
"""

from __future__ import annotations

import html
import json
import pathlib
import re
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / "assets" / "icons-free"

# The whole set in one file, rather than five thousand requests.
ICONIFY = "https://raw.githubusercontent.com/iconify/icon-sets/master/json/hugeicons.json"
CATALOGUE = "https://hugeicons.com/api/icons"

AGENT = {"User-Agent": "master-control-dock/icon-library"}


def get(url: str) -> dict:
    request = urllib.request.Request(url, headers=AGENT)

    with urllib.request.urlopen(request, timeout=120) as response:
        return json.loads(response.read().decode("utf-8"))


def title(category: str) -> str:
    """A category's name as a person reads it, from the id the site uses."""
    words = category.replace("-", " ")
    return words[:1].upper() + words[1:]


# A colour rather than currentColor: a file explorer's thumbnail and a browser
# tab both paint an unset currentColor black on black often enough.
SVG = (
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" '
    'width="48" height="48" color="#1c1f24">{body}</svg>\n'
)


def main() -> None:
    print("fetching the free set...")
    icons = get(ICONIFY)["icons"]

    print("fetching the categories...")
    catalogue = get(CATALOGUE)["icons"]

    where = {entry["name"]: entry.get("category") or "uncategorised" for entry in catalogue}
    words = {entry["name"]: entry.get("tags") or "" for entry in catalogue}

    # A folder that is already there has been thinned by hand, and thinning
    # by hand is the whole point: what is left is the shortlist, and the
    # page is rebuilt to show it rather than the five thousand again.
    thinned = OUT.exists()

    if thinned:
        kept = {path.stem for path in OUT.rglob("*.svg")}
        print(f"{OUT} is already there; keeping the {len(kept)} left in it")
    else:
        kept = set(icons)

    shelves: dict[str, list[tuple[str, str, str]]] = {}

    for name, icon in sorted(icons.items()):
        if name not in kept:
            continue

        category = where.get(name, "uncategorised")
        shelves.setdefault(category, []).append((name, icon["body"], words.get(name, "")))

    if not thinned:
        for category, drawings in sorted(shelves.items()):
            shelf = OUT / category
            shelf.mkdir(parents=True, exist_ok=True)

            for name, body, _ in drawings:
                (shelf / f"{name}.svg").write_text(SVG.format(body=body), encoding="utf-8")

    sheet(shelves)

    total = sum(len(d) for d in shelves.values())
    print(f"{total} icons in {len(shelves)} categories -> {OUT}")
    print(f"open {OUT / 'index.html'} to look through them")


def carried() -> set[str]:
    """The drawings the program already has, so they are not picked twice."""
    source = (ROOT / "tools" / "fetch_icons.py").read_text(encoding="utf-8")
    body = source.split("ICONS: dict[str, str] = {", 1)[-1].split(chr(10) + "}", 1)[0]

    return set(re.findall(r'"[^"]+":\s*"([^"]+)"', body))


def sheet(shelves: dict[str, list[tuple[str, str, str]]]) -> None:
    """One page showing every drawing, with a box that filters by name or word."""
    parts: list[str] = []
    already = carried()
    counts = {c: len(d) for c, d in shelves.items()}

    for category in sorted(shelves):
        parts.append(
            f'<section data-category="{html.escape(category)}">'
            f'<h2>{html.escape(title(category))} <small>{counts[category]}</small></h2><div class="grid">'
        )

        for name, body, tags in sorted(shelves[category]):
            used = name in already
            hunt = html.escape(
                f"{name} {tags} {category}{' used' if used else ''}".lower(), quote=True)

            parts.append(
                f'<figure data-hunt="{hunt}"{" class=used" if used else ""}>'
                f'<svg viewBox="0 0 24 24" width="30" height="30">{body}</svg>'
                f"<figcaption>{html.escape(name)}</figcaption></figure>"
            )

        parts.append("</div></section>")

    page = PAGE.replace("{{TOTAL}}", str(sum(counts.values())))
    page = page.replace("{{SHELVES}}", str(len(counts)))
    page = page.replace("{{BODY}}", "".join(parts))

    (OUT / "index.html").write_text(page, encoding="utf-8")


PAGE = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Hugeicons free set</title>
<style>
:root{color-scheme:dark light;--bg:#1c1f24;--card:#23272d;--line:#ffffff1f;--tx:#e9ecf1;--tx2:#98a0ac}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--tx);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
header{position:sticky;top:0;background:var(--bg);border-bottom:1px solid var(--line);padding:14px 20px;z-index:2}
h1{font-size:16px;margin:0 0 8px;font-weight:600}
h1 small{color:var(--tx2);font-weight:400;margin-left:8px}
input{width:100%;max-width:420px;padding:9px 12px;border-radius:8px;border:1px solid var(--line);
background:var(--card);color:var(--tx);font:inherit}
main{padding:8px 20px 60px}
h2{font-size:12px;letter-spacing:.16em;text-transform:uppercase;color:var(--tx2);
margin:26px 0 10px;font-weight:500}
h2 small{letter-spacing:0;text-transform:none;opacity:.7;margin-left:6px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(104px,1fr));gap:6px}
figure{margin:0;padding:10px 4px 8px;background:var(--card);border:1px solid var(--line);
border-radius:8px;display:flex;flex-direction:column;align-items:center;gap:7px;text-align:center}
figure svg{fill:none;stroke:currentColor;stroke-width:1.5;stroke-linecap:round;stroke-linejoin:round}
figcaption{font-size:10px;color:var(--tx2);word-break:break-word;line-height:1.3}
figure.used{border-color:#4ade8066;background:#4ade800f}
figure.used figcaption{color:#8fe0ad}
section[hidden],figure[hidden]{display:none}
</style></head><body>
<header>
<h1>Hugeicons free set <small>{{TOTAL}} drawings in {{SHELVES}} categories &middot; MIT</small></h1>
<input id="hunt" type="search" placeholder="Search by name, word or category - or &quot;used&quot; for the ones already in the program" autocomplete="off">
</header>
<main>{{BODY}}</main>
<script>
const box = document.getElementById('hunt');
const shelves = [...document.querySelectorAll('section')];
box.addEventListener('input', () => {
  const wanted = box.value.trim().toLowerCase();
  for (const shelf of shelves) {
    let shown = 0;
    for (const one of shelf.querySelectorAll('figure')) {
      const hit = !wanted || one.dataset.hunt.includes(wanted);
      one.hidden = !hit;
      if (hit) shown++;
    }
    shelf.hidden = shown === 0;
  }
});
</script></body></html>
"""


if __name__ == "__main__":
    main()
