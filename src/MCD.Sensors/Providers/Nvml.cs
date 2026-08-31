using System.Runtime.InteropServices;
using System.Text;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The NVIDIA management library, resolved by hand at run time.
/// </summary>
/// <remarks>
/// <para>
/// nvml.dll ships with the graphics driver, so on a machine with no NVIDIA card
/// it simply is not there. Declaring these with <c>DllImport</c> would turn that
/// into a <c>DllNotFoundException</c> thrown from inside a JIT-compiled method
/// the first time a widget asked for a temperature.
/// </para>
/// <para>
/// Each entry point is also looked up under several names. NVIDIA versions its
/// API by suffix, and which suffixes exist depends on the driver: a function
/// that is missing has to mean "this driver cannot do that", not "the program
/// crashes on this machine".
/// </para>
/// <para>
/// Reading needs no administrator rights. Only writing - fan curves, power
/// limits - does, and this program never writes.
/// </para>
/// </remarks>
internal sealed class Nvml : IDisposable
{
    private const int Success = 0;

    /// <summary>NVML_TEMPERATURE_GPU.</summary>
    public const uint TemperatureGpu = 0;

    /// <summary>NVML_CLOCK_GRAPHICS.</summary>
    public const uint ClockGraphics = 0;

    /// <summary>NVML_CLOCK_MEM.</summary>
    public const uint ClockMemory = 2;

    private readonly nint _library;
    private bool _started;

    private Nvml(nint library) => _library = library;

    private delegate int NoArgs();

    private delegate int OutUint(out uint value);

    private delegate int ByIndex(uint index, out nint device);

    private delegate int DeviceOutUint(nint device, out uint value);

    private delegate int DeviceKindOutUint(nint device, uint kind, out uint value);

    private delegate int DeviceUtilization(nint device, out Utilization value);

    private delegate int DeviceMemory(nint device, out Memory value);

    private delegate int DeviceText(nint device, nint buffer, uint length);

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    private NoArgs? _shutdown;
    private OutUint? _deviceCount;
    private ByIndex? _handleByIndex;
    private DeviceText? _uuid;
    private DeviceText? _name;
    private DeviceKindOutUint? _temperature;
    private DeviceUtilization? _utilization;
    private DeviceOutUint? _fanSpeed;
    private DeviceKindOutUint? _clock;
    private DeviceOutUint? _power;

    /// <summary>Loads and initialises the library, or returns null when it is not usable.</summary>
    public static Nvml? TryLoad()
    {
        if (!NativeLibrary.TryLoad("nvml.dll", out nint handle))
        {
            return null;
        }

        var nvml = new Nvml(handle);

        // Versioned suffixes, newest first. The unsuffixed name is the fallback
        // for old drivers.
        NoArgs? init = nvml.Resolve<NoArgs>("nvmlInit_v2", "nvmlInit");

        if (init is null || init() != Success)
        {
            NativeLibrary.Free(handle);
            return null;
        }

        nvml._started = true;
        nvml._shutdown = nvml.Resolve<NoArgs>("nvmlShutdown");
        nvml._deviceCount = nvml.Resolve<OutUint>("nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
        nvml._handleByIndex = nvml.Resolve<ByIndex>("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
        nvml._uuid = nvml.Resolve<DeviceText>("nvmlDeviceGetUUID");
        nvml._name = nvml.Resolve<DeviceText>("nvmlDeviceGetName");
        nvml._temperature = nvml.Resolve<DeviceKindOutUint>("nvmlDeviceGetTemperature");
        nvml._utilization = nvml.Resolve<DeviceUtilization>("nvmlDeviceGetUtilizationRates");
        nvml._memory = nvml.Resolve<DeviceMemory>("nvmlDeviceGetMemoryInfo");
        nvml._fanSpeed = nvml.Resolve<DeviceOutUint>("nvmlDeviceGetFanSpeed");
        nvml._clock = nvml.Resolve<DeviceKindOutUint>("nvmlDeviceGetClockInfo");
        nvml._power = nvml.Resolve<DeviceOutUint>("nvmlDeviceGetPowerUsage");

        return nvml;
    }

    public int DeviceCount => _deviceCount is not null && _deviceCount(out uint count) == Success ? (int)count : 0;

    public nint? Device(int index) =>
        _handleByIndex is not null && _handleByIndex((uint)index, out nint device) == Success
            ? device
            : null;

    /// <summary>Stable across reboots, driver updates and PCI slots. The natural hardware key.</summary>
    public string? Uuid(nint device) => Text(_uuid, device, 96);

    public string? Name(nint device) => Text(_name, device, 96);

    /// <summary>How full the card's own memory is, in per cent, or null.</summary>
    public double? MemoryUsed(nint device) =>
        _memory is not null && _memory(device, out Memory info) == Success && info.Total > 0
            ? 100.0 * info.Used / info.Total
            : null;

    private DeviceMemory? _memory;

    public double? Temperature(nint device) =>
        _temperature is not null && _temperature(device, TemperatureGpu, out uint value) == Success
            ? value
            : null;

    public Utilization? Load(nint device) =>
        _utilization is not null && _utilization(device, out Utilization value) == Success
            ? value
            : null;

    /// <summary>Percent of maximum, not RPM. Zero is normal: many cards stop the fans when cool.</summary>
    public double? FanSpeed(nint device) =>
        _fanSpeed is not null && _fanSpeed(device, out uint value) == Success ? value : null;

    public double? Clock(nint device, uint kind) =>
        _clock is not null && _clock(device, kind, out uint value) == Success ? value : null;

    /// <summary>Watts. NVML reports milliwatts.</summary>
    public double? Power(nint device) =>
        _power is not null && _power(device, out uint milliwatts) == Success ? milliwatts / 1000.0 : null;

    public void Dispose()
    {
        if (_started)
        {
            _shutdown?.Invoke();
            _started = false;
        }

        if (_library != 0)
        {
            NativeLibrary.Free(_library);
        }
    }

    private T? Resolve<T>(params string[] names)
        where T : Delegate
    {
        foreach (string name in names)
        {
            if (NativeLibrary.TryGetExport(_library, name, out nint address))
            {
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
        }

        return null;
    }

    private static string? Text(DeviceText? call, nint device, int length)
    {
        if (call is null)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(length);

        try
        {
            if (call(device, buffer, (uint)length) != Success)
            {
                return null;
            }

            // NVML writes plain ASCII, null terminated.
            string text = Marshal.PtrToStringAnsi(buffer) ?? string.Empty;
            return text.Length > 0 ? text : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
