using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Settings;

/// <summary>
/// Master Audio Switcher's segmented controls, built in code.
/// </summary>
/// <remarks>
/// Two shapes, both rows of rounded tiles. <see cref="Buttons"/> is the
/// small-caps kind - MATCH WINDOWS / DARK / LIGHT - where the chosen tile
/// wears the accent. <see cref="Tabs"/> is the icon-over-name kind the
/// device switcher uses, where the chosen tile is simply lit. Rebuilt whole
/// on every change rather than kept in sync: a handful of buttons is not
/// worth a control with state of its own.
/// </remarks>
public static class Seg
{
    /// <summary>A row of small-caps tiles; the chosen one wears the accent.</summary>
    public static StackPanel Buttons(IReadOnlyList<string> labels, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        for (int i = 0; i < labels.Count; i++)
        {
            bool on = i == selected;

            var text = new TextBlock
            {
                Text = labels[i].ToUpper(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 120,
            };

            var tile = new Button
            {
                Content = text,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(14, 7, 14, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources[
                    on ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources[
                    on ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush"],
                Foreground = (Brush)Application.Current.Resources[
                    on ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            };

            int index = i;
            tile.Click += (_, _) => pick(index);
            row.Children.Add(tile);
        }

        return row;
    }

    /// <summary>A row of icon-over-name tiles; the chosen one is lit.</summary>
    public static StackPanel Tabs(
        IReadOnlyList<(string Glyph, string Label)> items, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        for (int i = 0; i < items.Count; i++)
        {
            bool on = i == selected;

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
                Foreground = (Brush)Application.Current.Resources[
                    on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            });

            body.Children.Add(new TextBlock
            {
                Text = items[i].Label.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 120,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources[
                    on ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
            });

            var tile = new Button
            {
                Content = body,
                MinWidth = 88,
                MinHeight = 0,
                Padding = new Thickness(14, 9, 14, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources[
                    on ? "CardStrokeColorDefaultBrush" : "SubtleFillColorTransparentBrush"],
                Background = (Brush)Application.Current.Resources[
                    on ? "LayerOnAcrylicFillColorDefaultBrush" : "SubtleFillColorTransparentBrush"],
            };

            int index = i;
            tile.Click += (_, _) => pick(index);
            row.Children.Add(tile);
        }

        return row;
    }
}
