using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mcd.App.Widgets;

/// <summary>
/// Which language the keyboard types in, and whether Caps Lock is down.
/// </summary>
/// <remarks>
/// <para>
/// The two things people find out by typing a line of the wrong kind. The
/// language is written as its two letters and a press moves to the next
/// layout; Caps Lock is a plate of accent colour behind the letters for as
/// long as it is down, so that it is seen without being read and the widget
/// does not change its width when it happens.
/// </para>
/// <para>
/// The layout is the one of the window in front, as the taskbar's own
/// indicator has it - see <see cref="KeyboardLayouts"/>.
/// </para>
/// </remarks>
public sealed partial class LayoutWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.layout";

    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();

    public override string TypeId => Type;

    /// <summary>"EN", "RU".</summary>
    [ObservableProperty]
    public partial string Code { get; set; } = string.Empty;

    /// <summary>1 while Caps Lock is down: the plate behind the letters, and the letters on it.</summary>
    [ObservableProperty]
    public partial double Capital { get; set; }

    /// <summary>1 - <see cref="Capital"/>: the letters when nothing is behind them.</summary>
    [ObservableProperty]
    public partial double Plain { get; set; } = 1;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 12;

    /// <summary>The box the letters are drawn in, wide enough for the widest two.</summary>
    [ObservableProperty]
    public partial double BoxWidth { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    public override bool Pressable => true;

    public override void Attach() => Tick(SensorSnapshot.Empty);

    public override void Tick(SensorSnapshot snapshot)
    {
        KeyboardLayout layout = KeyboardLayouts.Current();
        bool caps = KeyboardLayouts.CapsLock();

        FontSize = ReadingFont;
        BoxWidth = Box(FontSize);
        Code = layout.Code;
        Capital = caps ? 1 : 0;
        Plain = caps ? 0 : 1;

        string state = layout.Name + (caps ? " · " + Loc.Tr("LayoutCapsOn", "Caps Lock is on") : string.Empty);

        Detail = KeyboardLayouts.Installed() > 1
            ? state + " · " + Loc.Tr("LayoutPressTip", "a press switches the layout")
            : state;
    }

    public override void Press()
    {
        KeyboardLayouts.Next();

        // The window in front takes the request in its own time; asking at
        // once would draw the language it has not left yet.
        _ = Task.Delay(150).ContinueWith(
            _ => _ui?.TryEnqueue(() => Tick(SensorSnapshot.Empty)),
            TaskScheduler.Default);
    }

    /// <summary>
    /// The width of the box the letters sit in, measured once per size.
    /// </summary>
    /// <remarks>
    /// Priced for the widest two capitals rather than the two in hand, so the
    /// bar does not shift along when the language changes.
    /// </remarks>
    private double Box(double font)
    {
        if (_boxFont != font)
        {
            _boxFont = font;
            _box = Metric.Wide("WW", font);
        }

        return _box;
    }

    private double _boxFont;
    private double _box;

    /// <summary>The letters, the plate's padding either side, and the margins.</summary>
    public override double Length() => Box(ReadingFont) + 10 + 6;

    public override string Summarise() => Loc.Tr("WidgetLayoutName", "Keyboard layout");
}
