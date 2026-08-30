using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// How much charge is left, on a machine that runs on charge.
/// </summary>
/// <remarks>
/// <para>
/// The first thing anybody wants on a laptop and the one thing that means
/// nothing on a desktop, which is the whole reason widgets are allowed to say
/// whether they are about anything. On a machine with no battery this one
/// never appears, is never offered, and holds no slot.
/// </para>
/// <para>
/// It goes red below fifteen percent rather than at some rounder figure: that
/// is where Windows itself starts warning, and two programs on one screen
/// disagreeing about when to worry is worse than either threshold.
/// </para>
/// </remarks>
public sealed partial class BatteryWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.battery";

    /// <summary>Where Windows itself starts calling it low.</summary>
    private const int Low = 15;

    private BatteryState _state = BatteryState.None;

    public override string TypeId => Type;

    /// <summary>
    /// How much of the body is filled, in the units the drawing is made of.
    /// </summary>
    /// <remarks>
    /// The whole point of a battery drawn rather than chosen: the set has
    /// four of them, so eighty-five per cent and twenty looked the same and
    /// charging showed no level at all. Fourteen units of room inside the
    /// body, and never quite nothing, so an almost-flat battery still reads
    /// as a battery rather than as an empty box.
    /// </remarks>
    [ObservableProperty]
    public partial double Fill { get; set; } = 14;

    /// <summary>Whether the bolt is drawn over the level.</summary>
    [ObservableProperty]
    public partial Visibility Charging { get; set; } = Visibility.Collapsed;

    /// <summary>How full, as a percentage.</summary>
    [ObservableProperty]
    public partial string Level { get; set; } = "--";

    /// <summary>What a hover says: the figure spelled out, and how long is left.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility LevelVisible { get; set; } = Visibility.Visible;

    /// <summary>
    /// The colour the icon and the figure are drawn in.
    /// </summary>
    /// <remarks>
    /// The same two brushes the readings use, resolved the same way, so that a
    /// bar where the processor has gone red and the battery has gone red shows
    /// one red rather than two.
    /// </remarks>
    [ObservableProperty]
    public partial Brush Colour { get; set; } = Ink;

    private static Brush Ink =>
        (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    private static Brush Alarmed =>
        (Brush)Application.Current.Resources["McdCriticalBrush"];

    [ObservableProperty]
    public partial double IconSize { get; set; } = 16;

    /// <summary>
    /// The stroke to draw the icon with, so that it lands on the same weight
    /// as the readings however big it is drawn.
    /// </summary>
    /// <remarks>
    /// The icon sits in a Viewbox that scales a 24-unit drawing to whatever
    /// size the bar's thickness asks for, and the stroke scales with it. A
    /// fixed 1.7 therefore came out heavier beside a reading drawn at 1.5 -
    /// seven different weights across the program, from 1.13 to 1.70.
    /// </remarks>
    [ObservableProperty]
    public partial double Stroke { get; set; } = 1.5;


    [ObservableProperty]
    public partial double FontSize { get; set; } = 12;

    /// <summary>
    /// This machine runs on a battery, and the battery is still in it.
    /// </summary>
    public override bool Matters => _state.Present;

    /// <summary>A machine with no battery is not going to grow one.</summary>
    public override bool Possible => _state.Present;

    public override void Attach()
    {
        _figure = null;
        Tick(SensorSnapshot.Empty);
    }

    /// <summary>
    /// The width of the figure beside the icon, measured once.
    /// </summary>
    /// <remarks>
    /// Length is asked for every second, and this part of it is a constant:
    /// the same string in the same size. Measuring text costs a full layout
    /// pass through the text engine, and doing it on every tick for every
    /// widget on every bar was two thirds of everything this program did
    /// while idle - a lesson learnt once in the readings and not carried
    /// across to here.
    /// </remarks>
    private double? _figure;


    public override void Tick(SensorSnapshot snapshot)
    {
        _state = Power.Read();

        if (!_state.Present)
        {
            return;
        }

        IconSize = ReadingIcon;
        Stroke = 36 / Math.Max(1, IconSize);
        FontSize = ReadingFont;

        // Unknown reads as full rather than as empty: a battery whose level
        // the firmware will not give is not a flat battery, and drawing it
        // flat would be the one mistake that sends somebody looking for a
        // socket they do not need.
        double share = _state.Percent < 0 ? 1 : _state.Percent / 100.0;

        Fill = Math.Max(1.5, 14 * share);
        Charging = _state.Charging ? Visibility.Visible : Visibility.Collapsed;

        Colour = !_state.Plugged && _state.Percent >= 0 && _state.Percent <= Low
            ? Alarmed
            : Ink;

        // The figure stays on a compact bar and down the side of a screen -
        // the two shapes somebody chooses when the screen is small, which is
        // where the charge matters most. Only the per-cent sign goes: three
        // characters instead of five, and the drawing beside it says what
        // kind of figure it is.
        bool roomy = Density == DockDensity.Default
            && Orientation == Microsoft.UI.Xaml.Controls.Orientation.Horizontal;

        LevelVisible = Visibility.Visible;

        Level = _state.Percent < 0
            ? "--"
            : _state.Percent.ToString("F0", CultureInfo.InvariantCulture)
                + (roomy ? " %" : string.Empty);

        Detail = Says();
    }

    /// <summary>
    /// The whole story, for a hover.
    /// </summary>
    /// <remarks>
    /// The time remaining is only shown when the machine gives one. Firmware
    /// withholds it for the first minutes after a cable is pulled out, and an
    /// estimate invented to fill the gap would be wrong exactly when somebody
    /// is deciding whether to go and find a socket.
    /// </remarks>
    private string Says()
    {
        string charge = _state.Percent < 0
            ? Loc.Tr("BatteryUnknown", "Charge not reported")
            : string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("BatteryCharge", "{0}% of a charge"),
                _state.Percent);

        if (_state.Charging)
        {
            return charge + " · " + Loc.Tr("BatteryCharging", "charging");
        }

        if (_state.Plugged)
        {
            return charge + " · " + Loc.Tr("BatteryPlugged", "on the mains");
        }

        if (_state.Minutes < 0)
        {
            return charge;
        }

        // Under an hour is said in minutes alone. "About 0 h 25 min left" is
        // a sentence written by an arithmetic expression, and it is read at
        // exactly the moment somebody is deciding whether to find a socket.
        if (_state.Minutes < 60)
        {
            return charge + " · " + string.Format(
                CultureInfo.CurrentCulture,
                Loc.Tr("BatteryLeftMinutes", "about {0} min left"),
                _state.Minutes);
        }

        return charge + " · " + string.Format(
            CultureInfo.CurrentCulture,
            Loc.Tr("BatteryLeft", "about {0} h {1} min left"),
            _state.Minutes / 60,
            _state.Minutes % 60);
    }

    /// <summary>
    /// What it is and what it says, in one line.
    /// </summary>
    /// <remarks>
    /// Read out by a screen reader, so it carries the figure as well as the
    /// name: "Battery" alone is a label on an empty box.
    /// </remarks>
    public override string Summarise() =>
        Loc.Tr("WidgetBatteryName", "Battery") + (Detail.Length > 0 ? " · " + Detail : string.Empty);

    /// <summary>
    /// Sized for "100 %" whether it says that or not, so that going from
    /// 99 to 100 does not shuffle everything to the right of it along.
    /// </summary>
    public override double Length()
    {
        _figure ??= Metric.Wide(
            Density == DockDensity.Default
                && Orientation == Microsoft.UI.Xaml.Controls.Orientation.Horizontal
                ? "100 %"
                : "100",
            ReadingFont);

        // Down the side of a screen the figure goes under the drawing rather
        // than beside it, so the length is the height of the two.
        if (Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical)
        {
            return ReadingIcon + ReadingFont + 12;
        }

        return ReadingIcon + 6 + DockMetrics.ChipPadding + _figure.Value + 6;
    }

    public override FrameworkElement? CreateEditor(Action<JsonElement?> changed) => null;
}
