using System.Diagnostics;
using Mcd.Interop.PawnIo;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Mcd.Sensors.Providers;

/// <summary>
/// The processor's own temperature and power, read from its registers
/// through the PawnIO driver.
/// </summary>
/// <remarks>
/// <para>
/// Intel only, for now: the module loaded is the driver author's signed
/// <c>IntelMSR</c>, which refuses to load on anything else. AMD goes through
/// a different module and a different arithmetic, and is its own day's work.
/// </para>
/// <para>
/// Three readings: the package temperature - the one figure a person means
/// by "how hot is the processor" - the hottest single core, and the package
/// power in watts. Per-core figures are read but folded into the hottest;
/// twenty-four rows saying 61, 62, 61 are noise on a bar and on a page.
/// </para>
/// </remarks>
public sealed class CpuMsrProvider(ILogger<CpuMsrProvider> log) : ISensorProvider
{
    private const string ProviderId = "cpu";
    private const string Hardware = "pkg0";

    private PawnIo.Module? _module;
    private int _tjMax;
    private double _joulesPerUnit;
    private ulong _energy;
    private long _energyAt;
    private string _model = string.Empty;
    private bool _saidNoDriver;

    public string Id => ProviderId;

    public Tier Tier => Tier.Driver;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public bool IsAvailable()
    {
        if (_module is not null)
        {
            return true;
        }

        if (!IsIntel())
        {
            return false;
        }

        if (PawnIo.InstalledVersion() is null && !PawnIo.Present())
        {
            if (!_saidNoDriver)
            {
                _saidNoDriver = true;
                log.LogInformation("sensors.cpu the PawnIO driver is not installed; no processor temperature");
            }

            return false;
        }

        PawnIo.Module? module = PawnIo.Load(IntelModule());

        if (module is null)
        {
            log.LogWarning("sensors.cpu PawnIO is installed but the IntelMSR module would not load");
            return false;
        }

        if (module.ReadMsr(IntelThermal.TemperatureTarget) is not { } target
            || IntelThermal.TjMax(target) is not { } tjMax
            || module.ReadMsr(IntelThermal.RaplPowerUnit) is not { } unit)
        {
            log.LogWarning("sensors.cpu the module loaded but the thermal registers did not answer");
            module.Dispose();
            return false;
        }

        _module = module;
        _tjMax = tjMax;
        _joulesPerUnit = IntelThermal.JoulesPerUnit(unit);
        _model = ModelName();
        _energy = module.ReadMsr(IntelThermal.PackageEnergyStatus) ?? 0;
        _energyAt = Stopwatch.GetTimestamp();

        log.LogInformation(
            "sensors.cpu reading {Model} through PawnIO {Version}; TjMax {TjMax}",
            _model, PawnIo.InstalledVersion() ?? "?", tjMax);

        return true;
    }

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        new(
            SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "package"),
            SensorKind.Temperature,
            HardwareGroup.Cpu,
            _model,
            "Package",
            "°C",
            Warning: _tjMax - 15,
            Critical: _tjMax - 5),

        new(
            SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "core-max"),
            SensorKind.Temperature,
            HardwareGroup.Cpu,
            _model,
            "Hottest core",
            "°C",
            Warning: _tjMax - 15,
            Critical: _tjMax - 5,
            Prominent: false),

        new(
            SensorKey.Make(ProviderId, Hardware, SensorKind.Power, "package"),
            SensorKind.Power,
            HardwareGroup.Cpu,
            _model,
            "Package power",
            "W",
            Prominent: false),
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (_module is not { } module)
        {
            return;
        }

        if (module.ReadMsr(IntelThermal.PackageThermStatus) is { } package
            && IntelThermal.Celsius(package, _tjMax) is { } degrees)
        {
            into[SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "package")] = degrees;
        }

        int hottest = int.MinValue;

        for (int cpu = 0; cpu < Environment.ProcessorCount; cpu++)
        {
            if (module.ReadMsr(IntelThermal.ThermStatus, cpu) is { } status
                && IntelThermal.Celsius(status, _tjMax) is { } core)
            {
                hottest = Math.Max(hottest, core);
            }
        }

        if (hottest != int.MinValue)
        {
            into[SensorKey.Make(ProviderId, Hardware, SensorKind.Temperature, "core-max")] = hottest;
        }

        if (module.ReadMsr(IntelThermal.PackageEnergyStatus) is { } energy)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - _energyAt) / (double)Stopwatch.Frequency;

            if (IntelThermal.Watts(_energy, energy, seconds, _joulesPerUnit) is { } watts)
            {
                into[SensorKey.Make(ProviderId, Hardware, SensorKind.Power, "package")] = watts;
            }

            _energy = energy;
            _energyAt = now;
        }
    }

    public void Dispose()
    {
        _module?.Dispose();
        _module = null;
    }

    private static byte[] IntelModule()
    {
        using Stream stream = typeof(CpuMsrProvider).Assembly
            .GetManifestResourceStream("Mcd.Sensors.Resources.PawnIO.IntelMSR.bin")
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
