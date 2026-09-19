using System.Collections.Immutable;
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

    /// <summary>
    /// The hover's second line: what the reading did over the last minute,
    /// and the worst it has been this session.
    /// </summary>
    [ObservableProperty]
    public partial string Story { get; set; } = string.Empty;

    /// <summary>Whether the hover has a story line yet.</summary>
    [ObservableProperty]
    public partial Visibility StoryShown { get; set; } = Visibility.Collapsed;

    /// <summary>The worst this reading has been since the program started watching it.</summary>
    private double _peak = double.MinValue;

    /// <summary>Story text is rebuilt on a slow beat, not every second.</summary>
    private uint _storyBeat;

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

    /// <summary>
    /// What to call the reading once its sensor is known, when somebody has
    /// a say in that.
    /// </summary>
    /// <remarks>
    /// The renaming pencil on the readings page changes a reading's name
    /// everywhere it appears - and the small caption under a figure on the
    /// bar is the place people actually mean when they rename "CPU" to
    /// something of their own. Null keeps the widget's built-in caption.
    /// </remarks>
    public Func<SensorDescriptor, string?>? NameFound { get; init; }

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

    /// <summary>
    /// The stroke that draws the icon at exactly a pixel and a half on
    /// screen, whatever the icon's size.
    /// </summary>
    /// <remarks>
    /// The standalone widgets already compute this; the reading chips wore a
    /// hardcoded 1.5 that the Viewbox then scaled, so a medium-size reading's
    /// icon came out lighter than the battery beside it - the seven-weights
    /// disease, back in one template.
    /// </remarks>
    public double Stroke => 36 / Math.Max(1, IconSize);

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
    /// <summary>The number by itself: "22", "1.2", or "--" while there is none.</summary>
    /// <remarks>
    /// Kept apart from what follows it so each can have a box of its own.
    /// One box for the pair moves the unit whenever the figure crosses ten,
    /// and the chip is centred in its slots, so that moved the icon too.
    /// </remarks>
    [ObservableProperty]
    public partial string Figure { get; set; } = "--";

    /// <summary>What follows the number: " %", " °C", " MB/s".</summary>
    [ObservableProperty]
    public partial string Suffix { get; set; } = string.Empty;

    /// <summary>The box the number is right-aligned in. Fixed for the chip's life.</summary>
    [ObservableProperty]
    public partial double FigureWidth { get; set; }

    /// <summary>The box the unit sits in, left-aligned. Fixed likewise.</summary>
    [ObservableProperty]
    public partial double SuffixWidth { get; set; }

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

    /// <summary>
    /// A battery drawn where the caption would be, instead of the caption.
    /// </summary>
    /// <remarks>
    /// For a device's charge, whose name is already in the hover: a word
    /// under the figure made the chip three times as wide as the figure.
    /// </remarks>
    public bool Badged { get; init; }

    /// <summary>The battery under the figure, filled as far as the charge.</summary>
    [ObservableProperty]
    public partial string Badge { get; set; } = "Battery";

    /// <summary>The battery's colour: quiet, and dimmed with the icon when the device is.</summary>
    [ObservableProperty]
    public partial Brush BadgeColour { get; set; } = Secondary;

    /// <summary>How big the battery under the figure is drawn.</summary>
    public double BadgeSize => 14;

    /// <summary>The badge's stroke, a pixel and a half on screen like the icon's.</summary>
    public double BadgeStroke => 36 / BadgeSize;

    public Visibility CaptionShown =>
        LabelVisible == Visibility.Visible && !Badged ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BadgeShown =>
        LabelVisible == Visibility.Visible && Badged ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>Whether lower is worse - a charge, where 5 % is the alarm and 100 % is fine.</summary>
    public bool Falls { get; init; }

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
    public partial Thickness Spacing { get; set; } = new(1, 0, 1, 0);

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
        OnPropertyChanged(nameof(Stroke));
        FontSize = font;
        Narrow = narrow;
        LabelVisible = subtitle ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(CaptionShown));
        OnPropertyChanged(nameof(BadgeShown));
        // Minus on top, plus underneath: a negative top margin alone shrinks
        // the box and the centring eats half the shift.
        Lift = subtitle ? new Thickness(0, -2, 0, 2) : new Thickness(0, -1, 0, 1);

        // The room this reading is entitled to, whatever it happens to say
        // this second: the sample's digits, and the widest unit it can turn
        // into. Decided here, once, and never touched by a value again.
        _labelWide = null;

        (string biggest, _) = Split(Sample);
        _figureBase = Wide(Eights(biggest), FontSize);
        FigureWidth = _figureBase;
        SuffixWidth = 0;

        foreach (string unit in Units())
        {
            SuffixWidth = Math.Max(SuffixWidth, Wide(unit, FontSize));
        }

        ValueWidth = FigureWidth + SuffixWidth;
        Reserve();
    }

    /// <summary>Whether this is on a bar with no room for a unit spelled out.</summary>
    public bool Narrow { get; private set; }

    /// <summary>
    /// Keeps the number's box as wide as the widest value seen this session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reserved by what the reading can ever show, not by what it shows this
    /// second. A box that follows the figure changes width every time the
    /// reading crosses ten - and since the chip is centred in the slots it
    /// holds, that width walked the icon, the unit and the caption sideways
    /// twice a minute on a busy processor. It also grew the widget's slots,
    /// which is where the undroppable stretches of bar came from.
    /// </para>
    /// <para>
    /// So the digits grow leftwards into room already kept for them, and the
    /// unit does not move at all. Measured at SemiBold, which is what
    /// critical readings switch to - a box that fits the ordinary weight only
    /// would clip the reading at the moment it matters most.
    /// </para>
    /// </remarks>
    private void Reserve()
    {
        (string figure, string suffix) = Split(Text);

        Figure = figure;
        Suffix = suffix;

        // Never narrower than the sample asked for, and wider only while
        // the figure is: the sample is two digits, because a box kept for
        // "100" stood a digit's width of air between the icon and every
        // ordinary reading, and the eye stopped pairing them. A reading that
        // does reach a hundred widens its chip for as long as it stays there.
        FigureWidth = Math.Max(_figureBase, Wide(Eights(figure), FontSize));
        SuffixWidth = Math.Max(SuffixWidth, Wide(suffix, FontSize));
        ValueWidth = FigureWidth + SuffixWidth;
    }

    /// <summary>Where the number stops and its unit begins.</summary>
    internal static (string Figure, string Suffix) Split(string shown)
    {
        int at = 0;

        while (at < shown.Length && (char.IsAsciiDigit(shown[at]) || shown[at] is '.' or '-'))
        {
            at++;
        }

        return (shown[..at], shown[at..]);
    }

    /// <summary>
    /// Every unit this reading can turn into, so the box is kept for the widest.
    /// </summary>
    /// <remarks>
    /// A rate walks from "B/s" through "kB/s" to "MB/s" as the traffic grows,
    /// and a unit that changes width takes the digits beside it along.
    /// </remarks>
    private string[] Units() => Unit switch
    {
        "B/s" => Narrow ? [" B", "k", "M"] : [" B/s", " kB/s", " MB/s"],
        "B" => Narrow ? [" B", "k", "M"] : [" B", " kB", " MB", " GB", " TB"],
        _ => [" " + Unit],
    };

    /// <summary>The figure with every digit at the widest a digit gets.</summary>
    internal static string Eights(string figure)
    {
        Span<char> priced = stackalloc char[figure.Length];

        for (int i = 0; i < figure.Length; i++)
        {
            priced[i] = char.IsAsciiDigit(figure[i]) ? '8' : figure[i];
        }

        return new string(priced);
    }

    /// <summary>
    /// What the caption measures, once it has been asked for. Cleared by
    /// <see cref="SizeFor"/> and by a rename, which are the only two things
    /// that can change it.
    /// </summary>
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
    public string Sample { get; set; } = "99 %";

    private double _figureBase;

    /// <summary>
    /// A degree ring on the icon's corner, for a chip whose icon only says
    /// of what: a processor's temperature and its load otherwise wear the
    /// same processor. In the figure's own colour - an amber one read as a
    /// warning.
    /// </summary>
    public bool Degrees { get; init; }

    public Visibility DegreesShown => Degrees ? Visibility.Visible : Visibility.Collapsed;

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
        // The name is fixed from the moment SizeFor runs, so it is measured
        // once and kept. Measuring it again on every tick was two thirds of
        // everything this program did while idle.
        _labelWide ??= Math.Min(100, Wide(Label, LabelFontSize));

        double value = FigureWidth + SuffixWidth;

        // A caption nobody will keep buys no width. Until the sensor turns
        // up the chip is captioned with a placeholder - the word
        // "Temperature", or the key itself for a reading that has gone - and
        // widths bought by those were held for the rest of the session:
        // a chip standing in a run half again its own width, with the air
        // beside it looking like a gap somebody left.
        double label = LabelVisible == Visibility.Visible && Sensor is not null && !Badged
            ? _labelWide.Value
            : 0;

        // Chip padding 3 either side, its margins, icon, the 6-point gap.
        return Mcd.App.Dock.DockMetrics.ChipPadding
            + Spacing.Left + Spacing.Right + IconSize + 4 + Math.Max(value, label);
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

            // The minute of history the hub keeps for whoever asks - and
            // until now nobody ever asked.
            sensors.Watch(found.Key);

            string? given = NameFound?.Invoke(found);
            string called = given ?? found.Label;

            Detail = found.Hardware.Length > 0 && found.Hardware != called
                ? $"{called} · {found.Hardware}"
                : called;

            if (Roving || given is not null)
            {
                Label = called;

                // The name changed, so the width reserved for it is a width for
                // a word that is no longer there.
                _labelWide = null;
            }

            Reserve();
        }

        if (Sensor is null)
        {
            // Not hidden - dashed. A chip that collapses itself while its
            // sensor is still loading is measured as nothing by its host on
            // the first pass, and that width was never reliably corrected:
            // the widget then painted itself in overflow with lopsided
            // margins, which is what the hover pill lit up. Whether a widget
            // belongs on this machine at all is the span machinery's call,
            // not the chip's.
            Text = "--";
            return;
        }

        Visibility = Visibility.Visible;

        SensorReading reading = snapshot[Sensor.Key];

        if (reading.HasValue && reading.Value > _peak)
        {
            _peak = reading.Value;
        }

        // The hover's second line: the minute's range and the session's
        // peak. Rebuilt every few seconds - it is read by a person hovering,
        // not by anyone at a glance.
        if (++_storyBeat % 5 == 0 && _peak > double.MinValue)
        {
            ImmutableArray<double> minute = snapshot.Trend(Sensor.Key);

            Story = minute.Length >= 5
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("MetricStory", "last minute {0}\u2013{1} \u00b7 peak {2}"),
                    Format(minute.Min()),
                    Format(minute.Max()),
                    Format(_peak))
                : string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("MetricPeak", "peak {0}"),
                    Format(_peak));

            StoryShown = Visibility.Visible;
        }

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

        if (Badged)
        {
            BadgeColour = !reading.HasValue || (_level == Level.Normal && reading.Quality == Quality.Stale)
                ? Dimmed
                : Secondary;
        }

        if (Badged && reading.HasValue)
        {
            Badge = reading.Value >= 60 ? "BatteryFull" : reading.Value >= 20 ? "Battery" : "BatteryLow";
        }

        bool critical = _level == Level.Critical;

        ValueWeight = critical
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        if (critical && AlertVisible == Visibility.Collapsed)
        {
            _alertBeats = 5;
        }

        // A badged chip's empty battery is its shape cue already, and the
        // chip keeps no room for the triangle beside it.
        AlertVisible = critical && !Badged ? Visibility.Visible : Visibility.Collapsed;

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
        (CritAt ?? Sensor!.Critical) is { } critical && Past(value, critical) ? Level.Critical
        : (WarnAt ?? Sensor!.Warning) is { } warning && Past(value, warning) ? Level.Warning
        : Level.Normal;

    private bool Past(double value, double limit) => Falls ? value <= limit : value >= limit;

    private Level Falling(double value)
    {
        double? floor = _level switch
        {
            Level.Critical => CritAt ?? Sensor!.Critical,
            Level.Warning => WarnAt ?? Sensor!.Warning,
            _ => null,
        };

        bool cleared = floor is { } limit
            && (Falls ? value > limit + Hysteresis : value < limit - Hysteresis);

        return cleared ? Raw(value) : _level;
    }

    private static Brush Neutral =>
        (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private Brush Accented => Braun
        ? (Brush)Application.Current.Resources["McdReadingAccent"]
        : (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

    private static Brush Secondary =>
        (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

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
