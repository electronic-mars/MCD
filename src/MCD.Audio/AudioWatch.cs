using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Mcd.Audio;

/// <summary>
/// Says the moment the sound or the microphone is switched off or on, turned,
/// or moved to another device - by anything: the keyboard, the tray, another
/// program.
/// </summary>
/// <remarks>
/// <para>
/// The readings themselves are still asked for afresh (see
/// <see cref="SystemVolume"/>); this only says when to ask. Without it the bar
/// found out on its next second, and a speaker that goes quiet a second after
/// the key is pressed looks like a speaker that did not hear the key.
/// </para>
/// <para>
/// Windows calls back on threads of its own, and a callback must not wait on
/// anything; following a new default device is therefore done on the thread
/// pool, never inside the callback that announced it.
/// </para>
/// <para>
/// It also keeps the switch honest. Muting is a statement about the machine,
/// not about one device: headphones muted and then dropped must not hand the
/// sound to speakers that were never muted, at the volume they were last left
/// at, and a muted headset that drops off must not leave the laptop's own
/// microphone open on a call. So when the device in use is switched off or on,
/// every other plugged-in device of its kind follows, and one that was not
/// plugged in at the time is brought into line when it comes back (see
/// <see cref="MuteMemory"/>).
/// </para>
/// </remarks>
public static unsafe class AudioWatch
{
    /// <summary>Raised on a thread of Windows' own: something about the sound or the microphone changed.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Raised on a thread of its own: this many other devices were switched to
    /// match the one in use. The first flag is true for microphones, false for
    /// speakers; the second says whether they were muted or unmuted.
    /// </summary>
    public static event Action<bool, bool, int>? Matched;

    /// <summary>One at a time: two copies at once would undo each other.</summary>
    private static readonly Lock MatchGate = new();

    private static readonly MuteMemory Speakers = new();
    private static readonly MuteMemory Microphones = new();

    private static readonly Lock Gate = new();
    private static readonly DeviceEar Devices = new();
    private static readonly VolumeEar SpeakerEar = new(microphone: false);
    private static readonly VolumeEar MicrophoneEar = new(microphone: true);
    private static IMMDeviceEnumerator? _enumerator;
    private static IAudioEndpointVolume? _speaker;
    private static IAudioEndpointVolume? _microphone;

