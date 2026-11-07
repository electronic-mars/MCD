# Privacy

Master Control Dock collects nothing, sends nothing, and has no accounts.

The details matter more than the promise, so here they are.

## What stays on your computer

- **Settings**: which screens have a bar, the widgets on each, pinned programs,
  folders and web addresses, saved layouts, theme, language and keys. In
  `%LOCALAPPDATA%\MasterControlDock\config.json`, or, for the Microsoft Store
  version, in the package's own folder under `%LOCALAPPDATA%\Packages`.
- **Icons** of pinned programs, cached beside the settings so the bar does not
  have to ask Windows for them at every start.
- **A log**, kept so that a problem can be explained rather than guessed at. It
  records screen names, device and sensor names, and what the program did with
  them. It does not record what is playing. Read it before attaching it to a bug
  report; you can delete lines first.

The history behind a reading (the last hour of a sensor) is kept in memory only
and is gone when the program closes. Nothing in the settings folder leaves it.

## What leaves your computer

Nothing until you ask for it. The program makes no request on startup or on a
timer unless you switch that on yourself.

- **Check for updates** (About page) asks GitHub for the description of the
  newest release, one small file. If you choose to install it, the installer is
  downloaded from GitHub and checked against a signature before it is run.
  **Update automatically** does the same once a day, and is off until you switch
  it on. GitHub sees what it sees for any download, including your address; the
  program sends no identifier and nothing about your machine. The Microsoft Store
  version has neither: the Store brings updates itself.
- **Pinned web addresses**, **What's new**, **Developer website** and
  **Support the project** open in your browser.

There is no telemetry, no usage statistics, no crash reporting, no license check
and no analytics of any kind.

## What the program reads on your computer

To draw the bar it asks Windows and the hardware for:

- processor, memory, disk, network and graphics load, and the temperatures of
  the parts that report one;
- the volume and mute state of the speakers and microphone, and screen
  brightness;
- what media players report about themselves (the program, the track, whether
  it is playing), to show it on the media widget;
- battery levels of wireless devices that report one; for a HyperX wireless
  headset this means sending its USB receiver the one request that asks for the
  charge, and nothing that changes any of its settings;
- the screens connected, to put a bar on each.

All of it is read to draw the bar and stored nowhere except as described above.

## Local connections

Some readings come from other programs on the same computer, and only if you use
them: LibreHardwareMonitor's web server (by default `http://localhost:8085`),
HWiNFO's shared memory, Master Audio Switcher on `127.0.0.1`, and the program's
own optional sensor service over a named pipe that only this computer can open.
None of these are reachable from the network through Master Control Dock.

## Questions

Anything unclear or anything that looks wrong: open an issue at
https://github.com/electronic-mars/MCD/issues
