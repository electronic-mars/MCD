using System.Net.NetworkInformation;

namespace Mcd.Interop.Machine;

/// <summary>How the machine is on the network, when it is on it wirelessly.</summary>
/// <param name="Wireless">True while the way out to the internet is a wireless one.</param>
/// <param name="Name">The network's name, or empty when there is none to give.</param>
/// <param name="Bars">Signal strength, 0 to 5, or -1 when it is not reported.</param>
public readonly record struct WirelessState(bool Wireless, string Name, int Bars)
{
    public static WirelessState None => new(false, string.Empty, -1);
}

/// <summary>
/// The wireless connection, when there is one.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>Windows.Networking.Connectivity</c> rather than from the WLAN
/// API: that one wants a handle, a thread to hold it and a callback, all to
/// answer a question the connection profile already answers in one call. What
/// is wanted here is not the adapter's inventory - it is which way this machine
/// is actually reaching the internet right now.
/// </para>
/// <para>
/// <see cref="Fitted"/> is a different question from <see cref="Read"/>: a
/// laptop plugged into a cable still has a wireless card, so it is still a
/// machine where a Wi-Fi widget makes sense to offer. A machine with no card
/// at all should never be offered one.
/// </para>
/// </remarks>
public static class Wireless
{
    /// <summary>Whether this machine has a wireless card at all.</summary>
    public static bool Fitted()
    {
        try
        {
            foreach (NetworkInterface card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The adapter list is a system boundary and it does fail - during
            // a driver reload, most often. Not having an answer is not the
            // same as the answer being no, but for offering a widget it may
            // as well be: the next call gets it right.
        }

        return false;
    }

    /// <summary>
    /// The connection as it was at most a second ago.
    /// </summary>
    /// <remarks>
    /// Asking the network stack is not free, and every bar on every screen
    /// asks once a second. One answer serves them all: nothing here changes
    /// faster than a second, and a bar is glanced at rather than watched.
    /// </remarks>
    public static WirelessState Read()
    {
        long now = Environment.TickCount64;

        lock (Gate)
        {
            if (now - _asked < Fresh)
            {
                return _last;
            }

            _asked = now;
            _last = Ask();
            return _last;
        }
    }

    private const long Fresh = 900;

    private static readonly Lock Gate = new();
    private static WirelessState _last = WirelessState.None;
    private static long _asked = -Fresh;

    private static WirelessState Ask()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation
                .GetInternetConnectionProfile();

            if (profile is null || !profile.IsWlanConnectionProfile)
            {
                return WirelessState.None;
            }

            return new WirelessState(
                Wireless: true,
                Name: profile.ProfileName ?? string.Empty,
                Bars: profile.GetSignalBars() is { } bars ? bars : -1);
        }
        catch (Exception)
        {
            // Every one of these is a call into the network stack, and a
            // machine changing networks under us throws rather than returning
            // nothing. A widget that vanishes for a second beats one that
            // takes the bar down with it.
            return WirelessState.None;
        }
    }
}
