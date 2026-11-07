"""Demo clips of Master Control Dock, recorded from the program's demo mode.

    python tools/demo/mcd.py hero            the bar, into tools/demo/out/hero.mp4
    python tools/demo/mcd.py settings        the settings window
    python tools/demo/mcd.py all             every clip
    python tools/demo/mcd.py shots           stills without the cursor, into tools/demo/out/shots/mcd
    python tools/demo/mcd.py pose [seconds]  stage and program only, for measuring

MCD_DEMO=1 gives the program one 1600x900 screen in the primary screen's top
left corner, readings that rest at 21 and take turns at 100, and a settings
folder of its own; nothing it does reaches the real settings, volume or
screens. A running copy is closed first, the way tools/close-mcd.ps1 closes it,
and started again at the end.

The coordinates below are on that 1600x900 screen, measured at 125 % scaling
on the layout in mcd-layout.json. Another scale or layout moves them.
"""
import ctypes
import os
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from democap import Clip, place_window, wait_for_line, window_of

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent / "out"
EXE = ROOT / "src/MCD.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/MasterControlDock.exe"
LAYOUT = Path(__file__).resolve().parent / "mcd-layout.json"
W, H = 1600, 900


def running() -> list[str]:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-c",
         "Get-Process MasterControlDock -ErrorAction SilentlyContinue | % Path"],
        capture_output=True, text=True).stdout
    return [line.strip() for line in out.splitlines() if line.strip()]


def stop(exe: str) -> None:
    """Through --exit, never a kill: a killed bar leaves its strip reserved for a second."""
    subprocess.run([exe, "--exit"])
    for _ in range(50):
        if not running():
            return
        time.sleep(0.2)
    raise RuntimeError("Master Control Dock did not stop")


def start_demo() -> Path:
    """Starts the program twice: once to learn the screen, once on the demo layout.

    A saved bar is matched to a screen by that screen's own identity, which only
    the program can read; a layout written beforehand is a different screen to
    it, and the standard bar is put up instead. So the first start writes the
    identity down, and the widgets of mcd-layout.json are put into it.
    """
    data = Path(tempfile.mkdtemp(prefix="mcd-demo-"))
    (data / "welcomed").touch()   # no first-run hint across the bar
    env = dict(os.environ, MCD_DEMO="1", MCD_DATA_DIR=str(data), MCD_LANG="en-US")

    subprocess.Popen([str(EXE)], env=env)
    wait_for_line(data / "logs/mcd.log", "dock.manager docks=1")
    stop(str(EXE))

    config = json.loads((data / "config.json").read_text(encoding="utf-8-sig"))
    layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
    config["monitors"][0].update(layout["monitors"][0])
    (data / "config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    (data / "logs/mcd.log").unlink()

    subprocess.Popen([str(EXE)], env=env)
    wait_for_line(data / "logs/mcd.log", "dock.manager docks=1")
    time.sleep(2.5)   # the readings' first rounds, so the bar is not caught half-drawn
    lift_bar()
    return data


def lift_bar() -> None:
    """Puts the bar back over the stage. Both are topmost, and now and then the
    bar came up under the stage: a take without a bar, and presses on it that
    landed on the sky. Then checks the top of the screen is not sky."""
    import ctypes.wintypes as wt
    from democap import user32
    bar = window_of("MasterControlDock.exe", min_width=W, topmost=True)
    user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint]
    user32.SetWindowPos(bar, wt.HWND(-1), 0, 0, 0, 0, 0x0013)   # HWND_TOPMOST, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
    time.sleep(0.5)
    probe = OUT / "bar-probe.png"
    Clip(0, 0, W, 40, probe).snap(probe)
    from PIL import Image
    r, g, b = Image.open(probe).convert("RGB").resize((1, 1), Image.BOX).getpixel((0, 0))
    probe.unlink()
    if r + g + b < 3 * 30:        # the stage's sky is (14, 18, 38) at the top; the bar is lighter
        raise RuntimeError(f"the bar is not on the stage (top of the screen reads {r, g, b})")


