using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcd.Interop.Storage;

/// <summary>What a drive says about itself.</summary>
/// <param name="Model">Product id from the device descriptor, trimmed.</param>
/// <param name="Serial">Serial number; the stable part of a drive's identity.</param>
public readonly record struct DriveIdentity(string Model, string Serial);

/// <summary>A drive's temperature, with the limits the drive itself declares.</summary>
/// <param name="Celsius">The composite reading - sensor zero, the one the firmware nominates.</param>
/// <param name="Warning">Where this drive says it starts to be too warm, if it says.</param>
/// <param name="Critical">Where this drive says it is too hot, if it says.</param>
public readonly record struct DriveTemperature(short Celsius, short? Warning, short? Critical);

/// <summary>
/// Drive temperatures, without administrator rights.
/// </summary>
/// <remarks>
/// <para>
/// The trick is the access mask. Opening <c>\\.\PhysicalDriveN</c> for
/// <c>GENERIC_READ</c> needs elevation and is what SMART tools do; opening it
/// for <b>zero</b> access needs nothing, and
/// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> still answers. That one difference is
/// why this program can show NVMe and SATA temperatures out of the box while
/// the CPU's own temperature stays out of reach.
/// </para>
/// <para>
/// Not every drive answers. USB enclosures often do not pass the command
/// through, and a RAID volume hides the members behind the controller. A drive
/// that says nothing is left out rather than shown as zero.
/// </para>
/// </remarks>
public static class StorageTemperature
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;

    /// <summary>
    /// STORAGE_PROPERTY_ID values. The temperature ones are 51 and 52; the
    /// small numbers near the start of that enum are other things entirely -
    /// asking for 8 gets StorageDeviceTrimProperty, which answers happily with
    /// twelve bytes saying TRIM is on and looks for all the world like a drive
    /// that has no thermometer.
    /// </summary>
    private const int StorageAdapterTemperatureProperty = 51;

    private const int StorageDeviceTemperatureProperty = 52;

    private const int PropertyStandardQuery = 0;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>Physical drive numbers that answered when asked to identify themselves.</summary>
    public static IEnumerable<int> Enumerate()
    {
        // Sixteen is well past any ordinary machine and costs nothing: opening a
        // drive that is not there fails immediately.
        for (int index = 0; index < 16; index++)
        {
            if (Identify(index) is not null)
            {
                yield return index;
            }
        }
    }

    /// <summary>Model and serial, or null when the drive does not exist or will not say.</summary>
    public static DriveIdentity? Identify(int driveIndex)
    {
        using SafeFileHandle handle = Open(driveIndex);

        if (handle.IsInvalid)
        {
            return null;
        }

        byte[]? buffer = Query(handle, StorageDeviceProperty, 1024);

        if (buffer is null || buffer.Length < 40)
        {
            return null;
        }

        // STORAGE_DEVICE_DESCRIPTOR holds byte offsets into itself, or zero for
        // fields the device did not supply.
        string model = AtOffset(buffer, BitConverter.ToInt32(buffer, 16));
        string serial = AtOffset(buffer, BitConverter.ToInt32(buffer, 24));

        return new DriveIdentity(
            model.Length > 0 ? model : $"Drive {driveIndex}",
            serial.Length > 0 ? serial : $"drive{driveIndex}");
    }

    /// <summary>Null when the drive does not exist or reports no temperature.</summary>
    public static DriveTemperature? Read(int driveIndex)
    {
        using SafeFileHandle handle = Open(driveIndex);

        if (handle.IsInvalid)
        {
            return null;
        }

        // Most drives answer the device question. A few only answer the adapter
        // one, and on the drive this was written against both give the same
        // bytes, so asking the second costs nothing when the first says nothing.
        return Parse(Query(handle, StorageDeviceTemperatureProperty, 512))
            ?? Parse(Query(handle, StorageAdapterTemperatureProperty, 512));
    }

    /// <summary>
    /// STORAGE_TEMPERATURE_DATA_DESCRIPTOR, then an array of
    /// STORAGE_TEMPERATURE_INFO.
    /// </summary>
    /// <remarks>
    /// <code>
    ///  0  ULONG  Version
    ///  4  ULONG  Size
    ///  8  SHORT  CriticalTemperature
    /// 10  SHORT  WarningTemperature
    /// 12  USHORT InfoCount
    /// 14  UCHAR  Reserved0[2]
    /// 16  ULONG  Reserved1[2]
    /// 24  STORAGE_TEMPERATURE_INFO[InfoCount]   16 bytes each:
    ///       0 USHORT Index      2 SHORT Temperature
    ///       4 SHORT  Over       6 SHORT UnderThreshold ...
    /// </code>
    /// Entry zero is the composite reading. On the NVMe drive this was written
    /// against, the three entries read 45, 46 and 51 degrees - a single figure
    /// has to be the one the firmware nominates, not the hottest sensor in the
    /// package.
    /// </remarks>
    public static DriveTemperature? Parse(byte[]? buffer)
    {
        const int firstEntry = 24;
        const int entrySize = 16;

        if (buffer is null || buffer.Length < firstEntry + entrySize)
        {
            return null;
        }

        ushort count = BitConverter.ToUInt16(buffer, 12);

        if (count == 0)
        {
            return null;
        }

        short celsius = BitConverter.ToInt16(buffer, firstEntry + 2);

        // Drives that mean "no reading" answer with zero or something absurd.
        if (celsius is <= 0 or >= 120)
        {
            return null;
        }

        return new DriveTemperature(celsius, Sane(BitConverter.ToInt16(buffer, 10)), Sane(BitConverter.ToInt16(buffer, 8)));
    }

    /// <summary>A threshold the drive did not set comes back as a sentinel such as -274.</summary>
    private static short? Sane(short value) => value is > 0 and < 150 ? value : null;

    private static SafeFileHandle Open(int driveIndex) =>
        CreateFileW(
            $@"\\.\PhysicalDrive{driveIndex}",

            // Zero. Asking for GENERIC_READ here is what makes this need
            // administrator rights; asking for nothing still allows the query.
            dwDesiredAccess: 0,
            FileShareRead | FileShareWrite,
            nint.Zero,
            OpenExisting,
            dwFlagsAndAttributes: 0,
            nint.Zero);

    private static byte[]? Query(SafeFileHandle handle, int propertyId, int size)
    {
        // STORAGE_PROPERTY_QUERY: PropertyId, QueryType, AdditionalParameters[1].
        Span<byte> request = stackalloc byte[12];
        BitConverter.TryWriteBytes(request, propertyId);
        BitConverter.TryWriteBytes(request[4..], PropertyStandardQuery);

        byte[] response = new byte[size];

        unsafe
        {
            fixed (byte* input = request)
            fixed (byte* output = response)
            {
                if (!DeviceIoControl(
                        handle, IoctlStorageQueryProperty,
                        (nint)input, (uint)request.Length,
                        (nint)output, (uint)response.Length,
                        out uint returned, nint.Zero)
                    || returned == 0)
                {
                    return null;
                }

                return response;
            }
        }
    }

    private static string AtOffset(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        int end = Array.IndexOf(buffer, (byte)0, offset);
        end = end < 0 ? buffer.Length : end;

        return System.Text.Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);
}
