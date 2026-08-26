# Borrowed from PowerToys

Master Control Dock adapts parts of [microsoft/PowerToys](https://github.com/microsoft/PowerToys),
MIT licensed. This table exists so that upstream can be diffed later: PowerToys
keeps fixing bugs in the code we started from, and without a record of which
file came from where those fixes are invisible to us.

Check this table once per release. `git log --oneline <upstream-path>` in a
PowerToys checkout shows what has changed since the commit named here.

| Our file | Upstream file | Commit | What we changed |
|---|---|---|---|
| `src/MCD.Interop/AppBar/AppBarHost.cs` | `src/modules/cmdpal/Microsoft.CmdPal.UI/Dock/DockWindow.xaml.cs` | main @ 2026-08 | Extracted into a standalone state machine with a process-wide registration counter; no settings or monitor code |
| `src/MCD.Interop/Windowing/WindowFrame.cs` | same | main @ 2026-08 | Extracted to static helpers over a raw HWND; no WinUIEx dependency |
| `src/MCD.App/Dock/DockWindow.xaml.cs` | same | main @ 2026-08 | Monitor selection and settings are owned elsewhere; this class knows only its edge, density and HWND |
| `src/MCD.App/Dock/DockMetrics.cs` | `.../Dock/DockSettingsToViews.cs`, `.../Dock/DockItemControl.xaml` | main @ 2026-08 | Numbers only, gathered into one file so the visual identity is a single diff |

## Deliberately not taken

- `MonitorConfigReconciler.cs` — its rule "an unmatched secondary monitor gets a
  disabled config with empty bands" is the direct cause of
  [#49604](https://github.com/microsoft/PowerToys/issues/49604). Ours clones the
  primary's configuration instead, so a blank dock is not a reachable state.
- `DockWindowManager.cs` — written fresh around the Provisional/Authoritative
  snapshot split.
- Anything touching `ICommandItem` or the Command Palette extension SDK. Reusing
  the command model is what limits their widgets to icon + title + subtitle;
  ours supply their own view model and template, which is what makes a graph,
  a colour that means something, and a detail panel possible at all.
