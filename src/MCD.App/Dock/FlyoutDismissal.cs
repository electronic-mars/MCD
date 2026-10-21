using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mcd.App.Dock;

/// <summary>
/// Puts away a panel opened from the bar - the volume, the brightness, a
/// reading's hour, the bar's menu - when it is no longer being used.
/// </summary>
/// <remarks>
/// <para>
/// A flyout closes by itself on a click inside the window it belongs to, and
/// the bar is a window nobody clicks into on the way to something else: a
/// click on the desktop or on another program went to that window, and the
/// slider stayed open over whatever was under it. So the pointer is watched
/// while one is open. A press anywhere that is not this program closes it,
/// and so do ten seconds with the pointer away from it, or resting on it
/// without moving.
/// </para>
/// <para>
/// Watched by polling, a few times a second and only while open, rather than
/// with a mouse hook: a hook is called for every movement on the whole
/// desktop for as long as the program runs.
/// </para>
/// </remarks>
public static class FlyoutDismissal
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(120);

    private static readonly ConditionalWeakTable<FlyoutBase, object> Watched = [];

    /// <summary>Held while running: a timer only a local refers to is garbage.</summary>
    private static readonly HashSet<DispatcherQueueTimer> Running = [];

    /// <summary>Watches this flyout every time it opens. Asking twice changes nothing.</summary>
    public static void Watch(FlyoutBase flyout)
    {
        if (Watched.TryGetValue(flyout, out _))
        {
            return;
        }

        Watched.Add(flyout, new object());
        flyout.Opened += (_, _) => Begin(flyout);
    }

    private static void Begin(FlyoutBase flyout)
    {
        DispatcherQueueTimer timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Beat;

        DateTime used = DateTime.UtcNow;
        (int X, int Y, bool Down, bool Ours) last = Mcd.Interop.Windowing.WindowFrame.PointerNow();

        void Stop(object? sender, object e)
        {
            timer.Stop();
            Running.Remove(timer);
            flyout.Closed -= Stop;
        }

        flyout.Closed += Stop;

        timer.Tick += (_, _) =>
        {
            (int X, int Y, bool Down, bool Ours) now = Mcd.Interop.Windowing.WindowFrame.PointerNow();

            if (now.Ours && (now.Down || now.X != last.X || now.Y != last.Y))
            {
                used = DateTime.UtcNow;
            }

            bool pressedElsewhere = now.Down && !last.Down && !now.Ours;
            last = now;

            if (pressedElsewhere || DateTime.UtcNow - used >= Idle)
            {
                flyout.Hide();
            }
        };

        Running.Add(timer);
        timer.Start();
    }
}
