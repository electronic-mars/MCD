using System.Runtime.InteropServices;
using Mcd.Core.Infrastructure;
using Mcd.Core.Settings;
using Mcd.Core.Update;
using Microsoft.Extensions.Logging;

namespace Mcd.App;

/// <summary>
/// The "update by itself" option: once a day it looks at the release page,
/// fetches a newer installer and checks its signature, and puts it in.
/// </summary>
/// <remarks>
/// <para>
/// Put in quietly only where that needs nobody's permission - a copy installed
/// for this user alone - and only once nobody has touched the machine for ten
/// minutes, because the bars go away for the few seconds the installer runs.
/// A copy under Program Files needs administrator rights to be replaced, and
/// a rights prompt out of nowhere is exactly what an unattended updater must
/// never produce: there a notification says the update is ready, and the
/// prompt comes only after somebody presses Install.
/// </para>
/// <para>
/// Everything is checked against the release key as for the button on the
/// About page (<see cref="Updater"/>); this only decides when.
/// </para>
/// </remarks>
internal sealed class AutoUpdate
{
    private static readonly TimeSpan FirstLook = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);
    private static readonly TimeSpan Retry = TimeSpan.FromHours(6);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    private readonly ILogger _log;
    private readonly SettingsService _settings;
    private readonly Action _exit;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private DateTimeOffset _next = DateTimeOffset.Now + FirstLook;

#if SELFTEST
    private static readonly bool Rehearsal = Environment.GetEnvironmentVariable("MCD_SELFTEST") == "1";
#endif
    private bool _busy;
    private bool _told;

    /// <summary>A newer version, downloaded and checked, waiting to be put in. For the About page.</summary>
    public static (UpdateOffer Offer, string Installer)? Ready { get; private set; }

    public AutoUpdate(ILogger log, SettingsService settings, Microsoft.UI.Dispatching.DispatcherQueue ui, Action exit)
    {
        _log = log;
        _settings = settings;
        _exit = exit;

        // Held in a field: a timer only a local refers to is collected, and
        // then simply stops.
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);

#if SELFTEST
        // A self-test run looks at once, and never puts anything in.
        if (Rehearsal)
        {
            _timer.Interval = TimeSpan.FromSeconds(3);
            _next = DateTimeOffset.Now;
        }
#endif
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Notices.On("update", () => Install());
    }

    private async void Tick()
    {
        if (!_settings.Current.App.AutoUpdate || _busy)
        {
            return;
        }

        if (Ready is { } ready)
        {
            if (Quietly())
            {
                if (IdleFor() >= Idle)
                {
                    _log.LogInformation("update.auto installing {Version} while idle", ready.Offer.Version);
                    Install();
                }
            }
            else if (!_told)
            {
                _told = true;
                Notices.Show(
                    "update",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        Loc.Tr("UpdateReadyTitle", "Version {0} is ready"),
                        ready.Offer.Version),
                    Loc.Tr("UpdateReadyText", "Installation requires administrator rights. The bars reopen automatically."),
                    (Loc.Tr("UpdateReadyButton", "Install"), "update"));
            }

            return;
        }

        if (DateTimeOffset.Now < _next)
        {
            return;
        }

        _busy = true;

        try
        {
            UpdateOffer offer = await Updater.LatestAsync();

            if (Updater.IsNewer(offer.Version, AppInfo.Version))
            {
                string installer = await Updater.DownloadAsync(offer, null);
                Ready = (offer, installer);
                _log.LogInformation("update.auto ready {Version}", offer.Version);
            }
            else
            {
                _log.LogInformation("update.auto nothing newer than {Version}", AppInfo.Version);
            }

            _next = DateTimeOffset.Now + Daily;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "update.auto check failed");
            _next = DateTimeOffset.Now + Retry;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Runs the downloaded installer and hands the bars back properly.</summary>
    public void Install()
    {
        if (Ready is not { } ready)
        {
            return;
        }

#if SELFTEST
        if (Rehearsal)
        {
            _log.LogInformation("update.install skipped in a self-test, version={Version}", ready.Offer.Version);
            return;
        }
#endif

        _log.LogInformation("update.install version={Version}", ready.Offer.Version);
        Updater.Install(ready.Installer);
        _exit();
    }

    /// <summary>Whether the program's own folder can be replaced without asking for rights.</summary>
    private static bool Quietly()
    {
        string probe = Path.Combine(AppContext.BaseDirectory, ".write-test");

        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static TimeSpan IdleFor()
    {
        var info = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };

        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time)
            : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput info);
}
