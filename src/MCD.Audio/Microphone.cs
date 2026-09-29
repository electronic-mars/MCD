using Windows.Win32.Media.Audio;

namespace Mcd.Audio;

/// <summary>
/// The microphone the machine listens through, and whether it is switched off.
/// </summary>
/// <remarks>
/// The same asking-afresh rule as <see cref="SystemVolume"/>, on the capture
/// side: the default recording device changes when a headset is plugged in,
/// and a held interface would go on muting the one that was unplugged. This
/// is the switch every conferencing program has, but for the whole machine -
/// the one that is off however the program in front is set.
/// </remarks>
public static unsafe class Microphone
{
    /// <summary>Whether recording is switched off, or null when there is nothing to record with.</summary>
    public static bool? Muted()
    {
        long now = Environment.TickCount64;

        lock (Gate)
        {
            if (now - _asked < Fresh)
            {
                return _last;
            }

            _asked = now;

            _last = SystemVolume.With(
                volume =>
                {
                    Windows.Win32.Foundation.BOOL muted;
                    volume.GetMute(&muted);

                    return (bool)muted;
                },
                EDataFlow.eCapture);

            return _last;
        }
    }

    /// <summary>Switches recording off, or on again.</summary>
    public static bool Mute(bool on)
    {
        lock (Gate)
        {
            _asked = -Fresh;
        }

        return SystemVolume.With<bool>(
            volume =>
            {
                volume.SetMute(on, null);
                return true;
            },
            EDataFlow.eCapture) ?? false;
    }

    private const long Fresh = 900;

    private static readonly Lock Gate = new();
    private static bool? _last;
    private static long _asked = -Fresh;
}
