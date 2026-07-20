using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public enum RuntimeMessageLane
{
    Control,
    Data,
}

public static class RuntimeMessageLaneClassifier
{
    public static RuntimeMessageLane Classify(RuntimeMessageKind kind) => kind switch
    {
        RuntimeMessageKind.CloudOcrCropRequest or RuntimeMessageKind.Thumbnail =>
            RuntimeMessageLane.Data,
        _ => RuntimeMessageLane.Control,
    };
}

public sealed record RuntimeIpcBackpressureOptions
{
    public RuntimeIpcBackpressureOptions(
        int controlMaxItems,
        int dataMaxItems,
        long controlMaxBytes,
        long dataMaxBytes,
        long totalMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(controlMaxItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataMaxItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(controlMaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataMaxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalMaxBytes, 1);

        ControlMaxItems = controlMaxItems;
        DataMaxItems = dataMaxItems;
        ControlMaxBytes = controlMaxBytes;
        DataMaxBytes = dataMaxBytes;
        TotalMaxBytes = totalMaxBytes;
    }

    public static RuntimeIpcBackpressureOptions Default { get; } = new(
        controlMaxItems: 64,
        dataMaxItems: 8,
        controlMaxBytes: 1_048_576,
        dataMaxBytes: RuntimeProtocol.MaxInFlightBytes,
        totalMaxBytes: RuntimeProtocol.MaxInFlightBytes);

    public int ControlMaxItems { get; }
    public int DataMaxItems { get; }
    public long ControlMaxBytes { get; }
    public long DataMaxBytes { get; }
    public long TotalMaxBytes { get; }
}

public sealed class RuntimeIpcAdmission
{
    private readonly object _gate = new();
    private readonly RuntimeIpcBackpressureOptions _options;
    private int _controlItems;
    private int _dataItems;
    private long _controlBytes;
    private long _dataBytes;

    public RuntimeIpcAdmission(RuntimeIpcBackpressureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public long TotalReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _controlBytes + _dataBytes;
            }
        }
    }

    public bool TryAcquire(RuntimeMessageLane lane, int bytes, out RuntimeIpcLease? lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        lock (_gate)
        {
            int items = lane == RuntimeMessageLane.Control ? _controlItems : _dataItems;
            int maxItems = lane == RuntimeMessageLane.Control
                ? _options.ControlMaxItems
                : _options.DataMaxItems;
            long laneBytes = lane == RuntimeMessageLane.Control ? _controlBytes : _dataBytes;
            long maxLaneBytes = lane == RuntimeMessageLane.Control
                ? _options.ControlMaxBytes
                : _options.DataMaxBytes;
            if (items >= maxItems || bytes > maxLaneBytes - laneBytes ||
                bytes > _options.TotalMaxBytes - (_controlBytes + _dataBytes))
            {
                lease = null;
                return false;
            }

            if (lane == RuntimeMessageLane.Control)
            {
                ++_controlItems;
                _controlBytes += bytes;
            }
            else
            {
                ++_dataItems;
                _dataBytes += bytes;
            }
            lease = new RuntimeIpcLease(this, lane, bytes);
            return true;
        }
    }

    internal void Release(RuntimeMessageLane lane, int bytes)
    {
        lock (_gate)
        {
            if (lane == RuntimeMessageLane.Control)
            {
                --_controlItems;
                _controlBytes -= bytes;
            }
            else
            {
                --_dataItems;
                _dataBytes -= bytes;
            }
        }
    }
}

public sealed class RuntimeIpcLease : IDisposable
{
    private RuntimeIpcAdmission? _owner;
    private readonly RuntimeMessageLane _lane;
    private readonly int _bytes;

    internal RuntimeIpcLease(RuntimeIpcAdmission owner, RuntimeMessageLane lane, int bytes)
    {
        _owner = owner;
        _lane = lane;
        _bytes = bytes;
    }

    public void Dispose()
    {
        RuntimeIpcAdmission? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(_lane, _bytes);
    }
}
