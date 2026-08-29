using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mcd.Core.Settings;
using Mcd.Interop.Shell;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mcd.App.Widgets;

/// <summary>
/// One pinned thing - a single slot of bar.
/// </summary>
/// <remarks>
/// Each pinned program, folder or address is a widget of its own, living in
/// its dock's run like any other: dragged between slots, dragged off to
/// unpin, added by dropping a file on the bar. There is no shared list any
/// more - what is pinned to a bar is pinned to that bar.
/// </remarks>
public sealed class IconWidget : WidgetViewModel
{
    public const string Type = "mcd.icon";

    public IconWidget(WidgetContext context, WidgetConfig entry)
        : base(context, entry)
    {
        Item = new LaunchButton(
            new LaunchItem
            {
                Id = entry.InstanceId,
                Name = WidgetOptions.Text(entry.Config, "name") ?? string.Empty,
                Target = WidgetOptions.Text(entry.Config, "target") ?? string.Empty,
                Icon = WidgetOptions.Text(entry.Config, "icon") ?? string.Empty,
            },
            context.Log);
    }

    public override string TypeId => Type;

    /// <summary>The one button this widget is.</summary>
    public LaunchButton Item { get; }

    /// <summary>
    /// Starts the program. What the bar hands back when a press turned out
    /// not to be the beginning of a drag.
    /// </summary>
    public override void Press() => Item.LaunchCommand.Execute(null);

    public string Target => Item.Target;

    public override void Attach() =>
        Item.Spacing = Orientation == Orientation.Vertical
            ? new Thickness(0, 1, 0, 1)
            : new Thickness(1, 0, 1, 0);

    /// <summary>Nothing here changes with the readings.</summary>
    public override void Tick(SensorSnapshot snapshot)
    {
    }

    public override string Summarise() => Item.Name;

    /// <summary>A 22-point icon with its padding and margins: exactly one slot.</summary>
    public override double Length() => 30;

    public override void Dispose()
    {
        Item.Dispose();
        base.Dispose();
    }

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var panel = new StackPanel { Spacing = 10 };

        var name = new TextBox
        {
            Header = Loc.Tr("PinNameHeader", "Name"),
            Text = Item.Name,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 220,
        };

        name.LostFocus += (_, _) =>
        {
            string typed = name.Text.Trim();

            changed(WidgetOptions.Merge(
                Options, ("name", typed.Length > 0 ? JsonValue.Create(typed) : null)));
        };

        panel.Children.Add(name);

        // The drawn icons on offer, and the way back to the program's own.
        var icons = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            MaximumRowsOrColumns = 8,
            ItemWidth = 34,
            ItemHeight = 34,
        };

        foreach (string glyph in IconLibrary.Paths.Keys)
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

            var button = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(3),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new Viewbox { Child = canvas },
            };

            string chosen = glyph;
            button.Click += (_, _) =>
                changed(WidgetOptions.Merge(Options, ("icon", JsonValue.Create(chosen))));

            icons.Children.Add(button);
        }

        var own = new Button
        {
            Content = Loc.Tr("LaunchIconAuto", "The program's own icon"),
        };

        own.Click += (_, _) => changed(WidgetOptions.Merge(Options, ("icon", null)));

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Tr("PinIconHeader", "Icon"),
            FontSize = 12,
            Opacity = 0.7,
        });
        panel.Children.Add(icons);
        panel.Children.Add(own);

        return panel;
    }

    /// <summary>
    /// A readable name for a target the person did not name themselves.
    /// </summary>
    /// <remarks>
    /// The file's own name without its extension, or the site for an address.
    /// "notepad" and "github.com" are both better than the full string, which on
    /// the bar is only ever a tooltip anyway.
    /// </remarks>
    public static string NameFor(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            return uri.Host.Length > 0 ? uri.Host : target;
        }

        string name = Path.GetFileNameWithoutExtension(target.TrimEnd('\\', '/'));

        return name.Length > 0 ? name : target;
    }

    /// <summary>
    /// The widget entry for a file dropped on a bar.
    /// </summary>
    /// <remarks>
    /// A shortcut is pinned as what it points at: the extracted icon then comes
    /// without the little link arrow, and tidying the shortcut away later does
    /// not break the pin. The shortcut's own name is kept - it is usually the
    /// friendlier one.
    /// </remarks>
    public static WidgetConfig Pin(string path)
    {
        string name = NameFor(path);

        if (ShellLinkResolver.Resolve(path) is { } target)
        {
            path = target;
        }

        return WidgetConfig.New(Type) with
        {
            Config = WidgetJson.Object(("target", path), ("name", name)),
        };
    }
}

