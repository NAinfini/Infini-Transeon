namespace InfiniTranseon.Contracts.Runtime;

public static class RuntimeProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 8_388_608;
    public const long MaxInFlightBytes = 33_554_432;
    public const int BootstrapNonceBytes = 32;
}

public enum RuntimeMessageKind
{
    HandshakeRequest,
    HandshakeResponse,
    ControlRequest,
    ControlResponse,
    TargetSnapshot,
    TargetLifecycle,
    OcrResult,
    CloudOcrCropRequest,
    TranslationOutput,
    TranslationStreamSnapshot,
    OverlayDesiredState,
    PolicyRevision,
    PolicyAcknowledgement,
    DegradationSnapshot,
    DiagnosticEvent,
    Thumbnail,
    ReconnectSnapshot,
    ShutdownRequest,
    ShutdownAcknowledgement,
}

public sealed record RuntimeEnvelopeHeader(
    int ProtocolVersion,
    RuntimeMessageKind MessageKind,
    Guid RequestId,
    Guid RuntimeEpoch,
    int PayloadLength,
    DateTimeOffset DeadlineUtc);

public sealed class RuntimeHandshakeRequest
{
    private readonly byte[] _bootstrapNonce;

    public RuntimeHandshakeRequest(
        int protocolVersion,
        Guid runtimeEpoch,
        int expectedPeerProcessId,
        ReadOnlySpan<byte> bootstrapNonce)
    {
        ProtocolVersion = protocolVersion;
        RuntimeEpoch = runtimeEpoch;
        ExpectedPeerProcessId = expectedPeerProcessId;
        _bootstrapNonce = bootstrapNonce.ToArray();
    }

    public int ProtocolVersion { get; }

    public Guid RuntimeEpoch { get; }

    public int ExpectedPeerProcessId { get; }

    public ReadOnlyMemory<byte> BootstrapNonce => _bootstrapNonce;
}

public enum RuntimeProtocolError
{
    VersionMismatch,
    UnknownMessageKind,
    InvalidRequestId,
    InvalidRuntimeEpoch,
    InvalidPayloadLength,
    DeadlineExpired,
    InvalidPeerProcessId,
    InvalidBootstrapNonce,
}

public sealed class RuntimeProtocolException : Exception
{
    public RuntimeProtocolException(RuntimeProtocolError error)
        : base($"Runtime protocol validation failed: {error}.")
    {
        Error = error;
    }

    public RuntimeProtocolError Error { get; }

    public string LocalizationKey => $"runtime.protocol.{Error}";
}

public static class RuntimeProtocolValidator
{
    public static void Validate(RuntimeEnvelopeHeader header, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(header);

        ValidateVersion(header.ProtocolVersion);
        if (!Enum.IsDefined(header.MessageKind))
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.UnknownMessageKind);
        }

        if (header.RequestId == Guid.Empty)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRequestId);
        }

        if (header.RuntimeEpoch == Guid.Empty)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        }

        if (header.PayloadLength is < 0 or > RuntimeProtocol.MaxMessageBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
        }

        if (header.DeadlineUtc <= utcNow)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.DeadlineExpired);
        }
    }

    public static void Validate(RuntimeHandshakeRequest handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        ValidateVersion(handshake.ProtocolVersion);
        if (handshake.RuntimeEpoch == Guid.Empty)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidRuntimeEpoch);
        }

        if (handshake.ExpectedPeerProcessId <= 0)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPeerProcessId);
        }

        if (handshake.BootstrapNonce.Length != RuntimeProtocol.BootstrapNonceBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidBootstrapNonce);
        }
    }

    private static void ValidateVersion(int protocolVersion)
    {
        if (protocolVersion != RuntimeProtocol.CurrentVersion)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.VersionMismatch);
        }
    }
}
