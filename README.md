# Master Control Dock

A dock for the edge of your screen: pinned programs and folders, live readings
for CPU, memory, disks and network, and temperatures for the parts of the
machine that will tell you theirs.

It looks and behaves like the Dock in the PowerToys Command Palette, and it
exists because that one has two problems this one is built around.

## Why this exists

**Widgets go missing on a second monitor.** The PowerToys dock appears on an
additional screen as an empty bar
([microsoft/PowerToys#49604](https://github.com/microsoft/PowerToys/issues/49604),
still open). Their own analysis says why: the per-monitor configuration is filed
under a hardware id, and during a display change that id briefly cannot be read.
The monitor then looks new, and a new secondary monitor is given a configuration
that is disabled with no widgets in it. Because settings are written on every
intermediate reading, that state sticks.

Master Control Dock is built so that this cannot happen:

- A topology reading that has not settled is a *different type* from one that
  has, and only the settled one can be passed to the code that decides what a
  monitor is. Not a rule to remember - the other case does not compile.
- A monitor that matches nothing inherits the primary screen's dock and is
  switched on. The worst outcome of a failed match is a duplicated layout, never
  an empty bar.
- Settings are written only when the set of monitors genuinely changed.
- Entries are never dropped automatically. Forgetting a monitor is a button.
- The listener for display changes lives in the process, not in a dock window,
  so it keeps working when every dock is switched off.

**There is no temperature widget.** Adding one to PowerToys is awkward because
their widgets reuse the Command Palette's command model, which affords an icon,
a title and a subtitle and nothing else. Here a widget brings its own view model
and its own template, so a reading can have a colour that changes at a threshold
and a panel behind it showing every sensor with a minute of history.

## What it can read

Without administrator rights, without a driver, and without any other software:

- CPU, memory, disk and network load
- GPU load, temperature, fan, clocks and power draw on NVIDIA
- NVMe and SATA drive temperatures, including the limits the drive itself declares

Drive temperatures work without administrator rights because of one detail: the
device is opened for *zero* access rather than for reading. The query still
answers, and Windows asks for no privilege. Opening it for `GENERIC_READ`, which
is what full SMART needs, is what would demand elevation.

The processor, the motherboard, its fans, and graphics from AMD and Intel are
**not** available to any program that does not load a kernel driver, and a
Microsoft Store package cannot contain one. Two programs that already have such
a driver will hand their readings over, and Master Control Dock will take them:

**[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)**
— free, open source, no time limit, and the one to recommend. In it, open
Options and switch on **Remote Web Server → Run**. Then in Master Control Dock,
open the settings, go to **Sensors**, switch **LibreHardwareMonitor** on, and
check the address matches the port it is serving on.

**[HWiNFO](https://www.hwinfo.com/)** — if you already run it:

1. In HWiNFO, open Settings and tick **Shared Memory Support**.
2. In Master Control Dock, open the settings, go to **Sensors** and switch
   **HWiNFO** on.

The processor temperature then appears within half a minute, and the Sensors
page says plainly what is happening if it does not. Graphics and drive
temperatures are still read directly rather than through HWiNFO, one step closer
to the source.

HWiNFO is neither bundled nor installed by this program, and everything above
works without it. Note that the free version publishes its shared memory for
twelve hours at a time and then stops; the dock notices, says so, and picks it
up again when HWiNFO is restarted.

## Installing it

```
pwsh tools/install-local.ps1
```

Builds it, copies it to `%LOCALAPPDATA%\Programs\MasterControlDock`, and puts a
shortcut on the desktop and in the Start menu. Nothing is written outside your
own profile and no administrator rights are needed. This is a stand-in for the
real installer, which arrives with the MSIX package.

## Using it

Starting it puts a bar on the edge of every screen. **Right-click an empty part
of a bar** to open the settings, on that screen: which screens have a dock,
which edge it sits on, how tall it is, which icon each reading is drawn with,
what the program has remembered about each monitor, and the Exit button.

There is no icon in the notification area, on purpose. A program whose whole
point is a bar of visible controls should not hide its own controls behind a
chevron in somebody else's bar. Starting the shortcut again while it is already
running opens the settings as well - which is also the way back in if every dock
has been switched off. **Start with Windows** lives on the About page.

**Arranging a dock.** A dock is one run of widgets, and where they sit is
decided by **spacers** - widgets that stretch to take up the free length. One
spacer in the middle splits the bar in two; two of them centre whatever is
between. **To move a widget, drag it on the bar itself** - a marker shows where
it will land, and while a drag is under way the spacers show themselves so they
can be grabbed too. **Right-click the bar** and choose *Add widget* to put a
new one exactly where you clicked.

Settings has a **Docks** page besides: pick a screen at the top, see the bar
itself drawn live at its real thickness, then set which edge it sits on, how
thick it is, and whether it hides itself. The widget list below is where a
widget's own settings live - open its row to set it up.

The two sizes differ in more than thickness: the full size writes each reading's
name under its figure, and the compact one shows the figure alone. Nothing is saved or
discarded - it happens as you do it, and **Undo** puts back the last change. The
temperature widget is where you choose which sensors it shows: either the
hottest reading, whatever it happens to be, or the ones you pick, drawn side by
side on the bar.

**Appearance.** The **Appearance** page sets the theme (light, dark, or the same
as Windows), whether the bars are translucent or solid, and whether the readings
take the accent colour from your Windows settings. Readings past their warning
level keep their warning colour either way, and past the critical level the
figure turns the system's critical red and goes semibold.

**Hiding the bar.** Each dock can either stay visible or hide itself, under
Behaviour on the Docks page. A hidden dock reserves no space at all - windows
maximise over it - and slides back out when the pointer reaches that edge of
that screen. It will not hide on an edge where the taskbar already hides,
because the two would be reaching for the same strip of screen; the setting says
so rather than silently doing nothing.

**Pinning things to the bar.** Settings has a **Launcher** page. Give it a
program, a folder or a web address and it appears on every dock, drawn with the
icon Explorer uses for it. Addresses have no icon of their own and show the
first letter of their name.

**Do not end it from Task Manager.** The dock reserves part of the desktop work
area through the shell, and only a proper shutdown gives that space back;
killing the process leaves the desktop short until the next sign-out. Use Exit
in the settings, or `MasterControlDock.exe --exit`, which asks a running copy
to stop and is what the install script uses.

## Building

Needs the .NET 10 SDK and the Windows 11 SDK (10.0.26100). No Visual Studio
workload is required.

```
dotnet build
dotnet test tests/MCD.Tests/MCD.Tests.csproj
pwsh tools/smoke.ps1
pwsh tools/smoke.ps1 -Lang ru-RU
```

`tools/smoke.ps1` runs the built program for a few seconds and checks the log
for the markers a healthy start leaves behind. It also makes the settings
window as small as it is allowed to be and measures every page: a paragraph
given less than two hundred points has collapsed, and a word wider than the box
it was drawn in has lost letters off both ends. Run it in both languages - a
caption that fits its key in English can be half again as long in Russian, and
the check only ever sees the language it was run in. `docs/manual-checks.md`
lists what has to be checked by hand on a real multi-monitor desk.

## Icons

Hugeicons, free set, MIT. `tools/fetch_icons.py` pulls them through Iconify,
keeps the SVG sources in `assets/icons-src/` and generates the path data the
program draws. Re-run it after changing the list at the top of that file.

## Licence

GPL-3.0-or-later. Parts of the dock window and the AppBar handling are adapted
from [PowerToys](https://github.com/microsoft/PowerToys) under the MIT licence;
see `THIRD-PARTY.md` and `docs/upstream.md`. Not affiliated with Microsoft.
