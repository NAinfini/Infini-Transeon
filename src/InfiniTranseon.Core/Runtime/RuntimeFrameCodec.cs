using System.Buffers;
using System.Buffers.Binary;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public static class RuntimeFrameCodec
{
    public static async ValueTask WriteAsync(
        Stream stream,
        RuntimeFrame frame,
        CancellationToken cancellationToken,
        bool validateDeadline = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);

        RuntimeEnvelopeHeader header = frame.Header;
        RuntimeProtocolValidator.Validate(
            header,
            validateDeadline ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue);
        if (header.PayloadLength != frame.Payload.Length)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
        }

        int bodyLength = checked(RuntimeProtocol.WireHeaderBytes + header.PayloadLength);
        byte[] metadata = new byte[RuntimeProtocol.FramePrefixBytes + RuntimeProtocol.WireHeaderBytes];
        Span<byte> prefix = metadata.AsSpan(0, RuntimeProtocol.FramePrefixBytes);
        Span<byte> wireHeader = metadata.AsSpan(RuntimeProtocol.FramePrefixBytes);
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bodyLength);
        WriteHeader(wireHeader, header);

        await stream.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
        if (!frame.Payload.IsEmpty)
        {
            await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<RuntimeFrame> ReadAsync(
        Stream stream,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] prefix = new byte[RuntimeProtocol.FramePrefixBytes];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (bodyLength is < RuntimeProtocol.WireHeaderBytes or > RuntimeProtocol.MaxMessageBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidFrameLength);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(bodyLength);
        try
        {
            Memory<byte> body = rented.AsMemory(0, bodyLength);
            await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
            RuntimeEnvelopeHeader header = ReadHeader(body.Span[..RuntimeProtocol.WireHeaderBytes]);
            RuntimeProtocolValidator.Validate(header, utcNow);
            if (header.PayloadLength != bodyLength - RuntimeProtocol.WireHeaderBytes)
            {
                throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
            }

            byte[] payload = body.Span[RuntimeProtocol.WireHeaderBytes..].ToArray();
            return RuntimeFrame.TakeOwnership(header, payload);
        }
        finally
        {
            Array.Clear(rented, 0, bodyLength);
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteHeader(Span<byte> destination, RuntimeEnvelopeHeader header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, RuntimeProtocol.WireMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], header.ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], (int)header.MessageKind);
        header.RequestId.TryWriteBytes(destination[12..28]);
        header.RuntimeEpoch.TryWriteBytes(destination[28..44]);
        BinaryPrimitives.WriteInt64LittleEndian(destination[44..], header.DeadlineUtc.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(destination[52..], header.PayloadLength);
    }

    private static RuntimeEnvelopeHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != RuntimeProtocol.WireMagic)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidMagic);
        }

        DateTimeOffset deadline;
        try
        {
            deadline = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(source[44..]), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidDeadline, error);
        }

        return new RuntimeEnvelopeHeader(
            BinaryPrimitives.ReadInt32LittleEndian(source[4..]),
            (RuntimeMessageKind)BinaryPrimitives.ReadInt32LittleEndian(source[8..]),
            new Guid(source[12..28]),
            new Guid(source[28..44]),
            BinaryPrimitives.ReadInt32LittleEndian(source[52..]),
            deadline);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException error)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.FrameTruncated, error);
        }
    }
}
