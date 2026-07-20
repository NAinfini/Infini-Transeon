using System.Threading.Channels;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public sealed class RuntimeFrameQueue : IDisposable
{
    private readonly Channel<RuntimeFrame> _channel;
    private readonly long _maxBytes;
    private long _reservedBytes;
    private int _disposed;

    public RuntimeFrameQueue(int maxItems, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        _maxBytes = maxBytes;
        _channel = Channel.CreateBounded<RuntimeFrame>(new BoundedChannelOptions(maxItems)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    public bool TryEnqueue(RuntimeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        int frameBytes = checked(RuntimeProtocol.WireHeaderBytes + frame.Payload.Length);
        if (!TryReserve(frameBytes))
        {
            return false;
        }

        if (_channel.Writer.TryWrite(frame))
        {
            return true;
        }

        Interlocked.Add(ref _reservedBytes, -frameBytes);
        return false;
    }

    public async ValueTask<RuntimeFrameLease> ReadAsync(CancellationToken cancellationToken)
    {
        RuntimeFrame frame = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        int frameBytes = checked(RuntimeProtocol.WireHeaderBytes + frame.Payload.Length);
        return new RuntimeFrameLease(this, frame, frameBytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out RuntimeFrame? frame))
        {
            int frameBytes = checked(RuntimeProtocol.WireHeaderBytes + frame.Payload.Length);
            frame.Dispose();
            Release(frameBytes);
        }
    }

    internal void Release(int frameBytes) => Interlocked.Add(ref _reservedBytes, -frameBytes);

    private bool TryReserve(int frameBytes)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _reservedBytes);
            if (frameBytes > _maxBytes - current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reservedBytes, current + frameBytes, current) == current)
            {
                return true;
            }
        }
    }
}

public sealed class RuntimeFrameLease : IAsyncDisposable
{
    private RuntimeFrameQueue? _owner;
    private readonly int _frameBytes;

    internal RuntimeFrameLease(RuntimeFrameQueue owner, RuntimeFrame frame, int frameBytes)
    {
        _owner = owner;
        Frame = frame;
        _frameBytes = frameBytes;
    }

    public RuntimeFrame Frame { get; }

    public ValueTask DisposeAsync()
    {
        RuntimeFrameQueue? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is not null)
        {
            Frame.Dispose();
            owner.Release(_frameBytes);
        }

        return ValueTask.CompletedTask;
    }
}
