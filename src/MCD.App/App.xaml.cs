using Mcd.App.Dock;
using Mcd.App.Settings;
using Mcd.Core.Infrastructure;
using Mcd.Core.Monitors;
using Mcd.Core.Settings;
using Mcd.Interop.Windowing;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Mcd.Sensors.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Mcd.App;

public partial class App : Application
{
    /// <summary>
    /// Local, not Global. A standard user is not allowed to create a Global
    /// mutex, so CreateMutex would fail with "access denied" rather than
    /// "already exists" - and every launch would think it was the first.
    /// </summary>
    private const string SingleInstanceMutex = "Local\\MasterControlDockSingleInstance";

    private Mutex? _onlyInstance;
    private readonly InstanceSignal _shutdown = InstanceSignal.Shutdown;
    private readonly InstanceSignal _showSettings = InstanceSignal.ShowSettings;
    private RegisteredWaitHandle? _shutdownWatch;
    private RegisteredWaitHandle? _settingsWatch;
    private SettingsWindow? _settingsWindow;
    private ILoggerFactory? _loggers;
    private FileLogger? _logProvider;
    private ServiceProvider? _services;
    private DockWindowManager? _docks;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private HotKeys? _keys;
    private bool _shutDown;

    /// <remarks>
    /// Ended by Exit and nothing else. Left to the default, the program
    /// stopped the moment its last window closed - and hiding the bars with
    /// no settings window open closes every window it has, so "hide" quietly
    /// meant "quit".
    /// </remarks>
    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    private static bool IsElevated() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _logProvider = new FileLogger();
        ILoggerFactory loggers = LoggerFactory.Create(builder => builder.AddProvider(_logProvider));

        ILogger start = loggers.CreateLogger("app");
        start.LogInformation(
            "=== start version={Version} packaged={Packaged} root={Root}",
            AppInfo.Version, AppPaths.IsPackaged, AppPaths.Root);

        if (WantsToStopAnotherCopy())
        {
            start.LogInformation("start.signalled stop: {Found}", _shutdown.Raise());
            _logProvider.Dispose();
            Exit();
            return;
        }

