using Mcd.Core.Settings;
using Mcd.Interop.Machine;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml.Controls;

namespace Mcd.App.Widgets;

/// <summary>
/// A full cup, and the machine stays awake; an empty one, and it may sleep.
/// </summary>
/// <remarks>
/// <para>
/// For the machine that has to look like somebody is at it: a call that runs
/// long without a key pressed, a build that outlasts the screen saver, a
/// chat program that turns amber after five minutes. A press fills the cup,
/// another empties it; the right click offers a time after which it empties
/// itself, so that nobody leaves a laptop awake in a bag over a weekend.
/// </para>
/// <para>
/// On by default: the first cup on a bar in a run of the program fills
/// itself, so that putting the widget there is enough, and a cup that has
/// been emptied by hand stays empty until the program is started again.
/// Whether it was on is not saved - a restart is a fresh decision, and the
/// answer to it is "on".
/// </para>
/// </remarks>
public sealed class AwakeWidget(WidgetContext context, WidgetConfig entry)
    : GlyphWidget(context, entry)
{
    public const string Type = "mcd.awake";

    public override string TypeId => Type;

    /// <summary>
    /// Whether this run of the program has already decided about the cup.
    /// Static, because the bar rebuilds its widgets whenever a setting
    /// changes and a rebuilt cup must not fill itself again after somebody
    /// emptied it.
    /// </summary>
    private static bool _decided;

    public override void Attach()
    {
        if (!_decided)
        {
            _decided = true;
            KeepAwake.Start(null);
        }

        base.Attach();
    }

    public override void Tick(SensorSnapshot snapshot)
    {
        if (!KeepAwake.On)
        {
            Draw(
                "CoffeeOff",
                faded: 0.55,
                alert: false,
                Loc.Tr("AwakeOffTip", "The computer can sleep · Click to keep it awake"));

            return;
        }

        string until = KeepAwake.Until is { } end
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc.Tr("AwakeFor", "Awake: {0} left"),
                Left(end - DateTimeOffset.Now))
            : Loc.Tr("AwakeUntilOff", "Awake until turned off");

        Draw("CoffeeOn", faded: 1, alert: false, until + " · " + Loc.Tr("AwakeOnTip", "Click to allow sleep"));
    }

    public override void Press()
    {
        if (KeepAwake.On)
        {
            KeepAwake.Stop();
        }
        else
        {
            KeepAwake.Start(null);
        }

        Tick(SensorSnapshot.Empty);
    }

    public override IEnumerable<MenuFlyoutItemBase> Menu()
    {
        (string Name, TimeSpan? Span)[] choices =
        [
            (Loc.Tr("AwakeMenu30", "Keep awake for 30 minutes"), TimeSpan.FromMinutes(30)),
            (Loc.Tr("AwakeMenu60", "Keep awake for an hour"), TimeSpan.FromHours(1)),
            (Loc.Tr("AwakeMenu120", "Keep awake for two hours"), TimeSpan.FromHours(2)),
            (Loc.Tr("AwakeMenuForever", "Keep awake until turned off"), null),
        ];

        foreach ((string name, TimeSpan? span) in choices)
        {
            var item = new MenuFlyoutItem { Text = name };

            item.Click += (_, _) =>
            {
                KeepAwake.Start(span);
                Tick(SensorSnapshot.Empty);
            };

            yield return item;
        }

        if (KeepAwake.On)
        {
            var off = new MenuFlyoutItem { Text = Loc.Tr("AwakeMenuOff", "Allow sleep") };

            off.Click += (_, _) =>
            {
                KeepAwake.Stop();
                Tick(SensorSnapshot.Empty);
            };

            yield return off;
        }

        yield return new MenuFlyoutSeparator();
    }

    /// <summary>"1 h 12 min", "45 min", "under a minute".</summary>
    private static string Left(TimeSpan span)
    {
        int minutes = (int)Math.Ceiling(Math.Max(0, span.TotalMinutes));

        return minutes < 1 ? Loc.Tr("AwakeLessThanMinute", "less than a minute")
            : minutes < 60 ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc.Tr("AwakeMinutes", "{0} min"),
                minutes)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Loc.Tr("AwakeHours", "{0} h {1} min"),
                minutes / 60,
                minutes % 60);
    }

    public override string Summarise() => KeepAwake.On
        ? Loc.Tr("AwakeSummaryOn", "Keeping the computer awake")
        : Loc.Tr("AwakeSummaryOff", "The computer can sleep");
}
