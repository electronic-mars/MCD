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
/// </remarks>
public static unsafe class AudioWatch
{
    /// <summary>Raised on a thread of Windows' own: something about the sound or the microphone changed.</summary>
    public static event Action? Changed;

    private static readonly Lock Gate = new();
    private static readonly Listener Ear = new();
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
                _enumerator.RegisterEndpointNotificationCallback(Ear);
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
    });

    /// <summary>Listens to the default devices as they are now, letting go of the ones before.</summary>
    private static void Follow()
    {
        _speaker = Listen(_speaker, EDataFlow.eRender);
        _microphone = Listen(_microphone, EDataFlow.eCapture);
    }

    private static IAudioEndpointVolume? Listen(IAudioEndpointVolume? before, EDataFlow flow)
    {
        if (before is not null)
        {
            try
            {
                before.UnregisterControlChangeNotify(Ear);
            }
            catch (Exception)
            {
                // The device is already gone; so is its list of listeners.
            }

            Marshal.FinalReleaseComObject(before);
        }

        object? device = null;

        try
        {
            _enumerator!.GetDefaultAudioEndpoint(flow, SystemVolume.Role, out IMMDevice found);
            device = found;

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            found.Activate(&iid, CLSCTX.CLSCTX_INPROC_SERVER, null, out object activated);

            var volume = (IAudioEndpointVolume)activated;
            volume.RegisterControlChangeNotify(Ear);

            return volume;
        }
        catch (Exception)
        {
            // No device of that kind just now; one being plugged in is
            // announced, and is listened to then.
            return null;
        }
        finally
        {
            if (device is not null)
            {
                Marshal.FinalReleaseComObject(device);
            }
        }
    }

    private static void Tell()
    {
        SystemVolume.Forget();
        Microphone.Forget();
        Changed?.Invoke();
    }

    /// <summary>A device came, went or became the default: listen to whatever is default now.</summary>
    private static void Refollow() => Task.Run(() =>
    {
        lock (Gate)
        {
            if (_enumerator is not null)
            {
                Follow();
            }
        }

        Tell();
    });

    [ComVisible(true)]
    private sealed class Listener : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public void OnNotify(AUDIO_VOLUME_NOTIFICATION_DATA* pNotify) => Tell();

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
