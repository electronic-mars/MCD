using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Mcd.Sensors.Providers;

/// <summary>What a HWiNFO reading measures.</summary>
public enum HwInfoReadingType
{
    None,
    Temperature,
    Voltage,
    Fan,
    Current,
    Power,
    Clock,
    Usage,
    Other,
}

/// <summary>One piece of hardware HWiNFO is watching.</summary>
public sealed record HwInfoSensor(uint Id, uint Instance, string Name);

/// <param name="SensorIndex">Which entry of the sensor table this belongs to.</param>
public sealed record HwInfoReading(
    HwInfoReadingType Type,
    int SensorIndex,
    uint Id,
    string Label,
    string Unit,
    double Value);

/// <summary>Where the tables sit right now, and when HWiNFO last wrote them.</summary>
public readonly record struct HwInfoShape(
    long Polled,
    int SensorAt,
    int SensorStride,
    int SensorCount,
    int ReadingAt,
    int ReadingStride,
    int ReadingCount);

/// <param name="PolledAt">
/// When HWiNFO last refreshed the block. The one field worth watching: in the
/// free version the shared memory is published for twelve hours and then simply
/// stops advancing, with the block still mapped and every value still readable.
/// </param>
public sealed record HwInfoBlock(
    DateTimeOffset PolledAt,
    ImmutableArray<HwInfoSensor> Sensors,
    ImmutableArray<HwInfoReading> Readings)
{
    /// <summary>The hardware a reading came from, or null if the block is inconsistent.</summary>
    public HwInfoSensor? Owner(HwInfoReading reading) =>
        (uint)reading.SensorIndex < (uint)Sensors.Length ? Sensors[reading.SensorIndex] : null;
}

/// <summary>
/// Reads the block HWiNFO publishes for other programs to use.
/// </summary>
/// <remarks>
/// <para>
/// The layout is HWiNFO's documented one and is fixed at the field level, but
/// the size of a table row is taken from the header rather than assumed: HWiNFO
/// has added fields to the end of a row before, and a program that hard-codes
/// the stride reads every row after the first at the wrong offset.
/// </para>
/// <para>
/// HWiNFO writes while we read, so a value can be caught half-updated. That is
/// left alone rather than locked against: the worst case is one odd number for
/// one second, and there is no lock on offer anyway.
/// </para>
/// </remarks>
public sealed class HwInfoSharedMemory : IDisposable
{
    /// <summary>
    /// Created by HWiNFO, which runs elevated. Readers do not need to be: HWiNFO
    /// grants read access to everyone precisely so that ordinary programs can
    /// use it.
    /// </summary>
    public const string SectionName = "Global\\HWiNFO_SENS_SM2";

    private const int HeaderSize = 48;
    private const int NameLength = 128;
    private const int UnitLength = 16;

    /// <summary>"HWiS", as it sits in the first four bytes.</summary>
    private const uint Signature = 0x53695748;

    /// <summary>Where the value sits in a reading row, after the alignment gap that follows the unit.</summary>
    private const int ValueAt = 12 + (2 * NameLength) + UnitLength + 4;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    private HwInfoSharedMemory(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _file = file;
        _view = view;
    }

