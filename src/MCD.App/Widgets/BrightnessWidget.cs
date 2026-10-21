using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.Core.Settings;
using Mcd.Interop.Display;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Mcd.App.Widgets;

/// <summary>One screen's slider in the flyout.</summary>
public sealed partial class ScreenLevel(string gdi, string label, Action<ScreenLevel> moved) : ObservableObject
{
    public string Gdi { get; } = gdi;

    public string Label { get; } = label;

    /// <summary>How bright, 0 to 100. The slider's value, both ways.</summary>
    [ObservableProperty]
    public partial double Value { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>True while the widget is the one writing <see cref="Value"/>, so that it is not taken for a hand on the slider.</summary>
    internal bool Reading { get; set; }

    partial void OnValueChanged(double value)
    {
        Text = Math.Round(value).ToString("F0", CultureInfo.InvariantCulture) + " %";

        if (!Reading)
        {
            moved(this);
        }
    }
}

/// <summary>
/// How bright the screens are, all of them with one slider.
/// </summary>
/// <remarks>
/// <para>
/// The sound widget's twin, without the figure: the icon alone opens a slider
/// - the figure is beside the slider - and the wheel over it turns it. One slider moves every screen to the same level, as the
/// volume slider moves every sound at once; screens differ in what they can
/// do, so an option puts a slider for each screen under it.
/// </para>
/// <para>
/// The screens answer slowly - a monitor over the cable takes a tenth of a
/// second to be asked - so nothing here asks one. The reading is whatever
/// <see cref="Brightness"/> last heard, and a value sent to a screen is
/// remembered, not waited for.
/// </para>
/// <para>
/// The widget hides where no screen will say how bright it is: a desktop
/// monitor that does not speak DDC/CI, or a machine with none attached.
/// </para>
/// </remarks>
public sealed partial class BrightnessWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.brightness";

    public override string TypeId => Type;

    [ObservableProperty]
    public partial string Icon { get; set; } = "Sun03";

    /// <summary>How bright, 0 to 100, as one number for all the screens. The master slider's value, both ways.</summary>
    [ObservableProperty]
    public partial double Level { get; set; }

    /// <summary>The master slider's own reading beside it, "42 %".</summary>
    [ObservableProperty]
    public partial string LevelText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility Shown { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial double IconSize { get; set; } = 16;

    [ObservableProperty]
    public partial double Stroke { get; set; } = 1.5;

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>What the master slider is called when each screen has its own beneath it.</summary>
    public string AllLabel { get; } = Loc.Tr("BrightnessAll", "All screens");

    /// <summary>The slider for each screen, when the widget is set to give each its own.</summary>
    public ObservableCollection<ScreenLevel> Each { get; } = [];

    [ObservableProperty]
    public partial Visibility EachVisible { get; set; } = Visibility.Collapsed;

    private bool _reading;
    private bool _sliding;

    /// <summary>Each screen's own slider, as the widget is set.</summary>
    private bool Separate => WidgetOptions.Number(Options, "each") is 1;

    public override bool Matters => Shown == Visibility.Visible;

    /// <summary>The widget is one button that opens the flyout; the wheel is its own.</summary>
    public override bool OwnButtons => true;

    partial void OnLevelChanged(double value)
    {
        LevelText = Math.Round(value).ToString("F0", CultureInfo.InvariantCulture) + " %";

        if (_reading)
        {
            return;
        }

        int percent = (int)Math.Round(value);

        // Every screen to the same level, and their own sliders along with it.
        foreach (ScreenLevel row in Each)
        {
            row.Reading = true;
            row.Value = percent;
            row.Reading = false;
            Brightness.Set(row.Gdi, percent);
        }

        if (Each.Count == 0)
        {
            foreach (ScreenBrightness screen in Brightness.Snapshot())
            {
                Brightness.Set(screen.Gdi, percent);
            }
        }
    }

    private void ScreenMoved(ScreenLevel row)
    {
        Brightness.Set(row.Gdi, (int)Math.Round(row.Value));

        // The master slider shows where the screens are on average.
        _reading = true;
        Level = Math.Round(Each.Average(r => r.Value));
        _reading = false;
    }

    public void SunLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button { Flyout: { } flyout } button)
        {
            flyout.XamlRoot = button.XamlRoot;
            flyout.Placement = PopupSide;
        }
    }

    public void FlyoutOpened() => _sliding = true;

    public void FlyoutClosed() => _sliding = false;

    /// <summary>The wheel over the widget turns every screen, five per cent a notch.</summary>
    public void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int notches = e.GetCurrentPoint(null).Properties.MouseWheelDelta / 120;

        if (notches != 0)
        {
            Level = Math.Clamp(Math.Round(Level) + (notches * 5), 0, 100);
            e.Handled = true;
        }
    }

    public override void Attach() => Tick(SensorSnapshot.Empty);

    public override void Tick(SensorSnapshot snapshot)
    {
        IReadOnlyList<ScreenBrightness> screens = Brightness.Snapshot();

        if (screens.Count == 0)
        {
            Shown = Visibility.Collapsed;
            return;
        }

        Shown = Visibility.Visible;

        IconSize = ReadingIcon;
        Stroke = 36 / Math.Max(1, IconSize);
        EachVisible = Separate && screens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        int average = (int)Math.Round(screens.Average(s => s.Percent));

        Detail = string.Format(CultureInfo.CurrentCulture, Loc.Tr("BrightnessTip", "Brightness: {0}%"), average)
            + " · " + Loc.Tr("BrightnessSliderTip", "Click for the slider · Scroll to change brightness");

        if (_sliding)
        {
            // The hand is on a slider: what the screens last said is older than
            // what the hand has just done.
            return;
        }

        _reading = true;
        Level = average;

        if (Separate && screens.Count > 1)
        {
            Fill(screens);
        }
        else if (Each.Count > 0)
        {
            Each.Clear();
        }

        _reading = false;
    }

    private void Fill(IReadOnlyList<ScreenBrightness> screens)
    {
        bool same = Each.Count == screens.Count
            && Each.Select(r => r.Gdi).SequenceEqual(screens.Select(s => s.Gdi), StringComparer.OrdinalIgnoreCase);

        if (!same)
        {
            Each.Clear();

            foreach (ScreenBrightness screen in screens)
            {
                // The number Windows shows in its own display settings.
                string digits = new([.. screen.Gdi.Where(char.IsDigit)]);

                Each.Add(new ScreenLevel(
                    screen.Gdi,
                    string.Format(CultureInfo.CurrentCulture, Loc.Tr("DisplayNumber", "Screen {0}"), digits),
                    ScreenMoved));
            }
        }

        for (int i = 0; i < screens.Count; i++)
        {
            Each[i].Reading = true;
            Each[i].Value = screens[i].Percent;
            Each[i].Reading = false;
        }
    }

    /// <summary>
    /// The icon alone, in the slot the sound widget's speaker takes: margin 1
    /// either side and the button's padding of 2 and 1.
    /// </summary>
    public override double Length() =>
        Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical ? 30 : 2 + ReadingIcon + 3;

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed) =>
        Mcd.App.Settings.Braun.Field(
            Loc.Tr("BrightnessEachLabel", "Each screen on its own"),
            Mcd.App.Settings.Braun.Switch(
                Separate,
                on => changed(WidgetOptions.Merge(Options, ("each", JsonValue.Create(on ? 1 : 0))))),
            Loc.Tr("BrightnessEachHint", "Adds a slider for every screen under the main one."));
}
