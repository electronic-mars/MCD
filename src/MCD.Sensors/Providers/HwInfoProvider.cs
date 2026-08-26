using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Providers;

/// <summary>
/// Processor and motherboard temperatures, by way of HWiNFO.
/// </summary>
/// <remarks>
/// <para>
/// The heat a processor reports about itself lives in a register only kernel
/// code may read, and a Store package cannot carry a driver. So the one honest
/// way to show a CPU temperature is to ask a program that already has such a
/// driver installed. HWiNFO publishes its readings in shared memory for exactly
/// this purpose, and reading it needs no rights at all.
/// </para>
/// <para>
/// Off until someone turns it on. It depends on software this program neither
/// ships nor installs, and everything else here works without it.
/// </para>
/// <para>
/// Only the processor and the board are taken. Graphics and drives are read
/// directly from the driver and from the drive itself, one step closer to the
/// source; taking HWiNFO's copy of them as well would put every one of those
/// temperatures on screen twice.
/// </para>
/// </remarks>
/// <param name="enabled">
/// Asked afresh each time, not captured once, so that switching the source on
/// in settings takes effect without a restart.
/// </param>
public sealed class HwInfoProvider(ILogger<HwInfoProvider> log, Func<bool> enabled) : ISensorProvider
{
    public const string ProviderId = "hwinfo";

    /// <summary>
    /// How long the block may sit unchanged before it counts as abandoned.
    /// </summary>
    /// <remarks>
    /// HWiNFO refreshes it about twice a second. A minute of no movement means
    /// it has stopped, not that it is slow.
    /// </remarks>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The processor temperature worth putting on the bar, best first.
    /// </summary>
    /// <remarks>
    /// A modern processor reports a temperature per core, and putting thirty-two
    /// of them on a dock helps nobody. These are the whole-package figures - the
    /// number every monitoring program shows as "the CPU temperature" - under
    /// the names Intel and AMD each give it.
    /// </remarks>
    private static readonly string[] PackageLabels =
    [
        "CPU Package",
        "CPU (Tctl/Tdie)",
        "CPU Die (average)",
        "CPU IA Cores",
        "Core Max",
        "CPU Temperature",
        "CPU",
    ];

    private HwInfoSharedMemory? _block;
    private List<Sensor> _sensors = [];

    private DateTimeOffset _publishedAt;
    private DateTimeOffset _movedAt;

    public string Id => ProviderId;

    public Tier Tier => Tier.External;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    /// <summary>Why the source is not being used, in words worth showing someone.</summary>
    public string? Trouble { get; private set; }

    public bool IsAvailable()
    {
        if (!enabled())
        {
            Close("switched off in settings");
            return false;
        }

        if (_block is not null)
        {
            return true;
        }

        _block = HwInfoSharedMemory.TryOpen();

        if (_block is null)
        {
            // Two causes, indistinguishable from here, so both are named. The
            // second is the common one: HWiNFO publishes nothing until the
            // "Shared Memory Support" box in its settings is ticked.
            Trouble = "HWiNFO is not running, or its Shared Memory Support is switched off.";
            return false;
        }

        if (Read() is not { } first)
        {
            Close("the shared memory is there but does not hold a readable block");
            return false;
        }

        _sensors = [.. Describe(first)];

        if (_sensors.Count == 0)
        {
            Close("HWiNFO reports no processor or motherboard temperature");
            return false;
        }

        Trouble = null;
        _publishedAt = first.PolledAt;
        _movedAt = DateTimeOffset.UtcNow;

        log.LogInformation(
            "sensors.hwinfo opened, {Count} of {Total} readings taken",
            _sensors.Count, first.Readings.Length);

        return true;
    }

    public IReadOnlyList<SensorDescriptor> Discover() => [.. _sensors.Select(s => s.Descriptor)];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        if (Read() is not { } block)
        {
            Close("the block stopped being readable");
            throw new InvalidOperationException("HWiNFO stopped publishing readable shared memory.");
        }

        WatchForAbandonment(block);

