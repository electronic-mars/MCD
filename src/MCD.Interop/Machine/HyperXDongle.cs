using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mcd.Interop.Machine;

/// <summary>
/// The USB dongle of a HyperX Cloud Flight S, asked for the headset's charge.
/// </summary>
/// <remarks>
/// <para>
/// A dongle headset tells Windows nothing about its battery. The charge
/// travels on the dongle's own HID collection (vendor page 0xFF13), and it is
/// only sent when asked: a 62-byte output report with a fixed header and a
/// command number, answered by input report 11. The layout is the one the
/// open HyperHeadset project worked out (MIT, see THIRD-PARTY.md).
/// </para>
/// <para>
/// A vendor collection is not one Windows keeps to itself, the way it keeps
/// a mouse's or a keyboard's, so an ordinary program may open it - no driver,
/// no rights. Every open handle gets every input report, so the headset's
/// own software and this one can both listen.
/// </para>
/// <para>
/// The answer comes from the headset, not from the dongle: with the headset
/// switched off the question goes unanswered, which is how "off" is told.
/// </para>
/// </remarks>
public sealed class HyperXDongle : IDisposable
{
    private const string Match = "vid_0951&pid_16ea";
    private const ushort VendorPage = 0xFF13;
    private const byte BatteryCommand = 2;

    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private static readonly byte[] Header =
        [0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB];

    private readonly FileStream _stream;
    private readonly int _reportLength;
    private readonly CancellationTokenSource _gone = new();

    private int _percent = -1;
    private long _heard = long.MinValue;

    private HyperXDongle(SafeFileHandle handle, int reportLength, string name)
    {
        _stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
        _reportLength = reportLength;
        Name = name;
        _ = Task.Run(Listen);
    }

    /// <summary>What the dongle calls the headset: "HyperX Cloud Flight S".</summary>
    public string Name { get; }

    /// <summary>Whether the dongle has gone - unplugged, or its handle lost.</summary>
    public bool Gone => _gone.IsCancellationRequested;

    /// <summary>The last charge the headset gave, or null when it has not answered lately.</summary>
    public int? Percent(TimeSpan within) =>
        _percent >= 0 && Environment.TickCount64 - Interlocked.Read(ref _heard) <= within.TotalMilliseconds
            ? _percent
            : null;

    /// <summary>Opens the dongle, or null when none is plugged in.</summary>
    public static HyperXDongle? Open()
    {
        foreach (string path in Interfaces())
        {
            if (!path.Contains(Match, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SafeFileHandle handle = CreateFileW(
                path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                continue;
            }

            if (Caps(handle) is { UsagePage: VendorPage, OutputReportByteLength: >= 16 } caps)
            {
                return new HyperXDongle(handle, caps.OutputReportByteLength, Product(handle));
            }

            handle.Dispose();
        }

        return null;
    }

    /// <summary>Asks for the charge. The answer arrives on its own, a moment later.</summary>
    public void Ask()
    {
        if (Gone)
        {
            return;
        }

        byte[] packet = new byte[_reportLength];
        Header.CopyTo(packet, 0);
        packet[15] = BatteryCommand;

        try
        {
            _stream.Write(packet);
        }
        catch (IOException)
        {
            _gone.Cancel();
        }
    }

    private async Task Listen()
    {
        byte[] report = new byte[_reportLength];

        try
        {
            while (!_gone.IsCancellationRequested)
            {
                int got = await _stream.ReadAsync(report, _gone.Token);

                // Report 11, the reply marker, and the command it answers.
                if (got >= 8 && report[0] == 11 && report[2] == 0xBB && report[3] == BatteryCommand
                    && report[7] <= 100)
                {
                    _percent = report[7];
                    Interlocked.Exchange(ref _heard, Environment.TickCount64);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _gone.Cancel();
        }
    }

    /// <remarks>
    /// Safe to call twice - the sensor hub closes its providers and the
    /// service container closes them again, and a second close that threw
    /// stopped the program's exit halfway. The token source is left for the
    /// collector: the listener may still cancel it on its way out.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _gone.Cancel();
        _stream.Dispose();
    }

    private int _disposed;

    private static List<string> Interfaces()
    {
        Guid hid = HidInterface;

        if (CM_Get_Device_Interface_List_SizeW(out uint length, hid, null, 0) != 0 || length == 0)
        {
            return [];
        }

        var buffer = new char[length];

        return CM_Get_Device_Interface_ListW(hid, null, buffer, length, 0) == 0
            ? [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)]
            : [];
    }

    private static HidCaps? Caps(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr data))
        {
            return null;
        }

        try
        {
            return HidP_GetCaps(data, out HidCaps caps) == HidpStatusSuccess ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    private static string Product(SafeFileHandle handle)
    {
        var buffer = new char[128];

        return HidD_GetProductString(handle, buffer, buffer.Length * 2)
            && new string(buffer).TrimEnd('\0') is { Length: > 0 } name
                ? name
                : "HyperX Cloud Flight S";
    }

    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public fixed ushort Counts[10];
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, in Guid cls, string? id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(in Guid cls, string? id, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, char[] buffer, int length);
}
