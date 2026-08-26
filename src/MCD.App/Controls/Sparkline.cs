using System.Collections.Immutable;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

// Shapes.Path, not IO.Path.
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace Mcd.App.Controls;

/// <summary>
/// A line of the last minute of a reading.
/// </summary>
/// <remarks>
/// <para>
/// Drawn with a <see cref="Polyline"/> over a filled path, rather than with a
/// canvas library. Sixty points redrawn once a second cost nothing, and it keeps
/// the program free of a rendering dependency it would otherwise carry for one
/// small graph.
/// </para>
/// <para>
/// The vertical scale is fixed when the reading has a natural range: a
/// percentage is always drawn against nought to a hundred. Auto-scaling a
/// percentage makes a processor idling between one and three percent look like a
/// machine in trouble.
/// </para>
/// </remarks>
public sealed class Sparkline : UserControl
{
    private readonly Polyline _line = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _wash = new();
    private ImmutableArray<double> _values = [];

    public Sparkline()
    {
        IsTabStop = false;

        var root = new Grid();
        root.Children.Add(_wash);
        root.Children.Add(_line);
        Content = root;

        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Set when the reading has a natural range, such as a percentage.</summary>
    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    public Brush LineBrush
    {
        get => _line.Stroke;
        set
        {
            _line.Stroke = value;

            // The wash under the line is the same colour, barely there. Anything
            // stronger competes with the figures beside it.
            _wash.Fill = value is SolidColorBrush solid
                ? new SolidColorBrush(solid.Color) { Opacity = 0.18 }
                : value;
        }
    }

    public void Show(ImmutableArray<double> values)
    {
        _values = values;
        Redraw();
    }

    private void Redraw()
    {
        double width = ActualWidth;
        double height = ActualHeight;

        _line.Points.Clear();
        _wash.Data = null;

        if (_values.Length < 2 || width <= 0 || height <= 0)
        {
            return;
        }

        double low = Minimum ?? _values.Min();
        double high = Maximum ?? _values.Max();

        // A flat line still has to be drawn somewhere sensible rather than
        // dividing by nothing.
        if (high - low < 0.001)
        {
            high = low + 1;
        }

        double step = width / (_values.Length - 1);
        var points = new PointCollection();

        for (int i = 0; i < _values.Length; i++)
        {
            double fraction = Math.Clamp((_values[i] - low) / (high - low), 0, 1);
            points.Add(new Point(i * step, height - (fraction * height)));
        }

        _line.Points = points;

        var area = new PathFigure { StartPoint = new Point(0, height), IsClosed = true };

        foreach (Point point in points)
        {
            area.Segments.Add(new LineSegment { Point = point });
        }

        area.Segments.Add(new LineSegment { Point = new Point(width, height) });
        _wash.Data = new PathGeometry { Figures = { area } };
    }
}
