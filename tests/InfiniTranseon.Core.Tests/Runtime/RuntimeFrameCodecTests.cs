using System.Buffers.Binary;
using InfiniTranseon.Contracts.Runtime;
using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeFrameCodecTests
{
    [Fact]
    public async Task FrameRoundTripsWithoutLosingEnvelopeIdentity()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] payload = [1, 2, 3, 4, 5];
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.ControlRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            payload.Length,
            now.AddSeconds(5));
        using var frame = new RuntimeFrame(header, payload);
        await using var stream = new MemoryStream();

        await RuntimeFrameCodec.WriteAsync(stream, frame, CancellationToken.None);
        stream.Position = 0;
        using RuntimeFrame decoded = await RuntimeFrameCodec.ReadAsync(stream, now, CancellationToken.None);

        Assert.Equal(header, decoded.Header);
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeBodyAllocation()
    {
        byte[] prefix = new byte[RuntimeProtocol.FramePrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, RuntimeProtocol.MaxMessageBytes + 1);
        await using var stream = new MemoryStream(prefix);

        RuntimeProtocolException error = await Assert.ThrowsAsync<RuntimeProtocolException>(
            () => RuntimeFrameCodec.ReadAsync(stream, DateTimeOffset.UtcNow, CancellationToken.None).AsTask());

        Assert.Equal(RuntimeProtocolError.InvalidFrameLength, error.Error);
    }

    [Fact]
    public async Task TruncatedFrameIsRejectedExplicitly()
    {
        byte[] bytes = new byte[RuntimeProtocol.FramePrefixBytes + 4];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(0, RuntimeProtocol.FramePrefixBytes),
            RuntimeProtocol.WireHeaderBytes);
        await using var stream = new MemoryStream(bytes);

        RuntimeProtocolException error = await Assert.ThrowsAsync<RuntimeProtocolException>(
            () => RuntimeFrameCodec.ReadAsync(stream, DateTimeOffset.UtcNow, CancellationToken.None).AsTask());

        Assert.Equal(RuntimeProtocolError.FrameTruncated, error.Error);
    }

    [Fact]
    public async Task PayloadLengthMismatchIsRejectedBeforeWriting()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.ControlRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            99,
            now.AddSeconds(5));
        using var frame = new RuntimeFrame(header, [1, 2, 3]);
        await using var stream = new MemoryStream();

        RuntimeProtocolException error = await Assert.ThrowsAsync<RuntimeProtocolException>(
            () => RuntimeFrameCodec.WriteAsync(stream, frame, CancellationToken.None).AsTask());

        Assert.Equal(RuntimeProtocolError.InvalidPayloadLength, error.Error);
    }

    [Fact]
    public async Task ExpiredFrameIsRejectedAfterDecoding()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.ControlRequest,
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            now.AddMilliseconds(-1));
        using var frame = new RuntimeFrame(header, []);
        await using var stream = new MemoryStream();

        await RuntimeFrameCodec.WriteAsync(stream, frame, CancellationToken.None, validateDeadline: false);
        stream.Position = 0;
        RuntimeProtocolException error = await Assert.ThrowsAsync<RuntimeProtocolException>(
            () => RuntimeFrameCodec.ReadAsync(stream, now, CancellationToken.None).AsTask());

        Assert.Equal(RuntimeProtocolError.DeadlineExpired, error.Error);
    }
}