def round_start(lead: float) -> None:
    """Waits until `lead` seconds before the processor's turn.

    The demo readings take eight-second turns, processor, graphics, memory, the
    rest, so a round starts every thirty-two seconds of the clock (Demo.cs).
    """
    time.sleep((32 - (time.time() + lead) % 32) % 32)


# --- the clips -----------------------------------------------------------------
# Points on the 1600x900 screen of mcd-layout.json.
CPU = (400, 25)
SUN, SPEAKER, VOLUME, MIC = (1278, 25), (1336, 25), (1387, 25), (1446, 25)
BARE = (530, 25)                                     # bar with no widget: a press opens the settings
VOLUME_21, VOLUME_100 = (1287, 93), (1462, 93)       # the volume slider's thumb
SUN_21, SUN_100 = (1183, 93), (1358, 93)             # the brightness slider's thumb
REST = (800, 500)

# The settings window, placed whole on the stage under the bar.
SETTINGS_AT = (202, 62)
WIDGETS, APPEARANCE = (298, 195), (298, 245)
LAYOUTS_TAB = (1243, 357)
MEMORY_LOAD = (665, 770)                             # its picture in the catalog
GAP = (245, 25)                                      # free cells between the pinned apps and the processor
LAYOUT_LIST = (952, 650)
MINIMAL, MONITORING = (952, 578), (952, 712)        # "Apply to this bar"
WORK = (1010, 797)                                   # the same, list scrolled; clear of the "Done" note
LIGHT, MATCH_WINDOWS = (953, 360), (705, 360)
ACCENT, NEUTRAL = (1140, 587), (767, 587)


def slide(c: Clip, low: tuple[int, int], high: tuple[int, int]) -> None:
    """Thumb from 21 to 100 and back: the slider follows the hand from where
    it was taken, so coming back to the same point is coming back to 21."""
    c.wait(0.4)                 # the slider's opening animation: a press during it was lost
    c.move(*low, 0.35)
    c.down()
    c.move(*high, 0.8)
    c.wait(0.25)
    c.move(*low, 0.8)
    c.up()


def hero(c: Clip) -> None:
    """Every feature of the bar in one pass.

    Readings rest at 21 and take turns going 75 (amber), 100 (red, and the
    temperature wears its warning), 75; the processor's last hour; the volume
    21 -> 100 -> 21; the speaker and the microphone off and on again with one
    click each; the brightness of every screen 21 -> 100 -> 21.
    """
    # A slider must be shut whenever a pair reaches 100 or leaves it (Demo.Slot):
    # the processor does at 2 and 3, the graphics at 10 and 11.
    c.wait(3.2)                 # the processor: 75 at 1, 100 at 2, 75 at 3
    c.move(*CPU, 0.6)
    c.click()
    c.wait(1.4)                 # its hour
    c.move(*VOLUME, 0.6)
    c.click()
    c.wait(0.3)
    slide(c, VOLUME_21, VOLUME_100)
    c.wait(0.3)
    c.move(*SPEAKER, 0.45)
    c.click()                   # silent: the speaker turns red
    c.wait(0.6)
    c.click()
    c.wait(0.3)
    c.move(*MIC, 0.4)
    c.click()                   # the microphone off; the graphics go up meanwhile
    c.wait(0.6)
    c.click()
    c.wait(0.3)
    c.move(*SUN, 0.4)
    c.click()
    c.wait(0.3)
    slide(c, SUN_21, SUN_100)
    c.wait(0.3)
    c.move(*REST, 0.6)
    c.click()
    c.wait(0.6)


