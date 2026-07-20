using System.Buffers.Binary;
using System.Security.Cryptography;
using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Runtime;

public sealed record RuntimeHandshakeResult(int AuthenticatedClientProcessId, Guid RuntimeEpoch);

public static class RuntimeHandshakeFrames
{
    private const int ProcessIdBytes = sizeof(int);
    private const int PayloadBytes = (2 * ProcessIdBytes) + RuntimeProtocol.BootstrapNonceBytes;
    private const int ResponsePayloadBytes = ProcessIdBytes;

    public static RuntimeFrame CreateRequest(
        int localProcessId,
        int expectedPeerProcessId,
        Guid epoch,
        ReadOnlySpan<byte> nonce,
        DateTimeOffset deadlineUtc)
    {
        if (localProcessId <= 0 || expectedPeerProcessId <= 0)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPeerProcessId);
        }

        if (epoch == Guid.Empty)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        }

        if (nonce.Length != RuntimeProtocol.BootstrapNonceBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidBootstrapNonce);
        }

        byte[] payload = new byte[PayloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, localProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(ProcessIdBytes), expectedPeerProcessId);
        nonce.CopyTo(payload.AsSpan(2 * ProcessIdBytes));
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.HandshakeRequest,
            Guid.NewGuid(),
            epoch,
            payload.Length,
            deadlineUtc);
        return RuntimeFrame.TakeOwnership(header, payload);
    }

    internal static RuntimeHandshakePayload DecodeRequest(RuntimeFrame frame)
    {
        if (frame.Header.MessageKind != RuntimeMessageKind.HandshakeRequest)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.UnexpectedMessageKind);
        }

        if (frame.Payload.Length != PayloadBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
        }

        ReadOnlySpan<byte> payload = frame.Payload.Span;
        return new RuntimeHandshakePayload(
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[ProcessIdBytes..]),
            payload[(2 * ProcessIdBytes)..].ToArray());
    }

    public static RuntimeFrame CreateAcceptedResponse(
        RuntimeEnvelopeHeader requestHeader,
        int localProcessId,
        DateTimeOffset deadlineUtc)
    {
        ArgumentNullException.ThrowIfNull(requestHeader);
        if (requestHeader.MessageKind != RuntimeMessageKind.HandshakeRequest)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.UnexpectedMessageKind);
        }

        if (localProcessId <= 0)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPeerProcessId);
        }

        byte[] payload = new byte[ResponsePayloadBytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, localProcessId);
        var header = new RuntimeEnvelopeHeader(
            RuntimeProtocol.CurrentVersion,
            RuntimeMessageKind.HandshakeResponse,
            requestHeader.RequestId,
            requestHeader.RuntimeEpoch,
            payload.Length,
            deadlineUtc);
        return RuntimeFrame.TakeOwnership(header, payload);
    }

    public static int ValidateAcceptedResponse(
        RuntimeFrame response,
        RuntimeEnvelopeHeader requestHeader,
        int expectedPeerProcessId,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(requestHeader);
        RuntimeProtocolValidator.Validate(response.Header, utcNow);
        bool valid = response.Header.MessageKind == RuntimeMessageKind.HandshakeResponse
            && response.Header.RequestId == requestHeader.RequestId
            && response.Header.RuntimeEpoch == requestHeader.RuntimeEpoch
            && response.Payload.Length == ResponsePayloadBytes;
        int peerProcessId = valid
            ? BinaryPrimitives.ReadInt32LittleEndian(response.Payload.Span)
            : 0;
        if (!valid || peerProcessId != expectedPeerProcessId)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
        }

        return peerProcessId;
    }

    internal sealed record RuntimeHandshakePayload(
        int ClaimedClientProcessId,
        int ExpectedPeerProcessId,
        byte[] Nonce);
}

public sealed class RuntimeHandshakeAuthenticator : IDisposable
{
    private readonly int _localProcessId;
    private readonly int _expectedClientProcessId;
    private readonly Guid _runtimeEpoch;
    private readonly byte[] _expectedNonce;
    private int _attempted;
    private int _disposed;

    public RuntimeHandshakeAuthenticator(
        int localProcessId,
        int expectedClientProcessId,
        Guid runtimeEpoch,
        ReadOnlySpan<byte> expectedNonce)
    {
        if (localProcessId <= 0 || expectedClientProcessId <= 0)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPeerProcessId);
        }

        if (runtimeEpoch == Guid.Empty)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        }

        if (expectedNonce.Length != RuntimeProtocol.BootstrapNonceBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidBootstrapNonce);
        }

        _localProcessId = localProcessId;
        _expectedClientProcessId = expectedClientProcessId;
        _runtimeEpoch = runtimeEpoch;
        _expectedNonce = expectedNonce.ToArray();
    }

    public RuntimeHandshakeResult Authenticate(
        RuntimeFrame request,
        int actualClientProcessId,
        DateTimeOffset utcNow)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.Exchange(ref _attempted, 1) != 0)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.HandshakeAlreadyAttempted);
        }

        try
        {
            RuntimeProtocolValidator.Validate(request.Header, utcNow);
            RuntimeHandshakeFrames.RuntimeHandshakePayload payload = RuntimeHandshakeFrames.DecodeRequest(request);
            bool authenticated = actualClientProcessId == _expectedClientProcessId
                && payload.ClaimedClientProcessId == actualClientProcessId
                && payload.ExpectedPeerProcessId == _localProcessId
                && request.Header.RuntimeEpoch == _runtimeEpoch
                && CryptographicOperations.FixedTimeEquals(payload.Nonce, _expectedNonce);
            CryptographicOperations.ZeroMemory(payload.Nonce);

            if (!authenticated)
            {
                throw new RuntimeProtocolException(RuntimeProtocolError.AuthenticationFailed);
            }

            return new RuntimeHandshakeResult(actualClientProcessId, _runtimeEpoch);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_expectedNonce);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(_expectedNonce);
        }
    }
}
