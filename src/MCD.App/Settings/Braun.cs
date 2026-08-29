using System.Globalization;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mcd.App.Settings;

/// <summary>
/// Master Audio Switcher's console, in the same numbers it is written in.
/// </summary>
/// <remarks>
/// <para>
/// Every value here is transcribed from that program's own stylesheet
/// (<c>src/mas/ui/app.css</c>) rather than read off a screenshot. Four rounds
/// of "it looks similar but it is not the same" were spent guessing at
/// colours that were written down all along; the accent is the plainest
/// example - Braun orange <c>#F26A21</c>, never the blue Windows hands out.
/// </para>
/// <para>
/// MAS is a web view, so its sizes are CSS pixels under <c>html{zoom:1.1}</c>.
/// They are multiplied by that factor here and rounded to whole effective
/// pixels, which is why the numbers are a tenth larger than the stylesheet's.
/// </para>
/// <para>
/// The colours are named rather than looked up. A control built in code is
/// not in the visual tree yet, so the window's own dictionary cannot be
/// reached from it - and asking the application for a key the window
/// overrode returns the system's value without complaint, which is how a
/// hand-built control ends up wearing the wrong palette beside cards wearing
/// the right one.
/// </para>
/// </remarks>
public static class Braun
{
    /// <summary>Which way round the palette goes. Set by the window that owns these.</summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Dark;

    private static bool Dark => Theme != ElementTheme.Light;

    // ---------------------------------------------------------------- palette

    /// <summary>The body of the instrument.</summary>
    public static Brush Bg => Paint(Dark ? 0xFF1C1F24 : 0xFFEDEAE3);

    /// <summary>A panel lifted off the body - a chosen tab, a sheet.</summary>
    public static Brush PanelHi => Paint(Dark ? 0xFF23272D : 0xFFF5F3EE);

    /// <summary>A card sunk into the body: the ground for a group of rows.</summary>
    public static Brush Card => Paint(Dark ? 0xFF191C21 : 0xFFF7F5F1);

    /// <summary>The ground for a control sitting on a card.</summary>
    public static Brush CardHi => Paint(Dark ? 0xFF212630 : 0xFFFFFFFF);

    /// <summary>A hairline: what divides one row from the next.</summary>
    public static Brush Line => Paint(Dark ? 0x11FFFFFFu : 0x17000000u);

    /// <summary>The same line where it has to be seen - a border, not a divider.</summary>
    public static Brush LineHi => Paint(Dark ? 0x1FFFFFFFu : 0x29000000u);

    /// <summary>Reading text.</summary>
    public static Brush Tx => Paint(Dark ? 0xFFE9ECF1 : 0xFF23262B);

    /// <summary>A name beside its value.</summary>
    public static Brush Tx2 => Paint(Dark ? 0xFF98A0AC : 0xFF5C6169);

    /// <summary>
    /// Explanations and small captions. Deliberately not dimmer than this:
    /// MAS's own note says the shade it replaced measured 2.99:1 on a card,
    /// which is formally unreadable.
    /// </summary>
    public static Brush Tx3 => Paint(Dark ? 0xFF88909B : 0xFF626871);

    /// <summary>The one accent this program owns. The same in both themes.</summary>
    public static Brush Acc => Paint(0xFFF26A21);

    /// <summary>Text and glyphs printed on the accent.</summary>
    public static Brush OnAcc => Paint(0xFF140702);

    /// <summary>A lamp that is lit.</summary>
    public static Brush Led => Paint(Dark ? 0xFF4ADE80 : 0xFF22A45D);

    /// <summary>The one irreversible thing on a screen.</summary>
    public static Brush Danger => Paint(0xFFFF6B6B);

    /// <summary>A recess: the rail a row of keys is set into.</summary>
    public static Brush Sunk => Paint(Dark ? 0x66000000u : 0x14000000u);

    /// <summary>A lamp, lit or dark.</summary>
    public static Brush Lamp(bool on) =>
        on ? Led : Paint(Dark ? 0x40FFFFFFu : 0x33000000u);

