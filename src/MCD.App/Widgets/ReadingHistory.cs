using System.Globalization;
using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// The last hour of every reading, and the panel a press on a reading opens
/// to show it.
/// </summary>
/// <remarks>
/// <para>
/// A figure on the bar says how things are this second; whether they were the
/// same a minute ago - a processor busy for a moment or busy for half an hour,
/// a drive warming up or cooling down - is the question that number cannot
/// answer, and the one people asked monitoring programs for.
/// </para>
/// <para>
/// Kept for every reading, not only the ones on a bar: an hour of one value a
/// second is 3 600 numbers, some thirty readings make a few hundred kilobytes,
/// and a history that starts the moment somebody adds the widget is a history
/// of nothing. Written on the hub's thread, read on the interface thread.
/// </para>
/// </remarks>
public static class ReadingHistory
{
    private const int Hour = 3600;

    private static readonly Dictionary<SensorKey, Ring> Rings = [];
    private static readonly Lock Gate = new();

    public static void Start(SensorHub hub) => hub.Updated += (_, snapshot) => Record(snapshot);

    private static void Record(SensorSnapshot snapshot)
    {
        lock (Gate)
        {
            foreach ((SensorKey key, SensorReading reading) in snapshot.Readings)
            {
                if (!Rings.TryGetValue(key, out Ring? ring))
                {
                    Rings[key] = ring = new Ring();
                }

                ring.Add(reading.HasValue ? (float)reading.Value : float.NaN);
            }
        }
    }

    /// <summary>The last hour, oldest first, one value a second; gaps are NaN.</summary>
    public static float[] Of(SensorKey key)
    {
        lock (Gate)
        {
            return Rings.TryGetValue(key, out Ring? ring) ? ring.Copy() : [];
        }
    }

    private sealed class Ring
    {
        private readonly float[] _values = new float[Hour];
        private int _next;
        private int _count;

        public void Add(float value)
        {
            _values[_next] = value;
            _next = (_next + 1) % Hour;
            _count = Math.Min(_count + 1, Hour);
        }

        public float[] Copy()
        {
            var copy = new float[_count];

            for (int i = 0; i < _count; i++)
            {
                copy[i] = _values[(_next - _count + i + Hour) % Hour];
            }

            return copy;
        }
    }

    /// <summary>
    /// The panel for one or more readings: per reading its name, the line of
    /// the last hour, and the lowest, the average and the highest over it.
    /// </summary>
    public static FrameworkElement Panel(IEnumerable<(string Name, SensorDescriptor Sensor)> readings)
    {
        var stack = new StackPanel { Spacing = 14, Width = 280 };

        foreach ((string name, SensorDescriptor sensor) in readings)
        {
            stack.Children.Add(Chart(name, sensor, Of(sensor.Key)));
        }

        return stack;
    }

    private static FrameworkElement Chart(string name, SensorDescriptor sensor, float[] values)
    {
        var block = new StackPanel { Spacing = 6 };
        float[] known = [.. values.Where(v => !float.IsNaN(v))];

        string Say(double v) => Readable.Value(v, sensor.Unit);

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        var now = new TextBlock
        {
            Text = known.Length > 0 ? Say(known[^1]) : "—",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

        Grid.SetColumn(now, 1);
        title.Children.Add(label);
        title.Children.Add(now);
        block.Children.Add(title);

        const double wide = 280, tall = 64;

        var canvas = new Canvas
        {
            Width = wide,
            Height = tall,
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
        };

        if (known.Length > 1)
        {
            double low = known.Min(), high = known.Max();

            // A flat line in the middle rather than a line glued to the floor
            // when nothing moved; and a little air above and below otherwise.
            double floor = sensor.Kind == SensorKind.Load ? 0 : low - Math.Max(1, (high - low) * 0.15);
            double ceiling = sensor.Kind == SensorKind.Load ? Math.Max(100, high) : high + Math.Max(1, (high - low) * 0.15);

            var line = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
            };

            // Over as much as there is: a program started ten minutes ago has
            // ten minutes to show, and squeezed into the last sixth of an
            // hour-wide box it looked like nothing at all. The right edge is
            // always "now"; the label on the left says how far back it goes.
            double step = wide / (values.Length - 1);
            double start = 0;

            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i]))
                {
                    continue;
                }

                double y = tall - ((values[i] - floor) / (ceiling - floor) * tall);
                line.Points.Add(new Windows.Foundation.Point(start + (i * step), Math.Clamp(y, 0, tall)));
            }

            canvas.Children.Add(line);
        }

        block.Children.Add(canvas);

        var axis = new Grid();
        axis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        axis.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int minutes = Math.Max(1, (int)Math.Round(values.Length / 60.0));

        var ago = new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Loc.Tr("HistoryAgo", "{0} min ago"), minutes),
            FontSize = 11,
            Opacity = 0.6,
        };
        var nowLabel = new TextBlock { Text = Loc.Tr("HistoryNow", "now"), FontSize = 11, Opacity = 0.6 };
        Grid.SetColumn(nowLabel, 1);
        axis.Children.Add(ago);
        axis.Children.Add(nowLabel);
        block.Children.Add(axis);

        block.Children.Add(new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            Text = known.Length > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    Loc.Tr("HistorySpread", "Lowest {0} · average {1} · highest {2}"),
                    Say(known.Min()),
                    Say(known.Average()),
                    Say(known.Max()))
                : Loc.Tr("HistoryNothing", "No data yet."),
        });

        return block;
    }
}
