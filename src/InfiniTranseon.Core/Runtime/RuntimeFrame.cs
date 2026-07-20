using System.Security.Cryptography;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public sealed class RuntimeFrame : IDisposable
{
    private readonly byte[] _payload;
    private readonly bool _clearPayloadOnDispose;
    private int _disposed;

    public RuntimeFrame(RuntimeEnvelopeHeader header, ReadOnlySpan<byte> payload)
        : this(header, payload.ToArray(), clearPayloadOnDispose: IsSensitive(header.MessageKind))
    {
    }

    private RuntimeFrame(RuntimeEnvelopeHeader header, byte[] payload, bool clearPayloadOnDispose)
    {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
        _payload = payload;
        _clearPayloadOnDispose = clearPayloadOnDispose;
    }

    public RuntimeEnvelopeHeader Header { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    internal static RuntimeFrame TakeOwnership(RuntimeEnvelopeHeader header, byte[] payload) =>
        new(header, payload, clearPayloadOnDispose: IsSensitive(header.MessageKind));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _clearPayloadOnDispose)
        {
            CryptographicOperations.ZeroMemory(_payload);
        }
    }

    private static bool IsSensitive(RuntimeMessageKind messageKind) =>
        messageKind is RuntimeMessageKind.HandshakeRequest or RuntimeMessageKind.HandshakeResponse;
}
