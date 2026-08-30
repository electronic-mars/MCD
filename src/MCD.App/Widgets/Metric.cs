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

    /// <summary>
    /// The name under the figure.
    /// </summary>
    /// <remarks>
    /// Settable, because a roving reading is about whichever part answers the
    /// question today, and a name that says "Temperature" over a figure whose
    /// subject keeps changing tells nobody whose temperature it is.
    /// </remarks>
    [ObservableProperty]
    public partial string Label { get; set; }

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

    /// <summary>True on a Braun-painted bar, where the accent is the orange.</summary>
    public bool Braun { get; init; }

    /// <summary>
    /// Whether the sensor is chosen afresh on every tick.
    /// </summary>
    /// <remarks>
    /// False for a reading that is about one named part: found once and kept,
    /// because the processor does not become a different processor. True where
    /// the widget is about a question rather than a part - "whichever is
    /// closest to its limit" - and the answer is meant to change.
    /// </remarks>
    public bool Roving { get; init; }

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
    public partial double LabelFontSize { get; set; } = 11;

    /// <summary>Shown while the reading is past its critical point.</summary>
    /// <remarks>
    /// The shape behind the colour: for some eyes the amber-to-red step
    /// barely exists, and a triangle is legible to everyone.
    /// </remarks>
    [ObservableProperty]
    public partial Visibility AlertVisible { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// Blinked three times as the reading crosses into critical, then steady.
    /// </summary>
    /// <remarks>
    /// One beat per tick. A permanent bar lives in peripheral vision, and a
    /// looping animation there is hostile - the entrance is announced once.
    /// </remarks>
    [ObservableProperty]
    public partial double AlertOpacity { get; set; } = 1;

    private int _alertBeats;

    /// <summary>This widget's own warning point, when one was set for it.</summary>
    public double? WarnAt { get; set; }

    /// <summary>This widget's own critical point, when one was set for it.</summary>
    public double? CritAt { get; set; }

    /// <summary>
    /// A small optical lift for the two-line block.
    /// </summary>
    /// <remarks>
    /// Geometrically the block is centred already, but the value line carries
    /// an empty ascender zone above its digits and the label line puts its
    /// descenders below, so the ink sits visibly low - measured at three
    /// pixels on a full-size bar. Two of margin brings the ink to the middle.
    /// One line alone is symmetrical and gets no lift.
    /// </remarks>
    [ObservableProperty]
    public partial Thickness Lift { get; set; }

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
        // Minus on top, plus underneath: a negative top margin alone shrinks
        // the box and the centring eats half the shift.
        Lift = subtitle ? new Thickness(0, -2, 0, 2) : new Thickness(0, -1, 0, 1);

        // The box starts empty and grows to the widest value actually seen,
        // and what the fixed parts measure is asked again at the new size.
        ValueWidth = 0;
        _sampleWide = null;
        _labelWide = null;
        Reserve();
    }

    /// <summary>Whether this is on a bar with no room for a unit spelled out.</summary>
    public bool Narrow { get; private set; }

    /// <summary>
    /// Keeps the number's box as wide as the widest value seen this session.
    /// </summary>
    /// <remarks>
    /// Wide enough that the row never shuffles sideways when a figure grows,
    /// and no wider: a box sized in advance for a rate that may never come
    /// leaves a dead stretch of bar that reads as a ragged gap - which is
    /// exactly what it did. Growth is immediate and kept; the box never
    /// shrinks while the dock is up, and the value sits centred in whatever
    /// slack is left. Measured with a real TextBlock rather than estimated
    /// per character: the estimate over-reserved on narrow glyphs and the
    /// spare width read as more ragged gap. Measured at SemiBold, which is
    /// what critical readings switch to - a box that fits the ordinary weight
    /// only would clip the reading at the moment it matters most.
    /// </remarks>
    private void Reserve()
    {
        Ruler.FontSize = FontSize;
        Ruler.Text = Text;
        Ruler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        ValueWidth = Math.Max(ValueWidth, Math.Ceiling(Ruler.DesiredSize.Width) + 1);
    }

    /// <summary>
    /// What the fixed parts of this chip measure, once they have been asked
    /// for. Cleared by <see cref="SizeFor"/>, which is the only thing that can
    /// change them.
    /// </summary>
    private double? _sampleWide;
    private double? _labelWide;

    /// <summary>One shared, never-shown TextBlock, used only to measure.</summary>
    private static readonly Microsoft.UI.Xaml.Controls.TextBlock Ruler = new()
    {
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    /// <summary>
    /// A figure as long as this reading is ever likely to show, so the slots it
    /// takes are decided once rather than the first time it says "--".
    /// </summary>
    public string Sample { get; set; } = "100 %";

    /// <summary>How wide this chip is, in effective pixels.</summary>
    /// <remarks>
    /// Worked out rather than measured off the screen: what a widget needs has
    /// to be known before the bar is laid out, and a control that has not been
    /// drawn yet measures as nothing. The parts are the ones the template puts
    /// side by side - chip padding, the icon, the gap, and the widest of the
    /// figure's box, the sample and the reading's name.
    /// </remarks>
    public double Width()
    {
        // The sample and the name are fixed from the moment SizeFor runs, so
        // they are measured once and kept. Measuring them again on every tick
        // was two thirds of everything this program did while idle.
        _sampleWide ??= Wide(Sample, FontSize);
        _labelWide ??= Math.Min(100, Wide(Label, LabelFontSize));

        double value = Math.Max(ValueWidth, _sampleWide.Value);
        double label = LabelVisible == Visibility.Visible ? _labelWide.Value : 0;

        // Chip padding 3 either side, its margins, icon, the 6-point gap.
        return Mcd.App.Dock.DockMetrics.ChipPadding
            + Spacing.Left + Spacing.Right + IconSize + 6 + Math.Max(value, label);
    }

    /// <summary>How tall this chip is, in effective pixels.</summary>
    /// <remarks>
    /// What a bar down the side of a screen needs to know. The chip is as tall
    /// as the taller of its icon and its two lines of text, plus the padding
    /// the template gives it.
    /// </remarks>
    public double Height()
    {
        double lines = (FontSize * 1.4)
            + (LabelVisible == Visibility.Visible ? LabelFontSize * 1.4 : 0);

        return 6 + Spacing.Top + Spacing.Bottom + Math.Max(IconSize, lines);
    }

    /// <summary>
    /// How wide a piece of text is, measured rather than estimated.
    /// </summary>
    /// <remarks>
    /// Shared with anything else that has to declare its length before it is
    /// drawn - the clock, whose slot must fit the longest time it can ever
    /// show rather than the one it shows now.
    /// </remarks>
    public static double Wide(string text, double size)
    {
        Ruler.FontSize = size;
        Ruler.Text = text;
        Ruler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        return Math.Ceiling(Ruler.DesiredSize.Width) + 1;
    }

    public void Update(SensorHub sensors, SensorSnapshot snapshot)
    {
        // Looked up again while it is missing, not once at creation. A graphics
        // driver that finishes loading after the dock is already on screen, or
        // an external monitor program started later, must fill its chip in
        // without the dock being rebuilt.
        //
        // And looked up every time when the question itself is "which one",
        // rather than "where is this one". Kept once, the widget that shows
        // whichever part is closest to its limit answered that question on the
        // first second after the program started - on a cold machine doing
        // nothing - and never asked again.
        if ((Sensor is null || Roving) && _find(sensors) is { } found && !ReferenceEquals(found, Sensor))
        {
            Sensor = found;

            if (IconFound is not null)
            {
                Icon = IconFound(found);
            }

            Detail = found.Hardware.Length > 0 && found.Hardware != found.Label
                ? $"{found.Label} · {found.Hardware}"
                : found.Label;

            if (Roving)
            {
                Label = found.Label;

                // The name changed, so the width reserved for it is a width for
                // a word that is no longer there.
                _labelWide = null;
            }

            Reserve();
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

        string shown = reading.HasValue ? Format(reading.Value) : "--";

        // Measured only when the digits actually moved. A reading that says
        // the same thing this second as last needs no text layout, and this
        // runs once a second for every widget on every bar for as long as the
        // program is up.
        if (shown != Text)
        {
            Text = shown;
            Reserve();
        }
        Colour = Paint(reading);

        bool critical = _level == Level.Critical;

        ValueWeight = critical
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        if (critical && AlertVisible == Visibility.Collapsed)
        {
            _alertBeats = 5;
        }

        AlertVisible = critical ? Visibility.Visible : Visibility.Collapsed;

        if (critical && _alertBeats > 0)
        {
            _alertBeats--;
            AlertOpacity = _alertBeats % 2 == 0 ? 1 : 0.35;
        }
        else
        {
            AlertOpacity = 1;
        }
    }

    // Anything counted in bytes - a rate, or the memory installed in the
    // machine - picks its unit from the value. Shared with the settings
    // window, which shows the same readings in a list.
    private string Format(double value) => Readable.Value(value, Unit, Narrow);

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
        (CritAt ?? Sensor!.Critical) is { } critical && value >= critical ? Level.Critical
        : (WarnAt ?? Sensor!.Warning) is { } warning && value >= warning ? Level.Warning
        : Level.Normal;

    private Level Falling(double value)
    {
        double? floor = _level switch
        {
            Level.Critical => CritAt ?? Sensor!.Critical,
            Level.Warning => WarnAt ?? Sensor!.Warning,
            _ => null,
        };

        return floor is { } limit && value < limit - Hysteresis ? Raw(value) : _level;
    }

    private static Brush Neutral =>
        (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private Brush Accented => Braun
        ? (Brush)Application.Current.Resources["McdReadingAccent"]
        : (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

    private static Brush Dimmed =>
        (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    /// <summary>
    /// Fluent's own caution and critical colours, which follow the theme:
    /// deep amber and a firm red on a light bar, brighter ones on a dark bar.
    /// </summary>
    private static Brush Warm =>
        (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

    private static Brush Hot =>
        (Brush)Application.Current.Resources["McdCriticalBrush"];

    private enum Level
    {
        Normal,
        Warning,
        Critical,
    }
}