/// <summary>One pinned thing: its picture, its name, and what starting it does.</summary>
public sealed partial class LaunchButton : ObservableObject, IDisposable
{
    /// <summary>
    /// Icons already extracted, by target.
    /// </summary>
    /// <remarks>
    /// Three docks on three screens ask for the same icons, and asking the shell
    /// three times for each is three times the work for the same answer. The
    /// pixels are cached rather than the bitmap: a bitmap is handed to an Image
    /// that outlives nothing, and sharing drawable objects between elements is
    /// how the dock ended up with no icons at all once before.
    /// </remarks>
    private static readonly ConcurrentDictionary<string, IconPixels?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Targets already reported as having no icon, so the log says it once.</summary>
    private static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _log;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly CancellationTokenSource _gone = new();

    public LaunchButton(LaunchItem item, ILogger log)
    {
        _log = log;
        Id = item.Id;
        Name = string.IsNullOrWhiteSpace(item.Name) ? item.Target : item.Name;
        Target = item.Target;
        Initial = Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
        Chosen = IconLibrary.Paths.ContainsKey(item.Icon) ? item.Icon : string.Empty;
        ChosenVisible = Chosen.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoVisible = Chosen.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (Chosen.Length == 0)
        {
            FindIcon();
        }
    }

    /// <summary>A library icon the person chose instead of the extracted one.</summary>
    public string Chosen { get; }

    public Visibility ChosenVisible { get; }

    public Visibility AutoVisible { get; }

    /// <summary>The pinned item's own id, so a dragged icon can say which it is.</summary>
    public string Id { get; }

    public string Name { get; }

    public string Target { get; }

    /// <summary>Drawn in place of the icon while there is not one.</summary>
    public string Initial { get; }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    [ObservableProperty]
    public partial Visibility Placeholder { get; set; } = Visibility.Visible;

    partial void OnPlaceholderChanged(Visibility value)
    {
        // A chosen glyph replaces both the extracted icon and the letter.
        if (Chosen.Length > 0 && value == Visibility.Visible)
        {
            Placeholder = Visibility.Collapsed;
        }
    }

    [ObservableProperty]
    public partial Thickness Spacing { get; set; }

    public void Dispose()
    {
        _gone.Cancel();
        _gone.Dispose();
    }

    [RelayCommand]
    private void Launch()
    {
        try
        {
            // Through the shell, so that a folder opens in Explorer, an address
            // opens in the browser, and a document opens in whatever claims it.
            Process.Start(new ProcessStartInfo(Target) { UseShellExecute = true });
            _log.LogInformation("launcher.started {Target}", Target);
        }
        catch (Exception e)
        {
            // A program that has since been uninstalled. Worth a line in the log
            // and nothing more: the dock must not throw a dialog at someone for
            // clicking a button.
            _log.LogWarning(e, "launcher.failed {Target}", Target);
        }
    }

    /// <summary>
    /// Fetches the icon without holding up the bar.
    /// </summary>
    /// <remarks>
    /// Asking the shell for an icon reaches the file system, and for a target on
    /// a drive that is no longer connected it reaches it for several seconds.
    /// That must not happen on the thread that draws the dock.
    /// </remarks>
    private async void FindIcon()
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            return;
        }

        IconPixels? pixels;

        try
        {
            pixels = await Task.Run(
                () => Cache.GetOrAdd(Target, ShellIcon.For), _gone.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (pixels is null)
        {
            // Ordinary for an address, which is a page rather than a file. Said
            // once per target: the docks are rebuilt whenever the settings
            // change, and each rebuild would otherwise repeat every line.
            if (Reported.TryAdd(Target, true))
            {
                _log.LogInformation("launcher.icon none for {Target}", Target);
            }

            return;
        }

        if (_gone.IsCancellationRequested)
        {
            return;
        }

        _ui.TryEnqueue(() => Show(pixels));
    }

    private void Show(IconPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);

        using (Stream target = bitmap.PixelBuffer.AsStream())
        {
            target.Write(pixels.Bgra, 0, pixels.Bgra.Length);
        }

        bitmap.Invalidate();

        Icon = bitmap;
        Placeholder = Visibility.Collapsed;
    }
}
