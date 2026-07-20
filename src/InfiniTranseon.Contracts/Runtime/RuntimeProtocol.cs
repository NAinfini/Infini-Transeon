namespace InfiniTranseon.Contracts.Runtime;

public static class RuntimeProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxMessageBytes = 8_388_608;
    public const long MaxInFlightBytes = 33_554_432;
    public const int BootstrapNonceBytes = 32;
    public const int FramePrefixBytes = sizeof(int);
    public const int WireHeaderBytes = 56;
    public const int MaxPayloadBytes = MaxMessageBytes - WireHeaderBytes;
    public const uint WireMagic = 0x50525449;
}

public enum RuntimeMessageKind
{
    HandshakeRequest = 0,
    HandshakeResponse = 1,
    ControlRequest = 2,
    ControlResponse = 3,
    TargetSnapshot = 4,
    TargetLifecycle = 5,
    OcrResult = 6,
    CloudOcrCropRequest = 7,
    TranslationOutput = 8,
    TranslationStreamSnapshot = 9,
    OverlayDesiredState = 10,
    PolicyRevision = 11,
    PolicyAcknowledgement = 12,
    DegradationSnapshot = 13,
    DiagnosticEvent = 14,
    Thumbnail = 15,
    ReconnectSnapshot = 16,
    ShutdownRequest = 17,
    ShutdownAcknowledgement = 18,
}

public sealed record RuntimeEnvelopeHeader(
    int ProtocolVersion,
    RuntimeMessageKind MessageKind,
    Guid RequestId,
    Guid RuntimeEpoch,
    int PayloadLength,
    DateTimeOffset DeadlineUtc);

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
    InvalidFrameLength,
    FrameTruncated,
    InvalidMagic,
    InvalidDeadline,
    UnexpectedMessageKind,
    AuthenticationFailed,
    HandshakeAlreadyAttempted,
}

public sealed class RuntimeProtocolException : Exception
{
    public RuntimeProtocolException(RuntimeProtocolError error)
        : base($"Runtime protocol validation failed: {error}.")
    {
        Error = error;
    }

    public RuntimeProtocolException(RuntimeProtocolError error, Exception innerException)
        : base($"Runtime protocol validation failed: {error}.", innerException)
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

        if (header.PayloadLength is < 0 or > RuntimeProtocol.MaxPayloadBytes)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.InvalidPayloadLength);
        }

        if (header.DeadlineUtc <= utcNow)
        {
            throw new RuntimeProtocolException(RuntimeProtocolError.DeadlineExpired);
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
