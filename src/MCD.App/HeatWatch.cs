using System.Globalization;
using Mcd.App.Widgets;
using Mcd.Core.Settings;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.App;

/// <summary>
/// The overheat warning: one Windows notification when a part goes past the
/// temperature its maker calls critical.
/// </summary>
/// <remarks>
/// <para>
/// Off unless asked for (the audit's red team: a notification goes against
/// the quiet the bar is for, and the bar's own colours already say it). When
/// on, it is for the moment somebody is looking at something else.
/// </para>
/// <para>
/// Only the part's own critical point counts, never a guess: a drive or a
/// processor that names none is never warned about. A part is warned about
/// once, and again only after it has cooled five degrees below that point
/// and half an hour has passed - a temperature that hovers on the line would
/// otherwise be a notification a second. "Not today" silences all of them
/// until midnight.
/// </para>
/// </remarks>
internal sealed class HeatWatch
{
    private const double Cooled = 5;
    private static readonly TimeSpan Again = TimeSpan.FromMinutes(30);

    private readonly ILogger _log;
    private readonly SettingsService _settings;
    private readonly SensorHub _hub;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui;
    private readonly Dictionary<SensorKey, DateTimeOffset> _warned = [];
    private readonly HashSet<SensorKey> _cooled = [];
    private DateTime _quietUntil;

    public HeatWatch(ILogger log, SettingsService settings, SensorHub hub, Microsoft.UI.Dispatching.DispatcherQueue ui)
    {
        _log = log;
        _settings = settings;
        _hub = hub;
        _ui = ui;

        Notices.On("heat-quiet", () => _quietUntil = DateTime.Today.AddDays(1));
        hub.Updated += (_, snapshot) => _ui.TryEnqueue(() => Look(snapshot));
    }

    private void Look(SensorSnapshot snapshot)
    {
        if (!_settings.Current.App.HeatAlert || DateTime.Now < _quietUntil)
        {
            return;
        }

        foreach (SensorDescriptor sensor in _hub.Catalog)
        {
            if (sensor.Kind != SensorKind.Temperature || sensor.Critical is not { } critical)
            {
                continue;
            }

            SensorReading reading = snapshot[sensor.Key];

            if (!reading.HasValue)
            {
                continue;
            }

            if (reading.Value < critical - Cooled)
            {
                // Cooled down: the next crossing is news again, after a while.
                _cooled.Add(sensor.Key);
                continue;
            }

            if (reading.Value < critical)
            {
                continue;
            }

            if (_warned.TryGetValue(sensor.Key, out DateTimeOffset last)
                && (!_cooled.Contains(sensor.Key) || DateTimeOffset.Now - last < Again))
            {
                continue;
            }

            _warned[sensor.Key] = DateTimeOffset.Now;
            _cooled.Remove(sensor.Key);

            string name = new SensorNames(_settings.Current.Sensors.Names).For(sensor);

            _log.LogInformation(
                "heat.warned {Sensor} at {Value} (critical {Critical})", sensor.Key.Value, reading.Value, critical);

            Notices.Show(
                "heat-" + sensor.Key.Value,
                string.Format(CultureInfo.CurrentCulture, Loc.Tr("HeatTitle", "{0} is overheating"), name),
                string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("HeatText", "{0:0} °C, past the {1:0} °C its maker calls critical."),
                    reading.Value,
                    critical),
                (Loc.Tr("HeatQuiet", "Not today"), "heat-quiet"));
        }
    }
}
