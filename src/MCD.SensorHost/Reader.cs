using System.Diagnostics;
using Mcd.Interop.PawnIo;
using Mcd.Sensors.Host;
using Mcd.Sensors.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RAMSPDToolkit.I2CSMBus;
using RAMSPDToolkit.SPD;
using RAMSPDToolkit.SPD.Interfaces;
using RAMSPDToolkit.SPD.Interop.Shared;
using RAMSPDToolkit.Windows.Driver;
using RAMSPDToolkit.Windows.Driver.Implementations;

namespace Mcd.SensorHost;

/// <summary>The latest snapshot, shared between the reader and the pipe.</summary>
public sealed class Readings
{
    private HostSnapshot _latest = HostSnapshot.Empty;

    public HostSnapshot Latest
    {
        get => Volatile.Read(ref _latest);
        set => Volatile.Write(ref _latest, value);
    }
}

/// <summary>
/// Reads the registers and the modules once a second.
/// </summary>
/// <remarks>
/// The processor every second, the memory every fifth: a module's
/// temperature moves slowly, and each read is a handful of bus transactions
/// under a system-wide mutex. The driver is opened once and kept; if it goes
/// away, the next round opens it again.
/// </remarks>
public sealed class Reader(Readings readings, ILogger<Reader> log) : BackgroundService
{
    private PawnIo.Module? _intel;
    private int _tjMax;
    private double _joulesPerUnit;
    private ulong _energy;
    private long _energyAt;
    private string _model = string.Empty;

    private readonly List<(int Slot, IThermalSensor Sensor)> _dimms = [];
    private bool _dimmsLooked;
    private int _tick;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                readings.Latest = ReadOnce();
            }
            catch (Exception e)
            {
                log.LogWarning(e, "a reading round failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private HostSnapshot ReadOnce()
    {
        _tick++;

        HostSnapshot previous = readings.Latest;

        double? package = null;
        double? coreMax = null;
        double? watts = null;

        if (_intel is null)
        {
            OpenIntel();
        }

        if (_intel is { } intel)
        {
            if (intel.ReadMsr(IntelThermal.PackageThermStatus) is { } status
                && IntelThermal.Celsius(status, _tjMax) is { } degrees)
            {
                package = degrees;
            }
            else
            {
                // The driver went away under us - an update, a stop. Let go;
                // the next round opens it again.
                log.LogWarning("the processor registers stopped answering; reopening next round");
                intel.Dispose();
                _intel = null;
            }

            int hottest = int.MinValue;

            for (int cpu = 0; cpu < Environment.ProcessorCount && _intel is not null; cpu++)
            {
                if (intel.ReadMsr(IntelThermal.ThermStatus, cpu) is { } core
                    && IntelThermal.Celsius(core, _tjMax) is { } c)
                {
                    hottest = Math.Max(hottest, c);
                }
            }

            if (hottest != int.MinValue)
            {
                coreMax = hottest;
            }

            if (_intel is not null && intel.ReadMsr(IntelThermal.PackageEnergyStatus) is { } energy)
            {
                long now = Stopwatch.GetTimestamp();
                double seconds = (now - _energyAt) / (double)Stopwatch.Frequency;
                watts = IntelThermal.Watts(_energy, energy, seconds, _joulesPerUnit);
                _energy = energy;
                _energyAt = now;
            }
        }

        DimmReading[] dimms = previous.Dimms;

        if (_tick % 5 == 1)
        {
            dimms = ReadDimms();
        }

        return new HostSnapshot(
            Version: 1,
            Model: _model,
            TjMax: _tjMax,
            Package: package,
            CoreMax: coreMax,
            Watts: watts,
            Dimms: dimms);
    }

    private void OpenIntel()
    {
        if (!IsIntel() || PawnIo.Load(IntelModule()) is not { } module)
        {
            return;
        }

        if (module.ReadMsr(IntelThermal.TemperatureTarget) is not { } target
            || IntelThermal.TjMax(target) is not { } tjMax
            || module.ReadMsr(IntelThermal.RaplPowerUnit) is not { } unit)
        {
            module.Dispose();
            return;
        }

        _intel = module;
        _tjMax = tjMax;
        _joulesPerUnit = IntelThermal.JoulesPerUnit(unit);
        _model = ModelName();
        _energy = module.ReadMsr(IntelThermal.PackageEnergyStatus) ?? 0;
        _energyAt = Stopwatch.GetTimestamp();

        log.LogInformation("reading {Model} through PawnIO; TjMax {TjMax}", _model, tjMax);
    }

    private DimmReading[] ReadDimms()
    {
        if (!_dimmsLooked)
        {
            _dimmsLooked = true;
            LookForDimms();
        }

        var found = new List<DimmReading>();

        foreach ((int slot, IThermalSensor sensor) in _dimms)
        {
            try
            {
                if (sensor.UpdateTemperature())
                {
                    found.Add(new DimmReading(slot, sensor.Temperature));
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "memory module {Slot} did not answer", slot);
            }
        }

        return [.. found];
    }

    private void LookForDimms()
    {
        try
        {
            if (!DriverManager.LoadDriver(DriverImplementation.PawnIO))
            {
                log.LogWarning("the SMBus module would not load");
                return;
            }

            SMBusManager.DetectSMBuses();

            foreach (SMBusInterface bus in SMBusManager.RegisteredSMBuses)
            {
                for (byte address = SPDConstants.SPD_BEGIN; address <= SPDConstants.SPD_END; address++)
                {
                    var detector = new SPDDetector(bus, address);

                    if (detector.IsValid && detector.Accessor is IThermalSensor sensor && sensor.HasThermalSensor)
                    {
                        _dimms.Add((address - SPDConstants.SPD_BEGIN, sensor));
                    }
                }
            }

            log.LogInformation("found {Count} memory module(s) with a thermometer", _dimms.Count);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "the memory bus could not be scanned");
            _dimms.Clear();
        }
    }

    public override void Dispose()
    {
        _intel?.Dispose();
        base.Dispose();
    }

    private static byte[] IntelModule()
    {
        using Stream stream = typeof(Reader).Assembly
            .GetManifestResourceStream("Mcd.SensorHost.Resources.PawnIO.IntelMSR.bin")
            ?? throw new InvalidOperationException("The IntelMSR module is not in the build.");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    private static bool IsIntel()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
        return key?.GetValue("VendorIdentifier") is string vendor
            && vendor.Contains("GenuineIntel", StringComparison.Ordinal);
    }

    private static string ModelName()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ProcessorKey);

        return key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0
            ? string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            : "Processor";
    }
}
