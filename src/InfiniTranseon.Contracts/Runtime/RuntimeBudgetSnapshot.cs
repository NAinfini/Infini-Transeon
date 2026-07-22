using System.Buffers.Binary;
using System.Text;

namespace InfiniTranseon.Contracts.Runtime;

public enum RuntimeBudgetUnit
{
    Bytes = 0,
    Slots = 1,
}

public sealed record RuntimeBudgetPool
{
    public RuntimeBudgetPool(
        string name,
        long limit,
        long committed,
        long reserved,
        RuntimeBudgetUnit unit = RuntimeBudgetUnit.Bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (limit < 0 || committed < 0 || reserved < 0 || committed > limit - reserved)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Budget values must be non-negative and fit within the limit.");
        }

        Name = name;
        Limit = limit;
        Committed = committed;
        Reserved = reserved;
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        Unit = unit;
    }

    public string Name { get; }

    public long Limit { get; }

    public long Committed { get; }

    public long Reserved { get; }

    public RuntimeBudgetUnit Unit { get; }

    public long Available => Limit - Committed - Reserved;
}

public sealed record RuntimeBudgetSnapshot
{
    public RuntimeBudgetSnapshot(
        int protocolVersion,
        Guid runtimeEpoch,
        IEnumerable<RuntimeBudgetPool> pools)
        : this(protocolVersion, runtimeEpoch, 1, DateTimeOffset.UnixEpoch, pools)
    {
    }

    public RuntimeBudgetSnapshot(
        int protocolVersion,
        Guid runtimeEpoch,
        long snapshotRevision,
        DateTimeOffset capturedAtUtc,
        IEnumerable<RuntimeBudgetPool> pools)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(protocolVersion, 1);
        if (runtimeEpoch == Guid.Empty)
        {
            throw new ArgumentException("Runtime epoch cannot be empty.", nameof(runtimeEpoch));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotRevision, 1);
        if (capturedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Budget capture time must use UTC.", nameof(capturedAtUtc));
        ArgumentNullException.ThrowIfNull(pools);

        RuntimeBudgetPool[] ownedPools = pools.ToArray();
        if (ownedPools.Select(pool => pool.Name).Distinct(StringComparer.Ordinal).Count() != ownedPools.Length)
        {
            throw new ArgumentException("Budget pool names must be unique.", nameof(pools));
        }

        ProtocolVersion = protocolVersion;
        RuntimeEpoch = runtimeEpoch;
        SnapshotRevision = snapshotRevision;
        CapturedAtUtc = capturedAtUtc;
        Pools = Array.AsReadOnly(ownedPools);
    }

    public int ProtocolVersion { get; }

    public Guid RuntimeEpoch { get; }

    public long SnapshotRevision { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public IReadOnlyList<RuntimeBudgetPool> Pools { get; }
}

public static class RuntimeBudgetSnapshotPayloadCodec
{
    private const int SchemaVersion = 1;
    private const int HeaderBytes = 40;
    private const int PoolFixedBytes = 32;
    private const int MaximumPools = 32;
    private const int MaximumPoolNameBytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(RuntimeBudgetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ProtocolVersion != RuntimeProtocol.CurrentVersion)
            throw new InvalidDataException("Budget protocol version is unsupported.");
        if (snapshot.Pools.Count > MaximumPools)
            throw new InvalidDataException("Budget snapshot contains too many pools.");
        byte[][] names = snapshot.Pools
            .Select(pool => StrictUtf8.GetBytes(pool.Name))
            .ToArray();
        if (names.Any(name => name.Length is < 1 or > MaximumPoolNameBytes))
            throw new InvalidDataException("Budget pool name is too large.");
        int length = checked(HeaderBytes +
            snapshot.Pools.Count * PoolFixedBytes + names.Sum(name => name.Length));
        byte[] payload = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, SchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), snapshot.Pools.Count);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), snapshot.SnapshotRevision);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16), snapshot.CapturedAtUtc.Ticks);
        snapshot.RuntimeEpoch.TryWriteBytes(payload.AsSpan(24, 16));
        int offset = HeaderBytes;
        for (int index = 0; index < snapshot.Pools.Count; index++)
        {
            RuntimeBudgetPool pool = snapshot.Pools[index];
            byte[] name = names[index];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), name.Length);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4), (int)pool.Unit);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 8), pool.Limit);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 16), pool.Committed);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 24), pool.Reserved);
            name.CopyTo(payload.AsSpan(offset + PoolFixedBytes));
            offset += PoolFixedBytes + name.Length;
        }
        return payload;
    }

    public static RuntimeBudgetSnapshot Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(payload) != SchemaVersion)
            throw new InvalidDataException("Budget snapshot header is invalid.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        if (count is < 0 or > MaximumPools)
            throw new InvalidDataException("Budget pool count is invalid.");
        long revision = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(payload[16..]);
        Guid epoch = new(payload.Slice(24, 16));
        var pools = new List<RuntimeBudgetPool>(count);
        int offset = HeaderBytes;
        try
        {
            for (int index = 0; index < count; index++)
            {
                if (payload.Length - offset < PoolFixedBytes)
                    throw new InvalidDataException("Budget pool entry is truncated.");
                int nameLength = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
                var unit = (RuntimeBudgetUnit)BinaryPrimitives.ReadInt32LittleEndian(
                    payload[(offset + 4)..]);
                long limit = BinaryPrimitives.ReadInt64LittleEndian(payload[(offset + 8)..]);
                long committed = BinaryPrimitives.ReadInt64LittleEndian(payload[(offset + 16)..]);
                long reserved = BinaryPrimitives.ReadInt64LittleEndian(payload[(offset + 24)..]);
                if (nameLength is < 1 or > MaximumPoolNameBytes ||
                    nameLength > payload.Length - offset - PoolFixedBytes ||
                    !Enum.IsDefined(unit))
                    throw new InvalidDataException("Budget pool entry is invalid.");
                string name = StrictUtf8.GetString(
                    payload.Slice(offset + PoolFixedBytes, nameLength));
                pools.Add(new RuntimeBudgetPool(name, limit, committed, reserved, unit));
                offset = checked(offset + PoolFixedBytes + nameLength);
            }
            if (offset != payload.Length)
                throw new InvalidDataException("Budget snapshot contains trailing bytes.");
            return new RuntimeBudgetSnapshot(
                RuntimeProtocol.CurrentVersion,
                epoch,
                revision,
                new DateTimeOffset(ticks, TimeSpan.Zero),
                pools);
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            throw new InvalidDataException("Budget snapshot payload is invalid.", exception);
        }
    }
}