        foreach (Sensor sensor in _sensors)
        {
            // Found by identity rather than by position: HWiNFO reorders the
            // table when hardware appears or a sensor is hidden, and a row index
            // captured at startup would then point at a different reading.
            HwInfoReading? reading = block.Readings.FirstOrDefault(
                r => r.Id == sensor.ReadingId && block.Owner(r) is { } o
                     && o.Id == sensor.SensorId && o.Instance == sensor.Instance);

            if (reading is not null)
            {
                into[sensor.Descriptor.Key] = reading.Value;
            }
        }
    }

    public void Dispose() => Close(null);

    /// <summary>
    /// Notices the block going quiet.
    /// </summary>
    /// <remarks>
    /// The free version of HWiNFO publishes shared memory for twelve hours and
    /// then stops, leaving the block mapped with every value frozen at whatever
    /// it last was. Nothing fails, nothing is missing, and the numbers on the
    /// dock quietly become a photograph of this morning. The only way to tell is
    /// that the time in the header stops moving.
    /// </remarks>
    private void WatchForAbandonment(HwInfoBlock block)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (block.PolledAt != _publishedAt)
        {
            _publishedAt = block.PolledAt;
            _movedAt = now;
            return;
        }

        if (now - _movedAt < Stall)
        {
            return;
        }

        Close("HWiNFO has stopped updating its shared memory. The free version "
            + "publishes it for twelve hours at a time - restart HWiNFO to continue.");

        throw new InvalidOperationException(Trouble);
    }

    private HwInfoBlock? Read() =>
        _block?.Copy() is { } bytes ? HwInfoSharedMemory.Parse(bytes) : null;

    private void Close(string? why)
    {
        _block?.Dispose();
        _block = null;
        _sensors = [];
        Trouble = why;

        if (why is not null)
        {
            log.LogInformation("sensors.hwinfo closed: {Why}", why);
        }
    }

    /// <summary>The temperatures in the block that are ours to show.</summary>
    private static IEnumerable<Sensor> Describe(HwInfoBlock block)
    {
        List<(HwInfoReading Reading, HwInfoSensor Owner, HardwareGroup Group)> wanted =
        [
            .. block.Readings
                .Where(r => r.Type == HwInfoReadingType.Temperature)
                .Select(r => (Reading: r, Owner: block.Owner(r)))
                .Where(p => p.Owner is not null)
                .Select(p => (p.Reading, Owner: p.Owner!, Group: GroupOf(p.Owner!.Name)))
                .Where(p => p.Group is HardwareGroup.Cpu or HardwareGroup.Motherboard)
        ];

        HwInfoReading? headline = Headline(wanted);

        foreach ((HwInfoReading reading, HwInfoSensor owner, HardwareGroup group) in wanted)
        {
            bool board = group == HardwareGroup.Motherboard;

            yield return new Sensor(
                owner.Id,
                owner.Instance,
                reading.Id,
                new SensorDescriptor(
                    SensorKey.Make(
                        ProviderId,
                        $"{owner.Id:x8}-{owner.Instance}",
                        SensorKind.Temperature,
                        reading.Id.ToString("x8")),
                    SensorKind.Temperature,
                    group,
                    owner.Name,
                    reading.Label,
                    "°C",

                    // HWiNFO knows each sensor's limits but does not publish
                    // them here, so these are the ordinary ones for the part.
                    // A processor is designed to run to about a hundred and to
                    // slow itself down rather than go past it.
                    Warning: board ? 70 : 85,
                    Critical: board ? 90 : 100,
                    Rank: 80,
                    Prominent: ReferenceEquals(reading, headline)
                               || (board && reading.Label.Equals("Motherboard", StringComparison.OrdinalIgnoreCase))));
        }
    }

    /// <summary>The one processor temperature to show without being asked.</summary>
    private static HwInfoReading? Headline(
        IEnumerable<(HwInfoReading Reading, HwInfoSensor Owner, HardwareGroup Group)> found)
    {
        List<HwInfoReading> cpu = [.. found.Where(p => p.Group == HardwareGroup.Cpu).Select(p => p.Reading)];

        foreach (string label in PackageLabels)
        {
            if (cpu.FirstOrDefault(r => r.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        // An unfamiliar processor still deserves a number on the bar.
        return cpu.FirstOrDefault();
    }

    /// <summary>
    /// Which part of the machine a HWiNFO sensor entry is about.
    /// </summary>
    /// <remarks>
    /// By the name of the entry rather than its numeric id. The names are
    /// HWiNFO's own and are English whatever language Windows is in, and they
    /// begin with the part: "CPU [#0]: ...", "GPU [#0]: ...", "Drive [#0]: ...".
    /// The ids are not documented as stable and differ between chipsets.
    /// </remarks>
    private static HardwareGroup GroupOf(string sensorName) =>
        sensorName.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ? HardwareGroup.Cpu
        : sensorName.StartsWith("GPU", StringComparison.OrdinalIgnoreCase) ? HardwareGroup.Gpu
        : sensorName.StartsWith("Drive", StringComparison.OrdinalIgnoreCase) ? HardwareGroup.Storage
        : sensorName.StartsWith("Network", StringComparison.OrdinalIgnoreCase) ? HardwareGroup.Network
        : HardwareGroup.Motherboard;

    /// <param name="SensorId">
    /// HWiNFO's own identifiers for the hardware and the reading, kept so a row
    /// can be found again after the table is reordered.
    /// </param>
    private sealed record Sensor(uint SensorId, uint Instance, uint ReadingId, SensorDescriptor Descriptor);
}