    private static SolidColorBrush Paint(uint argb) => new(Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static SolidColorBrush Clear => Paint(0x00000000u);

    // ------------------------------------------------------------- type sizes

    private const double MicroSize = 10;
    private const double NameSize = 14;
    private const double NoteSize = 12;
    private const double SegSize = 11;

    // ---------------------------------------------------------------- pieces

    /// <summary>
    /// A micro-caption: small caps, wide tracking, third-level text.
    /// </summary>
    public static TextBlock Micro(string text) => new()
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture),
        FontSize = MicroSize,
        FontWeight = FontWeights.Medium,
        CharacterSpacing = 220,
        Foreground = Tx3,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// A section heading: an icon in the caption's own colour, then the name.
    /// </summary>
    /// <remarks>
    /// Every heading has one. A page where some headings carry an icon and
    /// some do not reads as unfinished, which is precisely what it is.
    /// </remarks>
    public static StackPanel Heading(string glyph, string text)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Margin = new Thickness(3, 14, 0, 7),
        };

        row.Children.Add(new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Glyph = glyph,
            FontSize = 15,
            Foreground = Tx3,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(Micro(text));
        return row;
    }

    /// <summary>
    /// One panel holding a section's rows, divided by hairlines.
    /// </summary>
    /// <remarks>
    /// A card of its own for every switch doubles the height of a settings
    /// page and turns a dense instrument into a list of slabs. One panel per
    /// section also lines the right-hand column up by itself, with no fixed
    /// widths to get wrong.
    /// </remarks>
    public static Border Group(params FrameworkElement?[] rows)
    {
        var stack = new StackPanel();
        bool first = true;

        foreach (FrameworkElement? row in rows)
        {
            if (row is null)
            {
                continue;
            }

            if (!first)
            {
                stack.Children.Add(new Border { Height = 1, Background = Line });
            }

            stack.Children.Add(row);
            first = false;
        }

        return new Border
        {
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = stack,
        };
    }

    /// <summary>
    /// A settings row: a name and its explanation, and the control that acts.
    /// </summary>
    /// <param name="stack">
    /// True when the control is too wide to sit in the right-hand column and
    /// belongs under the name at full width. Getting this wrong is what turns
    /// an explanation into a column of single letters: an <c>Auto</c> column
    /// measures at whatever width it likes and leaves the starred one nothing.
    /// </param>
    public static Grid Row(string name, string? note, FrameworkElement? control, bool stack = false)
    {
        var grid = new Grid { Padding = new Thickness(13, 10, 13, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        words.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = NameSize,
            Foreground = Tx,
            TextWrapping = TextWrapping.Wrap,
        });

        if (note is { Length: > 0 })
        {
            words.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = NoteSize,
                Foreground = Tx3,
                Margin = new Thickness(0, 3, 0, 0),
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        grid.Children.Add(words);

        if (control is null)
        {
            return grid;
        }

        if (stack)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (control is FrameworkElement wide)
            {
                wide.Margin = new Thickness(0, 9, 0, 0);
                wide.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            Grid.SetRow(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        grid.ColumnSpacing = 13;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (control is FrameworkElement beside)
        {
            beside.VerticalAlignment = VerticalAlignment.Center;
            beside.HorizontalAlignment = HorizontalAlignment.Right;
        }

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A value read off a row rather than set on it.</summary>
    public static TextBlock Reading(string text) => new()
    {
        Text = text,
        FontSize = NameSize,
        Foreground = Tx2,
    };

    /// <summary>
    /// A row of small-caps keys; the chosen one wears the accent.
    /// </summary>
    /// <param name="wide">
    /// True when the row fills its line and the keys share it equally - the
    /// shape a three-way choice takes under its own caption.
    /// </param>
    public static Panel Segs(
        IReadOnlyList<string> labels, int selected, Action<int> pick, bool wide = false)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };

        for (int i = 0; i < labels.Count; i++)
        {
            bool on = i == selected;

            var key = new Button
            {
                Content = new TextBlock
                {
                    Text = labels[i].ToUpper(CultureInfo.CurrentCulture),
                    FontSize = SegSize,
                    FontWeight = on ? FontWeights.Medium : FontWeights.Normal,
                    CharacterSpacing = 160,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                MinWidth = 0,
                MinHeight = 0,
                Padding = wide ? new Thickness(4, 9, 4, 9) : new Thickness(13, 9, 13, 9),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = on ? Clear : Line,
                Background = on ? Acc : CardHi,
                Foreground = on ? OnAcc : Tx3,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            if (wide)
            {
                key.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            int index = i;
            key.Click += (_, _) => pick(index);
            row.Children.Add(key);
        }

        return wide ? Spread(row) : row;
    }

    /// <summary>
    /// Lays a row of keys out in equal shares of one line.
    /// </summary>
    /// <remarks>
    /// A stack of keys is as wide as its longest label; on a line of its own
    /// that leaves a ragged tail. Equal shares need star columns, which is a
    /// Grid, so the keys are moved into one.
    /// </remarks>
    private static Grid Spread(StackPanel row)
    {
        var grid = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        List<FrameworkElement> keys = [.. row.Children.Cast<FrameworkElement>()];
        row.Children.Clear();

        for (int i = 0; i < keys.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(keys[i], i);
            grid.Children.Add(keys[i]);
        }

        return grid;
    }

    /// <summary>
    /// A row of icon-over-name tiles set into a recessed rail.
    /// </summary>
    /// <param name="items">
    /// The glyph, the name, and whether the thing behind it is switched on -
    /// which is a lamp rather than a word, so a tile stays the width of its
    /// name however long "off" happens to be in this language.
    /// </param>
    public static Border Tabs(
        IReadOnlyList<(string Glyph, string Label, bool On)> items, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        for (int i = 0; i < items.Count; i++)
        {
            bool chosen = i == selected;

            var body = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            body.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = items[i].Glyph,
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = chosen ? Tx : Tx3,
            });

            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            line.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Lamp(items[i].On),
            });

            line.Children.Add(new TextBlock
            {
                Text = items[i].Label.ToUpper(CultureInfo.CurrentCulture),
                FontSize = MicroSize,
                FontWeight = FontWeights.Medium,
                CharacterSpacing = 120,
                Foreground = chosen ? Tx : Tx3,
            });

            body.Children.Add(line);

            var tile = new Button
            {
                Content = body,
                MinWidth = 96,
                MinHeight = 0,
                Padding = new Thickness(14, 8, 14, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = chosen ? PanelHi : Clear,
            };

            // The chosen tile stands off the rail. Only that one: a shadow
            // under everything highlights nothing.
            if (chosen)
            {
                tile.Shadow = new ThemeShadow();
                tile.Translation = new Vector3(0, 0, 12);
            }

            int index = i;
            tile.Click += (_, _) => pick(index);
            row.Children.Add(tile);
        }

        return new Border
        {
            Background = Sunk,
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = row,
        };
    }

    /// <summary>
    /// A switch, drawn as the pill it is.
    /// </summary>
    /// <remarks>
    /// Wordless on purpose. The framework's switch writes "On" or "Off"
    /// beside itself, which in a language where those words are long stretches
    /// the row it sits in and leaves the column ragged - and the pill already
    /// says which way it is set.
    /// </remarks>
    public static Button Switch(bool on, Action<bool> set)
    {
        var knob = new Ellipse
        {
            Width = 15,
            Height = 15,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(on ? 19 : 3, 0, 0, 0),
            Fill = on ? Paint(0xFFF4F6F9) : Paint(0xFF6D747E),
        };

        var track = new Grid { Width = 38, Height = 22 };
        track.Children.Add(knob);

        var pill = new Button
        {
            Content = track,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            BorderBrush = on ? Clear : LineHi,
            Background = on ? Acc : Sunk,
        };

        pill.Click += (_, _) => set(!on);
        return pill;
    }

    /// <summary>
    /// A button that does something, in the shape MAS gives its buttons.
    /// </summary>
    /// <param name="ghost">
    /// True for anything that is not the one thing this screen is for.
    /// </param>
    /// <param name="danger">
    /// True for something that cannot be undone. Red is spent on that alone.
    /// </param>
    public static Button Action(
        string text, Action click, string? glyph = null, bool ghost = true, bool danger = false)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Brush ink = danger ? Danger : ghost ? Tx2 : OnAcc;

        if (glyph is { Length: > 0 })
        {
            body.Children.Add(new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = glyph,
                FontSize = 13,
                Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = text.ToUpper(CultureInfo.CurrentCulture),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            CharacterSpacing = 140,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var button = new Button
        {
            Content = body,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(15, 10, 15, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = danger ? Paint(0x59FF6B6Bu) : ghost ? Line : Clear,
            Background = ghost || danger ? Card : Acc,
        };

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A tile a picture or a glyph sits in - an icon, a swatch.</summary>
    public static Border Tile(double size, UIElement? content = null) => new()
    {
        Width = size,
        Height = size,
        Background = CardHi,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(size / 3.6),
        Child = content,
    };
}
