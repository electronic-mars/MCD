using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// A stretch of empty bar.
/// </summary>
/// <remarks>
/// This is how things are placed. There are no regions on a bar, only one run
/// of widgets, and where they sit is decided by spacers that take up the free
/// length: one spacer in the middle splits the bar in two, two make a centred
/// group, and none packs everything to the start. Nothing is drawn at rest -
/// while a widget is being dragged, each spacer shows itself so it can be
/// grabbed and moved like anything else.
/// </remarks>
public sealed partial class SpacerWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.spacer";

    public override string TypeId => Type;

    /// <summary>Whether this takes a share of the free length, or a set width.</summary>
    public bool Expands => WidgetOptions.Text(Options, "mode") != "fixed";

    /// <summary>The width when it is fixed, in effective pixels.</summary>
    public double Dips => WidgetOptions.Number(Options, "dips") ?? 24;

    /// <summary>Shown while a drag is under way, so the spacer can be aimed at.</summary>
    [ObservableProperty]
    public partial Visibility HintVisible { get; set; } = Visibility.Collapsed;

    /// <summary>How much of the bar's length this asks for.</summary>
    public GridLength Length => Expands
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(Dips);

    public override void Tick(SensorSnapshot snapshot)
    {
    }

    public override string Summarise() => Expands
        ? Loc.Tr("SpacerStretches", "Stretches to take the free space")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Loc.Tr("SpacerFixedSummary", "A fixed gap, {0} across"),
            Dips.ToString("0", System.Globalization.CultureInfo.CurrentCulture));

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var mode = new ComboBox
        {
            Header = Loc.Tr("SpacerWidthHeader", "Width"),
            Items =
            {
                Loc.Tr("SpacerStretches", "Stretches to take the free space"),
                Loc.Tr("SpacerFixed", "Fixed"),
            },
            SelectedIndex = Expands ? 0 : 1,
        };

        var width = new NumberBox
        {
            Header = Loc.Tr("SpacerPixels", "Pixels across"),
            Minimum = 4,
            Maximum = 600,
            SmallChange = 4,
            Value = Dips,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Visibility = Expands ? Visibility.Collapsed : Visibility.Visible,
        };

        void Save()
        {
            bool expands = mode.SelectedIndex == 0;
            width.Visibility = expands ? Visibility.Collapsed : Visibility.Visible;

            changed(WidgetOptions.Merge(
                Options,
                ("mode", expands ? null : "fixed"),
                ("dips", expands ? null : (int)Math.Max(4, width.Value))));
        }

        mode.SelectionChanged += (_, _) => Save();
        width.ValueChanged += (_, _) => Save();

        return new StackPanel { Spacing = 8, Children = { mode, width } };
    }
}
