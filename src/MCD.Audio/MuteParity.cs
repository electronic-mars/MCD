using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Mcd.Audio;

/// <summary>
/// Silences every device of one kind that is plugged in.
/// </summary>
internal static unsafe class MuteParity
{
    /// <summary>Silences each active device not already silent, and says how many it had to.</summary>
    internal static int Silence(EDataFlow flow)
    {
        int silenced = 0;

        Visit(flow, volume =>
        {
            BOOL muted;
            volume.GetMute(&muted);

            if (!muted)
            {
                volume.SetMute(true, null);
                silenced++;
            }
        });

        return silenced;
    }

    /// <summary>Lets every active device of that kind speak. Only the self-test undoes a silence this way.</summary>
    internal static void Unsilence(EDataFlow flow) => Visit(flow, volume => volume.SetMute(false, null));

    /// <summary>How many active devices of that kind there are, and how many of them are silent.</summary>
    internal static (int Total, int Silent) Count(EDataFlow flow)
    {
        int total = 0;
        int silent = 0;

        Visit(flow, volume =>
        {
            BOOL muted;
            volume.GetMute(&muted);

            total++;

            if (muted)
            {
                silent++;
            }
        });

        return (total, silent);
    }

    /// <summary>
    /// Opens each active device of that kind in turn, does one thing with it,
    /// and lets it go. One that will not open does not stop the others.
    /// </summary>
    private static void Visit(EDataFlow flow, Action<IAudioEndpointVolume> work)
    {
        object? enumerator = null;
        object? collection = null;

        try
        {
            var mm = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator = mm;

            mm.EnumAudioEndpoints(flow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices);
            collection = devices;

            devices.GetCount(out uint count);

            for (uint i = 0; i < count; i++)
            {
                Visit(devices, i, work);
            }
        }
        catch (Exception)
        {
            // No audio service: nothing to match.
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }

    private static void Visit(IMMDeviceCollection devices, uint index, Action<IAudioEndpointVolume> work)
    {
        object? device = null;
        object? activated = null;

        try
        {
            devices.Item(index, out IMMDevice found);
            device = found;

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            found.Activate(&iid, CLSCTX.CLSCTX_INPROC_SERVER, null, out activated);

            work((IAudioEndpointVolume)activated);
        }
        catch (Exception)
        {
            // A device that went away between being listed and being opened,
            // or one that refuses: the others still count.
        }
        finally
        {
            Release(activated);
            Release(device);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.FinalReleaseComObject(com);
        }
    }
}
