using System.Collections.Immutable;
using Mcd.Sensors.Contracts;
using Mcd.Sensors.Providers;
using Shouldly;

namespace Mcd.Tests.Sensors;

/// <summary>
/// Reading LibreHardwareMonitor's published tree.
/// </summary>
/// <remarks>
/// Against a document in the shape its web server serves, rather than against a
/// running copy: the point of the source is the machines this one is not, and a
/// test that only passes where the program happens to be installed tests
/// nothing about them.
/// </remarks>
public sealed class LhmReportTests
{
    private static ImmutableArray<LhmSensor> Sensors() =>
        LhmReport.Temperatures(File.ReadAllText(Path.Combine("fixtures", "lhm", "data.json")));

    [Fact]
    public void EveryTemperatureIsFoundAndNothingElseIs()
    {
        ImmutableArray<LhmSensor> found = Sensors();

        // Six temperatures in the document; the load figure and the fan speed
        // sit beside them under headings of their own and must not be taken for
        // temperatures.
        found.Length.ShouldBe(6);
        found.ShouldNotContain(s => s.Label.Contains("Fan"));
        found.ShouldNotContain(s => s.Label.Contains("Total"));
    }

    [Fact]
    public void AReadingKeepsItsHardwareAndItsPlace()
    {
        LhmSensor cpu = Sensors().Single(s => s.Id == "/amdcpu/0/temperature/0");

        cpu.Label.ShouldBe("Core (Tctl/Tdie)");
        cpu.Hardware.ShouldBe("AMD Ryzen 7 5800X");
        cpu.Group.ShouldBe(HardwareGroup.Cpu);
        cpu.Celsius.ShouldBe(58.4);
    }

    [Fact]
    public void HardwareIsTakenFromTheIdentifierRatherThanTheName()
    {
        ImmutableArray<LhmSensor> found = Sensors();

        found.Single(s => s.Id == "/amdgpu/0/temperature/0").Group.ShouldBe(HardwareGroup.Gpu);
        found.Single(s => s.Id == "/nvme/0/temperature/0").Group.ShouldBe(HardwareGroup.Storage);

        // A motherboard's sensors hang off a chip whose node sits one level
        // deeper than the board itself, and the board is what they belong to.
        LhmSensor board = found.Single(s => s.Id == "/lpc/nct6798d/temperature/1");
        board.Group.ShouldBe(HardwareGroup.Motherboard);
        board.Hardware.ShouldBe("Nuvoton NCT6798D");
    }

    [Theory]
    [InlineData("58,4 °C", 58.4)]
    [InlineData("62.0 °C", 62.0)]
    [InlineData("-5,5 °C", -5.5)]
    [InlineData("100 °C", 100)]
    public void BothDecimalSeparatorsAreRead(string value, double expected)
    {
        // The value is a display string in the machine's own language. Reading
        // "45.0" with a Russian Windows' rules gives four hundred and fifty -
        // a plausible-looking number, which is the worst kind of wrong.
        LhmReport.Number(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("°C")]
    public void SomethingThatIsNotANumberIsRefused(string value)
    {
        LhmReport.Number(value).ShouldBeNull();
    }

    [Fact]
    public void SomethingThatIsNotTheDocumentGivesNothing()
    {
        LhmReport.Temperatures("<html>404 Not Found</html>").ShouldBeEmpty();
        LhmReport.Temperatures("{}").ShouldBeEmpty();
        LhmReport.Temperatures("[1,2,3]").ShouldBeEmpty();
    }

    [Fact]
    public void IdentifiersAreClassifiedByTheirFirstSegment()
    {
        LhmReport.Classify("/intelcpu/0/temperature/0").ShouldBe(HardwareGroup.Cpu);
        LhmReport.Classify("/nvidiagpu/0/temperature/0").ShouldBe(HardwareGroup.Gpu);
        LhmReport.Classify("/ram/0/load/0").ShouldBe(HardwareGroup.Memory);
        LhmReport.Classify("/nic/0/throughput/0").ShouldBe(HardwareGroup.Network);

        // Anything unfamiliar is the board rather than nothing: an unknown
        // sensor is still worth offering, and the board is where the odd ones
        // live.
        LhmReport.Classify("/somethingnew/0/temperature/0").ShouldBe(HardwareGroup.Motherboard);
    }
}
