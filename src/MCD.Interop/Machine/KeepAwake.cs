using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Mcd.Interop.Machine;

/// <summary>
/// Stops the machine from going to sleep, and from looking as if nobody is there.
/// </summary>
/// <remarks>
/// <para>
/// Two things keep a chat program showing green, and the first is not enough.
/// Asking Windows not to sleep stops the screen and the machine going dark,
/// but a chat program decides somebody has walked away by the time since the
/// last key or mouse movement, which the power request does not touch. So
/// every so often a mouse move of no distance goes in as well: Windows
/// counts it as input and resets that clock, and nothing moves, nothing is
/// pressed and nothing is typed anywhere. A key was the first idea - F15,
/// which no keyboard has - and was dropped because the people who have a
/// use for the far function keys are exactly the ones who bind them, to a
/// push-to-talk or a macro.
/// </para>
/// <para>
/// Everything happens on a thread of its own. The power request is tied to
/// the thread that made it, and the interface thread is busy and, with every
/// bar hidden, not necessarily ticking. One answer serves every bar: the
/// widget on each screen reads the same state.
/// </para>
/// </remarks>
public static class KeepAwake
{
    /// <summary>How often the move goes in. Chat programs wait five minutes or more.</summary>
    private static readonly TimeSpan Pulse = TimeSpan.FromSeconds(50);

    private static readonly Lock Gate = new();
    private static Run? _run;

    /// <summary>Whether the machine is being kept awake right now.</summary>
    public static bool On
    {
        get
        {
            lock (Gate)
            {
                return _run is not null;
            }
        }
    }

    /// <summary>When it stops by itself, or null for "until somebody says so".</summary>
    public static DateTimeOffset? Until
    {
        get
        {
            lock (Gate)
            {
                return _run?.Until;
            }
        }
    }

    /// <summary>Keeps the machine awake, for a while or until turned off.</summary>
    /// <param name="span">How long, or null for no end.</param>
    public static void Start(TimeSpan? span)
    {
        var run = new Run(span is { } s ? DateTimeOffset.Now + s : null);

        lock (Gate)
        {
            _run?.Off.Set();
            _run = run;
        }

        new Thread(() => Keep(run)) { IsBackground = true, Name = "Keep awake" }.Start();
    }

    /// <summary>Lets the machine sleep again.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _run?.Off.Set();
            _run = null;
        }
    }

    private static void Keep(Run run)
    {
        PInvoke.SetThreadExecutionState(
            EXECUTION_STATE.ES_CONTINUOUS
            | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            | EXECUTION_STATE.ES_DISPLAY_REQUIRED);

        try
        {
            Nudge();

            while (true)
            {
                TimeSpan wait = run.Until is { } end
                    ? TimeSpan.FromTicks(Math.Min(Pulse.Ticks, (end - DateTimeOffset.Now).Ticks))
                    : Pulse;

                if (wait <= TimeSpan.Zero || run.Off.Wait(wait))
                {
                    break;
                }

                if (run.Until is { } stop && DateTimeOffset.Now >= stop)
                {
                    break;
                }

                Nudge();
            }
        }
        finally
        {
            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);

            lock (Gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                }
            }
        }
    }

    /// <summary>One mouse move, a distance of nothing.</summary>
    private static unsafe void Nudge()
    {
        INPUT move = new() { type = INPUT_TYPE.INPUT_MOUSE };
        move.Anonymous.mi.dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE;

        PInvoke.SendInput([move], sizeof(INPUT));
    }

    private sealed class Run(DateTimeOffset? until)
    {
        public DateTimeOffset? Until { get; } = until;

        public ManualResetEventSlim Off { get; } = new();
    }
}
