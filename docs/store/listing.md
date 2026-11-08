# Microsoft Store listing — Master Control Dock

Everything Partner Center asks for, ready to paste. Field names match the
submission pages. Made the way Master Audio Switcher's was
(`MAS/workshop/store-listing.md`); the account and its three unchangeable answers
are already settled there.

## Product identity

Reserved on 7 November 2026. Written into `packaging/Package.appxmanifest`.

| | |
|---|---|
| Package/Identity/Name | `ElectronicMARS.MasterControlDock` |
| Package/Identity/Publisher | `CN=952B5818-8749-4493-B354-6D30C3A586B7` |
| PublisherDisplayName | `Electronic MARS` |
| Package Family Name | `ElectronicMARS.MasterControlDock_8b14r1jdzqs64` |
| Store ID | `9NJRSBM7J146` |

Store page once live: https://apps.microsoft.com/detail/9NJRSBM7J146

The reservation lapses if nothing is submitted within three months.

**Listing languages: English and Russian.** The package declares exactly
`en-us` and `ru-ru`, because Partner Center demands a complete listing for every
language a package declares, and rebuilds that list from the package whatever
is deleted on the languages page. The program itself still speaks all fifteen:
packaged, it picks its language from the person's Windows list on its own
(`App.ApplyLanguage`). Add a tag to the manifest only together with a listing.

## The package

The MSIX built by the release workflow (job `msix`, artifact `msix`) from a
version tag. Never one built on a developer machine. `tools/pack-msix.ps1`
builds the same thing locally for a try-out.

What differs from the installer version, all decided at run time by
`AppPaths.IsPackaged`:

- no updater: the About page has no Updates section, and nothing checks GitHub;
- **Start with Windows** is the manifest's StartupTask, not the Run key;
- no sensor service is carried or installed; one put in by the installer
  version is still read. Without it, processor and motherboard temperatures
  come from LibreHardwareMonitor or HWiNFO if the person runs one;
- settings live in the package's LocalState.

Tried on 7 November 2026, registered from the unpacked package on this machine:
starts with `packaged=True`, three bars, language from the Windows list,
readings through the installed service, settings open on a second start;
StartupTask `McdStartup` goes Disabled → Enabled → Disabled from inside the
package.

---

## Properties

- **Category:** Utilities & tools
- **Subcategory:** none
- **Privacy policy URL:** https://github.com/electronic-mars/MCD/blob/main/PRIVACY.md
- **Website:** https://electronic-mars.github.io/#mcd
- **Support contact info:** https://github.com/electronic-mars/MCD/issues
- **Does this product access, collect or transmit personal information?** No
- **Product declarations:** none apply (no in-app purchases, no ads).
- **Donations:** the About page has "Support the project", which opens the
  developer's site in the browser (Ko-fi, Patreon, Donatello, crypto). Nothing
  is unlocked by it. If Partner Center asks whether the product accepts
  donations through a third party, answer yes (policy 10.8.2).

## Age ratings

The IARC questionnaire, answered honestly, comes out at 3+ everywhere: no user
content, no chat, no ads, no purchases, no data collection, no location. The
About page links to GitHub and to the developer's site, so "does the product
contain links to external sites" is **yes**.

## Pricing and availability

Free. All markets. Public. No trial, no in-app purchases.

---

## Store listing — English

### Product name
Master Control Dock

### Short description
A bar on the edge of every screen: CPU, GPU, memory and drive readings with
temperatures, media buttons, volume, microphone, brightness and your pinned
programs.

### Description

Master Control Dock puts a slim bar along the edge of every screen you have,
and fills it with the things you glance at all day.

**Readings that mean something.** Processor, graphics, memory, drive and
network load, and the temperatures of the parts that report one. A figure turns
amber as it climbs and red with a warning when it is at the limit, so you notice
it without reading it. Press a reading to see its last hour.

**Controls you reach for.** Mute the speakers with one click on the icon, or
click the figure for a volume slider; scroll over it to turn the sound up and
down. Switch the microphone off for the whole system, and see it go red while it
is off. Brightness moves every screen at once, the laptop's own panel and any
monitor that answers DDC/CI. The media widget shows what is playing, with its
cover and the buttons to control it.

**Your programs, folders and websites.** Pin them to the bar, or drop a file
straight onto it.

**Every screen its own bar.** Top, bottom, left or right; docked so maximised
windows stop at it, hidden until you need it, or lying on the desktop. A monitor
plugged in later gets a bar of its own, never an empty one. Drag widgets from
the gallery onto the bar, keep layouts and put any of them on any screen.

