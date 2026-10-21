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
/// A flyout closes by itself on a click inside the window it belongs to, or
/// on one that takes the activation away from it. A click on the empty
/// desktop does neither, and the slider stayed open over it. So while one is
/// open, every press on the machine is heard (see <see cref="Mcd.Interop.Windowing.PressWatch"/>):
/// a press anywhere that is not this program closes it. And the pointer is
/// looked at a few times a second: ten seconds with it away, or resting on
/// the panel without moving, close it too.
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

        DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();

        // Called inside the hook, which has to answer at once: the window is
        // asked about there, the closing is done after.
        var presses = new Mcd.Interop.Windowing.PressWatch((x, y) =>
        {
            if (!Mcd.Interop.Windowing.WindowFrame.OursAt(x, y))
            {
                ui.TryEnqueue(flyout.Hide);
            }
        });

        void Stop(object? sender, object e)
        {
            timer.Stop();
            presses.Dispose();
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

            last = now;

            if (DateTime.UtcNow - used >= Idle)
            {
                flyout.Hide();
            }
        };

        Running.Add(timer);
        timer.Start();
    }
}
