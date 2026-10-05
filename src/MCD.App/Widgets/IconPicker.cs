using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Widgets;

/// <summary>
/// Choosing one of the program's icons: fifteen groups of about a hundred, and a search.
/// </summary>
/// <remarks>
/// <para>
/// The library is two thousand drawings, and a flat grid of two thousand is
/// not a way to find one. They are laid out in groups by meaning - Food,
/// Sport, Money and work - drawn by tools/panel/groups.py so that each holds
/// about the same number, and a group is a list a person can look down in
/// one go. The picker opens on the group the icon being replaced is in.
/// </para>
/// <para>
/// A search ignores the groups: somebody who knows the word wants the icon,
/// not the shelf. It matches the name with its spaces and dashes taken out.
/// </para>
/// </remarks>
public static class IconPicker
{
    private const int Columns = 9;
    private const int MostFound = 240;

    /// <summary>The English name of a group, for where the string table says nothing.</summary>
    private static readonly Dictionary<string, string> Fallback = new(StringComparer.Ordinal)
    {
        ["ui"] = "Interface and arrows",
        ["design"] = "Shapes and drawing",
        ["docs"] = "Files and learning",
        ["comm"] = "Social and messaging",
        ["soft"] = "Apps and code",
        ["money"] = "Money and work",
        ["tech"] = "Devices and power",
        ["places"] = "Transport and places",
        ["food"] = "Food",
        ["kitchen"] = "Kitchen and drinks",
        ["health"] = "Medicine",
        ["sport"] = "Sport",
        ["fun"] = "Games and fun",
        ["nature"] = "Nature and space",
        ["people"] = "People and faces",
    };

    public static string GroupName(string id) =>
        Loc.Tr("IconGroup_" + id, Fallback.GetValueOrDefault(id, id));

    /// <summary>An icon drawn at a size, as a stroked path on its 24-unit grid.</summary>
    public static FrameworkElement Drawn(string glyph, double box)
    {
        var shape = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
                typeof(Geometry), IconLibrary.Paths[glyph]),
            Stroke = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(shape);

        return new Viewbox { Width = box, Height = box, Child = canvas };
    }

    /// <summary>
    /// The picker: a group list and a search over a grid of icons.
    /// </summary>
    /// <param name="wearing">The icon being replaced, marked in the grid and opened on, or empty.</param>
    /// <param name="chosen">Called with the name of the icon picked.</param>
    public static FrameworkElement Build(string wearing, Action<string> chosen)
    {
        // Only groups that have something in them, with their counts.
        Dictionary<string, List<string>> members = IconLibrary.Groups
            .GroupBy(pair => pair.Value, pair => pair.Key)
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        List<string> ids = [.. IconLibrary.GroupIds.Where(members.ContainsKey)];

        var groups = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = ids.Select(id => $"{GroupName(id)} ({members[id].Count})").ToList(),
            SelectedIndex = Math.Max(
                0,
                ids.IndexOf(IconLibrary.Groups.GetValueOrDefault(wearing, ids[0]))),
        };

        var search = new TextBox
        {
            PlaceholderText = Loc.Tr("IconSearch", "Search by name"),
            MinWidth = 150,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var grid = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            MaximumRowsOrColumns = Columns,
            ItemWidth = 36,
            ItemHeight = 36,
        };

        void Fill()
        {
            grid.Children.Clear();

            string wanted = new([.. search.Text.Where(char.IsLetterOrDigit)]);

            IEnumerable<string> names = wanted.Length > 0
                ? IconLibrary.Paths.Keys
                    .Where(name => name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .Take(MostFound)
                : groups.SelectedIndex >= 0 ? members[ids[groups.SelectedIndex]] : [];

            foreach (string glyph in names)
            {
                bool current = glyph == wearing;

                var button = new Button
                {
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(3),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),

                    // The one already worn is marked, so the grid answers "which
                    // is it now" as well as "which could it be".
                    BorderThickness = new Thickness(current ? 1 : 0),
                    BorderBrush = current
                        ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
                        : null,
                    CornerRadius = new CornerRadius(6),
                    Content = Drawn(glyph, 24),
                };

                // Anonymous drawings; the name is the difference between
                // hunting and finding.
                ToolTipService.SetToolTip(button, glyph);

                string name = glyph;
                button.Click += (_, _) => chosen(name);
                grid.Children.Add(button);
            }
        }

        groups.SelectionChanged += (_, _) => Fill();
        search.TextChanged += (_, _) => Fill();

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(search, 1);
        header.Children.Add(groups);
        header.Children.Add(search);

        Fill();

        return new StackPanel
        {
            Spacing = 8,
            Width = (Columns * 36) + 24,
            Children =
            {
                header,
                new ScrollViewer { Content = grid, MaxHeight = 288 },
            },
        };
    }
}
