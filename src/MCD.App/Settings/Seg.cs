using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mcd.App.Settings;

/// <summary>
/// Master Audio Switcher's segmented controls, built in code.
/// </summary>
/// <remarks>
/// Two shapes, both rows of rounded tiles. <see cref="Buttons"/> is the
/// small-caps kind - MATCH WINDOWS / DARK / LIGHT - where the chosen tile
/// wears the accent and stands slightly off the page. <see cref="Tabs"/> is
/// the icon-over-name kind the device switcher uses, where the chosen tile is
/// lit instead. Rebuilt whole on every change rather than kept in step: a
/// handful of buttons is not worth a control with state of its own.
/// </remarks>
public static class Seg
{
    /// <summary>
    /// Which way round the palette goes.
    /// </summary>
    /// <remarks>
    /// Set by the window that owns these. The colours are named here rather
    /// than looked up: a control built in code is not in the tree yet, so the
    /// window's own dictionary - the Braun one - cannot be reached from it,
    /// and looking up the same key would quietly return the system's colour
    /// instead of this program's.
    /// </remarks>
    public static ElementTheme Theme { get; set; } = ElementTheme.Dark;

    private static bool Dark => Theme != ElementTheme.Light;

    /// <summary>Master Audio Switcher's console palette.</summary>
    private static SolidColorBrush Panel =>
        Paint(Dark ? 0xFF23272D : 0xFFF5F3EE);

    private static SolidColorBrush Edge =>
        Paint(Dark ? 0x14FFFFFFu : 0x1A000000u);

    private static SolidColorBrush Ink =>
        Paint(Dark ? 0xFFE9ECF1 : 0xFF2B2E33);

    private static SolidColorBrush Faint =>
        Paint(Dark ? 0xFF98A0AC : 0xFF5C6169);

    private static SolidColorBrush Lamp(bool on) => on
        ? Paint(Dark ? 0xFF4ADE80 : 0xFF22A45D)
        : Paint(Dark ? 0x40FFFFFFu : 0x33000000u);

    private static SolidColorBrush Clear => Paint(0x00000000u);

    /// <summary>The colour Windows is set to - the one accent this program owns.</summary>
    private static Brush Accent =>
        (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private static SolidColorBrush Paint(uint argb) => new(Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    /// <summary>A row of small-caps tiles; the chosen one wears the accent.</summary>
    public static StackPanel Buttons(IReadOnlyList<string> labels, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        for (int i = 0; i < labels.Count; i++)
        {
            bool on = i == selected;

            var tile = new Button
            {
                Content = new TextBlock
                {
                    Text = labels[i].ToUpper(System.Globalization.CultureInfo.CurrentCulture),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    CharacterSpacing = 140,
                },
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(16, 8, 16, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = on ? Accent : Edge,
                Background = on ? Accent : Panel,
                Foreground = on
                    ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
                    : Faint,
            };

            Raise(tile, on);

            int index = i;
            tile.Click += (_, _) => pick(index);
            row.Children.Add(tile);
        }

        return row;
    }

    /// <summary>
    /// A row of icon-over-name tiles, each with a lamp for whether it is on.
    /// </summary>
    /// <param name="items">
    /// The glyph, the name, and whether the thing behind it is switched on -
    /// which is a green or grey lamp rather than a word, so a tile stays the
    /// width of its name.
    /// </param>
    public static StackPanel Tabs(
        IReadOnlyList<(string Glyph, string Label, bool On)> items, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        for (int i = 0; i < items.Count; i++)
        {
            bool chosen = i == selected;

            var body = new StackPanel
            {
                Spacing = 5,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            body.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = items[i].Glyph,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = chosen ? Ink : Faint,
            });

            // The lamp sits with the name: on, or off and dark.
            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            line.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Lamp(items[i].On),
            });

            line.Children.Add(new TextBlock
            {
                Text = items[i].Label.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 140,
                Foreground = chosen ? Ink : Faint,
            });

            body.Children.Add(line);

            var tile = new Button
            {
                Content = body,
                MinWidth = 96,
                MinHeight = 0,
                Padding = new Thickness(14, 10, 14, 9),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = chosen ? Edge : Clear,
                Background = chosen ? Panel : Clear,
            };

            Raise(tile, chosen);

            int index = i;
            tile.Click += (_, _) => pick(index);
            row.Children.Add(tile);
        }

        return row;
    }

    /// <summary>
    /// Lifts the chosen tile off the page a little.
    /// </summary>
    /// <remarks>
    /// The soft shadow under the selected control is most of what makes Master
    /// Audio Switcher's panels read as physical. It costs a composition layer,
    /// so only the one tile that is chosen gets it.
    /// </remarks>
    private static void Raise(UIElement element, bool on)
    {
        if (!on)
        {
            return;
        }

        element.Shadow = new ThemeShadow();
        element.Translation = new Vector3(0, 0, 16);
    }
}
