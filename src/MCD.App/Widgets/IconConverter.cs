using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// Turns an icon's name into the shape drawn for it.
/// </summary>
/// <remarks>
/// <para>
/// The shapes come from the Hugeicons free set, fetched by
/// <c>tools/fetch_icons.py</c> and kept in <c>assets/icons-src</c>. They are
/// drawn as strokes on a 24 by 24 grid, which is why the dock uses a Path with
/// a Stroke rather than a PathIcon: PathIcon fills its geometry, and a filled
/// outline of a processor is just a square.
/// </para>
/// <para>
/// The result is deliberately <b>not</b> cached. A Geometry is a
/// DependencyObject and belongs to exactly one element; handing the same
/// instance to a second Path leaves that Path drawing nothing at all, with no
/// error anywhere. The dock is rebuilt whenever its size or edge changes, so a
/// cache meant every icon vanished the first time a setting was touched and
/// never came back.
/// </para>
/// </remarks>
public sealed class IconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string name = value as string ?? string.Empty;

        // A drawing handed over whole - another program's outline - is
        // path data already, and says so by starting with a move.
        string path = IconLibrary.Paths.TryGetValue(name, out string? found) ? found
            : name.StartsWith('M') ? name
            : IconLibrary.Paths["Activity"];

        return XamlBindingHelper.ConvertValue(typeof(Geometry), path);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
