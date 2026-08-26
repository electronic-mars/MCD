using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// One reading as it appears on the bar: an icon, a number, and a colour that
/// means something.
/// </summary>
public sealed partial class Metric : ObservableObject
{
    /// <summary>
    /// How far a reading must fall back below a threshold before the colour
    /// returns to normal.
    /// </summary>
    /// <remarks>
    /// Without a margin, a value sitting on its warning point flips colour once
    /// a second. A colour changing at the edge of vision is more distracting
    /// than whatever it is trying to report.
    /// </remarks>
    private const double Hysteresis = 2;

    private Level _level = Level.Normal;

    public Metric(
        string id,
        string defaultIcon,
        string label,
        string unit,
        Func<SensorHub, SensorDescriptor?> find)
    {
        Id = id;
        Icon = defaultIcon;
        Label = label;
        Detail = label;
        Unit = unit;
        _find = find;
    }

    private readonly Func<SensorHub, SensorDescriptor?> _find;

    /// <summary>
    /// Stable name for this reading, used as the key of the icon a person chose.
    /// </summary>
    /// <remarks>
    /// Not the label. Labels are shown to people and will be translated; a
    /// settings key that changes with the interface language is a settings key
    /// that forgets what it was for.
    /// </remarks>
    public string Id { get; }

    /// <summary>The icon to draw, by name. See <see cref="IconLibrary"/>.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; }

    public string Label { get; }

    /// <summary>
    /// What the tooltip says under the value: the hardware when it is known,
    /// the reading's own name otherwise.
    /// </summary>
    /// <remarks>
    /// "68 °C / NVIDIA GeForce RTX 4090" tells someone which of three
    /// temperatures they are looking at; "68 °C / Temperature" tells them
    /// nothing they could not already see.
    /// </remarks>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    public string Unit { get; }

    /// <summary>The sensor behind this, once one has been found for it.</summary>
    public SensorDescriptor? Sensor { get; private set; }

    /// <summary>
    /// Chooses the icon once the sensor turns up, when the icon depends on which
    /// hardware answered.
    /// </summary>
    /// <remarks>
    /// A chosen temperature is stored as a sensor key, and a key does not say
    /// whether it belongs to a processor, a drive or a board. Waiting for the
    /// sensor itself beats decoding the key, and the sensor may arrive seconds
    /// after the dock does.
    /// </remarks>
    public Func<SensorDescriptor, string>? IconFound { get; init; }

    [ObservableProperty]
    public partial string Text { get; set; } = "--";

    [ObservableProperty]
    public partial Brush Colour { get; set; } = Neutral;

    /// <summary>
    /// SemiBold while the reading is past its critical point.
    /// </summary>
    /// <remarks>
    /// The shape cue behind the colour cue: on a busy wallpaper behind thin
    /// acrylic a red can lose a point or two of contrast, and for some eyes
    /// the amber-to-red step barely exists. Critical speaks; warning whispers.
    /// </remarks>
    [ObservableProperty]
    public partial Windows.UI.Text.FontWeight ValueWeight { get; set; } =
        Microsoft.UI.Text.FontWeights.Normal;

