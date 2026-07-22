using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Contracts.Translation;

public static class LocalWorkerProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumTextCharacters = 16_384;
    public const long DefaultMaximumCommittedBytes = RuntimeCapabilities.ModelWorkerCommittedBytesLimit;
}

public sealed record LocalWorkerBootstrap(
    int ProtocolVersion,
    string PipeName,
    int ExpectedParentProcessId,
    Guid WorkerSessionEpoch,
    string BootstrapSecretBase64,
    string ManagedModelDirectory = "");

public sealed record LocalTranslationRequest(
    int ProtocolVersion,
    Guid WorkerSessionEpoch,
    Guid RequestId,
    string ModelId,
    string SourceLanguage,
    string TargetLanguage,
    string Text,
    int MaximumOutputCharacters);

public sealed record LocalTranslationResponse(
    int ProtocolVersion,
    Guid WorkerSessionEpoch,
    Guid RequestId,
    bool Success,
    string? Text,
    string? ErrorCode);
