using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Mcd.Interop.Windowing;

namespace Mcd.App.Dock;

/// <summary>
/// Puts away a panel opened from the bar - the volume, the brightness, a
/// reading's hour, the bar's menu - when it is no longer being used.
/// </summary>
/// <remarks>
/// <para>
/// A flyout closes by itself on a click inside the window it belongs to, or
/// on one that takes the activation away from it. A click on the empty
/// desktop does neither, and the slider stayed open over it. So while one is
/// open, every press on the machine is heard (see <see cref="PressWatch"/>):
/// a press anywhere but on the panel itself closes it - another program, the
/// desktop, the bar. And ten seconds unused close it too: the pointer away
/// from this program, or resting without moving, and no key pressed in it.
/// </para>
/// <para>
/// A panel is told from everything else by the window it is drawn in, so
/// every watched one is let out of its bar into a window of its own - which a
/// panel on a strip a few tens of points thick has to be anyway.
/// </para>
/// </remarks>
public static class FlyoutDismissal
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Long enough to cover the button that opens a panel hearing the same
    /// press that has just put the panel away.
    /// </summary>
    private static readonly TimeSpan Echo = TimeSpan.FromMilliseconds(600);

    private sealed class Watched
    {
        /// <summary>When it was last put away by a press on the bar, which may be its own button.</summary>
        public DateTime PressedOnBar = DateTime.MinValue;

        /// <summary>The last time somebody did something with it.</summary>
        public DateTime Used;
    }

    private static readonly ConditionalWeakTable<FlyoutBase, Watched> Known = [];

    /// <summary>The panels open now.</summary>
    private static readonly List<FlyoutBase> Open = [];

    private static DispatcherQueueTimer? _timer;
    private static (int X, int Y) _pointer;

    /// <summary>Watches this flyout every time it opens. Asking twice changes nothing.</summary>
    public static void Watch(FlyoutBase flyout)
    {
        if (Known.TryGetValue(flyout, out _))
        {
            return;
        }

        var watched = new Watched();
        Known.Add(flyout, watched);

        flyout.ShouldConstrainToRootBounds = false;

        // Keys count as use: a slider worked with the arrows has no pointer
        // moving over it. Heard even when the slider has handled them.
        if (flyout is Flyout { Content: UIElement content })
        {
            content.AddHandler(
                UIElement.KeyDownEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => watched.Used = DateTime.UtcNow),
                handledEventsToo: true);
        }

        flyout.Opened += (_, _) =>
        {
            // The press that put it away was on the bar, perhaps on the button
            // that opens it - which heard the same press and opened it again.
            // A press on the button of an open panel means "close". A press
            // anywhere else, then a quick press on the button, is a reopening
            // and is left alone.
            if (DateTime.UtcNow - watched.PressedOnBar < Echo)
            {
                watched.PressedOnBar = DateTime.MinValue;
                flyout.Hide();
                return;
            }

            watched.Used = DateTime.UtcNow;
            Begin(flyout);
        };

        flyout.Closed += (_, _) => End(flyout);
    }

    /// <summary>Watches every button with a panel inside this element, once it has been laid out.</summary>
    public static void WatchWithin(FrameworkElement root) =>
        root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Walk(root));

    private static void Walk(DependencyObject at)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(at, i);

            if (child is Button { Flyout: { } flyout })
            {
                Watch(flyout);
            }

            Walk(child);
        }
    }

    private static void Begin(FlyoutBase flyout)
    {
        if (!Open.Contains(flyout))
        {
            Open.Add(flyout);
        }

        if (_timer is null)
        {
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = Beat;
            _timer.Tick += (_, _) => Look();
        }

        (int x, int y, _, _) = WindowFrame.PointerNow();
        _pointer = (x, y);

        if (!_timer.IsRunning)
        {
            DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();

            // Inside the hook, which has to answer at once: the window under
            // the press is asked about there, at the moment of the press, and
            // the closing is done after, on the window's thread.
            PressWatch.On((px, py) =>
            {
                bool panel = WindowFrame.PopupAt(px, py);
                bool ours = !panel && WindowFrame.OursAt(px, py);

                if (!panel)
                {
                    ui.TryEnqueue(() => Pressed(ours));
                }
            });

            _timer.Start();
        }
    }

    private static void Pressed(bool onBar)
    {
        foreach (FlyoutBase flyout in Open.ToList())
        {
            if (onBar && Known.TryGetValue(flyout, out Watched? watched))
            {
                watched.PressedOnBar = DateTime.UtcNow;
            }

            flyout.Hide();
        }
    }

    private static void Look()
    {
        (int x, int y, bool down, bool ours) = WindowFrame.PointerNow();
        bool moved = (x, y) != _pointer;
        _pointer = (x, y);

        foreach (FlyoutBase flyout in Open.ToList())
        {
            // Its bar rebuilt under it, or closed without a word: nothing more
            // to watch.
            if (!flyout.IsOpen || flyout.XamlRoot is null)
            {
                End(flyout);
                continue;
            }

            if (!Known.TryGetValue(flyout, out Watched? watched))
            {
                continue;
            }

            if (ours && (down || moved))
            {
                watched.Used = DateTime.UtcNow;
            }

            if (DateTime.UtcNow - watched.Used >= Idle)
            {
                flyout.Hide();
            }
        }
    }

    private static void End(FlyoutBase flyout)
    {
        Open.Remove(flyout);

        if (Open.Count == 0 && _timer is { IsRunning: true })
        {
            _timer.Stop();
            PressWatch.Off();
        }
    }
}
