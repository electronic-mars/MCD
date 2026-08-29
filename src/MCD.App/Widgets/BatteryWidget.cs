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

    /// <summary>A battery, a battery filling up, or nothing at all.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; } = "Battery";

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

    [ObservableProperty]
    public partial double FontSize { get; set; } = 12;

    /// <summary>
    /// This machine runs on a battery, and the battery is still in it.
    /// </summary>
    public override bool Matters => _state.Present;

    public override void Attach() => Tick(SensorSnapshot.Empty);

    public override void Tick(SensorSnapshot snapshot)
    {
        _state = Power.Read();

        if (!_state.Present)
        {
            return;
        }

        IconSize = DockMetrics.ReadingIcon(Density);
        FontSize = DockMetrics.ReadingFont(Density);

        Icon = _state.Charging ? "BatteryCharging"
            : _state.Percent < 0 ? "Battery"
            : _state.Percent <= Low ? "BatteryLow"
            : _state.Percent >= 90 ? "BatteryFull"
            : "Battery";

        Colour = !_state.Plugged && _state.Percent >= 0 && _state.Percent <= Low
            ? Alarmed
            : Ink;

        bool room = Density == DockDensity.Default
            && Orientation == Microsoft.UI.Xaml.Controls.Orientation.Horizontal;

        LevelVisible = room ? Visibility.Visible : Visibility.Collapsed;

        Level = _state.Percent < 0
            ? "--"
            : _state.Percent.ToString("F0", CultureInfo.InvariantCulture) + " %";

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

        return charge + " · " + string.Format(
            CultureInfo.CurrentCulture,
            Loc.Tr("BatteryLeft", "about {0} h {1} min left"),
            _state.Minutes / 60,
            _state.Minutes % 60);
    }

    public override string Summarise() => Loc.Tr("WidgetBatteryName", "Battery");

    /// <summary>
    /// Sized for "100 %" whether it says that or not, so that going from
    /// 99 to 100 does not shuffle everything to the right of it along.
    /// </summary>
    public override double Length()
    {
        if (Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical)
        {
            return 30;
        }

        double along = DockMetrics.ReadingIcon(Density) + 12;

        if (LevelVisible == Visibility.Visible)
        {
            along += Metric.Wide("100 %", DockMetrics.ReadingFont(Density)) + 6;
        }

        return along;
    }

    public override FrameworkElement? CreateEditor(Action<JsonElement?> changed) => null;
}
