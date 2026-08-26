using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
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

/// <summary>The things pinned to the bar, in the order they were pinned.</summary>
/// <remarks>
/// The list is the same on every screen, and lives in the settings rather than
/// in this widget's own configuration - see <see cref="AppSettings.Launcher"/>.
/// </remarks>
public sealed class LauncherWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.launcher";

    public override string TypeId => Type;

    public ObservableCollection<LaunchButton> Items { get; } = [];

    public override void Attach()
    {
        Items.Clear();

        foreach (LaunchItem item in Context.Launcher)
        {
            Items.Add(new LaunchButton(item, Context.Log) { Spacing = Gap() });
        }
    }

    public override string Summarise() =>
        Context.Launcher.Length switch
        {
            0 => "Nothing pinned yet",
            1 => "1 pinned item",
            int n => $"{n} pinned items",
        };

    /// <summary>Nothing here changes with the readings.</summary>
    public override void Tick(SensorSnapshot snapshot)
    {
    }

    public override void Dispose()
    {
        foreach (LaunchButton item in Items)
        {
            item.Dispose();
        }

        Items.Clear();
        base.Dispose();
    }

    private Thickness Gap() =>
        Orientation == Orientation.Vertical ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0);
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
        Name = string.IsNullOrWhiteSpace(item.Name) ? item.Target : item.Name;
        Target = item.Target;
        Initial = Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

        FindIcon();
    }

    public string Name { get; }

    public string Target { get; }

    /// <summary>Drawn in place of the icon while there is not one.</summary>
    public string Initial { get; }

    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    [ObservableProperty]
    public partial Visibility Placeholder { get; set; } = Visibility.Visible;

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
