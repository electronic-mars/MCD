using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// Master Audio Switcher, on the bar instead of in the tray.
/// </summary>
/// <remarks>
/// The device the sound goes to, drawn with that program's own outline for
/// it. A press does what a click on its tray icon does - on to the next
/// device - and the right-click menu lists them all. While the program is
/// not running the chip is not there, and the program keeps its tray icon.
/// </remarks>
public sealed partial class SwitcherWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.switcher";

    public override string TypeId => Type;

    /// <summary>The device's outline, or the name of one of ours until it arrives.</summary>
    [ObservableProperty]
    public partial string Icon { get; set; } = "Speaker";

    [ObservableProperty]
    public partial Visibility Shown { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial double IconSize { get; set; } = 16;

    [ObservableProperty]
    public partial double Stroke { get; set; } = 1.5;

    /// <summary>The hover: where the sound goes, and what a press does.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    private bool _joined;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;

    public override bool Matters => Shown == Visibility.Visible;

    public override string Called => Loc.Tr("WidgetSwitcherName", "Audio Switcher");

    public override void Attach()
    {
        if (!_joined)
        {
            _joined = true;
            _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            AudioSwitcher.Answered += OnAnswered;
            AudioSwitcher.Join(Context.Log);
        }

        Tick(SensorSnapshot.Empty);
    }

    public override void Tick(SensorSnapshot snapshot)
    {
        IconSize = ReadingIcon;
        Stroke = 36 / Math.Max(1, IconSize);

        if (AudioSwitcher.Outputs is not { } outputs)
        {
            Shown = Visibility.Collapsed;
            return;
        }

        Shown = Visibility.Visible;
        AudioSwitcher.Drawn();

        SwitcherDevice? current = outputs.FirstOrDefault(d => d.Current);

        Icon = current is not null && AudioSwitcher.Drawing(current.Icon) is { } drawn ? drawn : "Speaker";

        Detail = (current?.Name ?? Loc.Tr("SwitcherNoDevice", "No sound device"))
            + " · " + Loc.Tr("SwitcherPressTip", "a press switches to the next");
    }

    public override void Press()
    {
        Context.Log.LogInformation("switcher.pressed");
        AudioSwitcher.Next();
    }

    /// <summary>A press came back: the new device is drawn now, not on the next tick.</summary>
    private void OnAnswered() => _ui?.TryEnqueue(() => Tick(SensorSnapshot.Empty));

    public override IEnumerable<MenuFlyoutItemBase> Menu()
    {
        if (AudioSwitcher.Outputs is null)
        {
            yield break;
        }

        var open = new MenuFlyoutItem { Text = Loc.Tr("SwitcherOpen", "Open Master Audio Switcher") };
        open.Click += (_, _) => AudioSwitcher.Open();

        yield return open;
        yield return new MenuFlyoutSeparator();

        foreach (SwitcherDevice device in AudioSwitcher.Outputs ?? [])
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = device.Name,
                IsChecked = device.Current,
                GroupName = Type,
            };

            string id = device.Id;
            item.Click += (_, _) => AudioSwitcher.SwitchTo(id);

            yield return item;
        }
    }

    public override double Length() =>
        Orientation == Orientation.Vertical ? 30 : ReadingIcon + 6 + DockMetrics.ChipPadding;

    public override void Dispose()
    {
        if (_joined)
        {
            _joined = false;
            AudioSwitcher.Answered -= OnAnswered;
            AudioSwitcher.Leave();
        }

        base.Dispose();
    }
}
