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
/// The wireless network, on a machine that is using one.
/// </summary>
/// <remarks>
/// <para>
/// Signal strength is the reading somebody actually wants: a network's name
/// they already know, and whether they are connected they can see from
/// everything else on the screen working. What they cannot see is that the
/// call is breaking up because they have walked two rooms too far.
/// </para>
/// <para>
/// The widget is about nothing while the machine is on a cable, and takes no
/// slot then. Plug the cable in and it steps out of the bar; pull it out and
/// it comes back to the same place, or the first free one.
/// </para>
/// </remarks>
public sealed partial class WifiWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.wifi";

    private WirelessState _state = WirelessState.None;

    public override string TypeId => Type;

    /// <summary>One of four aerials, by how much signal there is.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; } = "WifiHigh";

    /// <summary>The network's name, on a bar with room to write it.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>What a hover says: the network, and how good the signal is.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NameVisible { get; set; } = Visibility.Collapsed;

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

    /// <summary>This machine is reaching the internet wirelessly.</summary>
    public override bool Matters => _state.Wireless;

    /// <summary>There is a wireless card, whether or not it is being used.</summary>
    public override bool Possible => Mcd.Interop.Machine.Wireless.Fitted();

    /// <summary>Whether the network's name is written beside the aerial.</summary>
    private bool WithName => WidgetOptions.Number(Options, "name") is not 0;

    public override void Attach() => Tick(SensorSnapshot.Empty);

    public override void Tick(SensorSnapshot snapshot)
    {
        _state = Wireless.Read();

        if (!_state.Wireless)
        {
            return;
        }

        IconSize = DockMetrics.ReadingIcon(Density);
        FontSize = DockMetrics.ReadingFont(Density);

        Icon = _state.Bars switch
        {
            >= 4 => "WifiHigh",
            3 => "WifiMid",
            >= 1 => "WifiLow",
            0 => "WifiNone",
            _ => "WifiHigh",
        };

        // Two bars out of five is where a video call starts stuttering, which
        // is the moment worth colouring - not the moment it drops entirely,
        // by which time nobody needs telling.
        Colour = _state.Bars >= 0 && _state.Bars <= 2 ? Alarmed : Ink;

        bool room = WithName
            && Density == DockDensity.Default
            && Orientation == Microsoft.UI.Xaml.Controls.Orientation.Horizontal;

        NameVisible = room ? Visibility.Visible : Visibility.Collapsed;
        Name = room ? Shorten(_state.Name) : string.Empty;

        Detail = _state.Name.Length > 0
            ? _state.Name + (_state.Bars >= 0
                ? " · " + string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc.Tr("WifiBars", "signal {0} of 5"),
                    _state.Bars)
                : string.Empty)
            : Loc.Tr("WifiConnected", "On a wireless network");
    }

    /// <summary>
    /// The name, cut to something a bar can hold.
    /// </summary>
    /// <remarks>
    /// Cut rather than allowed to run: a network called after the router's
    /// serial number would take a quarter of the screen, and the first few
    /// characters are what tells somebody which of their two networks they
    /// are on.
    /// </remarks>
    private static string Shorten(string name) =>
        name.Length <= Longest ? name : name[..Longest] + "…";

    private const int Longest = 12;

    public override string Summarise() => Loc.Tr("WidgetWifiName", "Wi-Fi");

    public override double Length()
    {
        if (Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical)
        {
            return 30;
        }

        double along = DockMetrics.ReadingIcon(Density) + 12;

        if (NameVisible == Visibility.Visible)
        {
            // Reserved for the longest name that will be drawn rather than for
            // this one, so that walking from one network to another does not
            // shove everything to the right of it along.
            along += Metric.Wide(new string('m', Longest), DockMetrics.ReadingFont(Density)) + 6;
        }

        return along;
    }

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed) =>
        Mcd.App.Settings.Braun.Field(
            Loc.Tr("WifiNameLabel", "Write the network's name"),
            Mcd.App.Settings.Braun.Switch(
                WithName,
                on => changed(WidgetJson.Object(("name", on ? 1 : 0)))),
            Loc.Tr("WifiNameHint", "Useful where two networks are within reach and both work."));
}
