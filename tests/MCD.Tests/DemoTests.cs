using Mcd.Core.Monitors;
using Mcd.Interop;
using Mcd.Tests.Monitors;
using Shouldly;

namespace Mcd.Tests;

/// <summary>The made-up figures and the made-up screen behind MCD_DEMO=1.</summary>
public sealed class DemoTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Keys =
    [
        "pdh/cpu/load/total", "pdh/cpu/load/core0", "mem/ram/load/used", "nvml/gpu0/temp/core",
        "nvme/disk0/temp/composite", "pdh/net/bps/down", "pdh/disk/bps/read", "acpi/zone/temp/0",
    ];

    [Fact]
    public void EveryReadingShowsOnlyTwentyOneSeventyFiveOrOneHundred()
    {
        for (int second = 0; second < 600; second++)
        {
            foreach (string key in Keys)
            {
                Demo.Value(key, Start.AddSeconds(second)).ShouldBeOneOf(Demo.Rest, Demo.Warm, Demo.Peak);
                Demo.Past(key, second).ShouldBeOneOf(Demo.Rest, Demo.Warm, Demo.Peak);
            }
        }
    }

    [Fact]
    public void EveryReadingRestsMostOfTheTimeAndTakesItsTurnAtThePeak()
    {
        foreach (string key in Keys)
        {
            int peaks = Enumerable.Range(0, 128).Count(s => Demo.Value(key, Start.AddSeconds(s)) == Demo.Peak);

            peaks.ShouldBeGreaterThan(0, key);
            peaks.ShouldBeLessThan(40, key);
        }
    }

    [Fact]
    public void TheCoresOfOnePartRiseTogether()
    {
        for (int second = 0; second < 120; second++)
        {
            DateTimeOffset at = Start.AddSeconds(second);
            Demo.Value("pdh/cpu/load/total", at).ShouldBe(Demo.Value("pdh/cpu/load/core0", at));
        }
    }

    [Fact]
    public void TheRiseTravelsAlongTheDefaultBar()
    {
        // A turn is eight seconds and there are four, so a round starts every thirty-two.
        DateTimeOffset round = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 / 32 * 32).AddMilliseconds(500);

        double[] cpu = [.. Enumerable.Range(0, 5).Select(s => Demo.Value("pdh/cpu/load/total", round.AddSeconds(s)))];
        cpu.ShouldBe([Demo.Warm, Demo.Peak, Demo.Warm, Demo.Rest, Demo.Rest]);

        Demo.Value("msr/pkg0/temp/package", round.AddSeconds(1)).ShouldBe(Demo.Peak);
        Demo.Value("pdh/gpu/load/total", round.AddSeconds(9)).ShouldBe(Demo.Peak);
        Demo.Value("mem/ram/load/used", round.AddSeconds(17)).ShouldBe(Demo.Peak);
    }

    [Fact]
    public void TheHourOfHistoryIsMostlyFlat()
    {
        foreach (string key in Keys)
        {
            int peaks = Enumerable.Range(1, 3599).Count(s => Demo.Past(key, s) == Demo.Peak);

            peaks.ShouldBeInRange(1, 3600 / 10, key);
        }
    }

    [Fact]
    public void TheScreenIsSmallAndInThePrimaryScreensCorner()
    {
        MonitorInfo primary = Topology.Laptop();

        MonitorInfo screen = MonitorService.DemoScreen(primary);

        screen.Bounds.left.ShouldBe(primary.Bounds.left);
        screen.Bounds.top.ShouldBe(primary.Bounds.top);
        screen.Width.ShouldBe(Demo.ScreenWidth);
        screen.Height.ShouldBe(Demo.ScreenHeight);
        screen.WorkArea.ShouldBe(screen.Bounds);
        screen.IsPrimary.ShouldBeTrue();
    }
}