        // Started with administrator rights - by an installer that was itself
        // elevated, say - the program would run, but an elevated WinUI program
        // takes no part in drag and drop: nothing from the settings could be
        // dragged onto a bar. It starts itself again through the shell, which
        // is not elevated, and the elevated copy ends. Once only: a machine
        // whose shell is elevated too keeps the copy it has.
        if (IsElevated()
            && Environment.GetEnvironmentVariable("MCD_SELFTEST") != "1")
        {
            string stamp = Path.Combine(AppPaths.Root, "deelevate.stamp");

            try
            {
                // The shell cannot be told "this is the second try", so the
                // first leaves a note and the second reads it.
                if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromSeconds(30))
                {
                    throw new InvalidOperationException("already tried a moment ago");
                }

                File.WriteAllText(stamp, "x");
                start.LogWarning("start.elevated relaunching through the shell");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{Environment.ProcessPath}\"") { UseShellExecute = true });
                _logProvider.Dispose();
                Exit();
                return;
            }
            catch (Exception e)
            {
                start.LogWarning(e, "start.elevated could not relaunch; carrying on elevated");
            }
        }

        if (!ClaimSingleInstance(start))
        {
            // Starting it again is how someone asks for its settings. Doing
            // nothing visible would look exactly like a launch that failed.
            start.LogInformation("start.signalled settings: {Found}", _showSettings.Raise());

            // Flushed by hand. These two paths return before the process-exit
            // handler is wired up, and the log's writer is a background thread -
            // so without this the only record of what a second launch did is
            // thrown away as the process ends.
            _logProvider.Dispose();
            Exit();
            return;
        }

        ListenForSignals(start);

        _loggers = loggers;
        _services = BuildServices(loggers);

        ApplyLanguage(start);

        // The AppBar registration is the one piece of state that outlives a
        // crash: the shell keeps the reserved work area until someone sends
        // ABM_REMOVE. Every exit path has to reach the same teardown.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        // The XAML handler below only sees exceptions on the UI thread. This one
        // catches what the runtime is about to die from, which is the only
        // record left of a crash that takes the log thread with it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            start.LogCritical(e.ExceptionObject as Exception, "unhandled exception, the process is ending");
            _logProvider?.Dispose();
        };
        UnhandledException += (_, e) =>
        {
            start.LogCritical(e.Exception, "unhandled exception on the UI thread");
            Shutdown();
        };

        LogTopology(start);

        _docks = _services.GetRequiredService<DockWindowManager>();
        _docks.SettingsRequested += (_, request) => ShowSettings(request.Screen, request.WidgetId);
        _docks.ExitRequested += (_, _) => { Shutdown(); Exit(); };
        _docks.Start();

        // The keys the whole machine listens for. Taken again whenever the
        // settings change, because that is the only thing that alters them.
        _keys = new HotKeys(start);
        _docks.KeyRefused = chord => _keys?.Refused.Contains(chord) ?? false;
        TakeKeys();

        _services.GetRequiredService<SettingsService>().Changed += (_, _) =>
            _uiQueue?.TryEnqueue(() =>
            {
                TakeKeys();
                ApplyTray();
            });

        ApplyTray();

        string chosen = Environment.GetEnvironmentVariable("MCD_LANG")
            ?? _services!.GetRequiredService<SettingsService>().Current.App.Language;

        if (chosen is not (null or "" or "system"))
        {
            try
            {
                var culture = new System.Globalization.CultureInfo(chosen);

                // Formatting follows the interface language, or the clock
                // writes its months in the system region's tongue beside
                // widgets speaking the chosen one.
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
            catch (System.Globalization.CultureNotFoundException)
            {
                // A tag from a future settings file changes nothing.
            }
        }

#if SELFTEST
        if (Environment.GetEnvironmentVariable("MCD_SELFTEST") == "1")
        {
            ScheduleSelfTestExit(start);
        }
#endif
    }

    /// <summary>
    /// Applies the chosen interface language, before any window exists.
    /// </summary>
    /// <remarks>
    /// Through the Windows App SDK's own override, which is the one MRT Core
    /// reads in an unpackaged program - the Windows.Globalization one needs a
    /// package identity and does nothing here. MCD_LANG is a seam for checking
    /// the other language without touching the settings, like MCD_DATA_DIR.
    /// </remarks>
    private void ApplyLanguage(ILogger log)
    {
        string chosen = Environment.GetEnvironmentVariable("MCD_LANG")
            ?? _services!.GetRequiredService<SettingsService>().Current.App.Language;

        if (chosen is not { Length: > 0 } || chosen == "system")
        {
            return;
        }

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = chosen;
            log.LogInformation("app.language override={Language}", chosen);
        }
        catch (Exception e)
        {
            // An invalid tag. The program is no worse off in its default language.
            log.LogWarning(e, "app.language could not apply {Language}", chosen);
        }
    }

    /// <summary>
    /// False when another copy is already running.
    /// </summary>
    /// <remarks>
    /// Two copies would fight over the same edge of the same monitor: both
    /// register an AppBar, the shell gives the second one whatever the first left
    /// over, and the desktop loses twice the space it should.
    /// </remarks>
    private bool ClaimSingleInstance(ILogger log)
    {
        _onlyInstance = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool mine);

        if (mine)
        {
            return true;
        }

        log.LogInformation("start.refused another copy is already running");
        _onlyInstance.Dispose();
        _onlyInstance = null;
        return false;
    }

    /// <summary>
    /// True when this launch is only here to stop the copy that is already
    /// running, rather than to start another one.
    /// </summary>
    /// <remarks>
    /// The installer, the uninstaller and the build scripts all need to stop a
    /// running copy, and none of them can click a tray menu. Killing it would
    /// leave the AppBar registered and the desktop permanently short.
    /// </remarks>
    private static bool WantsToStopAnotherCopy() =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase));

    private void ListenForSignals(ILogger log)
    {
        _shutdownWatch = ThreadPool.RegisterWaitForSingleObject(
            _shutdown.Listen(),
            (_, timedOut) =>
            {
                if (timedOut)
                {
                    return;
                }

                log.LogInformation("shutdown.requested from another process");

                // Shutdown touches windows, so it has to happen on the UI thread.
                _uiQueue?.TryEnqueue(() => { Shutdown(); Exit(); });
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _settingsWatch = ThreadPool.RegisterWaitForSingleObject(
            _showSettings.Listen(),
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    // Starting the program again is the way back that needs
                    // no knowledge: hidden bars come back, then the window.
                    _uiQueue?.TryEnqueue(() =>
                    {
                        if (_docks is { Visible: false } docks)
                        {
                            docks.Visible = true;
                        }

                        ShowSettings();
                    });
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Brings the settings window up, creating it the first time.</summary>
    /// <param name="screen">
    /// The monitor to open on, when the request came from a dock. Null when it
    /// came from someone starting the program again, which has no screen of its
    /// own to speak of.
    /// </param>
    private void ShowSettings(MonitorInfo? screen = null, string? widgetId = null)
    {
        if (_shutDown || _services is null || _docks is null || _loggers is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            try
            {
                _settingsWindow = new SettingsWindow(
                    _loggers.CreateLogger<SettingsWindow>(),
                    _services.GetRequiredService<SettingsService>(),
                    _docks,
                    _services.GetRequiredService<SensorHub>(),
                    _services.GetRequiredService<Mcd.Sensors.Host.HostClient>(),
                    onExit: () => { Shutdown(); Exit(); });
            }
            catch (Exception e)
            {
                // The docks are the program; the settings are a window onto
                // them. One that cannot be built is a bug worth a line in the
                // log, not a reason to take three bars off the desktop - and
                // without this the whole process dies with nothing written down,
                // because a failure inside XAML arrives as a stowed exception
                // that no managed handler upstream ever sees.
                _loggers.CreateLogger("app").LogError(e, "settings.failed to build the window");
                _settingsWindow = null;
                return;
            }

            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        else
        {
            _settingsWindow.Reload();
        }

        if (screen is not null)
        {
            _settingsWindow.SizeAndCentre(screen);
            _settingsWindow.Show(screen, widgetId);
        }

        _settingsWindow.Activate();
        _settingsWindow.AppWindow.MoveInZOrderAtTop();

        // Activate alone is not enough. The click that asked for this landed on
        // a dock, and a dock never takes the foreground, so Windows treats the
        // request as coming from a background process and quietly leaves the
        // window behind whatever was in front.
        Mcd.Interop.Windowing.WindowFrame.BringToFront(
            WinRT.Interop.WindowNative.GetWindowHandle(_settingsWindow));
        _loggers.CreateLogger("app").LogInformation("settings.shown");
    }

    /// <summary>
    /// Binds every shortcut the settings name.
    /// </summary>
    /// <remarks>
    /// Each one hops back to the interface thread before doing anything: the
    /// keys arrive on a thread of their own, and everything they act on -
    /// windows, settings, the bars - belongs to the interface thread.
    /// </remarks>
    private void TakeKeys()
    {
        if (_keys is null || _services is null)
        {
            return;
        }

        AppSettings app = _services.GetRequiredService<SettingsService>().Current.App;

        List<(Chord Chord, Action Do)> wanted = [];

        foreach (string what in Shortcut.All)
        {
            if (!app.Keys.TryGetValue(what, out string? text))
            {
                continue;
            }

            Chord chord = Chord.Parse(text);

            if (!chord.Sane)
            {
                continue;
            }

            Action act = what switch
            {
                Shortcut.Bars => () => _uiQueue?.TryEnqueue(ShowOrHideBars),
                Shortcut.Settings => () => _uiQueue?.TryEnqueue(() => ShowSettings()),
                Shortcut.Mute => Silence,
                Shortcut.Mic => SwitchMic,
                Shortcut.Focus => () => _uiQueue?.TryEnqueue(() => _docks?.FocusBar()),
                _ => () => { },
            };

            wanted.Add((chord, act));
        }

        _keys.Listen(wanted);
    }

    /// <summary>Shows every bar, or hides every bar.</summary>
    private void ShowOrHideBars()
    {
        if (_docks is { } docks)
        {
            docks.Visible = !docks.Visible;
        }
    }

    /// <summary>Silences the machine, or lets it speak again.</summary>
    private static void Silence()
    {
        if (Mcd.Audio.SystemVolume.Muted() is { } muted)
        {
            Mcd.Audio.SystemVolume.Mute(!muted);
        }
    }

    /// <summary>Switches the microphone off, or on again.</summary>
    private static void SwitchMic()
    {
        if (Mcd.Audio.Microphone.Muted() is { } muted)
        {
            Mcd.Audio.Microphone.Mute(!muted);
        }
    }

    private static ServiceProvider BuildServices(ILoggerFactory loggers)
    {
        var services = new ServiceCollection();

        services.AddSingleton(loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<MonitorService>();
        services.AddSingleton<DisplayChangeWatcher>();
        services.AddSingleton<TopologyChangeCoalescer>();
        services.AddSingleton<ISensorProvider, PdhSensorProvider>();
        services.AddSingleton<ISensorProvider, MemoryProvider>();
        services.AddSingleton<ISensorProvider, NvidiaGpuProvider>();
        services.AddSingleton<ISensorProvider, StorageTemperatureProvider>();
        services.AddSingleton<ISensorProvider, AcpiThermalProvider>();
        services.AddSingleton<ISensorProvider, DeviceBatteryProvider>();

        // The processor and the memory modules, through the PawnIO driver
        // when the person has installed it. Registered always: each one says
        // "not without the driver" until the driver appears, and the hub asks
        // again every half minute.
        services.AddSingleton<Mcd.Sensors.Host.HostClient>();
        services.AddSingleton<ISensorProvider, CpuMsrProvider>();
        services.AddSingleton<ISensorProvider, DimmProvider>();

        // No source that lives in another program. The bridges to HWiNFO and
        // LibreHardwareMonitor are kept in the tree but not wired: the plan is
        // the program's own readings, and until then a line saying "run this
        // other thing" is a line about somebody else's software.

        services.AddSingleton<SensorHub>();
        services.AddSingleton<DockWindowManager>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Writes the topology to the log at startup.
    /// </summary>
    /// <remarks>
    /// Kept in the release build deliberately. When someone reports "the dock is
    /// blank on my second screen", this is the only record of what the program
    /// actually saw - the thing missing from every report on the PowerToys bug.
    /// </remarks>
    private void LogTopology(ILogger log)
    {
        MonitorSnapshot snapshot = _services!.GetRequiredService<MonitorService>().Enumerate();

        log.LogInformation(
            "monitors.snapshot {Kind} count={Count}",
            snapshot is AuthoritativeSnapshot ? "authoritative" : "provisional",
            snapshot.Count);

        foreach (MonitorInfo m in snapshot.Monitors)
        {
            log.LogInformation(
                "monitors.item id={Id} name={Name} edid={Edid} gdi={Gdi} primary={Primary} "
                + "bounds={Left},{Top},{Right},{Bottom} dpi={Dpi} path={Path}",
                m.StableId, m.Identity.FriendlyName, m.Identity.EdidKey, m.Identity.GdiName, m.IsPrimary,
                m.Bounds.left, m.Bounds.top, m.Bounds.right, m.Bounds.bottom, m.Dpi, m.Identity.DevicePath);
        }
    }

#if SELFTEST
    /// <summary>
    /// Ends the program the same way a person would, after a fixed run.
    /// </summary>
    /// <remarks>
    /// The CI smoke check cannot simply kill the process: taskkill skips
    /// ProcessExit, the AppBar registration survives, and the build agent's work
    /// area stays shrunk for every job that follows.
    /// </remarks>
    /// <summary>
    /// The self-test's timers, held in a field on purpose: a
    /// DispatcherQueueTimer referenced only by a local is garbage once the
    /// method returns, and whether its ticks ever fire then depends on when
    /// the collector happens to run. The eight-second exit tick was lost to
    /// exactly that.
    /// </summary>
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _selfTest = [];

    private void ScheduleSelfTestExit(ILogger log)
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("MCD_SELFTEST_SECONDS"), out int s)
            ? s
            : 8;

        log.LogInformation("selftest.armed seconds={Seconds}", seconds);

        // The settings window is opened and closed as part of the run. Every
        // page of it is built from XAML, and XAML names are checked when they
        // are read rather than when they are compiled - a wrong one is a parse
        // failure that ends the process without a word in the log. Only opening
        // the window finds them, so the check that runs unattended has to.
        Microsoft.UI.Dispatching.DispatcherQueueTimer settings =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        settings.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds / 3));
        settings.IsRepeating = false;
        settings.Tick += (_, _) =>
        {
            ShowSettings();

            // Then the page is built a second time, the way a right-click on
            // a dock builds it: naming a screen, and sometimes a widget. The
            // second build is where a page made of code goes wrong - an
            // element it holds on to still belongs to the page before it -
            // and opening the window with no screen never reaches it, which
            // is how a crash on every right-click got past a run of this.
            //
            // The page is asked directly rather than through ShowSettings:
            // that one also drags the window to the front, and joining input
            // queues with whatever is in front has no business happening in
            // an unattended run.
            if (_docks?.Plans.FirstOrDefault(p => p.ShouldShow) is { } plan)
            {
                _settingsWindow?.Show(
                    plan.Monitor, plan.Config.Widgets.FirstOrDefault()?.InstanceId);

                log.LogInformation("selftest.reopened");

                // The window has been shown but not yet arranged: measuring
                // now reads whatever the first measure guessed. Given a beat,
                // and then measured.
                Later(500, () => _settingsWindow?.SqueezeBegin());

                Later(1200, () =>
                {
                    try
                    {
                        log.LogInformation(
                            "selftest.squeezed narrowest={Narrowest}",
                            Math.Round(_settingsWindow?.Squeeze() ?? 0));
                    }
                    catch (Exception e)
                    {
                        log.LogError(e, "selftest.squeeze failed");
                    }
                });
            }

            // And the way a person actually opens it: from a bar's own menu,
            // with the menu still up. The window is closed first, so that this
            // builds one rather than raising the one already there - building
            // it is the part that went wrong.
            Later(1600, () =>
            {
                try
                {
                    _settingsWindow?.Close();
                    _settingsWindow = null;

                    log.LogInformation("selftest.menued shown={Shown}", _docks?.RehearseMenu() ?? false);
                }
                catch (Exception e)
                {
                    log.LogError(e, "selftest.menu failed");
                }
            });

            // And every path that rebuilds a bar, twice each - after the
            // squeeze, so the two are not laying the same window out at once.
            Later(2000, () =>
            {
                if (_docks is null)
                {
                    return;
                }

                try
                {
                    // Announced before it starts, so whatever is reading the
                    // log can tell the ordinary run from the rehearsal - the
                    // rehearsal writes on purpose.
                    log.LogInformation("selftest.rehearsing");
                    log.LogInformation("selftest.rehearsed hosts={Hosts}", _docks.Rehearse());
                }
                catch (Exception e)
                {
                    log.LogError(e, "selftest.rehearsal failed");
                }
            });

            // Left open, and walked through, when pages were asked for: a
            // visual change is verified by looking at the window it changed,
            // and the window that gets looked at must not be the one on
            // somebody's desk while they are working. Each page is announced
            // in the log so whatever is photographing can keep in step
            // instead of guessing at a cadence.
            // A density toggle, made the way a person makes it - the same
            // write the settings page commits - so the whole rebuild path is
            // exercised: teardown, appbar renegotiation, refit, write-back.
            // Announced in the log so the photographer keeps in step.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "density")
            {
                void Flip()
                {
                    SettingsService settings = _services!.GetRequiredService<SettingsService>();
                    SettingsModel current = settings.Current;

                    settings.Commit(
                        current with
                        {
                            Monitors =
                            [
                                .. current.Monitors.Select(m => m.Enabled
                                    ? m with
                                    {
                                        Density = m.Density == DockDensity.Compact
                                            ? DockDensity.Default
                                            : DockDensity.Compact,
                                    }
                                    : m)
                            ],
                        },
                        WriteReason.UserAction,
                        "толщина");

                    log.LogInformation("selftest.flipped");
                }

                Later(4000, Flip);
                Later(9000, Flip);
            }

            // The bars hidden and brought back, as the menu item does it -
            // what a widget that must know whether it is on screen is checked by.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "bars")
            {
                void Flip()
                {
                    ShowOrHideBars();
                    log.LogInformation("selftest.flipped bars={Visible}", _docks?.Visible);
                }

                Later(8000, Flip);
                Later(18000, Flip);
            }

            // A widget chosen on the widgets page, and a switch in its own
            // settings pressed - the whole way through, page included.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "editswitch")
            {
                Later(1500, () => log.LogInformation(
                    "selftest.editswitch {Said}", _settingsWindow?.RehearseSwitch("ram", -1)));

                Later(1800, () => log.LogInformation(
                    "selftest.editswitch {Said}", _settingsWindow?.RehearseSwitch("ram", 0)));

                Later(2100, () => log.LogInformation(
                    "selftest.editswitch {Said}", _settingsWindow?.RehearseSwitch("ram", 1)));

                Later(2400, () => log.LogInformation(
                    "selftest.editswitch {Said}", _settingsWindow?.RehearseSwitch("cpu", 0)));
            }

            // Every switch in a gauge's settings pressed twice, to see that
            // the second press says something the first did not.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "switch")
            {
                Later(3000, () => log.LogInformation("selftest.switches {Wrote}", _docks?.Switches()));
            }

            // The switcher's chip pressed twice, as a click on it presses it.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "press")
            {
                void Press() => log.LogInformation(
                    "selftest.pressed switchers={Count}", _docks?.Press(Mcd.App.Widgets.SwitcherWidget.Type));

                Later(8000, Press);
                Later(14000, Press);
            }

            // The cup pressed full and empty again, the volume moved by two
            // per cent and put back, and the slider opened - the presses a
            // person makes on the new widgets, without a person.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "glyphs")
            {
                void Cup(string what)
                {
                    _docks?.Press(Mcd.App.Widgets.AwakeWidget.Type);
                    var drawn = _docks?.Widgets(Mcd.App.Widgets.AwakeWidget.Type)
                        .OfType<Mcd.App.Widgets.GlyphWidget>().FirstOrDefault();

                    log.LogInformation(
                        "selftest.cup {What} on={On} until={Until} icon={Icon} faded={Faded} said={Said}",
                        what, Mcd.Interop.Machine.KeepAwake.On, Mcd.Interop.Machine.KeepAwake.Until,
                        drawn?.Icon, drawn?.Faded, drawn?.Detail);
                }

                Later(3000, () => Cup("filled"));
                Later(9000, () => Cup("emptied"));

                Later(4000, () =>
                {
                    float was = Mcd.Audio.SystemVolume.Level() ?? 0;

                    foreach (Mcd.App.Widgets.WidgetViewModel widget in _docks?.Widgets(Mcd.App.Widgets.SoundWidget.Type) ?? [])
                    {
                        ((Mcd.App.Widgets.SoundWidget)widget).Volume = Math.Round(was * 100) + 2;
                    }

                    float now = Mcd.Audio.SystemVolume.Level() ?? 0;
                    Mcd.Audio.SystemVolume.Set(was);

                    log.LogInformation(
                        "selftest.volume was={Was} moved={Now} back={Back}",
                        was, now, Mcd.Audio.SystemVolume.Level());
                });

                // What the gallery offers under "Programs", and what each
                // would put on a bar.
                Later(2000, () =>
                {
                    var hub = _services!.GetRequiredService<SensorHub>();

                    log.LogInformation(
                        "selftest.offers {Programs}",
                        string.Join(
                            "; ",
                            Mcd.App.Widgets.WidgetCatalog.Offers(hub)
                                .Where(o => o.Category == "apps")
                                .Select(o => o.Name + " -> " + o.Make().Config?.GetRawText())));
                });

                // What a drag over the bar shows, without a drag.
                Later(5000, () =>
                {
                    _docks?.Pretend(cell: 60, span: 3);
                    log.LogInformation("selftest.pretended");
                });

                Later(6000, () => log.LogInformation(
                    "selftest.flyout opened={Count}", _docks?.OpenFlyouts(Mcd.App.Widgets.SoundWidget.Type)));

                foreach (int after in new[] { 6300, 7000, 8000 })
                {
                    Later(after, () => log.LogInformation(
                        "selftest.flyout open={Count}", _docks?.FlyoutsOpen(Mcd.App.Widgets.SoundWidget.Type)));
                }
            }

            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_PAGE") is { Length: > 0 } pages)
            {
                // After the rehearsals, not alongside them. The rehearsal at
                // 1600 ms closes this window and builds another one on the
                // widgets page; a walk running at the same time announces a
                // page in the log while a different one is on screen, and the
                // photograph taken from that announcement is filed under the
                // wrong name - which is worse than a black one, because it
                // looks right.
                Later(2400, () => Walk(log, [.. pages.Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));

                return;
            }

            // The keyboard taken onto the bar, as the hotkey does, moved once
            // with the arrow keys' own handler path, and given back.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "identify")
            {
                Later(3000, () =>
                {
                    Dock.IdentifyScreens.Flash(_docks!.Plans.Select(p => p.Monitor));
                    log.LogInformation("selftest.identify shown");
                });

                return;
            }

            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "drag")
            {
                Later(300, () => _docks?.Pretend(cell: 60, span: 3));

                return;
            }

            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "keyboard")
            {
                Later(3000, () => _docks?.FocusBar());
                Later(5000, () => _docks?.LeaveBars());

                return;
            }

            // The list of icons for a pinned program, opened as a person opens
            // it, and then left open for the photograph.
            if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") is "picker" or "picker-search")
            {
                Later(3500, () => log.LogInformation(
                    "selftest.picker {Said}",
                    _settingsWindow?.OpenIconPicker(
                        Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "picker-search" ? "coffee" : string.Empty)));

                Later(4500, () =>
                {
                    if (Environment.GetEnvironmentVariable("MCD_SELFTEST_FLIP") == "picker-search")
                    {
                        _settingsWindow?.TypeInPicker("coffee");
                    }
                });

                Later(5300, () => log.LogInformation("selftest.picker shows={Count}", _settingsWindow?.PickerShows()));

                return;
            }

            // Closed last of all. The measuring above happens on later ticks,
            // and a window closed on this one is gone before any of it runs -
            // which is how the squeeze came back as nothing at all.
            Later(2700, () => _settingsWindow?.Close());
        };
        _selfTest.Add(settings);
        settings.Start();

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            log.LogInformation("selftest.done");
            Shutdown();
            Exit();
        };
        _selfTest.Add(timer);
        timer.Start();
    }

    /// <summary>Does something once, after a beat, on the interface thread.</summary>
    private void Later(int milliseconds, Action work)
    {
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => work();

        _selfTest.Add(timer);
        timer.Start();
    }

    /// <summary>Shows each page in turn, announcing every one it reaches.</summary>
    private void Walk(ILogger log, IReadOnlyList<string> pages)
    {
        int at = 0;

        Microsoft.UI.Dispatching.DispatcherQueueTimer step =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();

        step.Interval = TimeSpan.FromMilliseconds(1400);

        step.Tick += (_, _) =>
        {
            if (at >= pages.Count || _settingsWindow is null)
            {
                step.Stop();
                return;
            }

            _settingsWindow.GoTo(pages[at]);
            log.LogInformation("selftest.page {Page}", pages[at]);
            at++;
        };

        _selfTest.Add(step);
        step.Start();
    }
#endif

    private Mcd.Interop.Windowing.TrayIcon? _tray;

    /// <summary>
    /// Puts the notification-area icon up or takes it away, as the setting says.
    /// </summary>
    private void ApplyTray()
    {
        if (_services is null)
        {
            return;
        }

        AppSettings app = _services.GetRequiredService<SettingsService>().Current.App;

        if (!app.TrayIcon)
        {
            _tray?.Dispose();
            _tray = null;
            return;
        }

        _tray ??= new Mcd.Interop.Windowing.TrayIcon(
            () => ShowSettings(),
            () =>
            [
                (Loc.Tr("BarMenuSettings", "Settings..."), () => ShowSettings()),
                (
                    _docks is { Visible: false }
                        ? Loc.Tr("BarsShow", "Show the bars")
                        : Loc.Tr("BarMenuHideAll", "Hide the bars"),
                    ShowOrHideBars
                ),
                (Loc.Tr("BarMenuExit", "Exit"), () => { Shutdown(); Exit(); }),
            ]);

        // The taskbar's own theme picks the drawing, as it does for the window.
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

        bool light = key?.GetValue("SystemUsesLightTheme") is 1;

        _tray.Show(
            Path.Combine(AppContext.BaseDirectory, "Assets", light ? "icon-light.ico" : "icon.ico"),
            "Master Control Dock");
    }

    private void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;

        _keys?.Dispose();
        _keys = null;

        if (_shutDown)
        {
            return;
        }

        _shutDown = true;

        _settingsWindow?.Close();
        _settingsWindow = null;

        _docks?.Dispose();
        _docks = null;

        // The switcher's tray icon back now, not after its fifteen seconds.
        Mcd.App.Widgets.AudioSwitcher.HandBack();

        _services?.Dispose();
        _services = null;

        _shutdownWatch?.Unregister(null);
        _shutdownWatch = null;
        _settingsWatch?.Unregister(null);
        _settingsWatch = null;
        _shutdown.Dispose();
        _showSettings.Dispose();

        _logProvider?.Dispose();
        _logProvider = null;

        // Released only by the thread that took it. ProcessExit runs this on
        // a thread of its own, and a mutex released from the wrong thread
        // throws - which turned an orderly exit into a crash report. Closing
        // the handle lets the mutex go either way.
        try
        {
            _onlyInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _onlyInstance?.Dispose();
        _onlyInstance = null;
    }
}
