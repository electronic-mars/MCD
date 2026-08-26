using Mcd.Interop.Storage;
using Shouldly;

namespace Mcd.Tests.Sensors;

/// <summary>
/// The layout of what a drive sends back, pinned to bytes a real drive sent.
/// </summary>
/// <remarks>
/// The fixture is the reply from the NVMe drive in the machine this was written
/// on, captured verbatim. It exists because the first version of this parser
/// read the temperature array from the wrong offset and, before that, asked for
/// the wrong property entirely - property 8 is <c>StorageDeviceTrimProperty</c>,
/// which answers cheerfully with twelve bytes and looks exactly like a drive
/// with no thermometer. Neither mistake is visible in code review; both are
/// obvious against real bytes.
/// </remarks>
public sealed class StorageTemperatureTests
{
    private static byte[] SkHynix =>
        File.ReadAllBytes(Path.Combine("fixtures", "storage", "nvme-sk-hynix-pvc10.bin"));

    [Fact]
    public void TheCompositeReadingIsTakenFromTheFirstEntry()
    {
        DriveTemperature? reading = StorageTemperature.Parse(SkHynix);

        reading.ShouldNotBeNull();

        // Three sensors reported 45, 46 and 51. The figure to show is the one
        // the firmware nominates as the composite, not the hottest of the three.
        reading!.Value.Celsius.ShouldBe((short)45);
    }

    [Fact]
    public void TheDriveDeclaresItsOwnLimits()
    {
        DriveTemperature reading = StorageTemperature.Parse(SkHynix)!.Value;

        // Better than any number this program could hard-code: the drive knows
        // what it was designed for, and drives differ.
        reading.Warning.ShouldBe((short)83);
        reading.Critical.ShouldBe((short)85);
    }

    [Fact]
    public void AReplyThatIsTooShortIsRefused()
    {
        // What property 8 sends back: a valid, complete, entirely unrelated
        // descriptor. It must not be read as a temperature of zero.
        byte[] trimDescriptor = [0x0C, 0, 0, 0, 0x0C, 0, 0, 0, 0x01, 0, 0, 0];

        StorageTemperature.Parse(trimDescriptor).ShouldBeNull();
        StorageTemperature.Parse(null).ShouldBeNull();
        StorageTemperature.Parse([]).ShouldBeNull();
    }

    [Fact]
    public void AnImpossibleReadingIsRefused()
    {
        byte[] buffer = SkHynix;

        // Some drives answer with zero to mean "no sensor". Zero degrees would
        // be shown as a measurement, and a cold drive is not the same thing as
        // a drive that will not say.
        buffer[26] = 0;
        buffer[27] = 0;

        StorageTemperature.Parse(buffer).ShouldBeNull();
    }
}