def settings(c: Clip) -> None:
    """The settings window, whole: a widget dragged from the catalog onto the
    bar, three saved layouts applied in turn, the light theme and the accent
    colour on the readings, and back."""
    c.wait(1.0)                 # the Bars page
    c.move(*WIDGETS, 0.6)
    c.click()
    c.wait(1.0)
    c.move(*MEMORY_LOAD, 0.7)
    c.down()
    c.move(MEMORY_LOAD[0] + 15, MEMORY_LOAD[1] - 30, 0.25)
    c.move(400, 300, 0.5)
    c.move(*GAP, 0.6)
    c.wait(0.5)                 # the bar shows its cells
    c.up()
    c.wait(1.5)
    c.move(*LAYOUTS_TAB, 0.7)
    c.click()
    c.wait(0.8)
    # Applying a layout rebuilds the list at the top, so Work, the last row,
    # is scrolled to each time it is wanted.
    c.move(*MINIMAL, 0.6)
    c.click()
    c.wait(1.8)
    c.move(*LAYOUT_LIST, 0.4)
    c.wheel(-3, 0.5)
    c.wait(0.4)
    c.move(*WORK, 0.5)
    c.click()
    c.wait(1.8)
    c.move(*MONITORING, 0.6)
    c.click()
    c.wait(1.8)
    c.move(*APPEARANCE, 0.7)
    c.click()
    c.wait(1.0)
    c.move(*LIGHT, 0.6)
    c.click()
    c.wait(1.5)
    c.move(*ACCENT, 0.6)
    c.click()
    c.wait(2.0)                 # the readings on the bar in the accent colour
    c.move(*NEUTRAL, 0.5)
    c.click()
    c.wait(0.8)
    c.move(*MATCH_WINDOWS, 0.5)
    c.click()
    c.wait(1.5)


CLIPS = {"hero": hero, "settings": settings}

OUTSIDE = (1500, 450)                                # sky beside the settings: over the window, a Ctrl+Z tooltip came up


def cpu_colour() -> str:
    """The processor's reading as it is drawn now: "amber", "red" or "plain"."""
    from PIL import ImageGrab
    px = list(ImageGrab.grab((340, 0, 520, 40)).convert("RGB").getdata())
    if sum(1 for r, g, b in px if r > 200 and g > 170 and b < 100) > 20:
        return "amber"
    if sum(1 for r, g, b in px if r > 200 and g < 110 and b < 110) > 20:
        return "red"
    return "plain"


def history_snap(c: Clip, out: Path) -> None:
    """The processor's hour, opened on its way down from 100: at 75 the flyout
    reads 75 and no regrow is left to shut it. The bar is watched, not the clock."""
    from PIL import Image
    for _ in range(4):
        while cpu_colour() != "red":
            time.sleep(0.03)
        while cpu_colour() != "amber":
            time.sleep(0.03)
        c.place(*CPU)
        c.click()
        c.place(*REST)
        c.wait(0.45)
        c.snap(out)
        im = Image.open(out).convert("RGB")
        panel = im.crop((300, 60, 700, 260)).resize((1, 1), Image.BOX).getpixel((0, 0))
        c.click()                   # a press beside a flyout shuts it
        if sum(panel) > 3 * 28 and cpu_colour() in ("amber", "plain"):
            return
    raise RuntimeError("the processor's hour did not open at 75")


def calm_snap(c: Clip, out: Path) -> None:
    """A still taken when no reading on the bar is amber or red: the bar is read
    from the still itself, since its turns do not keep to the clock exactly."""
    from PIL import Image
    end = time.time() + 40
    while True:
        c.snap(out)
        strip = Image.open(out).crop((0, 0, W, 48)).convert("RGB")
        warm = sum(1 for r, g, b in strip.getdata() if r > 170 and r - b > 100)
        if warm < 20 or time.time() > end:
            return
        time.sleep(0.4)


