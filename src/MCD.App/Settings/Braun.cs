using System.Globalization;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Mcd.App.Widgets;

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

    /// <summary>
    /// The one accent this program owns.
    /// </summary>
    /// <remarks>
    /// Blue, not the reference program's Braun orange. What was worth taking
    /// from that program is how it is built - the recesses, the raised keys,
    /// the type scale - and not the colour it wears, which is that program's
    /// own. This one is the colour Windows is set to, so the bar agrees with
    /// the desktop it lives on.
    /// </remarks>
    public static Brush Acc =>
        (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    /// <summary>Text and glyphs printed on the accent.</summary>
    public static Brush OnAcc =>
        (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];

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
            Margin = new Thickness(0, 14, 0, 7),
        };

        row.Children.Add(Glyph(glyph, 16, Tx3));
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
        var grid = new Grid { Padding = new Thickness(13, 10, 13, 10), ColumnSpacing = 13 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

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

        grid.Children.Add(control);

        // Beside the words while there is room for both, underneath when
        // there is not.
        //
        // An Auto column measures at whatever width its content wants and
        // leaves the starred one the remainder, which can be almost nothing:
        // a wrapping explanation next to a switch then comes out set one word
        // to a line. Deciding once at build time is not enough, because the
        // window can be resized afterwards - so the row decides every time it
        // is given a width.
        void Lay(double width)
        {
            // What decides this is not how wide the row is but how much is
            // left for the words once the control has taken its share. A
            // button with a sentence on it can eat three hundred points of a
            // five-hundred-point row and leave the explanation in a gutter.
            double taken = Math.Max(control.ActualWidth, control.DesiredSize.Width);
            bool under = stack || width < Roomy || width - taken - 13 < Wordy;

            control.Margin = under ? new Thickness(0, 9, 0, 0) : new Thickness(0);
            control.HorizontalAlignment = under ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
            control.VerticalAlignment = under ? VerticalAlignment.Top : VerticalAlignment.Center;

            Grid.SetRow(control, under ? 1 : 0);
            Grid.SetColumn(control, under ? 0 : 1);
            Grid.SetColumnSpan(control, under ? 2 : 1);
        }

        Lay(stack ? 0 : double.MaxValue);

        if (!stack)
        {
            grid.SizeChanged += (_, e) => Lay(e.NewSize.Width);
        }

        return grid;
    }

    /// <summary>
    /// The width below which a row puts its control under the words.
    /// </summary>
    /// <remarks>
    /// Enough for the shortest useful explanation beside the widest ordinary
    /// control - a segmented pair, or a button with a word on it. Under this
    /// the two are stacked, which always reads, rather than squeezed, which
    /// stops reading well before it stops fitting.
    /// </remarks>
    private const double Roomy = 380;

    /// <summary>
    /// The narrowest column of words worth reading. Below this the control
    /// goes underneath, however wide the row is.
    /// </summary>
    private const double Wordy = 260;

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

            var caption = new TextBlock
            {
                Text = labels[i].ToUpper(CultureInfo.CurrentCulture),
                FontSize = MicroSize,
                FontWeight = on ? FontWeights.Medium : FontWeights.Normal,
                CharacterSpacing = 160,
                Foreground = on ? OnAcc : Tx3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = wide ? new Thickness(4, 9, 4, 9) : new Thickness(13, 9, 13, 9),
            };

            // The chosen key wears the accent and, like the chosen tab, a lit
            // line round its top edge. The reference leaves its own keys flat;
            // this program does not, because the same thing meaning "chosen"
            // should be drawn the same way wherever it appears.
            var key = new Button
            {
                Content = on
                    ? Raised(caption, Acc, radius: 8, blur: 5, drop: 1, depth: 0.28)
                    : Flat(caption, CardHi, radius: 8, edge: Line),
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Clear,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };

            if (wide)
            {
                key.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            int index = i;
            key.Click += (_, _) => pick(index);
            row.Children.Add(key);
        }

        return wide ? Spread(row, labels) : row;
    }

    /// <summary>
    /// Lays a row of keys out in equal shares of one line, or of as many lines
    /// as the longest word needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stack of keys is as wide as its longest label; on a line of its own
    /// that leaves a ragged tail. Equal shares need star columns, which is a
    /// Grid, so the keys are moved into one.
    /// </para>
    /// <para>
    /// How many share a line is worked out from the longest word and the room
    /// there is, not fixed. A number picked by hand is a number picked in one
    /// language: four keys fit "Translucent" and cut "Полупрозрачный" in half,
    /// with the first letter and the last both gone - which is worse than the
    /// clipping it was chosen to cure, because a cut word still looks like a
    /// word and nobody thinks to widen the window.
    /// </para>
    /// </remarks>
    private static Grid Spread(StackPanel row, IReadOnlyList<string> labels)
    {
        var grid = new Grid
        {
            ColumnSpacing = 4,
            RowSpacing = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        List<FrameworkElement> keys = [.. row.Children.Cast<FrameworkElement>()];
        row.Children.Clear();

        foreach (FrameworkElement key in keys)
        {
            grid.Children.Add(key);
        }

        double needed = Widest(labels);
        int across = 0;

        void Lay(double room)
        {
            // At least one to a line, never more than there are, and never so
            // many that the longest of them has to be cut.
            int fits = room > 1
                ? Math.Clamp((int)((room + 4) / (needed + 4)), 1, keys.Count)
                : keys.Count;

            // Balanced rather than filled: five keys where four fit go three
            // and two, not four and one. A lone key on a line of its own reads
            // as a mistake, and it is no narrower than sharing.
            int down = (int)Math.Ceiling(keys.Count / (double)fits);
            fits = (int)Math.Ceiling(keys.Count / (double)down);

            if (fits == across)
            {
                return;
            }

            across = fits;

            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();

            for (int i = 0; i < across; i++)
            {
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int i = 0; i < down; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < keys.Count; i++)
            {
                Grid.SetColumn(keys[i], i % across);
                Grid.SetRow(keys[i], i / across);
            }
        }

        grid.SizeChanged += (_, e) => Lay(e.NewSize.Width);
        Lay(0);

        return grid;
    }

    /// <summary>
    /// How wide the longest of these keys has to be to hold its word whole.
    /// </summary>
    /// <remarks>
    /// Measured rather than counted in characters. The keys are drawn in small
    /// capitals with the letters spaced apart, and an estimate that is a
    /// little short is a word with its ends missing.
    /// </remarks>
    private static double Widest(IReadOnlyList<string> labels)
    {
        _ruler ??= new TextBlock
        {
            FontSize = MicroSize,
            CharacterSpacing = 160,

            // The heaviest weight any of them can switch to: the chosen key is
            // Medium, and a width reserved at Normal clips the moment somebody
            // picks it.
            FontWeight = FontWeights.Medium,
        };

        double widest = 0;

        foreach (string label in labels)
        {
            _ruler.Text = label.ToUpper(CultureInfo.CurrentCulture);
            _ruler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, _ruler.DesiredSize.Width);
        }

        // The caption's own margins either side of the word.
        return widest + 8;
    }

    /// <summary>Never shown, never in the tree: it exists to be measured against.</summary>
    private static TextBlock? _ruler;

    /// <summary>
    /// A row of icon-over-name tiles set into a recessed rail.
    /// </summary>
    /// <param name="items">
    /// The glyph, the name, and whether the thing behind it is switched on -
    /// which is a lamp rather than a word, so a tile stays the width of its
    /// name however long "off" happens to be in this language.
    /// </param>
    public static Grid Tabs(
        IReadOnlyList<(string Glyph, string Label, bool On)> items, int selected, Action<int> pick)
    {
        // Equal shares of the rail, and the rail across the whole column.
        // The reference's tabs are flex:1 inside a tray that fills its
        // parent; measured on the rendered page, each is 119.5 wide of a
        // 369.6 rail. Tiles that hug their own labels and huddle at the left
        // are the single thing that made this read as a copy rather than the
        // same instrument.
        var row = new Grid { ColumnSpacing = 2 };

        for (int i = 0; i < items.Count; i++)
        {
            bool chosen = i == selected;

            row.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var body = new StackPanel
            {
                Spacing = 4,
                Padding = new Thickness(0, 8, 0, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 24, not 17. The reference's tab icon measures 24.2 on the
            // rendered page and it is what gives the tile its weight.
            body.Children.Add(Glyph(items[i].Glyph, 23, chosen ? Tx : Tx3));

            var line = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
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
                Text = items[i].Label.ToUpper(CultureInfo.CurrentCulture),
                FontSize = MicroSize,
                FontWeight = FontWeights.Medium,
                CharacterSpacing = 120,
                Foreground = chosen ? Tx : Tx3,
            });

            body.Children.Add(line);

            // The chosen tab is the one thing in this program that stands off
            // the page: a lit line along its top edge and a short shadow -
            // 0 2px 5px at three tenths, which is what the reference draws
            // and half the blur this had been using.
            FrameworkElement face = chosen
                ? Raised(body, PanelHi, radius: 8, blur: 5.5, drop: 2, depth: 0.30)
                : body;

            var tile = new Button
            {
                Content = face,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Clear,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };

            Grid.SetColumn(tile, i);

            int index = i;
            tile.Click += (_, _) => pick(index);
            row.Children.Add(tile);
        }

        return Recessed(row);
    }

    /// <summary>A surface flush with what it sits on.</summary>
    private static Grid Flat(UIElement content, Brush fill, double radius, Brush? edge = null)
    {
        var stack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        stack.Children.Add(new Rectangle
        {
            Fill = fill,
            Stroke = edge ?? Line,
            StrokeThickness = 1,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
        });

        stack.Children.Add(content);
        return stack;
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
        const double Wide = 38;
        const double Tall = 22;
        const double Ball = 16;

        var track = new Grid { Width = Wide, Height = Tall };

        // The track is a recess whichever way it is set, and it is drawn the
        // way every recess in this program is: a fill darker than the panel,
        // then a stroke round the whole outline that is dark at the top and
        // gone by halfway down.
        track.Children.Add(new Rectangle
        {
            RadiusX = Tall / 2,
            RadiusY = Tall / 2,
            Fill = on ? Acc : Sunk,
            Stroke = on ? Clear : LineHi,
            StrokeThickness = 1,
        });

        track.Children.Add(new Rectangle
        {
            RadiusX = Tall / 2,
            RadiusY = Tall / 2,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            Stroke = Down(Dark ? 0x59000000u : 0x24000000u, 0.45),
        });

        // And the knob is a raised thing, drawn the way every raised thing
        // is: light at the top of its own fill, and a shadow under it.
        var under = new Border
        {
            Width = Ball,
            Height = Ball,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(on ? Wide - Ball - 3 : 3, 0, 0, 0),
            IsHitTestVisible = false,
        };

        var ball = new Ellipse
        {
            Width = Ball,
            Height = Ball,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = under.Margin,
            IsHitTestVisible = false,
            Fill = Cylinder(
                on ? 0xFFFFFFFFu : 0xFF7C838Eu,
                on ? 0xFFE2E6ECu : 0xFF5A616Bu),
        };

        track.Children.Add(under);
        track.Children.Add(ball);

        // Small. On a pill this size a four-point blur reads as a dark
        // crescent under the cap and the cap looks pushed upwards - which is
        // what it was doing, though the cap is centred to within half a
        // pixel. The reference blurs two.
        Shadow(under, ball, blur: 2.5, drop: 1, depth: 0.38);

        var pill = new Button
        {
            Content = track,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Clear,
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
        Brush ink = danger ? Danger : ghost ? Tx2 : OnAcc;
        StackPanel body = Legend(text, glyph, ink);

        body.Margin = new Thickness(15, 10, 15, 10);

        // A button is a key on an instrument: it has a lit top edge and it
        // stands off the panel. Even the quiet ones - a key you can press
        // should look like a key.
        Grid face = Raised(
            body,
            danger ? Paint(Dark ? 0xFF2A1E20u : 0xFFF7EBEB) : ghost ? CardHi : Acc,
            radius: 10,
            blur: 8,
            drop: 1,
            depth: danger || ghost ? 0.28 : 0.36);

        var button = new Button
        {
            Content = face,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Clear,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// What is printed on a button: a glyph, then the word in small caps.
    /// </summary>
    /// <remarks>
    /// Separate from the button so a label can be changed without building a
    /// new button - which matters for the one button whose label changes
    /// while the page around it stays put.
    /// </remarks>
    public static StackPanel Legend(string text, string? glyph, Brush ink)
    {
        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (glyph is { Length: > 0 })
        {
            body.Children.Add(Glyph(glyph, 15, ink));
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

        return body;
    }

    // ----------------------------------------------------------------- depth
    //
    // The reference program is a web view, and what makes it look machined is
    // two box-shadows on one element and a third on the thing it sits in:
    //
    //   .tabs                  inset 0 1px 3px rgba(0,0,0,.35)      a recess
    //   .tab[selected]         inset 0 1px 0  rgba(255,255,255,.07) a lit edge
    //                          0 2px 5px rgba(0,0,0,.3)             standing off
    //
    // None of that is ThemeShadow, which is the framework's own shadow for
    // flyouts: it needs a receiver behind it, it is tuned for a card lifted
    // eight points off a page, and under a tile on a dark rail it shows
    // nothing at all. Drawn here as what it is - one lit line, one recess,
    // one real shadow from the compositor.

    /// <summary>
    /// A surface that stands off what it sits on.
    /// </summary>
    /// <remarks>
    /// Three layers, because a box-shadow is three things: the fill, a lit
    /// line along its top edge, and a soft shadow beneath. The shadow comes
    /// from the compositor with the fill's own alpha mask, so it follows the
    /// corner radius instead of squaring it off.
    /// </remarks>
    /// <param name="content">What is drawn on the surface.</param>
    /// <param name="fill">Its colour.</param>
    /// <param name="radius">Its corner.</param>
    /// <param name="blur">How soft the shadow is.</param>
    /// <param name="drop">How far it falls.</param>
    /// <param name="depth">How dark it is, nought to one.</param>
    public static Grid Raised(
        UIElement content,
        Brush fill,
        double radius = 8,
        double blur = 10,
        double drop = 2,
        double depth = 0.34)
    {
        var stack = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // The shadow is drawn into this one, behind everything: a sprite
        // visual renders inside its host, so the host has to be the layer
        // underneath rather than the face itself.
        var under = new Border { IsHitTestVisible = false };

        var face = new Rectangle
        {
            Fill = fill,
            RadiusX = radius,
            RadiusY = radius,
            IsHitTestVisible = false,
        };

        stack.Children.Add(under);
        stack.Children.Add(face);
        stack.Children.Add(Sheen(radius));
        stack.Children.Add(content);

        Shadow(under, face, blur, drop, depth);
        return stack;
    }

    /// <summary>
    /// A recess: the rail a row of keys is set into.
    /// </summary>
    /// <remarks>
    /// An inner shadow, which the compositor will not draw. Approximated the
    /// way it reads: the ground is darker than the body around it, and a
    /// short gradient down from the top edge stands in for the light the far
    /// wall does not get.
    /// </remarks>
    public static Grid Recessed(UIElement content, double radius = 11, double padding = 3)
    {
        var stack = new Grid();

        stack.Children.Add(new Border
        {
            Background = Sunk,
            CornerRadius = new CornerRadius(radius),
        });

        // The far wall of the recess, along the whole outline for the same
        // reason the lit edge is: a band across the top alone leaves the
        // corners flat and the hole stops reading as a hole.
        stack.Children.Add(new Rectangle
        {
            RadiusX = radius,
            RadiusY = radius,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            Stroke = Down(Dark ? 0x66000000u : 0x2E000000u, 0.45),
        });

        if (content is FrameworkElement inside)
        {
            inside.Margin = new Thickness(padding);
        }

        stack.Children.Add(content);
        return stack;
    }

    /// <summary>
    /// The lit edge of a raised surface, drawn along its whole outline.
    /// </summary>
    /// <remarks>
    /// A one-point line pinned to the top stops at the straight part and
    /// leaves the corners dark, which is visible as two nicks. A stroked
    /// rounded rectangle of the same radius follows the contour and turns the
    /// corner; the gradient down the stroke is what makes it a highlight on
    /// top rather than an outline all the way round.
    /// </remarks>
    private static Rectangle Sheen(double radius) => new()
    {
        RadiusX = radius,
        RadiusY = radius,
        StrokeThickness = 1,
        IsHitTestVisible = false,
        Stroke = Down(Dark ? 0x1FFFFFFFu : 0xB3FFFFFFu, 0.55),
    };

    /// <summary>
    /// A brush that is this colour at the top and nothing by the given point
    /// down the shape.
    /// </summary>
    /// <remarks>
    /// Used as a stroke rather than a fill. Along the top edge of the outline
    /// it draws at full strength, around the corners it fades, and down the
    /// sides it is gone - which is what an inset shadow of no blur and one
    /// point of offset actually looks like.
    /// </remarks>
    private static LinearGradientBrush Down(uint argb, double gone) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = Hue(argb) },
            new GradientStop { Offset = gone, Color = Hue(argb & 0x00FFFFFFu) },
            new GradientStop { Offset = 1, Color = Hue(argb & 0x00FFFFFFu) },
        },
    };

    private static Windows.UI.Color Hue(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>
    /// Hangs a real shadow under an element, shaped by the element itself.
    /// </summary>
    /// <remarks>
    /// The compositor draws it, so it is a genuine blur rather than a stack
    /// of rectangles pretending to be one. The mask is the element's own
    /// alpha, which is why the shadow has the same rounded corners it does.
    /// Sized on every layout pass: a sprite visual has no idea what its host
    /// was arranged into.
    /// </remarks>
    private static void Shadow(Border under, Shape face, double blur, double drop, double depth)
    {
        Microsoft.UI.Composition.Visual root =
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(under);

        Microsoft.UI.Composition.Compositor compositor = root.Compositor;

        Microsoft.UI.Composition.DropShadow shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = (float)blur;
        shadow.Offset = new Vector3(0, (float)drop, 0);
        shadow.Opacity = (float)depth;
        shadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);

        Microsoft.UI.Composition.SpriteVisual sprite = compositor.CreateSpriteVisual();
        sprite.Shadow = shadow;

        void Fit()
        {
            sprite.Size = new Vector2((float)face.ActualWidth, (float)face.ActualHeight);
            shadow.Mask = face.GetAlphaMask();
        }

        face.SizeChanged += (_, _) => Fit();
        face.Loaded += (_, _) => Fit();

        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(under, sprite);
    }

    /// <summary>
    /// One icon from this program's own set.
    /// </summary>
    /// <remarks>
    /// Hugeicons, drawn as strokes on a 24-unit grid at 1.7 with round ends -
    /// the same set and the same weight the bar draws its readings with. The
    /// system's glyph font is a different set by a different hand: two icon
    /// sets in one program read as two programs stitched together, and the
    /// join is exactly where somebody notices that the thing is a copy.
    /// </remarks>
    public static Viewbox Glyph(string name, double size, Brush ink) => new()
    {
        Width = size,
        Height = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = new Canvas
        {
            Width = IconLibrary.Grid,
            Height = IconLibrary.Grid,
            Children =
            {
                new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = IconRow.Draw(name),
                    Stroke = ink,
                    StrokeThickness = 1.7,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                },
            },
        },
    };

    /// <summary>
    /// A fill that is lighter at the top than at the bottom.
    /// </summary>
    /// <remarks>
    /// What makes a small round thing read as a cap rather than a dot. Never
    /// a flat fill: the reference has no raised surface anywhere that is one
    /// colour all the way down.
    /// </remarks>
    private static LinearGradientBrush Cylinder(uint top, uint bottom) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Offset = 0, Color = Hue(top) },
            new GradientStop { Offset = 1, Color = Hue(bottom) },
        },
    };

    /// <summary>
    /// A list to choose one thing from, dressed like everything else.
    /// </summary>
    /// <remarks>
    /// The framework's own list is a different grey, a different corner and a
    /// different height from the keys beside it, and next to them it reads as
    /// a control borrowed from another program - which it is.
    /// </remarks>
    public static ComboBox Choice(IReadOnlyList<string> labels, int selected, Action<int> pick)
    {
        var box = new ComboBox
        {
            MinWidth = 220,
            SelectedIndex = Math.Clamp(selected, -1, labels.Count - 1),
            Background = CardHi,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Foreground = Tx,
            Padding = new Thickness(11, 9, 11, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (string label in labels)
        {
            box.Items.Add(label);
        }

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                pick(box.SelectedIndex);
            }
        };

        return box;
    }

    /// <summary>
    /// A label above the thing it names, for the inside of a widget's own
    /// settings - where there is no room for a row's two columns.
    /// </summary>
    public static StackPanel Field(string label, FrameworkElement control, string? note = null)
    {
        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = NameSize,
            Foreground = Tx,
            TextWrapping = TextWrapping.Wrap,
        });

        if (note is { Length: > 0 })
        {
            stack.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = NoteSize,
                Foreground = Tx3,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        control.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(control);
        return stack;
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
