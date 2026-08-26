using System.Buffers.Binary;
using System.Text;
using Mcd.Sensors.Providers;
using Shouldly;

namespace Mcd.Tests.Sensors;

/// <summary>
/// Reading HWiNFO's published block.
/// </summary>
/// <remarks>
/// The block is laid out by a C compiler with its ordinary alignment rules, and
/// the two row sizes below are what those rules produce. They are asserted here
/// as constants rather than derived, because that is the whole content of the
/// format: get a stride wrong and every row after the first is read from the
/// middle of its neighbour, which does not throw and does not look wrong until
/// you notice the processor is apparently at four degrees.
/// </remarks>
public sealed class HwInfoSharedMemoryTests
{
    private const int SensorRow = 264;
    private const int ReadingRow = 320;

    [Fact]
    public void AWellFormedBlockIsRead()
    {
        byte[] block = Build(
            polledAt: 1_724_680_000,
            sensors: [(0xF0000100, 0, "CPU [#0]: Intel Core i9")],
            readings:
            [
                (1u, 0, 0x10, "CPU Package", "°C", 63.0),
                (1u, 0, 0x11, "Core 0", "°C", 58.0),
            ]);

        HwInfoBlock parsed = HwInfoSharedMemory.Parse(block).ShouldNotBeNull();

        parsed.PolledAt.ToUnixTimeSeconds().ShouldBe(1_724_680_000);
        parsed.Sensors.Single().Name.ShouldBe("CPU [#0]: Intel Core i9");
        parsed.Readings.Length.ShouldBe(2);

        HwInfoReading package = parsed.Readings[0];
        package.Type.ShouldBe(HwInfoReadingType.Temperature);
        package.Label.ShouldBe("CPU Package");
        package.Unit.ShouldBe("°C");
        package.Value.ShouldBe(63.0);
        parsed.Owner(package)!.Name.ShouldBe("CPU [#0]: Intel Core i9");

        // The second row, which is the one a wrong stride ruins.
        parsed.Readings[1].Label.ShouldBe("Core 0");
        parsed.Readings[1].Value.ShouldBe(58.0);
    }

    [Fact]
    public void ARowLongerThanWeExpectStillReadsCorrectly()
    {
        // HWiNFO has added fields to the end of a row before. Taking the stride
        // from the header rather than assuming it is what keeps that from
        // turning into nonsense.
        byte[] block = Build(
            polledAt: 1,
            sensors: [(0xF0000100, 0, "CPU [#0]")],
            readings: [(1u, 0, 0x10, "CPU Package", "°C", 71.0), (1u, 0, 0x11, "CPU IA Cores", "°C", 69.0)],
            readingStride: ReadingRow + 64);

        HwInfoBlock parsed = HwInfoSharedMemory.Parse(block).ShouldNotBeNull();

        parsed.Readings[1].Label.ShouldBe("CPU IA Cores");
        parsed.Readings[1].Value.ShouldBe(69.0);
    }

    [Fact]
    public void SomethingThatIsNotTheBlockIsRefused()
    {
        byte[] block = Build(1, [(1, 0, "CPU")], [(1u, 0, 1, "CPU Package", "°C", 50)]);
        block[0] = (byte)'X';

        HwInfoSharedMemory.Parse(block).ShouldBeNull();
    }

    [Fact]
    public void ARowTooShortToHoldItsFieldsIsRefused()
    {
        // Every number here comes from another process. A stride of eight would
        // otherwise have us read a temperature out of the middle of a label.
        byte[] block = Build(1, [(1, 0, "CPU")], [(1u, 0, 1, "CPU Package", "°C", 50)]);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40), 8);

        HwInfoSharedMemory.Parse(block).ShouldBeNull();
    }

    [Fact]
    public void ACountThatOverrunsTheBlockIsRefused()
    {
        byte[] block = Build(1, [(1, 0, "CPU")], [(1u, 0, 1, "CPU Package", "°C", 50)]);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(44), 900);

        HwInfoSharedMemory.Parse(block).ShouldBeNull();
    }

    /// <summary>A block in HWiNFO's layout, for the parser to read back.</summary>
    private static byte[] Build(
        long polledAt,
        (uint Id, uint Instance, string Name)[] sensors,
        (uint Type, int SensorIndex, uint Id, string Label, string Unit, double Value)[] readings,
        int readingStride = ReadingRow)
    {
        const int header = 48;
        int sensorAt = header;
        int readingAt = sensorAt + (SensorRow * sensors.Length);

        var block = new byte[readingAt + (readingStride * readings.Length)];

        Encoding.Latin1.GetBytes("HWiS").CopyTo(block, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(16), polledAt);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(24), (uint)sensorAt);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(28), SensorRow);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32), (uint)sensors.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(36), (uint)readingAt);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40), (uint)readingStride);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(44), (uint)readings.Length);

        for (int i = 0; i < sensors.Length; i++)
        {
            int at = sensorAt + (i * SensorRow);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), sensors[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 4), sensors[i].Instance);
            Encoding.Latin1.GetBytes(sensors[i].Name).CopyTo(block, at + 8);
        }

        for (int i = 0; i < readings.Length; i++)
        {
            int at = readingAt + (i * readingStride);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), readings[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 4), (uint)readings[i].SensorIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 8), readings[i].Id);
            Encoding.Latin1.GetBytes(readings[i].Label).CopyTo(block, at + 12);
            Encoding.Latin1.GetBytes(readings[i].Unit).CopyTo(block, at + 12 + 256);
            BinaryPrimitives.WriteDoubleLittleEndian(block.AsSpan(at + 12 + 256 + 16 + 4), readings[i].Value);
        }

        return block;
    }
}