def shots() -> None:
    """Stills for the READMEs and the site, into tools/demo/out/shots/mcd: the
    same scenes as the clips, taken from the screen itself without the cursor,
    which sits on empty space before each one so nothing wears a hover."""
    to = OUT / "shots/mcd"
    c = Clip(0, 0, W, H, to / "unused.mp4")
    c.show_stage()
    start_demo()
    try:
        c.place(*CPU)               # the first press after another window was lost: spend it
        c.click()
        c.wait(0.8)
        c.click()
        c.wait(0.8)
        c.place(*REST)
        round_start(lead=0.6)
        t0 = time.time() + 0.6

        def at(s: float) -> None:
            time.sleep(max(0.0, t0 + s - time.time()))

        at(2.5)
        c.snap(to / "bar.png")      # at 100: red, the temperature with its warning
        history_snap(c, to / "history.png")
        lift_bar()                  # the press that shut it landed on the stage
        c.wait(0.6)
        c.place(*VOLUME)
        c.click()
        c.wait(0.4)
        c.move(*VOLUME_21, 0.3)
        c.down()
        c.move(*VOLUME_100, 0.6)
        c.up()
        c.place(*REST)
        c.wait(0.6)
        c.snap(to / "volume.png")   # the volume at 100
        stop(str(EXE))              # a fresh demo is back at 21; a drag back landed anywhere from 13 to 81
        start_demo()

        win = open_settings(c)
        place_window(win, *SETTINGS_AT)
        c.place(*WIDGETS)
        c.click()
        c.place(*OUTSIDE)
        c.wait(1.5)
        calm_snap(c, to / "widgets.png")
        for button in (APPEARANCE, LIGHT, ACCENT):
            c.place(*button)
            c.click()
            c.place(*OUTSIDE)
            c.wait(1.0)
        c.wait(10)                  # the "Done" note with its Undo goes by itself
        calm_snap(c, to / "appearance.png")
    finally:
        c.stop()
        stop(str(EXE))


def open_settings(c: Clip) -> int:
    """A press on bare bar opens the settings; a press can be lost, so it is
    pressed again until the window is there."""
    for _ in range(4):
        lift_bar()                  # a press on the stage can bring it over the bar
        c.place(*BARE)
        c.click()
        end = time.time() + 3
        while time.time() < end:
            try:
                return window_of("MasterControlDock.exe")
            except LookupError:
                time.sleep(0.2)
    raise RuntimeError("the settings window did not open")


def record(name: str) -> None:
    clip = Clip(0, 0, W, H, OUT / f"{name}.mp4")
    clip.show_stage()
    start_demo()
    # The first press on the bar after another window had the foreground was
    # lost - the processor's hour never opened. So the hour is opened and shut
    # before the recording: on bare bar a press opens the settings, and Escape
    # goes to the stage, which has the keyboard.
    clip.place(*CPU)
    clip.click()
    clip.wait(0.8)
    clip.click()
    clip.wait(0.8)
    if name == "settings":
        place_window(open_settings(clip), *SETTINGS_AT)
    clip.place(*REST)
    round_start(lead=1.0 + 0.8)   # the recording's own lead-in is 0.8 s
    try:
        clip.start()
        CLIPS[name](clip)
    finally:
        clip.stop()
        stop(str(EXE))


def main() -> int:
    what = sys.argv[1] if len(sys.argv) > 1 else "hero"
    was = running()
    if was:
        stop(was[0])
    try:
        if what == "pose":
            # Held for a while for measuring by hand: the stage lives as long
            # as this process does.
            clip = Clip(0, 0, W, H, OUT / "pose.mp4")
            clip.show_stage()
            print("demo data:", start_demo(), flush=True)
            time.sleep(float(sys.argv[2]) if len(sys.argv) > 2 else 120)
            clip.stop()
            stop(str(EXE))
            return 0
        if what == "shots":
            shots()
            print("stills in", OUT / "shots/mcd")
            return 0
        for name in (CLIPS if what == "all" else [what]):
            record(name)
            print("recorded", OUT / f"{name}.mp4")
    finally:
        if was and what != "pose":
            subprocess.Popen([was[0]])
    return 0


if __name__ == "__main__":
    sys.exit(main())
