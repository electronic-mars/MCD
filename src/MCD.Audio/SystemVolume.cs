using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Mcd.Audio;

/// <summary>
/// The machine's own volume, on the device sound is coming out of.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is asked for afresh every time rather than kept. A default
/// playback device is not a fixed thing: headphones are plugged in, a monitor
/// with speakers wakes up, somebody switches device in the tray - and a held
/// interface then quietly controls a device nobody is listening to. Asking
/// again costs one COM call, and nothing here runs more than once a second.
/// </para>
/// <para>
/// Every call answers with what it managed rather than throwing. A machine
/// in a rack has no sound device at all, and a bar that shows a speaker
/// should hide it, not fall over.
/// </para>
/// </remarks>
public static unsafe class SystemVolume
{
    /// <summary>Whether sound is silenced, or null when nothing can say.</summary>
    public static bool? Muted() => With(volume =>
    {
        Windows.Win32.Foundation.BOOL muted;
        volume.GetMute(&muted);

        return (bool)muted;
    });

    /// <summary>How loud it is, nought to one, or null when nothing can say.</summary>
    public static float? Level() => With(volume =>
    {
        volume.GetMasterVolumeLevelScalar(out float level);

        return level;
    });

    /// <summary>
    /// Both readings at once, from an answer at most a second old.
    /// </summary>
    /// <remarks>
    /// Opening the endpoint means creating the enumerator, asking it for the
    /// default device and activating an interface on it - three calls across
    /// COM. A bar asked for the mute and the level separately, and there is a
    /// bar on every screen: six openings a second on a three-screen desk, for
    /// two numbers that nothing can change faster than a hand can move.
    /// </remarks>
    public static (bool? Muted, float Level) State()
    {
        long now = Environment.TickCount64;

        lock (Gate)
        {
            if (now - _asked < Fresh)
            {
                return _last;
            }

            _asked = now;
            _last = (Muted(), Level() ?? 0f);

            return _last;
        }
    }

    private const long Fresh = 900;

    private static readonly Lock Gate = new();
    private static (bool? Muted, float Level) _last;
    private static long _asked = -Fresh;

    /// <summary>Forgets the last answer, because this call just changed it.</summary>
    private static void Moved()
    {
        lock (Gate)
        {
            _asked = -Fresh;
        }
    }

    /// <summary>Silences the machine, or lets it speak again.</summary>
    public static bool Mute(bool on)
    {
        Moved();

        return With<bool>(volume =>
        {
            volume.SetMute(on, null);
            return true;
        }) ?? false;
    }

    /// <summary>Sets how loud it is, nought to one.</summary>
    public static bool Set(float level)
    {
        Moved();

        return With<bool>(volume =>
        {
            volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), null);
            return true;
        }) ?? false;
    }

    /// <summary>
    /// Opens the endpoint sound is playing through, does one thing with it,
    /// and lets it go.
    /// </summary>
    internal static T? With<T>(Func<IAudioEndpointVolume, T> work, EDataFlow flow = EDataFlow.eRender)
        where T : struct
    {
        // Every interface is let go the moment it has been used. Left to the
        // finalizer they hold registry keys of the endpoint's property store
        // open until the next collection: about three handles a second.
        object? enumerator = null;
        object? device = null;
        object? activated = null;

        try
        {
            var mm = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator = mm;

            mm.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out IMMDevice found);
            device = found;

            Guid iid = typeof(IAudioEndpointVolume).GUID;

            found.Activate(&iid, CLSCTX.CLSCTX_INPROC_SERVER, null, out activated);

            return work((IAudioEndpointVolume)activated);
        }
        catch (Exception)
        {
            // No playback device, or one that went away between being named
            // and being opened. Neither is this program's business.
            return null;
        }
        finally
        {
            foreach (object? com in new[] { activated, device, enumerator })
            {
                if (com is not null && System.Runtime.InteropServices.Marshal.IsComObject(com))
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com);
                }
            }
        }
    }
}