**And the small things:** the clock and date, Wi-Fi and battery, the charge of a
wireless mouse or headset, the keyboard layout and Caps Lock, a switch that
keeps the computer awake, and keys that work from any program.

It follows the Windows theme, light or dark, translucent or solid, with the
Windows accent colour on the readings if you like. It speaks fifteen languages.

**About temperatures:** graphics (NVIDIA) and drive temperatures are read
directly. Processor and motherboard temperatures need a hardware monitor that
can read them; if you run LibreHardwareMonitor or HWiNFO, the bar takes its
readings from there.

Free and open source (GPL-3.0). No account, no ads, no telemetry. Nothing leaves
your computer unless you open a link.

### What's new in this version
First release in the Microsoft Store.

### Product features
*(one per line, up to 20)*

- A bar on the edge of every screen, each with its own widgets
- CPU, GPU, memory, drive and network load and temperatures
- Readings turn amber, then red with a warning, as they climb
- Press a reading to see its last hour
- Mute, volume slider and scroll-to-change from the bar
- System-wide microphone switch that turns red while muted
- One brightness slider for every screen, DDC/CI monitors included
- Media widget with cover art and playback buttons
- Pinned programs, folders and websites, or a file dropped on the bar
- Docked, auto-hide or on-desktop, on any edge
- Saved layouts for any screen
- Light and dark themes, Windows accent colour
- Battery of wireless mice and headsets
- Keep-awake switch and global keys
- 15 languages, free and open source

### Search terms
*(up to seven)*

system monitor, taskbar, cpu temperature, dock, toolbar, widgets, multi monitor

### Copyright and trademark info
© electronic-mars. GPL-3.0-or-later.

### Additional license terms
https://github.com/electronic-mars/MCD/blob/main/LICENSE

### Developed by
Electronic MARS

---

## Store listing — Russian

### Product name
Master Control Dock

### Short description
Полоса у края каждого экрана: загрузка и температура процессора, видеокарты,
памяти и дисков, кнопки плеера, громкость, микрофон, яркость и ваши программы.

### Description

Master Control Dock ставит у края каждого экрана тонкую полосу и собирает на
ней то, на что вы смотрите весь день.

**Показания, которые что-то значат.** Загрузка процессора, видеокарты, памяти,
дисков и сети, и температура тех частей, что её сообщают. Число желтеет, когда
растёт, и краснеет с предупреждением у предела, так что это заметно без чтения.
Нажмите на показание, и откроется его последний час.

**Управление под рукой.** Звук выключается одним щелчком по значку, щелчок по
числу открывает ползунок громкости, колесо мыши над ним прибавляет и убавляет.
Микрофон выключается для всей системы и горит красным, пока выключен. Яркость
меняется на всех экранах сразу: на экране ноутбука и на мониторах с DDC/CI.
Виджет плеера показывает, что играет, с обложкой и кнопками.

**Ваши программы, папки и сайты.** Закрепите их на полосе или просто бросьте
файл на неё.

**У каждого экрана своя полоса.** Сверху, снизу, слева или справа; закреплённая,
чтобы развёрнутые окна останавливались у неё, скрытая до наведения или лежащая
на рабочем столе. Монитор, подключённый позже, получает свою полосу, а не
пустую. Виджеты перетаскиваются из галереи на полосу, раскладки сохраняются и
ставятся на любой экран.

**И мелочи:** часы и дата, Wi-Fi и батарея, заряд беспроводной мыши или
гарнитуры, раскладка клавиатуры и Caps Lock, переключатель «не засыпать» и
клавиши, работающие из любой программы.

Следует теме Windows, светлой или тёмной, полупрозрачной или сплошной, а
показания по желанию окрашиваются в цвет акцента Windows. Говорит на
пятнадцати языках.

**О температурах:** температура видеокарты (NVIDIA) и дисков читается
напрямую. Для температуры процессора и материнской платы нужна программа
мониторинга, которая умеет её читать: если у вас запущен LibreHardwareMonitor
или HWiNFO, полоса берёт показания оттуда.

Бесплатно и с открытым кодом (GPL-3.0). Без учётной записи, без рекламы, без
телеметрии. Ничего не уходит с компьютера, пока вы сами не откроете ссылку.

### What's new in this version
Первый выпуск в Microsoft Store.

### Product features

