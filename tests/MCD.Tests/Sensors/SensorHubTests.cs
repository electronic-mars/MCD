using Mcd.Sensors;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Mcd.Tests.Sensors;

/// <summary>
/// What the hub does when a source misbehaves.
/// </summary>
/// <remarks>
/// The rule these tests exist to hold is that a reading is never invented. A
/// widget showing "0 °C" is a lie about the hardware; a widget showing a dash is
/// the truth about the software.
/// </remarks>
public sealed class SensorHubTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly SensorKey Key = SensorKey.Make("fake", "cpu", SensorKind.Temperature, "package");

    [Fact]
    public void AWorkingSourceReachesTheSnapshot()
    {
        var provider = new FakeProvider { Value = 42 };
        using SensorHub hub = Build(provider);

        hub.Pump(Start);

        hub.Current[Key].Value.ShouldBe(42);
        hub.Current[Key].Quality.ShouldBe(Quality.Fresh);
        hub.Catalog.Length.ShouldBe(1);
    }

    [Fact]
    public void ASourceThatIsNotThereYetIsProbedAgainLater()
    {
        var provider = new FakeProvider { Available = false };
        using SensorHub hub = Build(provider);

        hub.Pump(Start);
        hub.Catalog.ShouldBeEmpty();

        // HWiNFO started after the dock did must be picked up on its own. Asking
        // the user to restart the dock for that would be absurd.
        provider.Available = true;
        hub.Pump(Start.AddMinutes(1));

        hub.Catalog.Length.ShouldBe(1);
    }

    [Fact]
    public void AStumbleKeepsTheLastValueRatherThanBlankingIt()
    {
        var provider = new FakeProvider { Value = 55 };
        using SensorHub hub = Build(provider);

        hub.Pump(Start);
        provider.Throw = true;
        hub.Pump(Start.AddSeconds(1));

        // One failed read is a stumble, not a loss. The number stays, marked as
        // no longer fresh, so the dock does not flicker on every hiccup.
        hub.Current[Key].Value.ShouldBe(55);
        hub.Current[Key].Quality.ShouldBe(Quality.Stale);
    }

    [Fact]
    public void ASourceThatKeepsFailingGoesBlankAndIsNotAskedAgainImmediately()
    {
        var provider = new FakeProvider { Value = 55 };
        using SensorHub hub = Build(provider);

        hub.Pump(Start);
        provider.Throw = true;

        for (int i = 1; i <= 3; i++)
        {
            hub.Pump(Start.AddSeconds(i));
        }

        hub.Current[Key].HasValue.ShouldBeFalse();
        hub.Current[Key].Quality.ShouldBe(Quality.Missing);
        hub.Catalog.ShouldBeEmpty();

        int reads = provider.Reads;
        hub.Pump(Start.AddSeconds(4));

        // Backing off matters: a source that fails because it is hammering a
        // locked device must not be hammered once a second forever.
        provider.Reads.ShouldBe(reads);
    }

    [Fact]
    public void OneBrokenSourceDoesNotSilenceTheOthers()
    {
        var broken = new FakeProvider { Id = "broken", Throw = true };
        var working = new FakeProvider { Id = "working", Value = 7 };
        using SensorHub hub = Build(broken, working);

        hub.Pump(Start);

        hub.Current[SensorKey.Make("working", "cpu", SensorKind.Temperature, "package")]
            .Value.ShouldBe(7);
    }

    private static SensorHub Build(params ISensorProvider[] providers) =>
        new(NullLogger<SensorHub>.Instance, providers, pumpItself: false);

    private sealed class FakeProvider : ISensorProvider
    {
        public string Id { get; set; } = "fake";

        public Tier Tier => Tier.Platform;

        public TimeSpan Interval => TimeSpan.FromSeconds(1);

        public bool Available { get; set; } = true;

        public bool Throw { get; set; }

        public double Value { get; set; }

        public int Reads { get; private set; }

        public bool IsAvailable() => Available;

        public IReadOnlyList<SensorDescriptor> Discover() =>
        [
            new(SensorKey.Make(Id, "cpu", SensorKind.Temperature, "package"),
                SensorKind.Temperature, HardwareGroup.Cpu, "cpu", "CPU", "°C"),
        ];

        public void Poll(IDictionary<SensorKey, double> into)
        {
            Reads++;

            if (Throw)
            {
                throw new InvalidOperationException("the fake source is unwell");
            }

            into[SensorKey.Make(Id, "cpu", SensorKind.Temperature, "package")] = Value;
        }

        public void Dispose()
        {
        }
    }
}
