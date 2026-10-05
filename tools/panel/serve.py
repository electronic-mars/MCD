"""The control panel: a small local site about the program, and a page of icons to tick.

    python tools/panel/serve.py [--open] [--port 8765]

Serves ``tools/panel/site`` on 127.0.0.1 only, and keeps one piece of state: the
icons ticked on the Icons page, in ``assets/icons-selected.json``. Whoever adds
them to the program reads that file (``python tools/panel/add_icons.py``).

Nothing here talks to the network except the first ``build.py`` run, which
fetches the icon set once and caches it.
"""

from __future__ import annotations

import argparse
import datetime
import http.server
import json
import pathlib
import re
import socketserver
import sys
import threading
import webbrowser

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
SITE = HERE / "site"
SELECTION = ROOT / "assets" / "icons-selected.json"

NAME = re.compile(r"^[a-z0-9][a-z0-9-]{0,80}$")


def read_selection() -> dict:
    try:
        return json.loads(SELECTION.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"selected": []}


def write_selection(names: list[str]) -> int:
    clean = sorted({n for n in names if isinstance(n, str) and NAME.match(n)})
    SELECTION.parent.mkdir(parents=True, exist_ok=True)

    # Written to a side file and renamed over, so a page closed mid-save never
    # leaves half a list for whoever reads it next.
    side = SELECTION.with_suffix(".tmp")
    side.write_text(
        json.dumps(
            {"updated": datetime.datetime.now().isoformat(timespec="seconds"), "selected": clean},
            ensure_ascii=False,
            indent=2,
        ) + "\n",
        encoding="utf-8",
    )
    side.replace(SELECTION)
    return len(clean)


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(SITE), **kwargs)

    def end_headers(self) -> None:
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def log_message(self, fmt: str, *args) -> None:  # quiet: the page polls nothing
        if "--verbose" in sys.argv:
            super().log_message(fmt, *args)

    def reply(self, status: int, body: dict) -> None:
        raw = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self) -> None:
        if self.path.split("?")[0] == "/api/selection":
            self.reply(200, read_selection())
        else:
            super().do_GET()

    def do_PUT(self) -> None:
        if self.path.split("?")[0] != "/api/selection":
            self.reply(404, {"error": "not found"})
            return

        try:
            length = int(self.headers.get("Content-Length", "0"))
            body = json.loads(self.rfile.read(min(length, 1_000_000)).decode("utf-8"))
            names = body["selected"]

            if not isinstance(names, list):
                raise ValueError("selected must be a list")
        except (ValueError, KeyError, UnicodeDecodeError) as problem:
            self.reply(400, {"error": str(problem)})
            return

        self.reply(200, {"ok": True, "count": write_selection(names)})

    do_POST = do_PUT


def ensure_data() -> None:
    data = SITE / "data"

    if (data / "icons.json").exists() and (data / "elements.json").exists():
        return

    sys.path.insert(0, str(HERE))
    import build

    build.main()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--open", action="store_true", help="open the page in the browser")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    ensure_data()

    socketserver.ThreadingTCPServer.allow_reuse_address = True
    server = None

    for port in range(args.port, args.port + 10):
        try:
            server = socketserver.ThreadingTCPServer(("127.0.0.1", port), Handler)
            break
        except OSError:
            continue

    if server is None:
        sys.exit(f"ports {args.port}-{args.port + 9} are all busy")

    url = f"http://127.0.0.1:{server.server_address[1]}/"
    print(f"panel: {url}   (Ctrl+C to stop)")
    print(f"ticked icons are kept in {SELECTION}")

    if args.open:
        threading.Timer(0.6, lambda: webbrowser.open(url)).start()

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
