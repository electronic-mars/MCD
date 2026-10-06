using Mcd.Core.Monitors;
using Mcd.Interop.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace Mcd.App.Dock;

/// <summary>
/// Puts each screen's own number on it for a couple of seconds, as the Windows
/// display settings do, so "Display 2" can be matched to a piece of glass.
/// </summary>
public static class IdentifyScreens
{
    private static readonly List<Window> Shown = [];
    private static DispatcherQueueTimer? _timer;

    public static void Flash(IEnumerable<MonitorInfo> screens)
    {
        Clear();

        foreach (MonitorInfo screen in screens)
        {
            string digits = new([.. screen.Identity.GdiName.Where(char.IsDigit)]);

            var window = new Window
            {
                Content = new Grid
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = digits.Length > 0 ? digits : "?",
                            FontSize = 96,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
            };

            nint hwnd = WindowNative.GetWindowHandle(window);
            WindowFrame.MakeChromeless(hwnd);

            // Top-left corner of that screen, 240 by 200 physical pixels.
            WindowFrame.MoveTo(hwnd, new Windows.Win32.Foundation.RECT
            {
                left = screen.Bounds.left + 40,
                top = screen.Bounds.top + 40,
                right = screen.Bounds.left + 280,
                bottom = screen.Bounds.top + 240,
            });

            WindowFrame.SetTopmost(hwnd, topmost: true);
            window.Activate();
            Shown.Add(window);
        }

        _timer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2.5);
        _timer.IsRepeating = false;
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private static void OnTick(DispatcherQueueTimer sender, object args) => Clear();

    private static void Clear()
    {
        foreach (Window window in Shown)
        {
            window.Close();
        }

        Shown.Clear();
    }
}
