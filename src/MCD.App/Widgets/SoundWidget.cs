using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Dock;
using Mcd.Audio;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;

namespace Mcd.App.Widgets;

/// <summary>
/// Sound on or off, and how loud it is.
/// </summary>
/// <remarks>
/// <para>
/// The most-wanted button on any control surface: every survey of what
/// people put on one puts silencing the machine first. It is also the one
/// thing a bar can do that a bar full of readings cannot - a number tells
/// you something, and this changes something.
/// </para>
/// <para>
/// The whole widget hides on a machine with no playback device rather than
/// showing a speaker that does nothing. An empty chip that never fills in
/// looks like a fault; its absence does not.
/// </para>
/// </remarks>
public sealed partial class SoundWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.sound";

    public override string TypeId => Type;

    /// <summary>Which speaker is drawn: silent, quiet, half, loud.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; } = "Speaker";

    /// <summary>How loud, as a percentage, or nothing on a compact bar.</summary>
    [ObservableProperty]
    public partial string Level { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility LevelVisible { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility Shown { get; set; } = Visibility.Visible;

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

    /// <summary>The hover: how loud, and that a press silences.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>The figure fades while silenced: the icon says quiet, the dim number says what comes back.</summary>
    [ObservableProperty]
    public partial double LevelShown { get; set; } = 1;

    /// <summary>Whether the figure is written beside the speaker.</summary>
    private bool WithLevel => WidgetOptions.Number(Options, "level") is not 0;

    /// <summary>
    /// This machine can make a sound.
    /// </summary>
    /// <remarks>
    /// Said here rather than by returning no length, because a length of
    /// nothing still rounds up to one slot: the widget was invisible and the
    /// gap it stood in was not.
    /// </remarks>
    public override bool Matters => Shown == Visibility.Visible;

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

    /// <summary>The box the figure is drawn in, wide enough for the loudest it gets.</summary>
    [ObservableProperty]
    public partial double FigureWidth { get; set; }


    public override void Tick(SensorSnapshot snapshot)
    {
        (bool? muted, float loud) = SystemVolume.State();

        if (muted is null)
        {
            Shown = Visibility.Collapsed;
            return;
        }

        Shown = Visibility.Visible;
        IconSize = ReadingIcon;
        Stroke = 36 / Math.Max(1, IconSize);
        FontSize = ReadingFont;

        Icon = muted.Value ? "SpeakerOff"
            : loud < 0.01f ? "SpeakerOff"
            : loud < 0.34f ? "SpeakerLow"
            : loud < 0.67f ? "SpeakerMid"
            : "Speaker";

        bool room = WithLevel && Density == DockDensity.Default;

        LevelVisible = room ? Visibility.Visible : Visibility.Collapsed;

        Level = room
            ? Math.Round(loud * 100).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " %"
            : string.Empty;

        // Priced for "100 %" whether it says that or not. Priced by the
        // figure in hand instead, the box changed width as the volume
        // crossed ten and a hundred - and the speaker beside it moved.
        FigureWidth = _figure ??= Metric.Wide("100 %", FontSize);

        LevelShown = muted.Value ? 0.5 : 1;

        string state = muted.Value
            ? Loc.Tr("SoundMutedTip", "Silenced")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc.Tr("SoundLevelTip", "Volume {0} %"),
                Math.Round(loud * 100));

        Detail = state + " · " + (muted.Value
            ? Loc.Tr("SoundPressUnmuteTip", "a press lets it speak")
            : Loc.Tr("SoundPressMuteTip", "a press silences it"));
    }

    /// <summary>
    /// A press silences the machine, or lets it speak again.
    /// </summary>
    /// <remarks>
    /// Read back rather than assumed: something else may have changed it
    /// between the last tick and this press, and a button that toggles what
    /// it last saw rather than what is true gets out of step and stays there.
    /// </remarks>
    public override bool Pressable => true;

    public override void Press()
    {
        if (SystemVolume.Muted() is not { } muted)
        {
            return;
        }

        SystemVolume.Mute(!muted);
        Tick(SensorSnapshot.Empty);
    }

    /// <summary>
    /// How much bar this takes.
    /// </summary>
    /// <remarks>
    /// Sized for "100 %" whether it says that or not: a slot that grew when
    /// the volume went from 99 to 100 would shuffle the bar along while
    /// somebody was turning a knob.
    /// </remarks>
    public override double Length()
    {
        double along = ReadingIcon + 6 + DockMetrics.ChipPadding + 4;

        if (LevelVisible == Visibility.Visible)
        {
            _figure ??= Metric.Wide("100 %", ReadingFont);
            along += _figure.Value + 4;
        }

        return Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical ? 30 : along;
    }

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed) =>
        Mcd.App.Settings.Braun.Field(
            Loc.Tr("SoundLevelLabel", "Write how loud it is"),
            Mcd.App.Settings.Braun.Switch(
                WithLevel,
                on => changed(WidgetOptions.Merge(Options, ("level", JsonValue.Create(on ? 1 : 0))))),
            Loc.Tr("SoundLevelHint", "The speaker alone already says whether there is any."));
}