    /// <summary>Starts listening. Once is enough; a second call does nothing.</summary>
    public static void Start() => Task.Run(() =>
    {
        lock (Gate)
        {
            if (_enumerator is not null)
            {
                return;
            }

            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                _enumerator.RegisterEndpointNotificationCallback(Devices);
            }
            catch (Exception)
            {
                // No audio service at all: the widgets hide, and there is
                // nothing to hear about.
                _enumerator = null;
                return;
            }

            Follow();
        }

        Arrive();
    });

    /// <summary>Listens to the default devices as they are now, letting go of the ones before.</summary>
    private static void Follow()
    {
        _speaker = Listen(_speaker, EDataFlow.eRender, SpeakerEar);
        _microphone = Listen(_microphone, EDataFlow.eCapture, MicrophoneEar);
    }

    private static IAudioEndpointVolume? Listen(IAudioEndpointVolume? before, EDataFlow flow, VolumeEar ear)
    {
        if (before is not null)
        {
            try
            {
                before.UnregisterControlChangeNotify(ear);
            }
            catch (Exception)
            {
                // The device is already gone; so is its list of listeners.
            }

            // Not a final release: the wrapper is shared (see SystemVolume.With).
            Marshal.ReleaseComObject(before);
        }

        object? device = null;
        IAudioEndpointVolume? listening = null;

        try
        {
            _enumerator!.GetDefaultAudioEndpoint(flow, SystemVolume.Role, out IMMDevice found);
            device = found;

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            found.Activate(&iid, CLSCTX.CLSCTX_INPROC_SERVER, null, out object activated);

            var volume = (IAudioEndpointVolume)activated;
            listening = volume;
            volume.RegisterControlChangeNotify(ear);

            // What the new default is now is the state to compare its next
            // change with. Moving to a device is not switching it.
            BOOL muted;
            volume.GetMute(&muted);
            Memory(flow == EDataFlow.eCapture).Followed((bool)muted);

            return volume;
        }
        catch (Exception)
        {
            // Not left listening to a device this is not following: its
            // switch would be taken for the one in use.
            if (listening is not null)
            {
                try
                {
                    listening.UnregisterControlChangeNotify(ear);
                }
                catch (Exception)
                {
                    // Gone already, and its listeners with it.
                }

                Marshal.ReleaseComObject(listening);
            }

            // No device of that kind just now; one being plugged in is
            // announced, and is listened to then.
            Memory(flow == EDataFlow.eCapture).Followed(null);
            return null;
        }
        finally
        {
            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }
        }
    }

    private static MuteMemory Memory(bool microphone) => microphone ? Microphones : Speakers;

    private static void Tell()
    {
        SystemVolume.Forget();
        Microphone.Forget();
        Changed?.Invoke();
    }

    /// <summary>The device in use was switched off or on: so is every other.</summary>
    /// <remarks>
    /// To what the switch is now, read inside the lock - not to what it was
    /// when this was asked for. Two presses close together queue two of these,
    /// and they need not run in the order they were asked: the older one
    /// running last put every device, the one in use included, back to the
    /// state before the second press - a microphone left open that its owner
    /// had just closed.
    /// </remarks>
    private static void Mirror(bool microphone) => Task.Run(() =>
    {
        lock (MatchGate)
        {
            if (Memory(microphone).Wanted is { } muted)
            {
                Copy(microphone, muted);
            }
        }
    });

    /// <summary>
    /// A device came, went or became the default: every plugged-in device is
    /// brought to the state the machine was last switched to.
    /// </summary>
    /// <remarks>
    /// Not to the state of the device now in use, which may be one that was
    /// unplugged when the switch was last thrown and has been waiting, stale,
    /// ever since. Read inside the lock, so that a switch thrown while this
    /// waited for its turn is not undone by the answer it fetched before.
    /// </remarks>
    private static void Arrive() => Task.Run(() =>
    {
        lock (MatchGate)
        {
            if (Speakers.Wanted is { } speakers)
            {
                Copy(microphone: false, muted: speakers);
            }

            if (Microphones.Wanted is { } microphones)
            {
                Copy(microphone: true, muted: microphones);
            }
        }
    });

    private static void Copy(bool microphone, bool muted)
    {
        int changed = MuteParity.Set(microphone ? EDataFlow.eCapture : EDataFlow.eRender, muted);

        if (changed > 0)
        {
            Matched?.Invoke(microphone, muted, changed);
        }
    }

    /// <summary>How many devices of that kind are plugged in, and how many are silent. For the self-test.</summary>
    public static (int Total, int Silent) Census(bool microphone) =>
        MuteParity.Count(microphone ? EDataFlow.eCapture : EDataFlow.eRender);

    /// <summary>A device came, went or became the default: listen to whatever is default now. Public for the self-test.</summary>
    public static void Refollow() => Task.Run(() =>
    {
        lock (Gate)
        {
            if (_enumerator is not null)
            {
                Follow();
            }
        }

        Tell();
        Arrive();
    });

    /// <summary>Hears the mute and the volume of one default device.</summary>
    [ComVisible(true)]
    private sealed class VolumeEar(bool microphone) : IAudioEndpointVolumeCallback
    {
        public void OnNotify(AUDIO_VOLUME_NOTIFICATION_DATA* pNotify)
        {
            bool muted = (bool)pNotify->bMuted;

            Tell();

            if (Memory(microphone).Thrown(muted))
            {
                Mirror(microphone);
            }
        }
    }

    /// <summary>Hears of devices coming, going and becoming the default.</summary>
    [ComVisible(true)]
    private sealed class DeviceEar : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR pwstrDefaultDeviceId)
        {
            if (role == SystemVolume.Role)
            {
                Refollow();
            }
        }

        public void OnDeviceStateChanged(PCWSTR pwstrDeviceId, DEVICE_STATE dwNewState) => Refollow();

        public void OnDeviceAdded(PCWSTR pwstrDeviceId) => Refollow();

        public void OnDeviceRemoved(PCWSTR pwstrDeviceId) => Refollow();

        public void OnPropertyValueChanged(PCWSTR pwstrDeviceId, PROPERTYKEY key)
        {
        }
    }
}