- Полоса у края каждого экрана, у каждой свои виджеты
- Загрузка и температура процессора, видеокарты, памяти, дисков и сети
- Показания желтеют, а затем краснеют с предупреждением
- Нажатие на показание открывает его последний час
- Выключение звука, ползунок громкости и колесо мыши прямо на полосе
- Выключатель микрофона для всей системы, красный, пока выключен
- Один ползунок яркости для всех экранов, включая мониторы с DDC/CI
- Виджет плеера с обложкой и кнопками
- Закреплённые программы, папки и сайты; файл можно бросить на полосу
- Закреплённая, скрытая или на рабочем столе, у любого края
- Сохранённые раскладки для любого экрана
- Светлая и тёмная темы, цвет акцента Windows
- Заряд беспроводных мышей и гарнитур
- Режим «не засыпать» и глобальные клавиши
- 15 языков, бесплатно и с открытым кодом

### Search terms

мониторинг системы, температура процессора, панель задач, док, виджеты, несколько мониторов, загрузка процессора

### Copyright and trademark info
© electronic-mars. GPL-3.0-or-later.

### Additional license terms
https://github.com/electronic-mars/MCD/blob/main/LICENSE

---

## Screenshots

The README's own, 1600×900 PNG from the demo mode (`MCD_DEMO=1`, `tools/demo/`),
every figure made up (21, 75 or 100). The same five go on both listings; the
first is the one shown in search results.

| # | File | Caption (English) | Подпись (русский) |
|---|---|---|---|
| 1 | `docs/screenshots/bar.png` | Readings turn red with a warning at the limit. | У предела показание краснеет и предупреждает. |
| 2 | `docs/screenshots/history.png` | Press a reading to see its last hour. | Нажатие на показание открывает его последний час. |
| 3 | `docs/screenshots/volume.png` | Volume, microphone and brightness from the bar. | Громкость, микрофон и яркость прямо с полосы. |
| 4 | `docs/screenshots/widgets.png` | Drag any widget onto the bar. | Любой виджет перетаскивается на полосу. |
| 5 | `docs/screenshots/appearance.png` | Light or dark, with the Windows accent. | Светлая или тёмная, с цветом акцента Windows. |

## Trailer (from 0.1.21)

`docs/store/0.1.21/trailer.mp4`: the site's two demo clips (`demo-bar`, then
`demo-settings`) joined and scaled to 1920×1080, H.264 High, a silent AAC
stereo track, 47 s. Thumbnail `trailer-thumbnail.png` and the 16:9 hero image
`hero-16x9.png` (it lets the trailer play at the top of the page and must not
carry the product name) are both `bar.png` at 1920×1080. Same files on both
listings.

- Title (English): The bar on every screen, and its settings
- Название (русский): Полоса на каждом экране и её настройки

What's new in 0.1.21: «The program's own icon in Start, on the taskbar and in
the Store.» / «Своя иконка программы в «Пуске», на панели задач и в Store.»

---

## Submission options — notes for certification

```
Master Control Dock is a desktop toolbar ("AppBar") packaged as MSIX with
runFullTrust. It reserves a strip at a screen edge through SHAppBarMessage and
shows system readings and controls on it.

Why runFullTrust: the program is a Win32 desktop application. It reserves
screen space with SHAppBarMessage, reads performance counters, the NVIDIA
driver's NVML library and drive temperatures through DeviceIoControl, sets the
volume and mute state of audio endpoints, changes monitor brightness through
DDC/CI, registers global hotkeys with RegisterHotKey, and watches for a mouse
press outside its own pop-ups with a low-level mouse hook so that they close
like any menu. None of this is available to a sandboxed app.

Other behaviour worth knowing:
- The optional "keep awake" widget calls SetThreadExecutionState and, while it
  is switched on, moves the mouse pointer by one pixel and back once a minute so
  that chat programs do not mark the user away. It is off until the user adds
  the widget and switches it on.
- For HyperX wireless headsets it sends the USB receiver the request that
  reports battery charge, and reads the answer. It changes no device setting.
- It installs no drivers and no services, and writes nothing outside its
  package. Start with Windows uses the manifest's StartupTask.
- There is no account, no telemetry and no network service. The Store version
  makes no network request of its own; links on the About page open the browser.
  It has no updater: updates come from the Store.

To try it: bars appear on every screen at startup. Right-click an empty part of
a bar for Settings, Hide the bars and Exit. Starting the app again while it runs
opens the settings. Processor temperature needs LibreHardwareMonitor or HWiNFO
and is shown as unavailable without them; every other reading works on its own.
```
