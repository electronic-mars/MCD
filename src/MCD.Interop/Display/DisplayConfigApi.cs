using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace Mcd.Interop.Display;

/// <summary>
/// Reads the active display paths and, for each, the stable device path of the
/// monitor behind it.
/// </summary>
/// <remarks>
/// Adapted in approach from microsoft/PowerToys,
/// src/modules/cmdpal/Microsoft.CmdPal.UI/Services/MonitorService.cs (MIT).
/// Copyright (c) Microsoft Corporation. See licenses/PowerToys-MIT.txt.
/// Changes: returns raw targets and never falls back to the GDI name, because
/// that fallback is what makes a live monitor look new during a topology change.
/// </remarks>
public static unsafe class DisplayConfigApi
{
    /// <summary>
    /// One entry per active output, keyed by GDI name. An empty result means the
    /// topology is mid-change and the caller must not treat it as authoritative.
    /// </summary>
    public static IReadOnlyDictionary<string, DisplayTarget> QueryActiveTargets()
    {
        var found = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);

        uint pathCount = 0;
        uint modeCount = 0;
        if (PInvoke.GetDisplayConfigBufferSizes(
                QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != 0)
        {
            return found;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        fixed (DISPLAYCONFIG_PATH_INFO* pathPtr = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* modePtr = modes)
        {
            if (PInvoke.QueryDisplayConfig(
                    QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    &pathCount, pathPtr,
                    &modeCount, modePtr,
                    null) != 0)
            {
                return found;
            }

            for (uint i = 0; i < pathCount; i++)
            {
                DISPLAYCONFIG_PATH_INFO path = pathPtr[i];

                string gdiName = SourceName(path);
                if (gdiName.Length == 0)
                {
                    continue;
                }

                if (!TryTargetName(path, out DISPLAYCONFIG_TARGET_DEVICE_NAME target))
                {
                    continue;
                }

                string devicePath = target.monitorDevicePath.ToString();
                if (devicePath.Length == 0)
                {
                    // No device path means the shell has not settled. Reporting
                    // the monitor without one would force a fallback key.
                    continue;
                }

                found[gdiName] = new DisplayTarget(
                    gdiName,
                    devicePath,
                    target.monitorFriendlyDeviceName.ToString(),
                    DecodeVendor(target.edidManufactureId),
                    target.edidProductCodeId.ToString("X4"));
            }
        }

        return found;
    }

    private static string SourceName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };

        return PInvoke.DisplayConfigGetDeviceInfo(&request.header) == 0
            ? request.viewGdiDeviceName.ToString()
            : string.Empty;
    }

    private static bool TryTargetName(DISPLAYCONFIG_PATH_INFO path, out DISPLAYCONFIG_TARGET_DEVICE_NAME target)
    {
        target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        fixed (DISPLAYCONFIG_TARGET_DEVICE_NAME* p = &target)
        {
            return PInvoke.DisplayConfigGetDeviceInfo(&p->header) == 0;
        }
    }

    /// <summary>
    /// EDID packs the three-letter PnP vendor code into one 16-bit word, five
    /// bits per letter, 'A' == 1, most significant letter first.
    /// </summary>
    private static string DecodeVendor(ushort edidManufactureId)
    {
        if (edidManufactureId == 0)
        {
            return string.Empty;
        }

        // The field is stored big-endian relative to how Windows hands it over.
        ushort id = (ushort)((edidManufactureId >> 8) | (edidManufactureId << 8));

        Span<char> letters =
        [
            (char)('A' + ((id >> 10) & 0x1F) - 1),
            (char)('A' + ((id >> 5) & 0x1F) - 1),
            (char)('A' + (id & 0x1F) - 1),
        ];

        foreach (char c in letters)
        {
            if (c is < 'A' or > 'Z')
            {
                return string.Empty;
            }
        }

        return new string(letters);
    }
}
