using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Mcd.Audio;

/// <summary>
/// Puts every device of one kind that is plugged in in the same state.
/// </summary>
internal static unsafe class MuteParity
{
    /// <summary>Puts each active device of that kind in a state, and says how many were not in it already.</summary>
    internal static int Set(EDataFlow flow, bool muted)
    {
        int changed = 0;

        Visit(flow, volume =>
        {
            BOOL now;
            volume.GetMute(&now);

            if ((bool)now != muted)
            {
                volume.SetMute(muted, null);
                changed++;
            }
        });

        return changed;
    }

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
            Marshal.ReleaseComObject(com);
        }
    }
}
