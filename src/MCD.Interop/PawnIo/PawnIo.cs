using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Mcd.Interop.PawnIo;

/// <summary>
/// The PawnIO driver, as far as this program needs it.
/// </summary>
/// <remarks>
/// <para>
/// A processor's own temperature lives in a machine-specific register that
/// only kernel code may read. This program carries no driver - it talks to
/// PawnIO, a signed, open driver the person installs once, over three device
/// controls: load a module, call a function in it, ask the version. The
/// modules are the driver author's own signed binaries; the driver refuses
/// any other.
/// </para>
/// <para>
/// Nothing here is a library binding. The device is opened like a file and
/// asked like a file, which is the whole protocol: a 32-byte function name,
/// then 64-bit cells in, 64-bit cells out.
/// </para>
/// </remarks>
public static class PawnIo
{
    private const string DevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    private const uint DeviceType = 41394u << 16;
    private const uint LoadBinary = DeviceType | (0x821u << 2);
    private const uint ExecuteFunction = DeviceType | (0x841u << 2);
    private const uint GetVersion = DeviceType | (0x861u << 2);

    private const int NameLength = 32;

    /// <summary>The installed driver's version as its installer wrote it, or null when it is not installed.</summary>
    public static string? InstalledVersion()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Default })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? found = root.OpenSubKey(key);

                if (found?.GetValue("DisplayVersion") is string version && version.Length > 0)
                {
                    return version;
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Then the device itself is the answer.
            }
        }

        return null;
    }

    /// <summary>Whether the driver's device can be opened right now.</summary>
    public static bool Present()
    {
        using SafeFileHandle handle = Open();
        return !handle.IsInvalid;
    }

    /// <summary>
    /// Opens the device and loads one module into it.
    /// </summary>
    /// <returns>The loaded module, or null when the driver is not there or would not take it.</returns>
    public static Module? Load(byte[] module)
    {
        SafeFileHandle handle = Open();

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        if (!DeviceIoControl(handle, LoadBinary, module, module.Length, null, 0, out _, IntPtr.Zero))
        {
            handle.Dispose();
            return null;
        }

        return new Module(handle);
    }

    /// <summary>The driver's version, packed major.minor.patch, or null.</summary>
    public static (int Major, int Minor, int Patch)? DriverVersion()
    {
        using SafeFileHandle handle = Open();

        if (handle.IsInvalid)
        {
            return null;
        }

        byte[] output = new byte[4];

        if (!DeviceIoControl(handle, GetVersion, null, 0, output, output.Length, out uint got, IntPtr.Zero)
            || got < 4)
        {
            return null;
        }

        uint packed = BitConverter.ToUInt32(output, 0);
        return ((int)(packed >> 16), (int)((packed >> 8) & 0xFF), (int)(packed & 0xFF));
    }

    private static SafeFileHandle Open() => CreateFileW(
        DevicePath,
        0x80000000u | 0x40000000u, // GENERIC_READ | GENERIC_WRITE
        0,
        IntPtr.Zero,
        3, // OPEN_EXISTING
        0,
        IntPtr.Zero);

    /// <summary>One module loaded into the driver, on its own handle.</summary>
    public sealed class Module(SafeFileHandle handle) : IDisposable
    {
        private readonly SafeFileHandle _handle = handle;

        /// <summary>
        /// Calls one of the module's functions.
        /// </summary>
        /// <returns>The cells the function wrote, or null when the call was refused.</returns>
        public long[]? Execute(string function, ReadOnlySpan<long> input, int outputCells)
        {
            byte[] request = new byte[NameLength + (input.Length * sizeof(long))];
            int written = Encoding.ASCII.GetBytes(function, request);

            if (written >= NameLength)
            {
                throw new ArgumentException("A PawnIO function name is at most 31 bytes.", nameof(function));
            }

            MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(NameLength));

            byte[] response = new byte[outputCells * sizeof(long)];

            if (!DeviceIoControl(
                    _handle, ExecuteFunction, request, request.Length, response, response.Length, out uint got, IntPtr.Zero))
            {
                return null;
            }

            long[] cells = new long[Math.Min(outputCells, (int)(got / sizeof(long)))];
            MemoryMarshal.Cast<byte, long>(response.AsSpan(0, cells.Length * sizeof(long))).CopyTo(cells);
            return cells;
        }

        /// <summary>Reads a model-specific register on whichever processor this thread is on.</summary>
        public ulong? ReadMsr(uint register)
        {
            long[]? cells = Execute("ioctl_read_msr", [register], 1);
            return cells is { Length: > 0 } ? (ulong)cells[0] : null;
        }

        /// <summary>
        /// Reads a model-specific register on one particular logical processor.
        /// </summary>
        /// <remarks>
        /// The driver reads on the processor the calling thread happens to be
        /// running on, so the thread is pinned there for the length of the
        /// read and then let go. Processor groups are sixty-four wide.
        /// </remarks>
        public ulong? ReadMsr(uint register, int logicalProcessor)
        {
            var wanted = new GroupAffinity
            {
                Mask = (nuint)1 << (logicalProcessor % 64),
                Group = (ushort)(logicalProcessor / 64),
            };

            IntPtr thread = GetCurrentThread();

            if (!SetThreadGroupAffinity(thread, in wanted, out GroupAffinity previous))
            {
                return null;
            }

            try
            {
                return ReadMsr(register);
            }
            finally
            {
                SetThreadGroupAffinity(thread, in previous, out _);
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinity
    {
        public nuint Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? input,
        int inputLength,
        byte[]? output,
        int outputLength,
        out uint returned,
        IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadGroupAffinity(
        IntPtr thread, in GroupAffinity affinity, out GroupAffinity previous);
}
