using System.Runtime.InteropServices;
using Mcd.Sensors.Contracts;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace Mcd.Sensors.Providers;

/// <summary>
/// How much memory is in use.
/// </summary>
/// <remarks>
/// Read straight from <c>GlobalMemoryStatusEx</c> rather than from a performance
/// counter or from WMI. It is a single call with no handles to keep, and it
/// cannot be missing.
/// </remarks>
public sealed class MemoryProvider : ISensorProvider
{
    private const string ProviderId = "mem";
    private static readonly string Hardware = "ram";

    public string Id => ProviderId;

    public Tier Tier => Tier.Platform;

    public TimeSpan Interval => TimeSpan.FromSeconds(1);

    public bool IsAvailable() => true;

    public IReadOnlyList<SensorDescriptor> Discover() =>
    [
        new(UsedPercent, SensorKind.Load, HardwareGroup.Memory, Hardware, "RAM", "%"),
        new(UsedBytes, SensorKind.Bytes, HardwareGroup.Memory, Hardware, "RAM used", "B"),
        new(TotalBytes, SensorKind.Bytes, HardwareGroup.Memory, Hardware, "RAM installed", "B"),

        // The same call already carries these; they are looked up rather
        // than glanced at, so none of them goes on the bar unasked.
        new(FreeBytes, SensorKind.Bytes, HardwareGroup.Memory, Hardware, "RAM free", "B", Prominent: false),
        new(CommitPercent, SensorKind.Load, HardwareGroup.Memory, Hardware, "Committed", "%", Prominent: false),
        new(CommitBytes, SensorKind.Bytes, HardwareGroup.Memory, Hardware, "Committed", "B", Prominent: false),
        new(CommitLimit, SensorKind.Bytes, HardwareGroup.Memory, Hardware, "Commit limit", "B", Prominent: false),
    ];

    public void Poll(IDictionary<SensorKey, double> into)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

        if (!PInvoke.GlobalMemoryStatusEx(ref status))
        {
            return;
        }

        into[UsedPercent] = status.dwMemoryLoad;
        into[UsedBytes] = status.ullTotalPhys - status.ullAvailPhys;
        into[TotalBytes] = status.ullTotalPhys;
        into[FreeBytes] = status.ullAvailPhys;

        // Commit: what Windows has promised against memory plus the page
        // file. The page file's own traffic is not published here; this is
        // the number the Task Manager calls "Committed".
        into[CommitBytes] = status.ullTotalPageFile - status.ullAvailPageFile;
        into[CommitLimit] = status.ullTotalPageFile;
        into[CommitPercent] = status.ullTotalPageFile > 0
            ? (status.ullTotalPageFile - status.ullAvailPageFile) * 100.0 / status.ullTotalPageFile
            : 0;
    }

    public void Dispose()
    {
    }

    private static SensorKey UsedPercent => SensorKey.Make(ProviderId, Hardware, SensorKind.Load, "used");

    private static SensorKey UsedBytes => SensorKey.Make(ProviderId, Hardware, SensorKind.Bytes, "used");

    private static SensorKey TotalBytes => SensorKey.Make(ProviderId, Hardware, SensorKind.Bytes, "total");

    private static SensorKey FreeBytes => SensorKey.Make(ProviderId, Hardware, SensorKind.Bytes, "free");

    private static SensorKey CommitPercent => SensorKey.Make(ProviderId, Hardware, SensorKind.Load, "commit");

    private static SensorKey CommitBytes => SensorKey.Make(ProviderId, Hardware, SensorKind.Bytes, "commit-used");

    private static SensorKey CommitLimit => SensorKey.Make(ProviderId, Hardware, SensorKind.Bytes, "commit-total");
}
