# Checks that have to be done by hand

Run this list on a machine with at least two monitors before every release, and
after any change to `MCD.Core/Monitors`, `MCD.Interop/AppBar` or
`MCD.App/Dock`. It is short on purpose: everything that could be automated has
been, and what is left is what no test harness can see.

Start the program, leave it running, and work down the list. After **every**
step the answer to all four questions must be yes:

1. Is there a dock on every attached monitor?
2. Does each one show a live CPU figure?
3. Did the desktop work area shrink by the dock's thickness, and no more?
4. Is `settings.commit` absent from the log unless a monitor genuinely appeared
   or disappeared?

The log is at `%LOCALAPPDATA%\MCD\logs\mcd.log`.

## Topology

| # | Do this | Watch for |
|---|---|---|
| 1 | **Win+P → Duplicate → Extend → Second screen only → PC screen only → Extend** | The literal reproduction of microsoft/PowerToys#49604. Docks must come back on every screen with their widgets |
| 2 | Unplug an external monitor, wait, plug it back into **the same** port | Its dock returns with the same settings; `monitors.note` says nothing, because the stable id matched |
| 3 | Unplug it and plug it into a **different** port | Its dock returns; the log shows a re-association note and `previousStableIds` gains an entry |
| 4 | Turn a monitor off at the power button and on again | No duplicate entries appear in `config.json` |
| 5 | Settings → System → Display: change which screen is primary | Docks stay put; nothing is written |
| 6 | Settings → System → Display: drag the monitors into a different arrangement | Docks follow their own screens |
| 7 | Change the scale of one monitor (100% → 150%) | That dock's thickness changes and the others do not |
| 8 | Close the laptop lid, open it | Docks return |
| 9 | Sleep the machine, wake it | Docks return |
| 10 | Lock (Win+L), unlock | Docks return |
| 11 | Connect over RDP with `/multimon`, disconnect | Docks return on the local screens; entries for the RDP monitors stay in `config.json` and are not shown |

## Shell and other programs

| # | Do this | Watch for |
|---|---|---|
| 12 | `taskkill /f /im explorer.exe`, then start Explorer again | Docks re-register and the work area is right |
| 13 | Set the taskbar to auto-hide on the same edge as a dock, then choose "Hides itself" for that dock | The dock stays visible and the settings say why, rather than vanishing |
| 14 | Move the taskbar to the left edge | Docks on the left move clear of it |
| 15 | Run a full-screen game | Docks go behind it and come back afterwards |
| 16 | Enable Energy Saver | The acrylic falls back to a solid colour that still reads in both themes |
| 17 | Exit from the settings, then check the work area | It is back to full size. This one matters more than any other: a leaked AppBar survives a reboot |

## Sensors

| # | Do this | Watch for |
|---|---|---|
| 18 | Compare the CPU figure with Task Manager | Within a percent or two |
| 19 | Compare GPU and drive temperatures with HWiNFO | Within a degree |
| 20 | Start HWiNFO **after** the dock | CPU package temperature appears on its own within 30 s |
| 20a | Switch LibreHardwareMonitor on with its web server running | Processor, motherboard and graphics temperatures appear within 30 s |
| 20b | Give the Sensors page an address nothing is serving | It says LibreHardwareMonitor is not answering, and says how to start it |
| 20c | With both a graphics driver and an external source running | The graphics temperature appears once on the bar, not twice; both entries are still offered on the Sensors page |
| 21 | Close HWiNFO while the dock runs | The value greys out and then reads "source unavailable"; no exception in the log |
| 22 | Leave free HWiNFO running past 12 hours | The message names the 12-hour shared-memory limit specifically, not a generic failure |

## Hiding

| # | Do this | Watch for |
|---|---|---|
| 23 | Set a dock to "Hides itself", then maximise a window on that screen | The window covers the whole screen, edge to edge: a hidden dock reserves nothing |
| 24 | Push the pointer against that edge | The bar slides out within a fifth of a second, and back a moment after the pointer leaves |
| 25 | Do it on a screen that has another screen directly against that edge | Nothing of the bar appears on the neighbouring screen, and the pointer works normally along that whole border |
| 26 | Open a widget's panel, then move the pointer onto the panel | The bar stays out while the panel is open |

## Widgets

| # | Do this | Watch for |
|---|---|---|
| 27 | Docks → pick a screen → add a widget to each region | It appears on that bar at once, at the right end of it |
| 28 | Drag a widget along the bar itself, and into another region of it | A marker shows where it will land; on release it is there, and `config.json` agrees |
| 28a | Drag a launcher button on the bar | It moves the widget rather than starting the program; a click without moving still starts it |
| 28b | Hover any reading on the bar | A light fill, and a tooltip below it with the value on the first line and the hardware on the second |
| 28c | Do 28b on a secondary monitor | The tooltip appears there too - it needs to be told which window it belongs to |
| 29 | Open the temperature widget's row, choose "the readings I choose", tick two | Both are drawn on the bar side by side, each with its own hardware's icon |
| 30 | Untick every reading in the load widget | The widget disappears from the bar and its row still says so |
| 31 | Edit `config.json` by hand to add a widget with an invented type, then reopen the settings | The row shows the raw type and says it is unknown; moving other widgets does not delete it |
| 32 | Appearance → Solid, then Translucent | The bars change at once, with no restart |
| 32a | Switch a dock between Default and Compact | The full size writes each reading's name under its figure; the compact one drops the names, as PowerToys does |
| 33 | Appearance → Dark while Windows is light | Only the bars and this window change |
| 33a | Watch the strip under "The bar" while a widget is added or moved | It shows the change at once, with live figures, at the dock's real thickness |
| 33b | Remove a widget, then press Undo | It comes back in the same band at the same position |
| 33c | Change a dock, switch to another screen in the picker, then press Undo | The picker returns to the screen that was changed |

## Launcher

| # | Do this | Watch for |
|---|---|---|
| 34 | Settings → Launcher: add a program, then a folder, then a web address | Each appears on every dock at once, without restarting. Programs and folders carry Explorer's icon; the address shows its first letter |
| 35 | Click each one on the dock | The program starts, the folder opens, the address opens in the browser. `launcher.started` in the log |
| 36 | Add something on a drive that is not connected, then reopen the settings | The dock still appears, promptly; the item draws a letter. This is what the icon thread is for |
| 37 | Point an item at a program, then uninstall that program and click it | `launcher.failed` in the log, and nothing on screen. The dock must not put up a dialog |