    /// <summary>Opens the block, or null when HWiNFO is not publishing one.</summary>
    /// <remarks>
    /// Absent for two quite different reasons - HWiNFO is not running, or it is
    /// running with shared memory switched off - and the two cannot be told
    /// apart from here. The provider says both.
    /// </remarks>
    public static HwInfoSharedMemory? TryOpen()
    {
        try
        {
            MemoryMappedFile file = MemoryMappedFile.OpenExisting(
                SectionName, MemoryMappedFileRights.Read);

            return new HwInfoSharedMemory(file, file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly byte[] _header = new byte[HeaderSize];

    /// <summary>
    /// The header, read fresh, or null when the block is not one.
    /// </summary>
    /// <remarks>
    /// The cheap read: forty-eight bytes, no strings. A tick that knows where
    /// its rows are needs nothing else from the block but the rows themselves.
    /// </remarks>
    public HwInfoShape? Shape()
    {
        if (_view.Capacity < HeaderSize)
        {
            return null;
        }

        _view.ReadArray(0, _header, 0, HeaderSize);

        if (Extent(_header) is not { } end || end > _view.Capacity)
        {
            return null;
        }

        return new HwInfoShape(
            BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(16)),
            (int)U32(_header, 24),
            (int)U32(_header, 28),
            (int)U32(_header, 32),
            (int)U32(_header, 36),
            (int)U32(_header, 40),
            (int)U32(_header, 44));
    }

    /// <summary>One reading row's identity and value, straight from the view.</summary>
    /// <remarks>The row must be inside the shape this same tick handed out.</remarks>
    public (uint Id, int SensorIndex, double Value) Reading(in HwInfoShape shape, int row)
    {
        long at = shape.ReadingAt + ((long)row * shape.ReadingStride);

        return (
            _view.ReadUInt32(at + 8),
            (int)_view.ReadUInt32(at + 4),
            _view.ReadDouble(at + ValueAt));
    }

    /// <summary>Which piece of hardware a sensor-table entry stands for.</summary>
    public (uint Id, uint Instance) SensorIdentity(in HwInfoShape shape, int index)
    {
        long at = shape.SensorAt + ((long)index * shape.SensorStride);

        return (_view.ReadUInt32(at), _view.ReadUInt32(at + 4));
    }

    /// <summary>A copy of the whole block, or null if it does not look like one.</summary>
    public byte[]? Copy()
    {
        var header = new byte[HeaderSize];
        _view.ReadArray(0, header, 0, HeaderSize);

        if (Extent(header) is not { } end || end > _view.Capacity)
        {
            return null;
        }

        var buffer = new byte[end];
        _view.ReadArray(0, buffer, 0, (int)end);
        return buffer;
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>The block as HWiNFO meant it, or null if the bytes are not one.</summary>
    public static HwInfoBlock? Parse(ReadOnlySpan<byte> buffer)
    {
        if (Extent(buffer) is not { } end || end > buffer.Length)
        {
            return null;
        }

        long polled = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]);

        int sensorAt = (int)U32(buffer, 24);
        int sensorStride = (int)U32(buffer, 28);
        int sensorCount = (int)U32(buffer, 32);
        int readingAt = (int)U32(buffer, 36);
        int readingStride = (int)U32(buffer, 40);
        int readingCount = (int)U32(buffer, 44);

        ImmutableArray<HwInfoSensor>.Builder sensors =
            ImmutableArray.CreateBuilder<HwInfoSensor>(sensorCount);

        for (int i = 0; i < sensorCount; i++)
        {
            ReadOnlySpan<byte> row = buffer.Slice(sensorAt + (i * sensorStride), sensorStride);

            // The user-edited name when there is one, because someone who
            // renamed a sensor in HWiNFO meant it to be called that.
            sensors.Add(new HwInfoSensor(
                U32(row, 0),
                U32(row, 4),
                Text(row, 8 + NameLength, NameLength) is { Length: > 0 } renamed
                    ? renamed
                    : Text(row, 8, NameLength)));
        }

        ImmutableArray<HwInfoReading>.Builder readings =
            ImmutableArray.CreateBuilder<HwInfoReading>(readingCount);

        for (int i = 0; i < readingCount; i++)
        {
            ReadOnlySpan<byte> row = buffer.Slice(readingAt + (i * readingStride), readingStride);

            readings.Add(new HwInfoReading(
                (HwInfoReadingType)Math.Min(U32(row, 0), (uint)HwInfoReadingType.Other),
                (int)U32(row, 4),
                U32(row, 8),
                Text(row, 12 + NameLength, NameLength) is { Length: > 0 } renamed
                    ? renamed
                    : Text(row, 12, NameLength),
                Text(row, 12 + (2 * NameLength), UnitLength),
                BinaryPrimitives.ReadDoubleLittleEndian(row[ValueAt..])));
        }

        return new HwInfoBlock(
            DateTimeOffset.FromUnixTimeSeconds(polled),
            sensors.MoveToImmutable(),
            readings.MoveToImmutable());
    }

    /// <summary>
    /// How many bytes the header says the block occupies, or null if the header
    /// is not a HWiNFO one or describes something impossible.
    /// </summary>
    /// <remarks>
    /// Every number here comes from another process and is checked before it is
    /// used as a length. A row stride shorter than the fields it must hold would
    /// have us read a value out of the middle of the row's own text.
    /// </remarks>
    private static long? Extent(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || U32(header, 0) != Signature)
        {
            return null;
        }

        long sensorAt = U32(header, 24);
        long sensorStride = U32(header, 28);
        long sensorCount = U32(header, 32);
        long readingAt = U32(header, 36);
        long readingStride = U32(header, 40);
        long readingCount = U32(header, 44);

        const int SensorRowNeeds = 8 + (2 * NameLength);
        const int ReadingRowNeeds = ValueAt + 8;

        if (sensorStride < SensorRowNeeds || readingStride < ReadingRowNeeds
            || sensorAt < HeaderSize || readingAt < HeaderSize
            || sensorCount > 4096 || readingCount > 65536)
        {
            return null;
        }

        return Math.Max(sensorAt + (sensorStride * sensorCount), readingAt + (readingStride * readingCount));
    }

    private static uint U32(ReadOnlySpan<byte> from, int at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(from[at..]);

    /// <summary>
    /// A fixed-width name, up to its first NUL.
    /// </summary>
    /// <remarks>
    /// Single-byte characters, so Latin-1 rather than UTF-8: a degree sign in a
    /// unit is one byte here, and decoding it as UTF-8 would turn it into a
    /// replacement character.
    /// </remarks>
    private static string Text(ReadOnlySpan<byte> from, int at, int length)
    {
        ReadOnlySpan<byte> raw = from.Slice(at, length);
        int end = raw.IndexOf((byte)0);

        return Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]).Trim();
    }
}
