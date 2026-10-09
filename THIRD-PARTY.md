# Third-party components

Master Control Dock is licensed under GPL-3.0-or-later. It builds on the
following components; every version is pinned exactly in
`Directory.Packages.props`.

| Component | Version | License |
|---|---|---|
| Microsoft.WindowsAppSDK | 2.4.0 | Microsoft Software License (redistributable) |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.6584 | Microsoft Software License |
| Microsoft.Windows.CsWin32 | 0.3.321 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT |
| Microsoft.Extensions.Logging | 10.0.0 | MIT |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 4.14.0 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| Shouldly | 4.3.0 | BSD-3-Clause |
| Microsoft.Extensions.TimeProvider.Testing | 9.10.0 | MIT |

## Hugeicons

The icons on the dock come from the [Hugeicons](https://hugeicons.com/) free
set, MIT licensed, fetched through [Iconify](https://iconify.design/). The SVG
sources are kept in `assets/icons-src/` and turned into path data by
`tools/fetch_icons.py`, so a build needs no network and a change to the set is a
diff rather than a mystery.

## Inno Setup Chinese messages

`build/languages/ChineseSimplified.isl` is the Simplified Chinese message file
of [Inno Setup](https://jrsoftware.org/isinfo.php), kept unchanged from its
repository (tag `is-6_7_1`, maintained there by Zhenghan Yang) because the
compiler does not ship it. The installer is built with Inno Setup, whose own
licence permits this use and redistribution of the language files.

## HyperHeadset

How a HyperX Cloud Flight S dongle is asked for the headset's charge - the
request's header, the command number and where the answer sits in the reply
- comes from [HyperHeadset](https://github.com/LennardKittner/HyperHeadset)
by Lennard Kittner, MIT licensed. No code is taken; the layout is
re-implemented in `src/MCD.Interop/Machine/HyperXDongle.cs`.

## Microsoft PowerToys

Parts of the dock window, the AppBar protocol and the visual metrics are adapted
from [microsoft/PowerToys](https://github.com/microsoft/PowerToys) under the MIT
license. The full notice is in `licenses/PowerToys-MIT.txt`; `docs/upstream.md`
lists which of our files came from which of theirs.

The MIT license grants no trademark rights. Master Control Dock uses neither the
PowerToys name nor its icons, and is not affiliated with or endorsed by
Microsoft.

## Optional external sensor sources

Master Control Dock reads CPU package temperature and fan speeds from HWiNFO or
LibreHardwareMonitor **if the user has already installed and started one of
them**. Neither is bundled, installed, or offered for download by this program,
and it is fully functional without either. Everything else — GPU temperature,
drive temperatures, and CPU/GPU/memory/disk/network load — comes from Windows
and from the graphics driver's own user-mode libraries.
