using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// The time, and the date under it.
/// </summary>
/// <remarks>
/// <para>
/// The oldest thing anybody puts on a bar, and the one this program did not
/// have. What a permanently visible strip is for is answering a question
/// without being asked, and "what time is it" is the question people ask
/// most often - the taskbar clock is the only part of the taskbar that
/// everyone uses.
/// </para>
/// <para>
/// Formats come from the machine's own settings, not from a pattern written
/// here: whether a day comes before a month, and whether the hours run to
/// twelve or twenty-four, are things Windows already knows and gets right for
/// wherever somebody is.
/// </para>
/// </remarks>
public sealed partial class ClockWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.clock";

    public override string TypeId => Type;

    /// <summary>The time as it is shown now.</summary>
    [ObservableProperty]
    public partial string Time { get; set; } = string.Empty;

    /// <summary>The date, or nothing when it is not wanted.</summary>
    [ObservableProperty]
    public partial string Date { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility DateVisible { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial double TimeSize { get; set; } = 14;

    [ObservableProperty]
    public partial double DateSize { get; set; } = 10;

    /// <summary>Whether the seconds are shown.</summary>
    private bool Seconds => WidgetOptions.Number(Options, "seconds") > 0;

    /// <summary>Whether the date is shown under the time.</summary>
    private bool WithDate => WidgetOptions.Number(Options, "date") is not 0;

    public override void Attach() => Tick(SensorSnapshot.Empty);

    public override void Tick(SensorSnapshot snapshot)
    {
        DateTime now = DateTime.Now;

        TimeSize = DockMetrics.ReadingFont(Density);
        DateSize = Math.Max(9, TimeSize - 4);

        Time = now.ToString(Pattern, CultureInfo.CurrentCulture);

        // The date is dropped on a compact bar whatever the setting says:
        // there is one line of room, and the time is the line worth having.
        bool room = WithDate && Density == DockDensity.Default;

        DateVisible = room ? Visibility.Visible : Visibility.Collapsed;
        Date = room ? now.ToString("ddd d MMM", CultureInfo.CurrentCulture) : string.Empty;
    }

    /// <summary>
    /// The machine's own short-time format, with seconds put back if asked.
    /// </summary>
    /// <remarks>
    /// "t" is whatever this Windows calls a short time - which is where the
    /// twelve-or-twenty-four-hour answer already lives. Adding seconds means
    /// putting them into that pattern rather than writing a new one, or a
    /// machine set to twelve hours loses its am and pm.
    /// </remarks>
    private string Pattern
    {
        get
        {
            string shortTime = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;

            if (!Seconds)
            {
                return shortTime;
            }

            int minutes = shortTime.IndexOf("mm", StringComparison.Ordinal);

            return minutes < 0
                ? shortTime
                : shortTime.Insert(minutes + 2, ":ss");
        }
    }

    /// <summary>
    /// How much bar this takes.
    /// </summary>
    /// <remarks>
    /// Declared from the widest the clock can ever be, not from what it says
    /// at this moment: a slot that grew when the time went from 9:59 to 10:00
    /// would shuffle the whole bar along once an hour.
    /// </remarks>
    public override double Length()
    {
        double time = Metric.Wide(Widest(Pattern), DockMetrics.ReadingFont(Density));

        double date = DateVisible == Visibility.Visible
            ? Metric.Wide(Widest("ddd d MMM"), Math.Max(9, DockMetrics.ReadingFont(Density) - 4))
            : 0;

        double along = Math.Max(time, date) + 14;

        return Orientation == Orientation.Vertical
            ? (DateVisible == Visibility.Visible ? 34 : 22)
            : along;
    }

    /// <summary>
    /// The longest this format ever gets.
    /// </summary>
    /// <remarks>
    /// Taken from a real date rather than counted: December on a Wednesday is
    /// longer than May on a Friday in every language, and in several of them
    /// the difference is more than one letter.
    /// </remarks>
    private static string Widest(string pattern)
    {
        var longest = new DateTime(2026, 12, 30, 22, 58, 58, DateTimeKind.Local);

        return longest.ToString(pattern, CultureInfo.CurrentCulture);
    }

    public override string Summarise() =>
        Seconds ? Loc.Tr("ClockWithSeconds", "with seconds") : string.Empty;

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var panel = new StackPanel { Spacing = 14 };

        panel.Children.Add(Mcd.App.Settings.Braun.Field(
            Loc.Tr("ClockSeconds", "Show seconds"),
            Mcd.App.Settings.Braun.Switch(
                Seconds,
                on => changed(WidgetJson.Object(
                    ("seconds", on ? 1 : 0), ("date", WithDate ? 1 : 0))))));

        panel.Children.Add(Mcd.App.Settings.Braun.Field(
            Loc.Tr("ClockDate", "Show the date underneath"),
            Mcd.App.Settings.Braun.Switch(
                WithDate,
                on => changed(WidgetJson.Object(
                    ("seconds", Seconds ? 1 : 0), ("date", on ? 1 : 0)))),
            Loc.Tr("ClockDateHint", "A compact bar has one line, and the time is the line worth having.")));

        return panel;
    }
}
