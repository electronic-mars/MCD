using Mcd.Interop.Machine;
using Shouldly;

namespace Mcd.Tests.Processor;

public sealed class CoresTests
{
    private static LogicalProcessor At(byte index, byte kind) => new((uint)(256 + index), 0, index, kind);

    [Fact]
    public void TheFastestKindIsPAndEveryOtherKindIsE()
    {
        // A laptop chip with three kinds: low-power E, E and P.
        Cores cores = Cores.Of([At(0, 2), At(1, 2), At(2, 1), At(3, 1), At(4, 0)]);

        cores.Hybrid.ShouldBeTrue();
        cores.Performance.Select(p => p.Index).ShouldBe(new byte[] { 0, 1 });
        cores.Efficient.Select(p => p.Index).ShouldBe(new byte[] { 2, 3, 4 });
    }

    [Fact]
    public void OneKindOfCoreIsNotHybridAndEveryCoreIsP()
    {
        Cores cores = Cores.Of([At(0, 0), At(1, 0)]);

        cores.Hybrid.ShouldBeFalse();
        cores.Performance.Length.ShouldBe(2);
        cores.Efficient.ShouldBeEmpty();
    }

    [Fact]
    public void TheCountersNameAProcessorByGroupAndIndex() =>
        new LogicalProcessor(300, 1, 7, 0).Instance.ShouldBe("1,7");

    [Fact]
    public void ThisMachineAnswers()
    {
        // Whatever it is, Windows names at least one logical processor, and
        // every one of them is either P or E.
        Cores here = Cores.Here;

        here.All.Length.ShouldBe(Environment.ProcessorCount);
        (here.Performance.Length + here.Efficient.Length).ShouldBe(here.All.Length);
    }
}