    [ObservableProperty]
    public partial Visibility Visibility { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// Whether an ordinary reading takes the accent colour Windows is set to.
    /// </summary>
    /// <remarks>
    /// Only the ordinary state. Warm and hot keep their own colours whatever
    /// the accent is: a temperature past its limit that matched the rest of the
    /// bar would be a warning nobody sees.
    /// </remarks>
    public bool Accent { get; init; }

    /// <summary>How big the icon is drawn, in effective pixels.</summary>
    [ObservableProperty]
    public partial double IconSize { get; set; } = 17;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 13;

    /// <summary>
    /// The width kept for the number, whatever the number happens to be.
    /// </summary>
    /// <remarks>
    /// A network rate goes from "0 B/s" to "12.4 MB/s" and back within a second.
    /// Left to size itself, the reading takes the width it needs and every
    /// reading after it on the bar shuffles sideways to make room - which is
    /// far more distracting than the figure it is reporting.
    /// </remarks>
    [ObservableProperty]
    public partial double ValueWidth { get; set; }

    /// <summary>
    /// The reading's name, under the figure, as PowerToys shows it.
    /// </summary>
    /// <remarks>
    /// Only on the full-size bar. That is what the size setting means in
    /// PowerToys - "subtitles of dock items are hidden in compact mode" - and
    /// without it the two sizes here differed only in how thick the bar was,
    /// which is not a choice worth offering.
    /// </remarks>
    [ObservableProperty]
    public partial Visibility LabelVisible { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial double LabelFontSize { get; set; } = 10;

    /// <summary>
    /// The gap to the next reading, on whichever side that is.
    /// </summary>
    /// <remarks>
    /// A fixed right margin puts the space in the wrong place on a vertical bar,
    /// where readings sit above one another.
    /// </remarks>
    [ObservableProperty]
    public partial Thickness Spacing { get; set; } = new(2, 0, 2, 0);

    /// <summary>
    /// Sizes this reading for the bar it is going on.
    /// </summary>
    /// <param name="narrow">
    /// True on a bar down the side of a screen. Those are 86 points wide and
    /// nothing else; a network rate written out in full needs a hundred and
    /// seven of them, and what does not fit is simply cut off.
    /// </param>
    public void SizeFor(double icon, double font, bool narrow = false, bool subtitle = false)
    {
        IconSize = icon;
        FontSize = font;
        Narrow = narrow;
        LabelVisible = subtitle ? Visibility.Visible : Visibility.Collapsed;

        // Allowance per character for the interface face at this size. Digits
        // in it run to about 0.55 em and the space and per-cent sign a little
        // more; the extra keeps the reserved box from cutting a value that
        // happens to be all eights.
        ValueWidth = Math.Ceiling(Widest() * font * 0.60);
    }

    /// <summary>Whether this is on a bar with no room for a unit spelled out.</summary>
    public bool Narrow { get; private set; }

    /// <summary>How many characters the largest sensible value takes.</summary>
    private int Widest() => Unit switch
    {
        "B/s" => Narrow ? 6 : 9,    // "12.4M" against "99.9 MB/s"
        "°C" => 6,                  // "100 °C"
        "MHz" => 8,                 // "5900 MHz"
        _ => 5,                     // "100 %"
    };

    public void Update(SensorHub sensors, SensorSnapshot snapshot)
    {
        // Looked up again while it is missing, not once at creation. A graphics
        // driver that finishes loading after the dock is already on screen, or
        // an external monitor program started later, must fill its chip in
        // without the dock being rebuilt.
        if (Sensor is null && _find(sensors) is { } found)
        {
            Sensor = found;

            if (IconFound is not null)
            {
                Icon = IconFound(found);
            }

            Detail = found.Hardware.Length > 0 && found.Hardware != found.Label
                ? $"{found.Label} · {found.Hardware}"
                : found.Label;
        }

        if (Sensor is null)
        {
            // Hardware this machine does not have. An empty chip that never
            // fills in looks like a fault; its absence does not.
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;

        SensorReading reading = snapshot[Sensor.Key];

        Text = reading.HasValue ? Format(reading.Value) : "--";
        Colour = Paint(reading);
        ValueWeight = _level == Level.Critical
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private string Format(double value) => Unit switch
    {
        // Rates are the only reading that needs a unit chosen per value; a
        // network figure spends most of its life in the kilobytes and its
        // interesting moments in the megabytes.
        "B/s" => Rate(value),
        _ => value.ToString("F0", CultureInfo.InvariantCulture) + " " + Unit,
    };

    private string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => (bytesPerSecond / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture)
                          + (Narrow ? "M" : " MB/s"),
        >= 1024 => (bytesPerSecond / 1024).ToString("F0", CultureInfo.InvariantCulture)
                   + (Narrow ? "k" : " kB/s"),
        _ => bytesPerSecond.ToString("F0", CultureInfo.InvariantCulture) + (Narrow ? string.Empty : " B/s"),
    };

    private Brush Paint(SensorReading reading)
    {
        if (!reading.HasValue)
        {
            _level = Level.Normal;
            return Dimmed;
        }

        Level raw = Raw(reading.Value);

        // Rising takes effect at once; falling has to clear the threshold by the
        // margin first.
        _level = raw > _level ? raw : Falling(reading.Value);

        return _level switch
        {
            Level.Critical => Hot,
            Level.Warning => Warm,
            _ => reading.Quality == Quality.Stale ? Dimmed : (Accent ? Accented : Neutral),
        };
    }

    private Level Raw(double value) =>
        Sensor!.Critical is { } critical && value >= critical ? Level.Critical
        : Sensor.Warning is { } warning && value >= warning ? Level.Warning
        : Level.Normal;

    private Level Falling(double value)
    {
        double? floor = _level switch
        {
            Level.Critical => Sensor!.Critical,
            Level.Warning => Sensor!.Warning,
            _ => null,
        };

        return floor is { } limit && value < limit - Hysteresis ? Raw(value) : _level;
    }

    private static Brush Neutral =>
        (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private static Brush Accented =>
        (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

    private static Brush Dimmed =>
        (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    /// <summary>
    /// Fluent's own caution and critical colours, which follow the theme:
    /// deep amber and a firm red on a light bar, brighter ones on a dark bar.
    /// </summary>
    private static Brush Warm =>
        (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

    private static Brush Hot =>
        (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

    private enum Level
    {
        Normal,
        Warning,
        Critical,
    }
}
