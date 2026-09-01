using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WiFi;

namespace Mcd.Interop.Machine;

/// <summary>The physical side of the wireless link.</summary>
/// <param name="Ghz">The band, 2.4 or 5, or 0 when it is not known.</param>
/// <param name="Mbit">The negotiated receive rate in Mbit/s, or 0 when it is not known.</param>
/// <param name="Standard">"Wi-Fi 6" and its siblings, or empty for anything older or unknown.</param>
public readonly record struct WirelessLink(double Ghz, int Mbit, string Standard)
{
    public static WirelessLink None => new(0, 0, string.Empty);
}

/// <summary>
/// What the WLAN API knows about the current connection that the connection
/// profile does not: the band, the link speed and the standard.
/// </summary>
/// <remarks>
/// <para>
/// This is the one question the profile cannot answer, which is why the handle
/// is worth having after all. It is opened once and kept; no callbacks are
/// registered, and everything here is a synchronous query against it.
/// </para>
/// <para>
/// The band is told from the channel number, which cannot distinguish 6 GHz
/// channels from the 2.4 GHz ones that share their numbers. Machines on 6 GHz
/// are rare enough that the simple answer is the better trade.
/// </para>
/// </remarks>
public static class WlanLink
{
    private static HANDLE _wlan;
    private static bool _refused;

    /// <summary>The link as it is right now, or <see cref="WirelessLink.None"/>.</summary>
    public static unsafe WirelessLink Read()
    {
        if (_refused)
        {
            return WirelessLink.None;
        }

        try
        {
            if (_wlan.IsNull)
            {
                uint version;
                HANDLE opened;

                if (PInvoke.WlanOpenHandle(2, null, &version, &opened) != 0)
                {
                    // The service is not running, or the call is not allowed.
                    // Neither changes while the program runs; stop asking.
                    _refused = true;
                    return WirelessLink.None;
                }

                _wlan = opened;
            }

            return Ask();
        }
        catch (Exception)
        {
            // The WLAN service can go away mid-call. The tooltip just says
            // less until it is back.
            return WirelessLink.None;
        }
    }

    private static unsafe WirelessLink Ask()
    {
        WLAN_INTERFACE_INFO_LIST* list;

        if (PInvoke.WlanEnumInterfaces(_wlan, null, &list) != 0)
        {
            return WirelessLink.None;
        }

        try
        {
            for (int i = 0; i < list->dwNumberOfItems; i++)
            {
                WLAN_INTERFACE_INFO card = list->InterfaceInfo[i];

                if (card.isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                {
                    continue;
                }

                return Connection(card.InterfaceGuid);
            }
        }
        finally
        {
            PInvoke.WlanFreeMemory(list);
        }

        return WirelessLink.None;
    }

    private static unsafe WirelessLink Connection(Guid card)
    {
        int mbit = 0;
        string standard = string.Empty;
        double ghz = 0;

        uint size;
        void* data;

        if (PInvoke.WlanQueryInterface(
                _wlan, &card, WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                null, &size, &data, null) == 0)
        {
            var attributes = (WLAN_CONNECTION_ATTRIBUTES*)data;

            mbit = (int)(attributes->wlanAssociationAttributes.ulRxRate / 1000);

            standard = attributes->wlanAssociationAttributes.dot11PhyType switch
            {
                DOT11_PHY_TYPE.dot11_phy_type_ht => "Wi-Fi 4",
                DOT11_PHY_TYPE.dot11_phy_type_vht => "Wi-Fi 5",
                DOT11_PHY_TYPE.dot11_phy_type_he => "Wi-Fi 6",
                DOT11_PHY_TYPE.dot11_phy_type_eht => "Wi-Fi 7",
                _ => string.Empty,
            };

            PInvoke.WlanFreeMemory(data);
        }

        if (PInvoke.WlanQueryInterface(
                _wlan, &card, WLAN_INTF_OPCODE.wlan_intf_opcode_channel_number,
                null, &size, &data, null) == 0)
        {
            uint channel = *(uint*)data;
            ghz = channel is >= 1 and <= 14 ? 2.4 : 5;
            PInvoke.WlanFreeMemory(data);
        }

        return new WirelessLink(ghz, mbit, standard);
    }
}
