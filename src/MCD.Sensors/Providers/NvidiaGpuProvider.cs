using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// Temperature, load, clocks, fan and power for NVIDIA graphics.
/// </summary>
/// <remarks>
/// This is the one temperature a Store-packaged program can read on its own.
/// Everything the CPU knows about its own heat sits behind a machine-specific
/// register that only kernel code may touch, and an MSIX package cannot carry a
/// driver. The graphics driver, on the other hand, publishes its readings
/// through an ordinary user-mode library that needs no rights at all.
/// </remarks>
public sealed class NvidiaGpuProvider(ILogger<NvidiaGpuProvider> log) : ISensorProvider
{
    private const string ProviderId = "nvml";

    private Nvml? _nvml;
    private readonly List<Gpu> _gpus = [];

    public string Id => ProviderId;

    public Tier Tier => Tier.Vendor;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public bool IsAvailable()
    {
        if (_nvml is not null)
        {
            return true;
        }

        _nvml = Nvml.TryLoad();

        if (_nvml is null)
        {
            return false;
        }

        Enumerate();

        if (_gpus.Count != 0)
        {
            return true;
        }

        // The library loaded but there is nothing behind it. Let go of it rather
        // than sit on an initialised handle for a card that is not there.
        _nvml.Dispose();
        _nvml = null;
        return false;
    }

    public IReadOnlyList<SensorDescriptor> Discover() => [.. _gpus.SelectMany(Describe)];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (_nvml is null)
        {
            return;
        }

        foreach (Gpu gpu in _gpus)
        {
            Put(into, gpu, SensorKind.Temperature, "core", _nvml.Temperature(gpu.Handle));
            Put(into, gpu, SensorKind.Fan, "main", _nvml.FanSpeed(gpu.Handle));
            Put(into, gpu, SensorKind.Clock, "core", _nvml.Clock(gpu.Handle, Nvml.ClockGraphics));
            Put(into, gpu, SensorKind.Clock, "memory", _nvml.Clock(gpu.Handle, Nvml.ClockMemory));
            Put(into, gpu, SensorKind.Power, "board", _nvml.Power(gpu.Handle));

            if (_nvml.Load(gpu.Handle) is { } load)
            {
                Put(into, gpu, SensorKind.Load, "core", load.Gpu);
                Put(into, gpu, SensorKind.Load, "memory", load.Memory);
            }

            Put(into, gpu, SensorKind.Load, "vram", _nvml.MemoryUsed(gpu.Handle));
        }
    }

    public void Dispose()
    {
        _nvml?.Dispose();
        _nvml = null;
        _gpus.Clear();
    }

    private void Enumerate()
    {
        _gpus.Clear();

        for (int i = 0; i < _nvml!.DeviceCount; i++)
        {
            if (_nvml.Device(i) is not { } handle)
            {
                continue;
            }

            // The UUID is the hardware key: stable across reboots, driver
            // updates and PCI slots, unlike the enumeration index. A widget's
            // saved sensor key has to survive all three.
            string uuid = _nvml.Uuid(handle) ?? $"gpu{i}";
            string name = _nvml.Name(handle) ?? "NVIDIA GPU";

            _gpus.Add(new Gpu(handle, Shorten(uuid), name));
            log.LogInformation("sensors.nvml found {Name} ({Uuid})", name, uuid);
        }
    }

    private static IEnumerable<SensorDescriptor> Describe(Gpu gpu)
    {
        // 83 °C is where a modern GeForce starts pulling its clocks back, and
        // NVIDIA's own limit sits at 90. Those are the numbers worth colouring.
        yield return Sensor(gpu, SensorKind.Temperature, "core", "GPU", "°C", 83, 90);
        yield return Sensor(gpu, SensorKind.Load, "core", "GPU", "%");
        // The controller's busyness and the memory's fullness are different
        // numbers, and the first was labelled as the second for a while -
        // which taught people the wrong figure.
        yield return Sensor(gpu, SensorKind.Load, "memory", "GPU memory controller", "%");
        yield return Sensor(gpu, SensorKind.Load, "vram", "GPU memory used", "%");
        yield return Sensor(gpu, SensorKind.Fan, "main", "GPU fan", "%");
        yield return Sensor(gpu, SensorKind.Clock, "core", "GPU clock", "MHz");
        yield return Sensor(gpu, SensorKind.Clock, "memory", "GPU memory clock", "MHz");
        yield return Sensor(gpu, SensorKind.Power, "board", "GPU power", "W");
    }

    private static SensorDescriptor Sensor(
        Gpu gpu, SensorKind kind, string slot, string label, string unit,
        double? warning = null, double? critical = null) =>
        new(SensorKey.Make(ProviderId, gpu.Key, kind, slot),
            kind, HardwareGroup.Gpu, gpu.Name, label, unit, warning, critical,

            // Straight from the driver. A relayed copy of the same number from
            // HWiNFO or LibreHardwareMonitor loses to this.
            Rank: 100);

    private static void Put(
        IDictionary<SensorKey, double> into, Gpu gpu, SensorKind kind, string slot, double? value)
    {
        if (value.HasValue)
        {
            into[SensorKey.Make(ProviderId, gpu.Key, kind, slot)] = value.Value;
        }
    }

    /// <summary>The tail of the UUID is enough to tell two cards apart and keeps the key readable.</summary>
    private static string Shorten(string uuid) =>
        uuid.Length <= 12 ? uuid : "gpu-" + uuid[^8..];

    private sealed record Gpu(nint Handle, string Key, string Name);
}
